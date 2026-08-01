using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace VictusControl.Core.Monitoring;

/// <summary>
/// Reads live system state. Every call here is potentially slow (WMI, process
/// enumeration, spawning nvidia-smi) and must be made from a worker thread.
/// </summary>
public sealed class SystemMonitor : IDisposable {

    private readonly PerformanceCounter? _cpuCounter;

    // Per-process CPU is a rate, so it needs the previous sample to compare against
    private readonly Dictionary<int, (TimeSpan cpu, DateTime at)> _lastProcCpu = new();

    private long _lastNetBytesSent, _lastNetBytesReceived;
    private DateTime _lastNetAt = DateTime.MinValue;

    public SystemMonitor() {
        // "% Processor Utility" matches Task Manager; the older "% Processor Time"
        // measures against base clock and reads low whenever the CPU boosts.
        _cpuCounter = TryCreateCounter("Processor Information", "% Processor Utility", "_Total")
                   ?? TryCreateCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter?.NextValue();
    }

    private static PerformanceCounter? TryCreateCounter(string category, string counter, string instance) {
        try {
            var pc = new PerformanceCounter(category, counter, instance);
            pc.NextValue();
            return pc;
        } catch {
            return null;
        }
    }

    public float GetCpuUsagePercent() {
        if (_cpuCounter == null) return 0;
        return Math.Clamp(_cpuCounter.NextValue(), 0f, 100f);
    }

    /// <summary>
    /// Hottest ACPI thermal zone, in Celsius. HP's BIOS temperature command
    /// returns all zeros on Victus models, so this is the reliable source.
    /// </summary>
    public double? GetCpuTemperatureC() {
        try {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            double? hottest = null;
            foreach (ManagementObject obj in searcher.Get()) {
                double celsius = (Convert.ToDouble(obj["CurrentTemperature"]) - 2732) / 10.0;
                if (celsius > 0 && celsius < 130 && (hottest == null || celsius > hottest))
                    hottest = celsius;
            }
            return hottest;
        } catch {
            return null;
        }
    }

    public GpuStats GetGpuStats() {
        var stats = new GpuStats();
        try {
            var psi = new ProcessStartInfo {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return stats;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            var parts = output.Split(',');
            if (parts.Length >= 5) {
                stats.UtilizationPercent = ParseDouble(parts[0]);
                stats.TemperatureC = ParseDouble(parts[1]);
                stats.MemoryUsedMb = ParseDouble(parts[2]);
                stats.MemoryTotalMb = ParseDouble(parts[3]);
                stats.PowerWatts = ParseDouble(parts[4]);
                stats.Available = true;
            }
        } catch {
            stats.Available = false;
        }
        return stats;
    }

    // ---------- Memory ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public MemoryStats GetMemoryStats() {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return new MemoryStats();

        const double gb = 1024d * 1024 * 1024;
        double total = m.ullTotalPhys / gb;
        return new MemoryStats {
            TotalGb = total,
            UsedGb = total - (m.ullAvailPhys / gb)
        };
    }

    // ---------- Storage ----------

    public IReadOnlyList<DriveStats> GetDrives() {
        var list = new List<DriveStats>();
        try {
            const double gb = 1024d * 1024 * 1024;
            foreach (var d in System.IO.DriveInfo.GetDrives()) {
                if (!d.IsReady || d.DriveType != System.IO.DriveType.Fixed) continue;
                list.Add(new DriveStats {
                    Name = d.Name.TrimEnd('\\'),
                    FreeGb = d.AvailableFreeSpace / gb,
                    TotalGb = d.TotalSize / gb
                });
            }
        } catch {
            // a drive disappearing mid-enumeration is not worth failing the snapshot
        }
        return list;
    }

    // ---------- Network ----------

    public NetworkStats GetNetworkStats() {
        var stats = new NetworkStats();
        try {
            long sent = 0, received = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = ni.GetIPStatistics();
                sent += s.BytesSent;
                received += s.BytesReceived;
            }

            var now = DateTime.UtcNow;
            if (_lastNetAt != DateTime.MinValue) {
                double seconds = (now - _lastNetAt).TotalSeconds;
                if (seconds > 0.05) {
                    // bytes -> megabits
                    stats.UploadMbps = Math.Max(0, (sent - _lastNetBytesSent) * 8.0 / 1_000_000.0 / seconds);
                    stats.DownloadMbps = Math.Max(0, (received - _lastNetBytesReceived) * 8.0 / 1_000_000.0 / seconds);
                }
            }

            _lastNetBytesSent = sent;
            _lastNetBytesReceived = received;
            _lastNetAt = now;
        } catch {
            // leave zeros
        }
        return stats;
    }

    // ---------- Processes ----------

    // "Utilization Percentage" is a rate counter: its first read is always zero
    // because there is no earlier sample to compare against. The counters must
    // therefore live across calls — creating and disposing them per call, as an
    // obvious implementation does, reports 0% for everything, forever.
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();

    /// <summary>
    /// Per-process 3D GPU usage, keyed by pid. Read from the same "GPU Engine"
    /// counters Task Manager uses, so the numbers agree with it. The first call
    /// after start-up primes the counters and returns zeros.
    /// </summary>
    public Dictionary<int, double> GetGpuUsageByPid() {
        var result = new Dictionary<int, double>();
        try {
            var category = new PerformanceCounterCategory("GPU Engine");
            var live = new HashSet<string>();

            foreach (string instance in category.GetInstanceNames()) {
                if (!instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;

                int pidStart = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                if (pidStart < 0) continue;
                int pidEnd = instance.IndexOf('_', pidStart + 4);
                if (pidEnd < 0) continue;
                if (!int.TryParse(instance.AsSpan(pidStart + 4, pidEnd - pidStart - 4), out int pid)) continue;

                live.Add(instance);

                try {
                    if (!_gpuCounters.TryGetValue(instance, out var counter)) {
                        counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();          // prime; this sample is meaningless
                        _gpuCounters[instance] = counter;
                        continue;                     // no usable value yet this round
                    }

                    double v = counter.NextValue();
                    result[pid] = result.TryGetValue(pid, out var existing) ? existing + v : v;
                } catch {
                    // an instance can vanish between enumeration and read
                    if (_gpuCounters.Remove(instance, out var dead)) dead.Dispose();
                }
            }

            // Drop counters whose process has gone
            foreach (var gone in _gpuCounters.Keys.Where(k => !live.Contains(k)).ToList()) {
                if (_gpuCounters.Remove(gone, out var dead)) dead.Dispose();
            }
        } catch {
            // GPU Engine counters are unavailable on some systems
        }
        return result;
    }

    /// <summary>Top processes by CPU, with GPU and memory alongside.</summary>
    public IReadOnlyList<ProcessStats> GetTopProcesses(int count, Dictionary<int, double>? gpuByPid = null) {
        var results = new List<ProcessStats>();
        var now = DateTime.UtcNow;
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses()) {
            try {
                if (p.Id <= 4) continue;
                seen.Add(p.Id);

                var cpuTime = p.TotalProcessorTime;
                double cpuPercent = 0;
                if (_lastProcCpu.TryGetValue(p.Id, out var prev)) {
                    double elapsed = (now - prev.at).TotalMilliseconds;
                    if (elapsed > 100) {
                        cpuPercent = (cpuTime - prev.cpu).TotalMilliseconds
                                     / elapsed / Environment.ProcessorCount * 100.0;
                    }
                }
                _lastProcCpu[p.Id] = (cpuTime, now);

                results.Add(new ProcessStats {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    CpuPercent = Math.Clamp(cpuPercent, 0, 100),
                    MemoryMb = p.WorkingSet64 / (1024.0 * 1024.0),
                    GpuPercent = gpuByPid != null && gpuByPid.TryGetValue(p.Id, out var g) ? g : 0
                });
            } catch {
                // access denied on protected processes is expected
            } finally {
                p.Dispose();
            }
        }

        // Drop entries for processes that have exited
        foreach (var stale in _lastProcCpu.Keys.Where(k => !seen.Contains(k)).ToList())
            _lastProcCpu.Remove(stale);

        return results
            .OrderByDescending(r => r.CpuPercent)
            .ThenByDescending(r => r.MemoryMb)
            .Take(count)
            .ToList();
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public void Dispose() {
        _cpuCounter?.Dispose();
        foreach (var c in _gpuCounters.Values) c.Dispose();
        _gpuCounters.Clear();
    }
}

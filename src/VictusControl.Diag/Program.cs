// Diagnostic probe: dumps raw responses from HP's BIOS WMI interface plus
// alternative temperature/CPU sources, so we can see what this specific
// Victus model actually supports rather than assuming Omen behaviour.

using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using VictusControl.Core.Bios;
using VictusControl.Core.Monitoring;

var log = new StringBuilder();

void Line(string s) {
    Console.WriteLine(s);
    log.AppendLine(s);
}

Line("=== VictusControl BIOS diagnostic ===");
Line($"Time: {DateTime.Now}");
Line($"Elevated: {IsElevated()}");
Line("");

var bios = HpBios.Instance;

try {
    bios.Initialize();
    Line("BIOS WMI interface: initialized OK");
} catch (Exception ex) {
    Line($"BIOS WMI interface: FAILED -> {ex.GetType().Name}: {ex.Message}");
}
Line("");

// Dumps per-process 3D GPU usage, which is what game detection depends on.
// Run with: VictusControl.Diag.exe gpupid
if (args.Length > 0 && args[0] == "gpupid") {
    Line("--- PER-PROCESS 3D GPU USAGE ---");
    var mon = new SystemMonitor();

    // Counters are rates: the first read primes, the second measures.
    mon.GetGpuUsageByPid();
    Thread.Sleep(1500);
    var usage = mon.GetGpuUsageByPid();

    Line($"  processes reporting 3D engine activity: {usage.Count}");
    Line("");
    foreach (var (pid, pct) in usage.OrderByDescending(k => k.Value).Take(15)) {
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { name = "(exited)"; }
        Line($"    pid {pid,-7} {name,-30} {pct,6:0.0} %");
    }

    if (usage.Count == 0)
        Line("    NONE - the GPU Engine counters returned nothing; game detection cannot work.");
    else if (usage.Values.All(v => v < 0.05))
        Line("    All zero - counters exist but report no activity right now (nothing is rendering).");

    mon.Dispose();
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diag-gpupid.txt"), log.ToString());
    return;
}

// Times each telemetry source, to see what a single refresh actually costs.
// Run with: VictusControl.Diag.exe bench
if (args.Length > 0 && args[0] == "bench") {
    Line("--- COST OF ONE REFRESH (ms, median of 5) ---");

    double Time(Action a) {
        var samples = new List<double>();
        for (int i = 0; i < 5; i++) {
            var sw = Stopwatch.StartNew();
            a();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return samples[2];
    }

    using var pc = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
    pc.NextValue();

    double tFan = Time(() => bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out _));
    double tThrottle = Time(() => bios.Send(HpBiosData.Cmd.Default, 0x35, new byte[4] { 0, 4, 0, 0 }, 128, out _));
    double tMaxFan = Time(() => bios.Send(HpBiosData.Cmd.Default, 0x26, new byte[4], 4, out _));
    double tCpuPc = Time(() => pc.NextValue());

    double tZone = Time(() => {
        using var s = new ManagementObjectSearcher(@"root\WMI",
            "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        foreach (ManagementObject o in s.Get()) { _ = o["CurrentTemperature"]; }
    });

    double tSmi = Time(() => {
        var psi = new ProcessStartInfo {
            FileName = "nvidia-smi",
            Arguments = "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw --format=csv,noheader,nounits",
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(2000);
    });

    Line($"  bios: fan level        {tFan,8:0.0}");
    Line($"  bios: throttle state   {tThrottle,8:0.0}");
    Line($"  bios: max fan state    {tMaxFan,8:0.0}");
    Line($"  wmi:  thermal zone     {tZone,8:0.0}");
    Line($"  perf: cpu utility      {tCpuPc,8:0.0}");
    Line($"  proc: nvidia-smi       {tSmi,8:0.0}");
    Line($"  {"",22}--------");
    Line($"  TOTAL PER REFRESH      {tFan + tThrottle + tMaxFan + tZone + tCpuPc + tSmi,8:0.0} ms");
    Line("");
    Line("  All of this currently runs on the UI thread every 1.5 s.");

    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diag-bench.txt"), log.ToString());
    return;
}

// Exercises every core feature, reports pass/fail, and leaves the hardware
// on its default fan mode with max-fan released.
// Run with: VictusControl.Diag.exe verify
if (args.Length > 0 && args[0] == "verify") {
    int pass = 0, fail = 0;
    void Check(string name, Func<string> probe) {
        try {
            string detail = probe();
            Line($"  [PASS] {name,-26} {detail}");
            pass++;
        } catch (Exception ex) {
            Line($"  [FAIL] {name,-26} {ex.Message}");
            fail++;
        }
    }
    string Rc(int rc) => rc == 0 ? "rc=0" : throw new Exception($"BIOS returned {rc}");

    Line("--- CORE FEATURE VERIFICATION ---");

    Check("fan telemetry", () => {
        Rc(bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out var l));
        if (l[0] == 0 && l[1] == 0) throw new Exception("both fans read zero");
        return $"cpu {l[0] * 100} rpm, gpu {l[1] * 100} rpm";
    });

    Check("cpu temperature", () => {
        using var s = new ManagementObjectSearcher(@"root\WMI",
            "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        foreach (ManagementObject o in s.Get())
            return $"{(Convert.ToDouble(o["CurrentTemperature"]) - 2732) / 10.0:0} C (ACPI zone)";
        throw new Exception("no thermal zone reported");
    });

    Check("cpu usage counter", () => {
        using var pc = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
        pc.NextValue(); Thread.Sleep(600);
        return $"{pc.NextValue():0.0}%";
    });

    Check("gpu telemetry", () => {
        var psi = new ProcessStartInfo {
            FileName = "nvidia-smi",
            Arguments = "--query-gpu=utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits",
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(3000);
        if (string.IsNullOrWhiteSpace(o)) throw new Exception("nvidia-smi returned nothing");
        return o;
    });

    foreach (var (n, v) in new[] {
        ("fan mode: default", (byte)0x30), ("fan mode: performance", (byte)0x31),
        ("fan mode: cool", (byte)0x50), ("fan mode: quiet", (byte)0x03) })
        Check(n, () => Rc(bios.Send(HpBiosData.Cmd.Default, 0x1A, new byte[4] { 0xFF, v, 0, 0 })));

    Check("manual fan level", () => Rc(bios.Send(HpBiosData.Cmd.Default, 0x2E, new byte[4] { 30, 30, 0, 0 })));

    Check("max fan engage", () => Rc(bios.Send(HpBiosData.Cmd.Default, 0x27, new byte[4] { 1, 0, 0, 0 })));
    Thread.Sleep(700);
    Check("max fan reads back", () => {
        Rc(bios.Send(HpBiosData.Cmd.Default, 0x26, new byte[4], 4, out var s));
        return s[0] == 1 ? "reported ON" : throw new Exception("engaged but reads OFF");
    });
    Check("max fan release", () => Rc(bios.Send(HpBiosData.Cmd.Default, 0x27, new byte[4] { 0, 0, 0, 0 })));

    foreach (var (n, lvl) in new[] { ("gpu power: minimum", 0), ("gpu power: medium", 1), ("gpu power: maximum", 2) })
        Check(n, () => Rc(bios.Send(HpBiosData.Cmd.Default, 0x22,
            new byte[4] { (byte)(lvl == 0 ? 0 : 1), (byte)(lvl == 2 ? 1 : 0), 0x01, 0x00 })));

    Check("throttle state", () => {
        bios.Send(HpBiosData.Cmd.Default, 0x35, new byte[4] { 0x00, 0x04, 0, 0 }, 128, out var d);
        return d[1] == 1 ? "THROTTLING" : d[1] == 4 ? "not throttling" : $"unknown ({d[1]})";
    });

    Line("");
    Line("  [INFO] gpu mode switch     unsupported on this machine (BIOS error 4) - expected");

    // Leave the machine in a safe, default state
    Line("");
    Line("--- RESTORING DEFAULTS ---");
    bios.Send(HpBiosData.Cmd.Default, 0x27, new byte[4] { 0, 0, 0, 0 });
    bios.Send(HpBiosData.Cmd.Default, 0x1A, new byte[4] { 0xFF, 0x30, 0, 0 });
    Thread.Sleep(500);
    bios.Send(HpBiosData.Cmd.Default, 0x26, new byte[4], 4, out var fin);
    bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out var finLvl);
    Line($"  max fan: {(fin[0] == 0 ? "OFF" : "STILL ON")}   fan mode: Default   " +
         $"fans: cpu {finLvl[0] * 100} rpm, gpu {finLvl[1] * 100} rpm");

    Line("");
    Line($"RESULT: {pass} passed, {fail} failed");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diag-verify.txt"), log.ToString());
    return;
}

// Releases max-fan mode and reports the resulting state.
// Run with: VictusControl.Diag.exe fanoff
if (args.Length > 0 && args[0] == "fanoff") {
    int rc = bios.Send(HpBiosData.Cmd.Default, 0x27, new byte[4] { 0x00, 0, 0, 0 });
    Line($"Max fan release -> rc={rc}");
    bios.Send(HpBiosData.Cmd.Default, 0x26, new byte[4], 4, out var st);
    Line($"Max fan state now: {(st[0] == 0 ? "OFF" : "ON")} (byte {st[0]})");
    bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out var lv);
    Line($"Fan level: cpu={lv[0]} ({lv[0] * 100} rpm)  gpu={lv[1]} ({lv[1] * 100} rpm)");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diag-fanoff.txt"), log.ToString());
    return;
}

// Measures the real maximum fan level by engaging max-fan mode briefly.
// Run with: VictusControl.Diag.exe maxfan   (fans get loud for ~8 seconds)
if (args.Length > 0 && args[0] == "maxfan") {
    Line("--- Max fan level probe ---");
    bios.Send(HpBiosData.Cmd.Default, 0x27, new byte[4] { 0x01, 0, 0, 0 });
    for (int i = 0; i < 8; i++) {
        Thread.Sleep(1000);
        bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out var l);
        Line($"  t+{i + 1}s  cpu={l[0]} ({l[0] * 100} rpm)  gpu={l[1]} ({l[1] * 100} rpm)");
    }
    bios.Send(HpBiosData.Cmd.Default, 0x27, new byte[4] { 0x00, 0, 0, 0 });
    Line("  max fan disabled again");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diag-maxfan.txt"), log.ToString());
    return;
}

// --- Raw BIOS command probes ---
Line("--- Raw BIOS reads (cmd/type -> returnCode : bytes) ---");

Probe("Fan count",        HpBiosData.Cmd.Default, 0x10, new byte[4], 4);
Probe("Fan type",         HpBiosData.Cmd.Default, 0x2C, new byte[4], 128);
Probe("Fan level",        HpBiosData.Cmd.Default, 0x2D, new byte[4], 128);
Probe("Fan table",        HpBiosData.Cmd.Default, 0x2F, new byte[4], 128);
Probe("Max fan state",    HpBiosData.Cmd.Default, 0x26, new byte[4], 4);
Probe("Temperature 0x23", HpBiosData.Cmd.Default, 0x23, new byte[4] { 0x01, 0, 0, 0 }, 4);
Probe("Temperature alt0", HpBiosData.Cmd.Default, 0x23, new byte[4] { 0x00, 0, 0, 0 }, 4);
Probe("System data",      HpBiosData.Cmd.Default, 0x28, null, 128);
Probe("Throttling",       HpBiosData.Cmd.Default, 0x35, new byte[4] { 0x00, 0x04, 0, 0 }, 128);
Probe("GPU mode",         HpBiosData.Cmd.Legacy,  0x52, null, 4);
Probe("GPU power",        HpBiosData.Cmd.Default, 0x21, new byte[4], 4);

Line("");
Line("--- Fan mode WRITE test (0x1A) ---");
foreach (var (name, value) in new[] {
    ("Default(0x30)", (byte)0x30),
    ("Performance(0x31)", (byte)0x31),
    ("Cool(0x50)", (byte)0x50),
}) {
    try {
        int rc = bios.Send(HpBiosData.Cmd.Default, 0x1A, new byte[4] { 0xFF, value, 0x00, 0x00 });
        Line($"  SetFanMode {name,-18} -> returnCode {rc}");
        Thread.Sleep(400);
        int rc2 = bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out var lvl);
        Line($"     fan level after: rc={rc2} cpu={lvl[0]} gpu={lvl[1]}");
    } catch (Exception ex) {
        Line($"  SetFanMode {name,-18} -> EXCEPTION {ex.Message}");
    }
}

// --- Alternative temperature sources ---
Line("");
Line("--- WMI MSAcpi_ThermalZoneTemperature ---");
try {
    using var searcher = new ManagementObjectSearcher(
        @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
    int n = 0;
    foreach (ManagementObject obj in searcher.Get()) {
        var raw = Convert.ToDouble(obj["CurrentTemperature"]);
        Line($"  zone[{n++}] {obj["InstanceName"]}: {(raw - 2732) / 10.0:0.0} C");
    }
    if (n == 0) Line("  (no thermal zones reported)");
} catch (Exception ex) {
    Line($"  FAILED: {ex.Message}");
}

// --- CPU usage comparison ---
Line("");
Line("--- CPU usage: PerformanceCounter vs GetSystemTimes ---");
try {
    using var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total");
    pc.NextValue();
    var t1 = GetTimes();
    Thread.Sleep(1500);
    float counterVal = pc.NextValue();
    var t2 = GetTimes();

    double idle = t2.idle - t1.idle;
    double kernel = t2.kernel - t1.kernel;
    double user = t2.user - t1.user;
    double total = kernel + user;
    double sysTimesVal = total > 0 ? (total - idle) / total * 100.0 : 0;

    Line($"  PerformanceCounter : {counterVal:0.0}%");
    Line($"  GetSystemTimes     : {sysTimesVal:0.0}%");
} catch (Exception ex) {
    Line($"  FAILED: {ex.Message}");
}

// --- nvidia-smi ---
Line("");
Line("--- nvidia-smi ---");
try {
    var psi = new ProcessStartInfo {
        FileName = "nvidia-smi",
        Arguments = "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw --format=csv,noheader,nounits",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var p = Process.Start(psi)!;
    string outp = p.StandardOutput.ReadToEnd().Trim();
    string err = p.StandardError.ReadToEnd().Trim();
    p.WaitForExit(3000);
    Line($"  stdout: {(string.IsNullOrEmpty(outp) ? "(empty)" : outp)}");
    if (!string.IsNullOrEmpty(err)) Line($"  stderr: {err}");
} catch (Exception ex) {
    Line($"  FAILED: {ex.Message}");
}

var outPath = Path.Combine(AppContext.BaseDirectory, "diag-output.txt");
File.WriteAllText(outPath, log.ToString());
Console.WriteLine();
Console.WriteLine($"Saved to: {outPath}");

void Probe(string name, HpBiosData.Cmd cmd, uint type, byte[]? inData, byte outSize) {
    try {
        int rc = bios.Send(cmd, type, inData!, outSize, out var outData);
        string hex = outData.Length == 0
            ? "(none)"
            : string.Join(" ", outData.Take(16).Select(b => b.ToString("X2")))
              + (outData.Length > 16 ? $" ... ({outData.Length} bytes)" : "");
        Line($"  {name,-18} 0x{type:X2} -> rc={rc,-3} : {hex}");
    } catch (Exception ex) {
        Line($"  {name,-18} 0x{type:X2} -> EXCEPTION {ex.GetType().Name}: {ex.Message}");
    }
}

static bool IsElevated() {
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

static (double idle, double kernel, double user) GetTimes() {
    GetSystemTimes(out var i, out var k, out var u);
    return (ToDouble(i), ToDouble(k), ToDouble(u));

    static double ToDouble(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetSystemTimes(
    out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
    out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
    out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

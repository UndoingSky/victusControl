using System.Collections.Generic;

namespace VictusControl.Core.Monitoring;

public sealed class GpuStats {
    public double UtilizationPercent { get; set; }
    public double TemperatureC { get; set; }
    public double MemoryUsedMb { get; set; }
    public double MemoryTotalMb { get; set; }
    public double PowerWatts { get; set; }
    public bool Available { get; set; }
}

public sealed class MemoryStats {
    public double UsedGb { get; set; }
    public double TotalGb { get; set; }
    public double UsedPercent => TotalGb > 0 ? UsedGb / TotalGb * 100.0 : 0;
}

public sealed class DriveStats {
    public string Name { get; set; } = "";
    public double FreeGb { get; set; }
    public double TotalGb { get; set; }
    public double UsedPercent => TotalGb > 0 ? (TotalGb - FreeGb) / TotalGb * 100.0 : 0;
}

public sealed class NetworkStats {
    public double UploadMbps { get; set; }
    public double DownloadMbps { get; set; }
}

public sealed class ProcessStats {
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double GpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public string? ExecutablePath { get; set; }
}

/// <summary>Everything the dashboard shows, gathered once on a worker thread.</summary>
public sealed class SystemSnapshot {
    public double CpuUsagePercent { get; set; }
    public double? CpuTempC { get; set; }
    public GpuStats? Gpu { get; set; }
    public MemoryStats? Memory { get; set; }
    public IReadOnlyList<DriveStats>? Drives { get; set; }
    public NetworkStats? Network { get; set; }
    public IReadOnlyList<ProcessStats>? TopProcesses { get; set; }
}

using VictusControl.Core.Bios;
using VictusControl.Core.Monitoring;

namespace VictusControl.App;

/// <summary>
/// One reading of the machine, gathered on a background thread and handed to the
/// UI thread as finished data. Nothing here touches hardware — by the time the UI
/// sees a snapshot, every slow call has already happened somewhere else.
/// </summary>
public sealed class TelemetrySnapshot {

    public float CpuUsagePercent { get; init; }
    public double? CpuTempC { get; init; }

    // Fans and GPU refresh on their own cadence, so a snapshot may carry values
    // from a slightly earlier sample rather than none at all.
    public byte[]? FanLevels { get; init; }
    public bool FanReadFailed { get; init; }

    public GpuStats? Gpu { get; init; }

    public HpBiosData.Throttling? Throttling { get; init; }
    public bool? MaxFanOn { get; init; }
}

using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>Pure slot lists for CPU, RAM and disk (GPU: <see cref="GpuNormalizer.Plan"/>). Always 4 slots per part.</summary>
internal static class SlotPlanner
{
    /// <summary>CPU: clock (PDH, or unavailable), temp/fan need M6b, power from EMI only when tied to the package.</summary>
    public static IReadOnlyList<MetricSlot> Cpu(bool clock, double? maxNominalMHz, string? clockReason, bool power, string? powerReason) =>
    [
        clock
            ? new("clock", "GHz", SlotSources.Pdh, true, Max: CpuNormalizer.ClockMaxGhz(maxNominalMHz))
            : new("clock", "GHz", SlotSources.None, false, Reason: clockReason ?? SlotReasons.NotSupported),
        new("temp", "°C", SlotSources.None, false, Reason: SlotReasons.NeedsAdvancedSensors),
        power
            ? new("power", "W", SlotSources.Emi, true, Max: 250)
            : new("power", "W", SlotSources.None, false, Reason: powerReason ?? SlotReasons.NotSupported),
        new("fan", "RPM", SlotSources.None, false, Reason: SlotReasons.NeedsAdvancedSensors),
    ];

    /// <summary>RAM: all four live values come from the worker ("live" events).</summary>
    public static IReadOnlyList<MetricSlot> Ram() =>
    [
        new("read", "GB/s", SlotSources.Worker, true, Max: 120),
        new("write", "GB/s", SlotSources.Worker, true, Max: 120),
        new("copy", "GB/s", SlotSources.Worker, true, Max: 120),
        new("latency", "ns", SlotSources.Worker, true, Max: 200),
    ];

    /// <summary>Disk: throughput from the worker (scale by media), temperature from WMI when readable.</summary>
    public static IReadOnlyList<MetricSlot> Disk(string media, bool temp, string? tempReason)
    {
        var (seq, rnd) = DiskNormalizer.Scale(media);
        return
        [
            new("read", "MB/s", SlotSources.Worker, true, Max: seq),
            new("write", "MB/s", SlotSources.Worker, true, Max: seq),
            new("random4k", "MB/s", SlotSources.Worker, true, Max: rnd),
            temp
                ? new("temp", "°C", SlotSources.Wmi, true, Max: 80)
                : new("temp", "°C", SlotSources.None, false, Reason: tempReason ?? SlotReasons.NotSupported),
        ];
    }
}

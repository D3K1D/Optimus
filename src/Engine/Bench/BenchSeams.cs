namespace OptimizationTool.Engine.Bench;

// M6a seams (contracts/m6-benchmark.md §2–§4). The engine owns the run (lease, lifecycle, store, score, verdicts);
// src/Windows/Sensors implements ISensorHub and IBenchHardware; src/Bench (OptimizationTool.Bench.exe) implements the
// worker side of BenchWire. Nothing here touches Windows.

/// <summary>Part ids in run order (§4: RAM → CPU → GPU → disk).</summary>
public static class BenchParts
{
    public const string Ram = "ram", Cpu = "cpu", Gpu = "gpu", Disk = "disk";
    public static readonly IReadOnlyList<string> RunOrder = [Ram, Cpu, Gpu, Disk];
    public const string BenchVersion = "1";
}

/// <summary>
/// One part as the start screen shows it. <see cref="Reason"/> when not testable:
/// needs_space | no_gpu | unsupported | low_memory | busy.
/// </summary>
public sealed record BenchPartInfo(string Id, string Device, bool Testable, string? Reason);

/// <summary>
/// Hardware facts for the start screen and the configId (§5). Model names are public specs; never serials, user or
/// PC names. <see cref="DiskVolume"/> is the volume holding the bench folder, e.g. "C:"; <see cref="DiskMedia"/>:
/// nvme|ssd|hdd|unknown. <see cref="GpuLuid"/> is the DXGI high-performance adapter's LUID as 16 hex chars, or null.
/// </summary>
public sealed record BenchHardware(
    string CpuModel, int LogicalProcessors, double? NominalMHz,
    string? GpuModel, string? GpuDriverVersion, string? GpuLuid,
    long RamBytes, int RamModules, int? RamConfiguredMTs, int? RamRatedMTs,
    string DiskVolume, string? DiskModel, long? DiskSizeBytes, string DiskMedia, long DiskFreeBytes,
    long AvailableMemoryBytes, string WindowsBuild);

/// <summary>Reads hardware facts (WMI/DXGI/registry), bounded, current user. Implemented in src/Windows/Sensors.</summary>
public interface IBenchHardware
{
    /// <summary>
    /// Bounded (≤ 5 s). <paramref name="benchFolder"/> is the folder the disk test will use; its volume is reported.
    /// Throws only on cancellation.
    /// </summary>
    Task<BenchHardware> DescribeAsync(string benchFolder, CancellationToken ct);
}

/// <summary>AC: online|offline|unknown. Battery: present|absent|unknown.</summary>
public sealed record PowerState(string Ac, string Battery)
{
    /// <summary>Blocked when AC is offline, or a battery is present and AC is unknown (§2).</summary>
    public bool BlocksBenchmark => Ac == "offline" || (Ac == "unknown" && Battery == "present");
}

/// <summary>
/// One live metric slot. Ids per part (always 4, §2): cpu/gpu clock|temp|power|fan; ram read|write|copy|latency;
/// disk read|write|random4k|temp. <see cref="Source"/>: pdh|emi|d3dkmt|nvml|wmi|worker|none. <see cref="Unit"/> as
/// displayed (GHz, °C, W, %, RPM, GB/s, ns, MB/s). <see cref="Max"/> fixes the chart's y scale for the whole test.
/// <see cref="Reason"/> when unavailable: needs_advanced_sensors | not_supported | needs_admin | timed_out |
/// source_failed (the source stopped answering during this run).
/// </summary>
public sealed record MetricSlot(string Id, string Unit, string Source, bool Available, double? Max = null,
    double? Expected = null, double? Limit = null, string? Reason = null)
{
    public const string SourceFailed = "source_failed";
}

/// <summary>
/// GPU facts NVML can add for verdicts (§6). <see cref="SlowdownTempC"/> = NVML slowdown threshold.
/// <see cref="ThermalSlowdown"/> = HW_THERMAL_SLOWDOWN or SW_THERMAL_SLOWDOWN active in this sample;
/// <see cref="PowerCap"/> = SW_POWER_CAP active. Null when NVML is not in use for this adapter.
/// </summary>
public sealed record GpuThrottle(double? SlowdownTempC, bool ThermalSlowdown, bool PowerCap);

/// <summary>One 1 Hz sample: metric id → value (null = not read this time) for the part being tested.
/// <see cref="Failed"/>: slot ids whose source the hub disabled during this run (they read null from then on).</summary>
public sealed record SensorSample(IReadOnlyDictionary<string, double?> Values, GpuThrottle? Gpu, double? CpuPerfLimitPercent,
    IReadOnlyCollection<string>? Failed = null);

/// <summary>
/// Driver-free sensors (§3), implemented in src/Windows/Sensors. The engine calls <see cref="Prepare"/> once per part,
/// then <see cref="Read"/> at 1 Hz on its own sampler thread with a per-call timeout; a source that hangs or throws is
/// reported unavailable by the hub for the rest of the run (the hub tracks that itself). Thread-safe for one sampler.
/// </summary>
public interface ISensorHub : IDisposable
{
    /// <summary>The 4 slots for <paramref name="part"/> on this PC. <see cref="BenchHardware.GpuLuid"/> picks the adapter.</summary>
    /// <summary>Cancelling <paramref name="ct"/> abandons the probes and throws <see cref="OperationCanceledException"/>.</summary>
    IReadOnlyList<MetricSlot> Prepare(string part, BenchHardware hardware, CancellationToken ct = default);
    /// <summary>Current values for the prepared part's available slots. Must return within ~200 ms.</summary>
    SensorSample Read();
    PowerState Power();
    /// <summary>Raised from any thread when the AC/DC source changes (RegisterPowerSettingNotification).</summary>
    event Action<PowerState>? PowerChanged;
}

/// <summary>Creates a hub per run (disposed at run end).</summary>
public interface ISensorHubFactory
{
    ISensorHub Create();
}

// ---------------------------------------------------------------------------------------------------------------
// Worker wire (§4): OptimizationTool.Bench.exe speaks UTF-8 JSON lines on stdin/stdout, one object per line,
// ≤ 64 KiB per line. The host creates the process suspended, assigns it to a kill-on-close job, then resumes it.
// The worker writes nothing else to stdout (diagnostics go to stderr, ≤ 64 KiB total, logged as codes only).
//
// host → worker
//   {"cmd":"run","part":"cpu|gpu|ram|disk","benchVersion":"1","folder":"<bench folder, disk only>",
//    "ramBudgetBytes":<long, ram only>,"gpuLuid":"<16 hex or null>","repeat":"<subtest id or null>"}
//   {"cmd":"stop"}                 // stop the current part: finish within 1 s, release resources, reply "stopped"
//   {"cmd":"exit"}                 // after the last part
// worker → host
//   {"ev":"hello","version":"1","pid":<int>}
//   {"ev":"hb"}                                                              // every 250 ms, always, even mid-test
//   {"ev":"progress","part":"cpu","subtest":"multi","progress":0.0-1.0,"liveScore":<double|null>,"etaSec":<int>}
//   {"ev":"result","part":"cpu","subtests":{"single":{"value":2410.5,"unit":"ops/s"},…},
//    "info":{"adapterLuid":"…","volume":"C:","written":<bytes>}}            // part finished, all subtests valid
//   {"ev":"failed","part":"cpu","reason":"check_failed|gpu_reset|too_slow|low_memory|needs_space|unsupported|error",
//    "subtest":"<id or null>"}
//   {"ev":"stopped","part":"cpu"}
//
// Subtest ids and units (benchVersion 1):
//   cpu: single (ops/s), multi (ops/s)
//   gpu: fma (GFLOPS), copy (GB/s)
//   ram: read (GB/s), write (GB/s), copy (GB/s), latency (ns)
//   disk: seqWrite (MB/s), seqRead (MB/s), rand4k (MB/s)
// Live metrics that come from the worker (ram read|write|copy|latency, disk read|write|random4k) are sent as
// "progress" plus a "live" object: {"ev":"live","part":"ram","values":{"read":31.2}}.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Subtest ids per part for benchVersion 1 (worker wire above).</summary>
public static class BenchSubtests
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByPart = new Dictionary<string, IReadOnlyList<string>>
    {
        [BenchParts.Cpu] = ["single", "multi"],
        [BenchParts.Gpu] = ["fma", "copy"],
        [BenchParts.Ram] = ["read", "write", "copy", "latency"],
        [BenchParts.Disk] = ["seqWrite", "seqRead", "rand4k"],
    };
}

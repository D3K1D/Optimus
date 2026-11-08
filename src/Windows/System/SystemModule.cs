using OptimizationTool.Engine;
using OptimizationTool.Windows.SystemDomain.Rules;

namespace OptimizationTool.Windows.SystemDomain;

/// <summary>System & performance: OS, hardware, power, volumes and the live performance sample.</summary>
public sealed class SystemModule : IAuditModule
{
    public string Name => "system";

    public IEnumerable<ICollector> Collectors(CollectorOptions options) =>
    [
        new OsCollector(options),
        new HardwareCollector(options),
        new PowerCollector(options),
        new VolumesCollector(options),
        new PerfSampler(options),
    ];

    public IEnumerable<IRule> Rules() =>
    [
        new HighPerfOnBatteryRule(),
        new SystemVolumeLowRule(),
        new VolumeLowRule(),
        new SustainedCpuRule(),
        new MemoryPressureRule(),
        new UptimeLongRule(),
    ];

    public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => [];
}

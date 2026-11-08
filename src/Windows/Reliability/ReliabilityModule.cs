using OptimizationTool.Engine;
using OptimizationTool.Windows.Reliability.Rules;

namespace OptimizationTool.Windows.Reliability;

/// <summary>Reliability, apps and settings (contracts/m2-collectors.md §4). Read-only, current user, no network.</summary>
public sealed class ReliabilityModule : IAuditModule
{
    public string Name => "reliability";

    public IEnumerable<ICollector> Collectors(CollectorOptions options) =>
    [
        new EventsCollector(EventChannelSpec.System, options),
        new EventsCollector(EventChannelSpec.Application, options),
        new EventsCollector(EventChannelSpec.Update, options),
        new EventsCollector(EventChannelSpec.Defender, options),
        new PnpCollector(options),
        new InstalledAppsCollector(options),
        new DiagDataCollector(options),
        new DisplayCollector(options),
        new StartCollector(options),
    ];

    public IEnumerable<IRule> Rules() =>
    [
        new KernelPowerRule(),
        new AppCrashesRule(),
        new UpdateFailuresRule(),
        new DeviceProblemRule(),
        new OptionalDiagRule(),
        new DisplayRefreshRule(),
        new StartSuggestionsRule(),
        new LargeInstallsRule(),
    ];

    public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => [];
}

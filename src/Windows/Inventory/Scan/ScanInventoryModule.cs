using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Inventory;

/// <summary>
/// m9 §6.10: the scan's general inventory of what is on this PC, five read-only collectors (win.inventory.*), one evidence
/// item each. No rules: it is a list, not a detector. Registered last in <see cref="WindowsModules.All"/> so the audit's
/// own checks take their slots first; category misc, so a full scan runs it and a targeted one does not.
/// </summary>
public sealed class ScanInventoryModule : IAuditModule
{
    public string Name => "pc-inventory";

    public IEnumerable<ICollector> Collectors(CollectorOptions options) =>
    [
        new HardwareInventoryCollector(),
        new DriversInventoryCollector(),
        new WindowsInventoryCollector(),
        new ProgramsInventoryCollector(),
        new BackgroundInventoryCollector(),
    ];

    public IEnumerable<IRule> Rules() => [];

    public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => [];
}

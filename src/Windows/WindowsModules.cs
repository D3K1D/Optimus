using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows;

/// <summary>Every read-only Windows audit module, in plan order. Each domain adds its module here.</summary>
public static class WindowsModules
{
    public static IReadOnlyList<IAuditModule> All { get; } =
    [
        // Domain modules register here (contracts/m2-collectors.md §Ownership).
        new Inventory.InventoryModule(),
        new SystemDomain.SystemModule(),
        new Reliability.ReliabilityModule(),
        new Security.SecurityModule(),
    ];

    /// <summary>Compiled M4 action handlers (contracts/m4-actions.md). One line per domain.</summary>
    public static IReadOnlyList<IActionModule> Actions { get; } =
    [
        new global::OptimizationTool.Windows.Actions.Defender.DefenderActionModule(),
        new Actions.Power.PowerActionModule(),
        new Actions.Firewall.FirewallActionModule(),
    ];
}

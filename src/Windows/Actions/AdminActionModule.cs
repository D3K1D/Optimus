using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Firewall;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Repair;
using OptimizationTool.Windows.Actions.Services;
using OptimizationTool.Windows.Actions.Tasks;

namespace OptimizationTool.Windows.Actions;

/// <summary>
/// The administrator actions of P3b-2 (contracts/m9-ai-powers.md §3.2): service.disable, task.disable, firewall.block_app,
/// repair.sfc and repair.dism_restorehealth. Every one runs through the broker and only with --enable-admin-actions (the
/// engine lists them disabled otherwise). Constructing them touches nothing on the PC.
/// </summary>
public sealed class AdminActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context)
    {
        var files = new WindowsFileFacts();
        var repair = new WindowsRepairTool();
        return
        [
            new ServiceDisableHandler(new WindowsServiceControl(), files, WindowsSecurityProducts.Shared),
            new TaskDisableHandler(new WindowsTaskScheduler(), files, WindowsSecurityProducts.Shared),
            new BlockAppHandler(new WindowsFirewallRules(), new WindowsPublicFirewallReader(), files, products: WindowsSecurityProducts.Shared),
            new RepairHandler(RepairKind.Sfc, repair),
            new RepairHandler(RepairKind.Dism, repair),
        ];
    }
}

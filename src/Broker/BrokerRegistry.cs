using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;
using OptimizationTool.Windows.Actions.RestorePoint;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Startup;

namespace OptimizationTool.Broker;

/// <summary>
/// The broker's compiled administrator handlers (m9 §3.5), one per actionId. Its set equals the host's elevating handlers
/// and <see cref="BrokerCatalog.ActionIds"/> (asserted by test). Nothing else can run elevated.
/// </summary>
internal static class BrokerRegistry
{
    public static IReadOnlyList<IBrokerHandler> Create() =>
    [
        new FirewallBrokerHandler(new WindowsPublicFirewallReader(), new WindowsPublicFirewallWriter()),
        new MachineSettingBrokerHandler(new WindowsRegistryStore(), () => Environment.OSVersion.Version.Build),
        new MachineStartupBrokerHandler(new WindowsRegistryStore(), new WindowsStartupFiles(), Windows.Actions.Protection.WindowsSecurityProducts.Shared),
        new RestorePointBrokerHandler(new WindowsRestorePointSystem()),
        new Windows.Actions.Services.ServiceBrokerHandler(new Windows.Actions.Services.WindowsServiceControl(), new Windows.Actions.Protection.WindowsFileFacts(), Windows.Actions.Protection.WindowsSecurityProducts.Shared),
        new Windows.Actions.Tasks.TaskBrokerHandler(new Windows.Actions.Tasks.WindowsTaskScheduler(), new Windows.Actions.Protection.WindowsFileFacts(), Windows.Actions.Protection.WindowsSecurityProducts.Shared),
        new BlockAppBrokerHandler(new WindowsFirewallRules(), new WindowsPublicFirewallReader(), new Windows.Actions.Protection.WindowsFileFacts(), Windows.Actions.Protection.WindowsSecurityProducts.Shared),
        new Windows.Actions.Repair.RepairBrokerHandler(Windows.Actions.Repair.RepairKind.Sfc, new Windows.Actions.Repair.WindowsRepairTool()),
        new Windows.Actions.Repair.RepairBrokerHandler(Windows.Actions.Repair.RepairKind.Dism, new Windows.Actions.Repair.WindowsRepairTool()),
    ];
}

using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Apps;
using OptimizationTool.Windows.Actions.Cleanup;
using OptimizationTool.Windows.Actions.Network;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Shell;

namespace OptimizationTool.Windows.Actions;

/// <summary>
/// The last user-scope actions of m9 §3.2: app.set_gpu_preference, network.flush_dns, explorer.restart,
/// cleanup.empty_recycle_bin and app.uninstall_store. All run as the user, without elevation. Constructing them touches
/// nothing on the PC.
/// </summary>
public sealed class UserActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context)
    {
        var processes = new WindowsProcessControl();
        return
        [
            new GpuPreferenceHandler(new WindowsRegistryStore(), processes, new WindowsGpuAdapters(), WindowsSecurityProducts.Shared, files: new WindowsFileFacts()),
            new FlushDnsHandler(new WindowsDnsCache()),
            new ExplorerRestartHandler(new WindowsShellControl(), processes),
            new EmptyRecycleBinHandler(new WindowsRecycleBin()),
            new StoreUninstallHandler(new WindowsStorePackages(), WindowsSecurityProducts.Shared),
        ];
    }
}

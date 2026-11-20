using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Services;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Startup;
using OptimizationTool.Windows.Tests.Actions.Admin;
using OptimizationTool.Windows.Tests.Actions.Processes;
using OptimizationTool.Windows.Tests.Actions.Settings;
using OptimizationTool.Windows.Tests.Actions.Startup;

namespace OptimizationTool.Windows.Tests.Actions;

/// <summary>A fixed Security Center product list. Never reads Windows.</summary>
internal sealed class FakeSecurityProducts(params string[] names) : ISecurityProducts
{
    public int Reads;
    public IReadOnlyList<string> Names() { Reads++; return names; }
}

/// <summary>
/// m9 §3.2 protected lists: a product Windows Security Center reports protects its processes, services, startup entries
/// and Store apps even when it is not on the compiled vendor list ("Fabrikam" is on none of them).
/// </summary>
public class SecurityCenterProtectionTests
{
    private static readonly string[] Wsc = ["Fabrikam Total Security", "Windows Defender Antivirus"];

    [Fact]
    public void The_vendor_key_skips_generic_words_and_Microsofts_own_products()
    {
        Assert.Equal("fabrikam", ProtectedTargets.VendorKey("Fabrikam Total Security"));
        Assert.Equal("avast", ProtectedTargets.VendorKey("Avast Free Antivirus"));
        Assert.Equal("norton", ProtectedTargets.VendorKey("Norton 360"));
        Assert.Null(ProtectedTargets.VendorKey("Windows Defender Antivirus"));
        Assert.Null(ProtectedTargets.VendorKey("Microsoft Defender Antivirus"));
        Assert.Null(ProtectedTargets.VendorKey("Total Security"));
        Assert.Null(ProtectedTargets.VendorKey(""));
    }

    [Fact]
    public void Each_target_kind_is_protected_by_the_live_list_and_only_by_it()
    {
        Assert.Null(ProtectedTargets.Process(@"Q:\Fabrikam\fabsvc.exe"));
        Assert.Equal("protected", ProtectedTargets.Process(@"Q:\Fabrikam\FabrikamTray.exe", null, Wsc)!.Code);
        Assert.Equal("protected", ProtectedTargets.Process(@"Q:\Fabrikam\fabsvc.exe", "Fabrikam Inc.", Wsc)!.Code);

        Assert.Null(ProtectedTargets.Service("FabSvc", "Fabrikam Core", null));
        Assert.Equal("protected", ProtectedTargets.Service("FabSvc", "Fabrikam Core", null, securityProducts: Wsc)!.Code);

        Assert.Null(ProtectedTargets.StartupEntry("Fabrikam Tray", @"Q:\Fabrikam\tray.exe"));
        Assert.Equal("protected", ProtectedTargets.StartupEntry("Fabrikam Tray", @"Q:\Fabrikam\tray.exe", null, Wsc)!.Code);

        Assert.Null(ProtectedTargets.StoreApp("Fabrikam.Shield_abcdefghjkmnp", "Fabrikam Shield"));
        Assert.Equal("protected", ProtectedTargets.StoreApp("Fabrikam.Shield_abcdefghjkmnp", "Fabrikam Shield", null, Wsc)!.Code);

        // Unrelated software stays unprotected with the list present; Defender's own entry adds nothing.
        Assert.Null(ProtectedTargets.Process(@"Q:\Apps\editor.exe", "Contoso Ltd", Wsc));
        Assert.Null(ProtectedTargets.StartupEntry("Notes", @"Q:\Apps\notes.exe", "Contoso Ltd", Wsc));
    }

    [Fact]
    public void Process_end_reads_the_live_list()
    {
        var p = new FakeProcessControl();
        p.Running[4242] = new ProcessInfo(4242, 77, @"Q:\Fabrikam\fabui.exe", 1, FakeProcessControl.Me, false);
        var ev = new TargetEvidence("ev-1", EndProcessHandler.CollectorId, "perf.process.0badc0de", new JsonObject { ["image"] = "fabui.exe", ["pid"] = 4242, ["createTime"] = 77 }, null);
        Assert.True(new EndProcessHandler(p).Resolve(new ActionRequest(EndProcessHandler.Id, null, null, null) { Evidence = ev }).Eligible);
        var wsc = new FakeSecurityProducts("Fabui Security");
        Assert.Equal("protected", new EndProcessHandler(p, wsc).Resolve(new ActionRequest(EndProcessHandler.Id, null, null, null) { Evidence = ev }).IneligibleCode);
        Assert.True(wsc.Reads > 0);
    }

    [Fact]
    public void Service_disable_reads_the_live_list_in_host_and_broker()
    {
        const string Image = @"Q:\Program Files\Fabrikam\fabsvc.exe";
        var scm = new FakeScm();
        var files = new FakeFiles();
        files.Hashes[Image] = Admin.Admin.Hash;
        scm.Services["FabSvc"] = new ServiceInfo("FabSvc", "Fabrikam Core Service", 0x10, 2, false, "\"" + Image + "\"", Image, null, "LocalSystem", 4, []);
        var ev = new TargetEvidence("ev-1", "win.security.startup", "startup.service.0badc0de", new JsonObject
        {
            ["source"] = "service", ["location"] = "services", ["name"] = "Fabrikam Core Service", ["entryKey"] = StartupKeys.Of("FabSvc"),
        }, null);
        ActionRequest Req() => new(ServiceDisableHandler.Id, null, new JsonObject { ["startType"] = "disabled", ["stop"] = false }, null) { Evidence = ev };
        Assert.True(new ServiceDisableHandler(scm, files).Resolve(Req()).Eligible);
        var wsc = new FakeSecurityProducts("Fabrikam Total Security");
        Assert.Equal("protected", new ServiceDisableHandler(scm, files, wsc).Resolve(Req()).IneligibleCode);
        var forged = new ActionResolution(true, ServiceDisableHandler.Target("FabSvc", "Fabrikam Core Service"), new JsonObject(), [], [], [], [], null);
        Assert.Null(new ServiceBrokerHandler(scm, files).Protect(forged));
        Assert.Equal("protected", new ServiceBrokerHandler(scm, files, wsc).Protect(forged)!.Code);
    }

    // ---------------------------------------------------------------- the publisher, read locally

    /// <summary>A generic, renamed helper (helper.exe) signed by a registered security product's vendor.</summary>
    private const string Helper = @"Q:\Program Files\Fabrikam\bin\helper.exe";

    [Fact]
    public void Gpu_preference_and_process_end_read_the_executables_publisher()
    {
        var p = new FakeProcessControl();
        p.Running[4242] = new ProcessInfo(4242, 77, Helper, 1, FakeProcessControl.Me, false);
        var files = new FakeFiles();
        files.Publishers[Helper] = "Fabrikam Inc.";
        var wsc = new FakeSecurityProducts(Wsc);
        var ev = new TargetEvidence("ev-1", "win.perf.sample", "perf.process.0badc0de", new JsonObject { ["image"] = "helper.exe", ["pid"] = 4242, ["createTime"] = 77 }, null);

        var gpu = new ActionRequest(OptimizationTool.Windows.Actions.Apps.GpuPreferenceHandler.Id, null, new JsonObject { ["preference"] = "high_performance" }, null) { Evidence = ev };
        var gpus = new OptimizationTool.Windows.Tests.Actions.Apps.FakeGpus(2);
        Assert.NotEqual("protected", new OptimizationTool.Windows.Actions.Apps.GpuPreferenceHandler(new FakeRegistryStore(), p, gpus, wsc, @"Q:\Windows").Resolve(gpu).IneligibleCode);
        Assert.Equal("protected", new OptimizationTool.Windows.Actions.Apps.GpuPreferenceHandler(new FakeRegistryStore(), p, gpus, wsc, @"Q:\Windows", files).Resolve(gpu).IneligibleCode);

        var end = new ActionRequest(EndProcessHandler.Id, null, null, null) { Evidence = ev with { CollectorId = EndProcessHandler.CollectorId } };
        Assert.True(new EndProcessHandler(p, wsc).Resolve(end).Eligible); // the file name alone says nothing
        Assert.Equal("protected", new EndProcessHandler(p, wsc, files).Resolve(end).IneligibleCode);
    }

    [Fact]
    public void Block_app_and_task_disable_check_the_live_list_with_the_publisher()
    {
        var files = new FakeFiles();
        files.Hashes[Helper] = Admin.Admin.Hash;
        files.Publishers[Helper] = "Fabrikam Inc.";
        var wsc = new FakeSecurityProducts(Wsc);
        Assert.Null(OptimizationTool.Windows.Actions.Firewall.BlockAppHandler.Refuse(Helper, files));
        Assert.Equal("protected", OptimizationTool.Windows.Actions.Firewall.BlockAppHandler.Refuse(Helper, files, wsc)!.Code);

        var task = new OptimizationTool.Windows.Actions.Tasks.TaskInfo(@"\Fabrikam\Update", true, "S-1-5-18", 1, 5,
            [new OptimizationTool.Windows.Actions.Tasks.TaskActionInfo("exec", Helper, null, null, Helper)], ["logon"]);
        Assert.Null(OptimizationTool.Windows.Actions.Tasks.TaskDisableHandler.Refuse(task, files));
        Assert.Equal("protected", OptimizationTool.Windows.Actions.Tasks.TaskDisableHandler.Refuse(task, files, wsc)!.Code);
    }

    [Fact]
    public void Startup_disable_reads_the_images_publisher_itself()
    {
        var reg = new FakeRegistryStore();
        var files = new FakeStartupFiles();
        files.Files[Helper] = Admin.Admin.Hash;
        files.Publishers[Helper] = "Fabrikam Inc.";
        reg.Set(StartupDisableHandler.Run("Helper"), RegData.String($"\"{Helper}\""));
        var ev = new TargetEvidence("ev-1", StartupDisableHandler.CollectorId, "startup.run.hkcu.0badc0de", new JsonObject
        {
            ["source"] = "run", ["location"] = "hkcu", ["name"] = "Helper", ["enabled"] = true, ["entryKey"] = StartupKeys.Of("Helper"), ["signer"] = null,
        }, null);
        var req = new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = ev };
        Assert.Equal("protected", new StartupDisableHandler(reg, files, products: new FakeSecurityProducts(Wsc)).Resolve(req).IneligibleCode);
    }

    [Fact]
    public void Startup_disable_reads_the_live_list()
    {
        const string App = @"Q:\Apps\Fabrikam\fabtray.exe";
        var reg = new FakeRegistryStore();
        var files = new FakeStartupFiles();
        files.Files[App] = Admin.Admin.Hash;
        reg.Set(StartupDisableHandler.Run("FabTray"), RegData.String($"\"{App}\""));
        var ev = new TargetEvidence("ev-1", StartupDisableHandler.CollectorId, "startup.run.hkcu.0badc0de", new JsonObject
        {
            ["source"] = "run", ["location"] = "hkcu", ["name"] = "FabTray", ["enabled"] = true, ["entryKey"] = StartupKeys.Of("FabTray"),
        }, null);
        var req = new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = ev };
        Assert.True(new StartupDisableHandler(reg, files).Resolve(req).Eligible);
        Assert.Equal("protected", new StartupDisableHandler(reg, files, products: new FakeSecurityProducts("Fabtray Antivirus")).Resolve(req).IneligibleCode);
    }
}

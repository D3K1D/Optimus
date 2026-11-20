using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions;
using OptimizationTool.Windows.Actions.Apps;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Tests.Actions.Processes;
using OptimizationTool.Windows.Tests.Actions.Settings;

namespace OptimizationTool.Windows.Tests.Actions.Apps;

internal sealed class FakeGpus(int? count) : IGpuAdapters
{
    public int? Count = count;
    public int? HardwareAdapterCount() => Count;
}

/// <summary>app.set_gpu_preference over a fake registry, process table and adapter count. Never touches Windows.</summary>
public class GpuPreferenceHandlerTests
{
    private const uint Pid = 4242;
    private const long Created = 133_700_000_000_000_000;
    private const string Image = @"Q:\Games\Contoso Racer\racer.exe";
    private static readonly RegLocation At = GpuPreferenceHandler.At(Image);

    private static (GpuPreferenceHandler H, FakeRegistryStore R, FakeProcessControl P, FakeGpus G) New(int? gpus = 2, params string[] security)
    {
        var p = new FakeProcessControl();
        p.Running[Pid] = new ProcessInfo(Pid, Created, Image, 1, FakeProcessControl.Me, false);
        var r = new FakeRegistryStore();
        var g = new FakeGpus(gpus);
        return (new GpuPreferenceHandler(r, p, g, new FakeSecurityProducts(security), windowsDirectory: @"Q:\Windows"), r, p, g);
    }

    internal static TargetEvidence Ev(uint pid = Pid, long created = Created, string image = "racer.exe", string collector = "win.perf.sample", string subject = "perf.process.0badc0de") =>
        new("ev-1", collector, subject, new JsonObject { ["image"] = image, ["pid"] = pid, ["createTime"] = created }, null);

    private static ActionRequest Fresh(string pref = "high_performance", TargetEvidence? ev = null) =>
        new(GpuPreferenceHandler.Id, null, new JsonObject { ["preference"] = pref }, null) { Evidence = ev ?? Ev() };

    private static JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);

    private static void AssertNoName(params string?[] texts)
    {
        foreach (var t in texts.Where(t => t is not null))
        {
            Assert.DoesNotContain("racer", t!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"Q:\", t!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Metadata_matches_the_contract()
    {
        var (h, _, _, _) = New();
        Assert.Equal("app.set_gpu_preference", h.ActionId);
        Assert.Equal("setting", h.Kind);
        Assert.True(h.SupportsUndo);
        Assert.Empty(h.Flags);
        Assert.NotNull(((IActionHandler)h).TargetKind);
        Assert.Equal(["default", "power_saving", "high_performance"], h.ParameterSchema!["properties"]!["preference"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(WindowsModules.Actions, m => m is UserActionModule);
    }

    [Fact]
    public void Writes_the_value_Settings_writes_reads_it_back_and_undo_restores_absence()
    {
        var (h, r, _, _) = New();
        var res = h.Resolve(Fresh());
        Assert.True(res.Eligible, res.IneligibleMessage);
        Assert.Equal(Image, res.Target["path"]!.GetValue<string>());
        Assert.Contains(res.Rows, row => row.Before == "racer.exe");
        Assert.Contains(res.Rows, row => row.Before == "Let Windows decide" && row.After == "High performance");
        Assert.DoesNotContain(@"Q:\", Assert.Single(res.Resources!), StringComparison.OrdinalIgnoreCase);

        var recheck = h.Resolve(new ActionRequest(GpuPreferenceHandler.Id, Rt(res.Target), new JsonObject { ["preference"] = "high_performance" }, null) { Recheck = true });
        Assert.True(JsonNode.DeepEquals(res.Before, recheck.Before));
        var outcome = h.Execute(Exec(res));
        Assert.Equal("verified", outcome.State);
        Assert.Equal("GpuPreference=2;", r.Read(At).Data!.GetValue<string>());
        AssertNoName(outcome.ReceiptText, outcome.Message, res.IneligibleMessage);

        var undo = h.UndoRequest(res.Target, res.Before)!;
        var ur = h.Resolve(undo);
        Assert.True(ur.Eligible, ur.IneligibleMessage);
        Assert.Equal("verified", h.Execute(Exec(ur)).State);
        Assert.False(r.Read(At).Present);
    }

    [Fact]
    public void Keeps_other_pairs_Windows_stores_in_the_same_value_and_undo_restores_them_exactly()
    {
        var (h, r, _, _) = New();
        r.Set(At, RegData.String("SwapEffectUpgradeEnable=1;GpuPreference=1;"));
        var res = h.Resolve(Fresh());
        Assert.True(res.Eligible);
        h.Execute(Exec(res));
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", r.Read(At).Data!.GetValue<string>());
        var ur = h.Resolve(h.UndoRequest(res.Target, res.Before)!);
        h.Execute(Exec(ur));
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", r.Read(At).Data!.GetValue<string>());

        r.Set(At, RegData.String("SwapEffectUpgradeEnable=0;"));
        var add = h.Resolve(Fresh("power_saving"));
        h.Execute(Exec(add));
        Assert.Equal("SwapEffectUpgradeEnable=0;GpuPreference=1;", r.Read(At).Data!.GetValue<string>());
    }

    [Fact]
    public void Ineligible_paths()
    {
        var (h, r, p, g) = New();
        Assert.Equal("already_set", h.Resolve(Fresh("default")).IneligibleCode); // absent means Windows decides
        r.Set(At, RegData.String("GpuPreference=2;"));
        Assert.Equal("already_set", h.Resolve(Fresh()).IneligibleCode);
        r.Set(At, RegData.String("GpuPreference=7;"));
        Assert.Equal("unexpected_value", h.Resolve(Fresh()).IneligibleCode);
        r.Set(At, RegData.String("nonsense"));
        Assert.Equal("unexpected_value", h.Resolve(Fresh()).IneligibleCode);
        r.Set(At, RegData.Dword(2));
        Assert.Equal("unexpected_value", h.Resolve(Fresh()).IneligibleCode);
        r.Set(At, RegData.Absent);

        g.Count = 1;
        Assert.Equal("unsupported", h.Resolve(Fresh()).IneligibleCode);
        g.Count = null;
        Assert.Equal("read_failed", h.Resolve(Fresh()).IneligibleCode);
        g.Count = 2;

        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(ev: Ev(image: "other.exe"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(ev: Ev(collector: "win.security.startup"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(GpuPreferenceHandler.Id, null, new JsonObject { ["preference"] = "high_performance" }, null)).IneligibleCode);
        Assert.Equal("not_found", h.Resolve(Fresh(ev: Ev(created: Created + 1))).IneligibleCode);
        p.Running.Clear();
        Assert.Equal("not_found", h.Resolve(Fresh()).IneligibleCode);

        // Protected, hardware-list and Windows' own programs.
        foreach (var (image, code) in new[] { (@"Q:\Windows\System32\dwm.exe", "protected"), (@"Q:\Windows\notepad.exe", "protected"),
                     (@"Q:\Apps\MSI Afterburner\MSIAfterburner.exe", "hardware_limit"), (@"Q:\Apps\Optimus\OptimizationTool.Desktop.exe", "protected") })
        {
            p.Running[Pid] = new ProcessInfo(Pid, Created, image, 1, FakeProcessControl.Me, false);
            var res = h.Resolve(Fresh(ev: Ev(image: image[(image.LastIndexOf('\\') + 1)..])));
            Assert.Equal(code, res.IneligibleCode);
            AssertNoName(res.IneligibleMessage);
        }
        Assert.Equal(0, r.Writes);
    }

    [Fact]
    public void Hostile_inputs_are_refused()
    {
        var (h, r, _, _) = New();
        foreach (var bad in new JsonObject?[] { null, new() { ["preference"] = "turbo" }, new() { ["preference"] = 2 },
                     new() { ["preference"] = "high_performance", ["path"] = @"C:\x.exe" },
                     new() { ["restore"] = new JsonObject { ["present"] = true, ["data"] = "GpuPreference=2;" } } })
            Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(GpuPreferenceHandler.Id, null, bad, null) { Evidence = Ev() }).IneligibleCode);

        // A caller's raw target is never trusted; a tampered plan target fails its id check.
        var raw = new JsonObject { ["type"] = "app", ["id"] = GpuPreferenceHandler.IdOf(@"C:\evil.exe"), ["path"] = @"C:\evil.exe", ["label"] = "x" };
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(GpuPreferenceHandler.Id, raw, new JsonObject { ["preference"] = "high_performance" }, null) { Evidence = null }).IneligibleCode);
        var tampered = new JsonObject { ["type"] = "app", ["id"] = GpuPreferenceHandler.IdOf(Image), ["path"] = @"C:\evil.exe", ["label"] = "x" };
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(GpuPreferenceHandler.Id, tampered, new JsonObject { ["preference"] = "high_performance" }, null) { Recheck = true }).IneligibleCode);
        foreach (var path in new[] { @"Q:\a\..\b.exe", "relative.exe", @"Q:\a\b.bat", "Q:\\a\\b\u0001.exe" })
            Assert.False(GpuPreferenceHandler.ValidPath(path));
        // An undo with data that isn't the "Name=Value;" shape.
        var target = h.Resolve(Fresh()).Target;
        var badUndo = new ActionRequest(GpuPreferenceHandler.Id, target, new JsonObject { ["restore"] = new JsonObject { ["present"] = true, ["data"] = "a=b=c" } }, null) { Undo = true };
        Assert.Equal("invalid_arguments", h.Resolve(badUndo).IneligibleCode);
        Assert.Equal(0, r.Writes);
    }

    [Fact]
    public void A_write_that_does_not_stick_is_failed_or_indeterminate()
    {
        var (h, r, _, _) = New();
        var res = h.Resolve(Fresh());
        r.OnWrite = _ => RegData.Absent;
        Assert.Equal("failed", h.Execute(Exec(res)).State);
        r.OnWrite = _ => RegData.String("GpuPreference=1;");
        Assert.Equal("indeterminate", h.Execute(Exec(res)).State);
    }

    [Fact]
    public void A_security_product_Windows_reports_is_protected()
    {
        var (h, _, p, _) = New(2, "Contoso Antivirus");
        p.Running[Pid] = new ProcessInfo(Pid, Created, @"Q:\Apps\Contoso\ContosoUI.exe", 1, FakeProcessControl.Me, false);
        Assert.Equal("protected", h.Resolve(Fresh(ev: Ev(image: "ContosoUI.exe"))).IneligibleCode);
    }
}

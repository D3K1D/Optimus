using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Apps;

namespace OptimizationTool.Windows.Tests.Actions.Apps;

/// <summary>An in-memory package list for the current user. Never removes a real package.</summary>
internal sealed class FakeStorePackages : IStorePackages
{
    public readonly List<StorePackage> Installed = [];
    public readonly List<string> Removed = [];
    public Func<string, StoreRemoveResult>? OnRemove;
    /// <summary>An asynchronous removal (sees the cancellation token), instead of <see cref="OnRemove"/>.</summary>
    public Func<string, CancellationToken, Task<StoreRemoveResult>>? OnRemoveAsync;

    public IReadOnlyList<StorePackage> ForUser(string familyName) =>
        Installed.Where(p => string.Equals(p.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task<StoreRemoveResult> RemoveAsync(string fullName, CancellationToken ct)
    {
        Removed.Add(fullName);
        if (OnRemoveAsync is not null) return await OnRemoveAsync(fullName, ct);
        if (OnRemove is not null) return OnRemove(fullName);
        Installed.RemoveAll(p => p.FullName == fullName);
        return new StoreRemoveResult("removed");
    }
}

public class StoreUninstallHandlerTests
{
    private const string Family = "Contoso.PhotoFun_8wekyb3d8bbwe";
    private const string Full = "Contoso.PhotoFun_1.2.3.0_x64__8wekyb3d8bbwe";

    private static StorePackage Pkg(string family = Family, string full = Full, string? name = "Photo Fun", string? publisher = "Contoso Ltd",
        bool framework = false, bool resource = false, string kind = "store", string publisherId = "CN=Contoso") =>
        new(full, family, name, publisher, publisherId, framework, resource, false, kind);

    private static (StoreUninstallHandler H, FakeStorePackages P) New(params string[] security)
    {
        var p = new FakeStorePackages();
        p.Installed.Add(Pkg());
        return (new StoreUninstallHandler(p, new FakeSecurityProducts(security)), p);
    }

    internal static TargetEvidence Ev(string? family = Family, string collector = "win.apps.installed", string subject = "apps.package.0badc0de") =>
        new("ev-2", collector, subject, new JsonObject { ["name"] = "Photo Fun", ["source"] = "package", ["familyName"] = family }, null);

    private static ActionRequest Fresh(TargetEvidence? ev = null) => new(StoreUninstallHandler.Id, null, null, null) { Evidence = ev ?? Ev() };
    private static JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);

    private static void AssertNoName(params string?[] texts)
    {
        foreach (var t in texts.Where(t => t is not null))
        {
            Assert.DoesNotContain("Photo", t!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Contoso", t!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Metadata_matches_the_contract()
    {
        var (h, _) = New();
        Assert.Equal("app.uninstall_store", h.ActionId);
        Assert.Equal([ActionFlags.Irreversible], h.Flags);
        Assert.False(h.SupportsUndo);
        Assert.Equal("setting", h.Kind);
        Assert.NotNull(((IActionHandler)h).TargetKind);
    }

    [Fact]
    public async Task A_removal_that_doesnt_finish_in_time_is_cancelled_and_the_handler_returns()
    {
        var p = new FakeStorePackages();
        p.Installed.Add(Pkg());
        var h = new StoreUninstallHandler(p, new FakeSecurityProducts([]), removeTimeout: TimeSpan.FromMilliseconds(50));
        var cancelled = false;
        p.OnRemoveAsync = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new StoreRemoveResult("removed");
        };
        var r = h.Resolve(Fresh());
        var o = await h.ExecuteAsync(Exec(r), null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cancelled); // Windows' operation is cancelled, not abandoned
        Assert.Equal(("failed", "timeout"), (o.State, o.Code));
        Assert.True(StoreUninstallHandler.DefaultRemoveTimeout < TimeSpan.FromSeconds(90)); // inside the engine's setting deadline

        // The engine's own deadline (its token) cancels it too.
        cancelled = false;
        using var engine = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var slow = new StoreUninstallHandler(p, new FakeSecurityProducts([]));
        o = await slow.ExecuteAsync(Exec(r), null, engine.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cancelled);
        Assert.Equal("timeout", o.Code);
    }

    [Fact]
    public void Removes_the_exact_package_for_this_user_and_verifies_it_is_gone()
    {
        var (h, p) = New();
        var r = h.Resolve(Fresh());
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(Full, r.Target["fullName"]!.GetValue<string>());
        Assert.Contains(r.Rows, row => row.Before == "Photo Fun");
        Assert.Null(h.UndoRequest(r.Target, r.Before));
        Assert.True(h.Resolve(new ActionRequest(StoreUninstallHandler.Id, Rt(r.Target), null, null) { Recheck = true }).Eligible);

        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State);
        Assert.Equal([Full], p.Removed);
        Assert.Contains("install it again from the Microsoft Store", o.ReceiptText);
        AssertNoName(o.ReceiptText, o.Message);
    }

    [Fact]
    public void Refuses_frameworks_resources_system_windows_protected_and_missing_apps()
    {
        var (h, p) = New();
        void Only(StorePackage pkg) { p.Installed.Clear(); p.Installed.Add(pkg); }

        Only(Pkg(framework: true));
        Assert.Equal("protected", h.Resolve(Fresh()).IneligibleCode);
        Only(Pkg(resource: true));
        Assert.Equal("protected", h.Resolve(Fresh()).IneligibleCode);
        Only(Pkg(kind: "system"));
        Assert.Equal("protected", h.Resolve(Fresh()).IneligibleCode);
        Only(Pkg(publisherId: "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
        Assert.Equal("protected", h.Resolve(Fresh()).IneligibleCode);
        const string StoreFamily = "Microsoft.WindowsStore_8wekyb3d8bbwe";
        Only(Pkg(StoreFamily, "Microsoft.WindowsStore_22410.1401.5.0_x64__8wekyb3d8bbwe", "Microsoft Store", "Microsoft Corporation"));
        Assert.Equal("protected", h.Resolve(Fresh(Ev(StoreFamily))).IneligibleCode);
        const string Armoury = "B9ECED6F.ArmouryCrate_qmba6cd70vzyy";
        Only(Pkg(Armoury, "B9ECED6F.ArmouryCrate_5.9.0.0_x64__qmba6cd70vzyy", "ARMOURY CRATE", "ASUSTeK COMPUTER INC."));
        Assert.Equal("hardware_limit", h.Resolve(Fresh(Ev(Armoury))).IneligibleCode);
        p.Installed.Clear();
        var missing = h.Resolve(Fresh());
        Assert.Equal("not_found", missing.IneligibleCode);
        AssertNoName(missing.IneligibleMessage);
        Assert.Empty(p.Removed);
    }

    [Fact]
    public void Hostile_and_unknown_targets_are_refused()
    {
        var (h, p) = New();
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(family: null))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev("C:\\Windows\\x"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(collector: "win.security.startup"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(subject: "apps.installed.0badc0de"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(StoreUninstallHandler.Id,
            new JsonObject { ["type"] = "store_app", ["id"] = Family, ["fullName"] = Full }, null, null)).IneligibleCode); // a caller's raw target
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(StoreUninstallHandler.Id, null, new JsonObject { ["fullName"] = Full }, null) { Evidence = Ev() }).IneligibleCode);
        Assert.Equal("not_supported", h.Resolve(new ActionRequest(StoreUninstallHandler.Id, null, null, null) { Undo = true }).IneligibleCode);
        // Updated since the preview: the pinned full name no longer matches.
        var r = h.Resolve(Fresh());
        p.Installed[0] = Pkg(full: "Contoso.PhotoFun_1.2.4.0_x64__8wekyb3d8bbwe");
        Assert.Equal("changed_since", h.Resolve(new ActionRequest(StoreUninstallHandler.Id, Rt(r.Target), null, null) { Recheck = true }).IneligibleCode);
        Assert.Empty(p.Removed);
    }

    [Fact]
    public void A_removal_that_fails_reads_back_as_failed()
    {
        var (h, p) = New();
        var r = h.Resolve(Fresh());
        p.OnRemove = _ => new StoreRemoveResult("error", unchecked((int)0x80073CFA));
        var o = h.Execute(Exec(r));
        Assert.Equal("failed", o.State);
        Assert.Equal("write_failed", o.Code);
    }

    [Fact]
    public void A_security_product_Windows_reports_is_protected()
    {
        var (h, p) = New("Fabrikam Total Security");
        p.Installed[0] = Pkg(name: "Fabrikam Security Center", publisher: "Fabrikam Inc.");
        Assert.Equal("protected", h.Resolve(Fresh()).IneligibleCode);
    }
}

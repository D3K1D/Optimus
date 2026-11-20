using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Services;
using OptimizationTool.Windows.Actions.Startup;

namespace OptimizationTool.Windows.Tests.Actions.Admin;

/// <summary>service.disable (m9 §3.2), host half and broker half, with a fake Service Control Manager and fake file reads.</summary>
public sealed class ServiceDisableTests
{
    private const string Image = @"Q:\Program Files\Contoso\agent.exe";

    private static ServiceInfo Svc(string name = "ContosoAgent", int type = 0x10, int start = 2, int state = 4, string? image = Image, string? dll = null,
        IReadOnlyList<string>? dependents = null, string? display = "Contoso Agent") =>
        new(name, display, type, start, false, image is null ? null : "\"" + image + "\" -svc", image, dll, "LocalSystem", state, dependents ?? []);

    private static (ServiceDisableHandler H, FakeScm Scm, FakeFiles Files) New(ServiceInfo? svc = null)
    {
        var scm = new FakeScm();
        var files = new FakeFiles();
        files.Hashes[Image] = Admin.Hash;
        var s = svc ?? Svc();
        scm.Services[s.Name] = s;
        return (new ServiceDisableHandler(scm, files), scm, files);
    }

    private static TargetEvidence Ev(string keyName = "ContosoAgent", string source = "service") =>
        new("ev-1", "win.security.startup", $"startup.{source}.0badc0de", new JsonObject
        {
            ["source"] = source, ["location"] = "services", ["name"] = "Contoso Agent", ["entryKey"] = StartupKeys.Of(keyName),
        }, "2026-11-12T08:00:00Z");

    private static ActionRequest Fresh(string startType = "disabled", bool stop = true, TargetEvidence? ev = null) =>
        new(ServiceDisableHandler.Id, null, new JsonObject { ["startType"] = startType, ["stop"] = stop }, null) { Evidence = ev ?? Ev() };

    [Fact]
    public async Task Eligible_disable_and_stop_is_administrator_with_data_loss_and_a_fingerprint()
    {
        var (h, _, _) = New();
        Assert.Equal(("admin", "setting", true), (h.Privilege, h.Kind, ((IActionHandler)h).CanElevate));
        var r = h.Resolve(Fresh());
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal("admin", r.Privilege);
        Assert.Equal([ActionFlags.DataLoss], r.Flags);
        Assert.Equal(["service:CONTOSOAGENT", @"reg:64:HKLM\SYSTEM\CURRENTCONTROLSET\SERVICES\CONTOSOAGENT"], r.Resources);
        Assert.Equal(("auto", true), (r.Before.Str("startType"), r.Before["running"]!.GetValue<bool>()));
        Assert.Equal(("disabled", false), (r.After.Str("startType"), r.After["running"]!.GetValue<bool>()));
        var fp = r.Before["fingerprint"]!.AsObject();
        Assert.Equal(string.Join('|', Image, Admin.Hash, "LocalSystem", "ContosoAgent"), string.Join('|', fp.Str("image"), fp.Str("imageSha256"), fp.Str("account"), fp.Str("name")));
        Assert.NotEqual(false, r.UndoSupported);
        Assert.Equal("Starts", r.Rows[0].Label);
        Assert.Equal(("Automatic", "Disabled"), (r.Rows[0].Before, r.Rows[0].After));
        Assert.Contains(r.Rows, row => row.Label == "Running now" && row.After == "No");

        // Manual without stopping: no data_loss, still running.
        var manual = h.Resolve(Fresh("manual", stop: false));
        Assert.True(manual.Eligible);
        Assert.Null(manual.Flags);
        Assert.True(manual.After["running"]!.GetValue<bool>());
        // The host never writes it.
        Assert.Equal("failed", (await h.ExecuteAsync(Admin.Exec(r), null, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData("disabled_x", true)]
    [InlineData("auto", true)]
    public void Only_manual_or_disabled_with_an_explicit_stop(string startType, bool stop)
    {
        var (h, _, _) = New();
        Assert.Equal("invalid_arguments", h.Resolve(Fresh(startType, stop)).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(ServiceDisableHandler.Id, null, new JsonObject { ["startType"] = "manual" }, null) { Evidence = Ev() }).IneligibleCode);
        // A caller's restore is never accepted (engine-only undo).
        var forged = new JsonObject { ["restore"] = new JsonObject { ["startType"] = "auto", ["running"] = true } };
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(ServiceDisableHandler.Id, null, forged, null) { Evidence = Ev() }).IneligibleCode);
    }

    [Fact]
    public void Ineligible_paths_protected_driver_hardware_in_use_already_set_not_found_unknown()
    {
        // A Windows service on the protected list.
        Assert.Equal("protected", New(Svc("WinDefend")).H.Resolve(Fresh(ev: Ev("WinDefend"))).IneligibleCode);
        // Every driver type is protected.
        Assert.Equal("protected", New(Svc(type: 0x1)).H.Resolve(Fresh()).IneligibleCode);
        Assert.Equal("protected", New(Svc(type: 0x2)).H.Resolve(Fresh()).IneligibleCode);
        // Third-party security by publisher, read live from the image (never from evidence).
        var (sec, _, secFiles) = New();
        secFiles.Publishers[Image] = "Bitdefender SRL";
        Assert.Equal("protected", sec.Resolve(Fresh()).IneligibleCode);
        // A hardware tuning or fan service.
        Assert.Equal("hardware_limit", New(Svc("MSI Afterburner Service", display: "MSI Afterburner")).H.Resolve(Fresh(ev: Ev("MSI Afterburner Service"))).IneligibleCode);
        // Other running services depend on it.
        Assert.Equal("in_use", New(Svc(dependents: ["ContosoUi"])).H.Resolve(Fresh()).IneligibleCode);
        // Already disabled and stopped.
        Assert.Equal("already_set", New(Svc(start: 4, state: 1)).H.Resolve(Fresh()).IneligibleCode);
        // Gone, or the evidence names something else.
        var (gone, scm, _) = New();
        scm.Services.Clear();
        Assert.Equal("not_found", gone.Resolve(Fresh()).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(Fresh(ev: Ev(source: "run"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(new ActionRequest(ServiceDisableHandler.Id, null, Fresh().Parameters, null)).IneligibleCode);
        // A caller's raw target is never trusted on a fresh request.
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(new ActionRequest(ServiceDisableHandler.Id, ServiceDisableHandler.Target("ContosoAgent", "x"), Fresh().Parameters, null)).IneligibleCode);
    }

    [Fact]
    public void Broker_writes_reads_back_and_undo_restores_only_while_the_fingerprint_matches()
    {
        var (h, scm, files) = New();
        var p = Fresh().Parameters!;
        var r = h.Resolve(Fresh());
        var broker = new ServiceBrokerHandler(scm, files);
        var step = Admin.Step(ServiceDisableHandler.Id, r, p);
        var live = broker.Resolve(step);
        Assert.True(live.Eligible);
        Assert.Null(broker.Protect(live));
        Assert.Equal(step.ContentHash, BrokerContent.Hash(broker.ActionId, broker.Version, live.Target, live.Before, live.After, p));
        var outcome = broker.WriteAndReadBack(live, step, CancellationToken.None);
        Assert.Equal("verified", outcome.State);
        Assert.Equal(["start:ContosoAgent:4:False", "stop:ContosoAgent"], scm.Writes); // start type first, then stop
        Assert.Equal((4, 1), (scm.Services["ContosoAgent"].StartType, scm.Services["ContosoAgent"].State));

        // Undo: the engine builds it (restore = the recorded start type and state), flagged runs_code.
        var undo = h.UndoRequest(r.Target, r.Before)!;
        Assert.True(undo.Undo);
        var ur = h.Resolve(undo);
        Assert.True(ur.Eligible, ur.IneligibleMessage);
        Assert.Contains(ActionFlags.RunsCode, ur.Flags!);
        Assert.True(JsonNode.DeepEquals(ur.After, r.Before));
        var ustep = Admin.Step(ServiceDisableHandler.Id, ur, undo.Parameters);
        Assert.True(broker.IsUndo(ustep));
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(ustep), ustep, CancellationToken.None).State);
        Assert.Equal((2, 4), (scm.Services["ContosoAgent"].StartType, scm.Services["ContosoAgent"].State));

        // Disable again, then the program changes: undo is refused (changed_since), in the host and in the broker.
        scm.Services["ContosoAgent"] = Svc(start: 4, state: 1);
        files.Hashes[Image] = Admin.Hash2;
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
        Assert.Equal("changed_since", broker.Resolve(ustep).IneligibleCode);
        // Another account or command line is a different fingerprint too.
        files.Hashes[Image] = Admin.Hash;
        scm.Services["ContosoAgent"] = Svc(start: 4, state: 1) with { Account = @".\other" };
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
    }

    [Fact]
    public void An_ordinary_change_never_turns_a_disabled_service_back_on()
    {
        // Optimus disabled it; its program changed since. A fresh "manual" request must not re-enable it outside the undo.
        var (h, scm, files) = New(Svc(start: 2, state: 4));
        var r = h.Resolve(Fresh());
        var broker = new ServiceBrokerHandler(scm, files);
        var step = Admin.Step(ServiceDisableHandler.Id, r, Fresh().Parameters!);
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(step), step, CancellationToken.None).State);
        files.Hashes[Image] = Admin.Hash2;
        scm.Writes.Clear();

        foreach (var stop in new[] { false, true })
        {
            var manual = h.Resolve(Fresh("manual", stop));
            Assert.Equal("not_supported", manual.IneligibleCode);
        }
        // The broker refuses it too, even for a step the host let through (it re-resolves the same request itself).
        var now = h.State(scm.Services["ContosoAgent"]);
        var after = (JsonObject)now.DeepClone();
        after["startType"] = "manual";
        var forged = Admin.Step(ServiceDisableHandler.Id, new ActionResolution(true, r.Target, now, after, [], [], [], null),
            new JsonObject { ["startType"] = "manual", ["stop"] = false });
        Assert.Equal("not_supported", broker.Resolve(forged).IneligibleCode);
        Assert.Empty(scm.Writes);
        Assert.Equal(4, scm.Services["ContosoAgent"].StartType);

        // Manual → disabled and automatic → manual are still ordinary changes.
        var (h2, _, _) = New(Svc(start: 3, state: 1));
        Assert.True(h2.Resolve(Fresh("disabled", false)).Eligible);
        var (h3, _, _) = New(Svc(start: 2, state: 4));
        Assert.True(h3.Resolve(Fresh("manual", false)).Eligible);
    }

    [Fact]
    public void A_program_change_between_the_brokers_resolve_and_its_write_means_no_write()
    {
        var (h, scm, files) = New(Svc(start: 2, state: 4));
        var p = Fresh().Parameters!;
        var r = h.Resolve(Fresh());
        var broker = new ServiceBrokerHandler(scm, files);
        var step = Admin.Step(ServiceDisableHandler.Id, r, p);
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(step), step, CancellationToken.None).State);
        var undo = h.UndoRequest(r.Target, r.Before)!;
        var ustep = Admin.Step(ServiceDisableHandler.Id, h.Resolve(undo), undo.Parameters);
        var live = broker.Resolve(ustep);
        Assert.True(live.Eligible);
        scm.Writes.Clear();
        files.Hashes[Image] = Admin.Hash2; // replaced after the broker's resolve, before its write
        var o = broker.WriteAndReadBack(live, ustep, CancellationToken.None);
        Assert.Equal(("failed", "changed_since"), (o.State, o.Code));
        Assert.Empty(scm.Writes);
        Assert.Equal((4, 1), (scm.Services["ContosoAgent"].StartType, scm.Services["ContosoAgent"].State));
    }

    [Fact]
    public void No_fingerprint_no_undo_and_failed_or_unexpected_writes()
    {
        var (h, scm, files) = New();
        files.Hashes[Image] = null; // the binary can't be read
        var r = h.Resolve(Fresh());
        Assert.True(r.Eligible);
        Assert.False(r.UndoSupported);
        Assert.Null(h.UndoRequest(r.Target, r.Before));

        files.Hashes[Image] = Admin.Hash;
        r = h.Resolve(Fresh());
        scm.Ignore = true;
        Assert.Equal("failed", h.Execute(Admin.Exec(r)).State);
        scm.Services["ContosoAgent"] = Svc(start: 3, state: 4); // something else set it to manual meanwhile; our write doesn't stick
        Assert.Equal("indeterminate", h.Execute(Admin.Exec(r)).State);
    }

    [Fact]
    public void Broker_rechecks_protection_even_when_the_host_let_it_through()
    {
        var (_, scm, files) = New();
        var broker = new ServiceBrokerHandler(scm, files);
        // A resolution naming a protected service (a compromised host could send it): the broker's own lists refuse it.
        var forged = new ActionResolution(true, ServiceDisableHandler.Target("WinDefend", "Defender"), new JsonObject { ["fingerprint"] = new JsonObject { ["type"] = 0x10 } }, [], [], [], [], null);
        Assert.Equal("protected", broker.Protect(forged)!.Code);
        // The publisher is read by the broker itself: a security product's service is refused even if the host said nothing.
        files.Publishers[Image] = "ESET, spol. s r.o.";
        var r = new ActionResolution(true, ServiceDisableHandler.Target("ContosoAgent", "Contoso"), new JsonObject(), [], [], [], [], null);
        Assert.Equal("protected", broker.Protect(r)!.Code);
        // And a driver named in the request's fingerprint.
        scm.Services.Clear();
        var drv = new ActionResolution(true, ServiceDisableHandler.Target("contosodrv", "x"), new JsonObject { ["fingerprint"] = new JsonObject { ["type"] = 1 } }, [], [], [], [], null);
        Assert.Equal("protected", broker.Protect(drv)!.Code);
        // The broker's resolve refuses the protected service by itself too.
        scm.Services["WinDefend"] = Svc("WinDefend");
        var step = BrokerStepRequest.From(new ElevatedStep(1, ServiceDisableHandler.Id, "1.0.0", ServiceDisableHandler.Target("WinDefend", "x"), [], [], Fresh().Parameters));
        Assert.Equal("protected", broker.Resolve(step).IneligibleCode);
    }
}

internal static class JsonTestExtensions
{
    public static string? Str(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

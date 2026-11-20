using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows;
using OptimizationTool.Windows.Actions.Network;

namespace OptimizationTool.Tests;

/// <summary>
/// Design v62 "Nothing to undo": the handler decides it (<see cref="IActionHandler.NothingToUndo"/>), and its plans carry
/// <c>undo {supported: false, nothingToUndo: true}</c> (omitted otherwise). Such an operation gets no Undo, so it is never
/// part of Undo all. The catalog is constructed for its metadata only; the engine test runs a fake DNS cache.
/// </summary>
public sealed partial class NothingToUndoTests
{
    private static readonly string[] Expected =
    [
        "defender.update_signatures", "defender.quick_scan", "defender.full_scan", "repair.sfc", "repair.dism_restorehealth",
        "network.flush_dns", "restore_point.create",
    ];

    private static IReadOnlyList<IActionHandler> Catalog() =>
        [.. WindowsModules.Actions.SelectMany(m => m.Handlers(new ActionContext(null!, EnableFirewallHandler: true, EnableAdminActions: true)))];

    [Fact]
    public void Exactly_the_designed_handlers_have_nothing_to_undo_and_none_of_them_can_undo_or_is_flagged()
    {
        var all = Catalog();
        Assert.Equal(Expected.Order(), all.Where(h => h.NothingToUndo).Select(h => h.ActionId).Order());
        foreach (var h in all.Where(h => h.NothingToUndo))
        {
            Assert.False(h.SupportsUndo, h.ActionId + ": nothing to undo is never undoable");
            Assert.DoesNotContain(ActionFlags.Irreversible, h.Flags);
            Assert.DoesNotContain(ActionFlags.DataLoss, h.Flags);
        }
        // explorer.restart keeps data_loss (it may lose unsaved work), so it is not "nothing to undo".
        var explorer = Assert.Single(all, h => h.ActionId == "explorer.restart");
        Assert.False(explorer.NothingToUndo);
        Assert.Contains(ActionFlags.DataLoss, explorer.Flags);
    }

    [GeneratedRegex(@"const NOTHING_TO_UNDO = \[(?<body>[^\]]*)\];")]
    private static partial Regex FixtureList();

    /// <summary>The UI's parity test lists the fixture's nothing-to-undo actions; the host adds restore_point.create (not in the fixture's catalog).</summary>
    [Fact]
    public void The_fixture_parity_list_matches_the_host()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OptimizationTool.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "ui", "tests", "fixture-host-parity.test.mjs"));
        var m = FixtureList().Match(js);
        Assert.True(m.Success, "NOTHING_TO_UNDO not found in fixture-host-parity.test.mjs");
        var ids = Regex.Matches(m.Groups["body"].Value, "'([^']+)'").Select(x => x.Groups[1].Value).Append("restore_point.create");
        Assert.Equal(Expected.Order(), ids.Order());
    }

    private sealed class Dns : IDnsCache { public bool Flush() => true; }

    /// <summary>Claims nothing to undo but its resolution adds data_loss: the flag wins, so the plan doesn't carry it.</summary>
    private sealed class Flagged : IActionHandler
    {
        public string ActionId => "test.flagged";
        public string Version => "1.0.0";
        public string Label => "Flagged";
        public string Privilege => "none";
        public bool SupportsUndo => false;
        public bool NothingToUndo => true;
        public string Kind => "provider";
        public string Group => "test";
        public string What => "Does a thing.";
        public IReadOnlyList<string> Flags => [];
        public JsonObject? ParameterSchema => null;
        public bool TakesLease => false;
        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
            Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "test", ["id"] = "x" }, new JsonObject(), new JsonObject { ["done"] = true },
                [new PlanRow("Thing", "Before", "After")], [], [], null, Flags: [ActionFlags.DataLoss]));
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new ActionOutcome("completed", new JsonObject(), "Done."));
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
    }

    [Fact]
    public async Task Plans_carry_nothing_to_undo_only_when_the_handler_says_so_and_the_operation_has_no_undo()
    {
        var pc = new FakePc();
        await using var host = TestHost.StartActions([new FakeActionModule(new FlushDnsHandler(new Dns()), new FakeSchemeHandler(pc), new Flagged())]);
        var c = await host.ConnectAsync(minor: 3);

        var dns = await Act.Preview(c, FlushDnsHandler.Id, scheme: null);
        SchemaTests.AssertValid("action-plan", dns);
        Assert.Equal("""{"supported":false,"conditions":null,"nothingToUndo":true}""", dns["undo"]!.ToJsonString());

        var scheme = await Act.Preview(c);
        SchemaTests.AssertValid("action-plan", scheme);
        Assert.Null(scheme["undo"]!["nothingToUndo"]);
        Assert.False(scheme["undo"]!.AsObject().ContainsKey("nothingToUndo"), "omitted, not false");

        var flagged = await Act.Preview(c, "test.flagged", scheme: null);
        SchemaTests.AssertValid("action-plan", flagged);
        Assert.False(flagged["undo"]!.AsObject().ContainsKey("nothingToUndo"), "an irreversible or data_loss plan changes something");

        var op = await Act.Approve(c, dns);
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("operationId") == op.Str("operationId") && e["payload"].Str("state") == "completed");
        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.False(done["undo"]!["available"]!.GetValue<bool>(), "no Undo afterwards, so never part of Undo all");
        var undo = await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u", ["operationId"] = op.Str("operationId") });
        Assert.Equal("ineligible", undo.Str("status"));
        SchemaTests.AssertValid("action-plan", undo);
        Assert.False(undo["undo"]!.AsObject().ContainsKey("nothingToUndo"));
    }
}

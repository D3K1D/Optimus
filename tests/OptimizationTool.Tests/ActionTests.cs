using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Tests;

/// <summary>M4 action pipeline with fake handlers only (contracts/m4-actions.md; no test writes a real setting).</summary>
public class ActionTests
{
    private static (TestHost Host, FakePc Pc) Start(ManualTime? time = null, IElevation? elevation = null, bool firewall = false, IReadOnlyList<IAuditModule>? modules = null)
    {
        var pc = new FakePc();
        var handlers = new List<IActionHandler> { new FakeSchemeHandler(pc), new FakeScanHandler(pc) };
        if (elevation is not null) handlers.Add(new FakeFirewallHandler(elevation));
        return (TestHost.StartActions([new FakeActionModule([.. handlers])], modules, elevation, firewall, time), pc);
    }

    [Fact]
    public async Task ApprovedPlan_WritesExactlyOnce_UnderDuplicateRetryAndConcurrentApproval()
    {
        var (host, pc) = Start();
        await using var _ = host;
        pc.WriteDelay = TimeSpan.FromMilliseconds(100);
        var c = await host.ConnectAsync(minor: 3);

        var catalog = (await c.Call("action.catalog"))["actions"]!.AsArray();
        Assert.Contains(catalog, a => a.Str("actionId") == "power.select_scheme" && (bool)a!["enabled"]!);
        Assert.Contains(catalog, a => a.Str("actionId") == "manual.open");

        var plan = await Act.Preview(c, key: "p1");
        Assert.Equal("awaiting_user", plan.Str("status"));
        Assert.Equal("Change power plan", plan.Str("label"));
        Assert.Equal("high", plan["before"].Str("scheme"));
        Assert.Matches("^[0-9a-f]{64}$", plan.Str("planHash"));
        Assert.Equal(plan.Str("planId"), (await Act.Preview(c, key: "p1")).Str("planId")); // idempotent preview

        // Two approvals race with two valid nonces: one write, one operation.
        var n1 = await Act.Nonce(c, plan.Str("planId"));
        var n2 = await Act.Nonce(c, plan.Str("planId"));
        var both = await Task.WhenAll(c.Call("action.approve", Act.ApproveParams(plan, n1)), c.Call("action.approve", Act.ApproveParams(plan, n2)));
        Assert.Equal(both[0].Str("operationId"), both[1].Str("operationId"));
        Assert.Equal("verified", both[0].Str("state"));
        Assert.Equal(1, pc.Writes);
        Assert.Equal("balanced", pc.Scheme);

        // A retry with a reused nonce replays the receipt; still one write.
        var replay = await c.Call("action.approve", Act.ApproveParams(plan, n1));
        Assert.Equal(both[0].Str("operationId"), replay.Str("operationId"));
        Assert.Equal(1, pc.Writes);

        var op = await c.Call("operation.get", new JsonObject { ["operationId"] = replay.Str("operationId") });
        Assert.Equal(["prepared", "attempted", "verified"], op["steps"]!.AsArray().Select(s => s.Str("state")));
        Assert.Equal("balanced", op["receipt"]!["observed"].Str("scheme"));
        Assert.True((bool)op["undo"]!["available"]!);
        Assert.Equal("verified", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Single((await c.Call("operation.list", new JsonObject { ["limit"] = 10 }))["operations"]!.AsArray());
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("state") == "verified");
        Assert.Contains(c.Events, e => e.Str("type") == "action.plan" && e["payload"].Str("status") == "awaiting_user");
    }

    [Fact]
    public async Task Decline_Expiry_Stale_And_NonceRules_NeverWrite()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var (host, pc) = Start(time);
        await using var _ = host;
        var c = await host.ConnectAsync();

        var declined = await Act.Preview(c);
        await c.Call("action.decline", new JsonObject { ["planId"] = declined.Str("planId") });
        Assert.Equal("declined", (await c.Fails("action.challenge", new JsonObject { ["planId"] = declined.Str("planId") })).ErrorData.Str("reason"));

        var expiring = await Act.Preview(c);
        var early = await Act.Nonce(c, expiring.Str("planId"));
        time.Advance(TimeSpan.FromMinutes(6));
        var expired = await c.Fails("action.approve", Act.ApproveParams(expiring, early));
        Assert.Equal(ErrorCodes.InvalidState, expired.Code);
        Assert.Equal("expired", expired.ErrorData.Str("reason"));

        var stale = await Act.Preview(c);
        var nonce = await Act.Nonce(c, stale.Str("planId"));
        pc.Scheme = "power saver"; // the PC changed after the preview
        var staleError = await c.Fails("action.approve", Act.ApproveParams(stale, nonce));
        Assert.Equal(ErrorCodes.StaleRevision, staleError.Code);
        Assert.Equal("stale_plan", staleError.ErrorData.Str("reason"));
        pc.Scheme = "high";

        // Nonces: wrong hash, reuse on another plan, and expiry after 60 s.
        var a = await Act.Preview(c);
        var na = await Act.Nonce(c, a.Str("planId"));
        var badHash = Act.ApproveParams(a, na);
        badHash["planHash"] = new string('0', 64);
        Assert.Equal(ErrorCodes.StaleRevision, (await c.Fails("action.approve", badHash)).Code);
        var b = await Act.Preview(c, scheme: "power saver");
        Assert.Equal("invalid_nonce", (await c.Fails("action.approve", Act.ApproveParams(b, na))).ErrorData.Str("reason"));
        var late = await Act.Nonce(c, b.Str("planId"));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("invalid_nonce", (await c.Fails("action.approve", Act.ApproveParams(b, late))).ErrorData.Str("reason"));

        Assert.Equal(0, pc.Writes);
    }

    [Fact]
    public async Task Approve_IsRejectedForAgentClients_AndAgentsCannotApprove()
    {
        var (host, pc) = Start();
        await using var _ = host;
        var c = await host.ConnectAsync();
        var plan = await Act.Preview(c);
        var nonce = await Act.Nonce(c, plan.Str("planId"));
        var agent = await host.ConnectAsync(kind: "agent", minor: 3);
        foreach (var method in new[] { "action.approve", "action.challenge", "action.preview", "action.undo" })
            Assert.Equal(ErrorCodes.MethodNotAllowed, (await agent.Fails(method, Act.ApproveParams(plan, nonce))).Code);
        // Even in-process, an agent context is refused by the engine itself.
        var engineRefusal = await Assert.ThrowsAsync<RpcError>(() => host.Server.Engine.Actions.ApproveAsync(Act.ApproveParams(plan, nonce),
            new OptimizationTool.Engine.Agent.CallContext("c-x", "agent", Environment.ProcessId)));
        Assert.Equal("not_allowed", engineRefusal.ErrorData.Str("reason"));
        Assert.Equal(0, pc.Writes);
    }

    [Theory]
    [InlineData("balanced", "verified")]   // live == after
    [InlineData("high", "failed")]         // live == before
    [InlineData("power saver", "indeterminate")]
    public async Task Restart_ReconcilesInterruptedOperations_WithoutWriting(string live, string expected)
    {
        var (host, pc) = Start();
        await using var _ = host;
        var c = await host.ConnectAsync();
        var plan = await Act.Preview(c);
        var op = await Act.Approve(c, plan);
        Assert.Equal(1, pc.Writes);
        await host.StopAsync();

        // Simulate a host that died between "prepared" and the read-back.
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, Store.FileName)};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE operations SET state = 'prepared', body = json_set(body, '$.state', 'prepared', '$.receipt', json('null')) WHERE operation_id = $o;
                DELETE FROM operation_steps WHERE operation_id = $o AND state <> 'prepared';
                UPDATE action_plans SET status = 'running', body = json_set(body, '$.status', 'running') WHERE plan_id = $p;
                """;
            cmd.Parameters.AddWithValue("$o", op.Str("operationId"));
            cmd.Parameters.AddWithValue("$p", plan.Str("planId"));
            cmd.ExecuteNonQuery();
        }
        pc.Scheme = live;
        await host.RestartAsync();

        var c2 = await host.ConnectAsync();
        var reconciled = await c2.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal(expected, reconciled.Str("state"));
        Assert.True((bool)reconciled["reconciled"]!);
        if (expected == "verified") Assert.Equal("authorship uncertain after interruption", reconciled.Str("note"));
        Assert.Equal(1, pc.Writes); // never repeated
        Assert.Equal(live, pc.Scheme);
    }

    [Fact]
    public async Task Undo_IsANewPlan_AndDriftMakesItIneligible()
    {
        var (host, pc) = Start();
        await using var _ = host;
        var c = await host.ConnectAsync();
        var op = await Act.Approve(c, await Act.Preview(c));

        var undo = await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u1", ["operationId"] = op.Str("operationId") });
        Assert.Equal("awaiting_user", undo.Str("status"));
        Assert.Equal("high", undo["after"].Str("scheme"));
        Assert.Equal(op.Str("operationId"), undo.Str("undoOf"));
        await Act.Approve(c, undo);
        Assert.Equal(("high", 2), (pc.Scheme, pc.Writes));

        var op2 = await Act.Approve(c, await Act.Preview(c));
        pc.Scheme = "power saver"; // someone changed it since
        var drift = await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u2", ["operationId"] = op2.Str("operationId") });
        Assert.Equal("ineligible", drift.Str("status"));
        Assert.Equal("changed_since", drift["ineligible"].Str("code"));
        Assert.Equal(3, pc.Writes);
    }

    [Fact]
    public async Task UacCancelled_IsDeclined_AndTheFirewallStaysOffUnlessEnabled()
    {
        var uac = new CancelledUac();
        var (off, _) = Start(elevation: uac, firewall: false);
        await using (off)
        {
            var c = await off.ConnectAsync();
            var row = (await c.Call("action.catalog"))["actions"]!.AsArray().Single(a => a.Str("actionId") == "firewall.enable_public")!;
            Assert.False((bool)row["enabled"]!);
            var plan = await Act.Preview(c, "firewall.enable_public", scheme: null);
            Assert.Equal("ineligible", plan.Str("status"));
            Assert.Equal("disabled", plan["ineligible"].Str("code"));
        }

        var (on, _) = Start(elevation: uac, firewall: true);
        await using var __ = on;
        var c2 = await on.ConnectAsync();
        var fw = await Act.Preview(c2, "firewall.enable_public", scheme: null);
        Assert.Equal("admin", fw.Str("privilege"));
        var op = await Act.Approve(c2, fw);
        Assert.Equal("failed", op.Str("state"));
        Assert.Equal("uac_cancelled", op["error"].Str("code"));
        Assert.Equal("declined", (await c2.Call("action.get", new JsonObject { ["planId"] = fw.Str("planId") })).Str("status"));
        Assert.Equal(1, uac.Calls);
    }

    [Fact]
    public async Task ProviderOperation_ReportsProgress_ThenCompletes()
    {
        var (host, pc) = Start();
        await using var _ = host;
        var c = await host.ConnectAsync();
        var plan = await Act.Preview(c, "defender.quick_scan", scheme: null);
        pc.LastScan = "2026-11-02T00:00:00Z"; // Defender moved its own Before values: not stale for provider operations
        var op = await Act.Approve(c, plan);
        Assert.Contains(op.Str("state"), new[] { "prepared", "attempted" });

        var progress = await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Scanning · quick");
        Assert.Equal(op.Str("operationId"), progress["payload"].Str("operationId"));
        pc.ProviderGate.SetResult();
        var done = await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("state") == "verified");
        Assert.Equal("Quick scan finished. No threats found.", done["payload"]!["receipt"].Str("text"));
        var final = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("no_threats", final.Str("result"));
        Assert.False((bool)final["undo"]!["available"]!);
        Assert.Equal(1, pc.ProviderRuns);
    }

    [Fact]
    public async Task VerifiedChange_ClosesItsFindingCard_AndManualTasksRecheck()
    {
        var world = new FakeWorld { FreeGiB = 10, DisplayBelowMax = false };
        var modules = new List<IAuditModule>(FakeAudit.Modules(world, new CollectorOptions(), sampler: false));
        modules.Add(new FakeModule("power", [new FakeCollector("fake.power", "performance", _ => Task.FromResult(FakeCollector.One("power.active_scheme",
            new JsonObject { ["scheme"] = "high" }, "High performance is active.")))],
            [new FakeRule("power.high_perf_on_battery", "performance", ["fake.power"], ctx =>
            {
                var e = ctx.Get("power.active_scheme")!;
                return RuleResult.Found(new FindingDraft(e.Subject, "High performance on battery", "low", "high", [e.Id],
                    new CardDraft("Try Balanced?", ["Preview change", "Ignore"], ActionId: "power.select_scheme", ActionParameters: new JsonObject { ["scheme"] = "balanced" })));
            })]));
        var (host, pc) = Start(modules: modules);
        await using var _ = host;
        var c = await host.ConnectAsync();
        var (scan, _) = await c.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        var power = report["findings"]!.AsArray().Single(f => f.Str("ruleId") == "power.high_perf_on_battery")!;

        // Preview from the card: the rule's action parameters are used.
        var plan = await Act.Preview(c, scheme: null, findingId: power.Str("id"));
        Assert.Equal("balanced", plan["after"].Str("scheme"));
        Assert.Equal(power.Str("id"), plan.Str("findingId"));
        var op = await Act.Approve(c, plan);
        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        var decision = followup["decisions"]!.AsArray().Single(d => d.Str("findingId") == power.Str("id"))!;
        Assert.Equal("verified", decision.Str("status"));
        Assert.Equal(op.Str("operationId"), decision.Str("operationId"));
        Assert.True((bool)decision["undo"]!["available"]!);

        // A manual task: storage has a settings page; free space recovers; Check again verifies it.
        var storage = report["findings"]!.AsArray().Single(f => f.Str("ruleId") == "storage.system_low")!;
        var manual = await Act.Preview(c, "manual.open", scheme: null, findingId: storage.Str("id"));
        Assert.Equal(FakeAudit.Settings, manual["target"].Str("id"));
        Assert.Equal("manual", (await c.Fails("action.approve", Act.ApproveParams(manual, await Act.Nonce(c, manual.Str("planId"))))).ErrorData.Str("reason"));
        Assert.Equal("awaiting_rescan", (await c.Call("manual.recheck", new JsonObject { ["findingId"] = storage.Str("id") })).Str("status"));
        world.FreeGiB = 80;
        var recheck = await c.Call("manual.recheck", new JsonObject { ["findingId"] = storage.Str("id") });
        Assert.Equal("verified_by_rescan", recheck.Str("status"));
        Assert.NotEmpty(recheck["evidenceIds"]!.AsArray());
        followup = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        Assert.Equal("verified", followup["decisions"]!.AsArray().Single(d => d.Str("findingId") == storage.Str("id")).Str("status"));
        Assert.Equal(1, pc.Writes);
    }
}

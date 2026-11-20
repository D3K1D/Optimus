using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Tests;

/// <summary>
/// m9 §3.5 on the host: --enable-admin-actions (D6), administrator steps of a fix in ONE broker session after the user
/// steps, declined vs failed, stop, undo naming the broker's record, and restart recovery from the protected journal.
/// The broker is a fake (FakeElevation): nothing is elevated and nothing is written to this PC.
/// </summary>
public class AdminActionTests
{
    private sealed record Rig(TestHost Host, FakeSettings Pc, FakeElevation Broker, Client C) : IAsyncDisposable
    {
        public ActionService Actions => Host.Server.Engine.Actions;
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>A fake broker that applies each step to the fake PC, stopping at the first one that fails (the rest skipped).</summary>
    private static FakeElevation BrokerFor(FakeSettings pc) => new()
    {
        Answer = req =>
        {
            var list = new List<(int, ActionOutcome)>();
            var halted = false;
            foreach (var s in req.Steps)
            {
                if (halted) { list.Add((s.N, new ActionOutcome("skipped", [], "Not started."))); continue; }
                var id = s.Target["id"]!.GetValue<string>();
                if (pc.Fail.TryGetValue(id, out var f))
                {
                    list.Add((s.N, new ActionOutcome(f, new JsonObject { ["value"] = pc.Values[id] }, "Windows did not take it.", "write_failed")));
                    halted = true;
                    continue;
                }
                pc.Values[id] = s.After["value"]!.GetValue<string>();
                list.Add((s.N, new ActionOutcome("verified", new JsonObject { ["value"] = pc.Values[id] }, "Done, read back.")));
            }
            return new ElevatedResult(list, "rcpt-00000000000000b1", true);
        },
    };

    private static async Task<Rig> Start(bool admin = true, FakeElevation? broker = null, Func<FakeSettings, FakeElevation>? make = null)
    {
        var pc = new FakeSettings().Set("a", "off").Set("b", "off").Set("c", "off").Set("s1", "auto");
        pc.Privilege["c"] = "admin"; // setting.set with a machine-scope target
        var elevation = broker ?? make?.Invoke(pc) ?? BrokerFor(pc);
        var host = TestHost.StartActions([new FakeActionModule(
                new FakeSettingHandler(pc), new FakeSettingHandler(pc, "service.disable", privilege: "admin"))],
            elevation: elevation, enableAdmin: admin);
        return new Rig(host, pc, elevation, await host.ConnectAsync(minor: 3));
    }

    private static FixStepRequest S(string id, string value = "on", string actionId = "setting.set") =>
        new(actionId, new JsonObject { ["settingId"] = id, ["value"] = value }, null, null);

    private static Task<JsonObject> Propose(Rig r, params FixStepRequest[] steps) =>
        r.Actions.CreateFixAsync("Quieter startup", "Settings slow it down.", "Claude", steps, null, RequestedBy.User);

    private static Task<JsonObject> Finished(Client c, string planId) =>
        c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == planId && e["payload"]!["step"] is null
            && e["payload"].Str("state") is "verified" or "failed" or "indeterminate" or "partial");

    private static async Task<JsonObject> Run(Rig r, JsonObject plan)
    {
        var approved = await Act.Approve(r.C, plan);
        await Finished(r.C, plan.Str("planId"));
        return await r.C.Call("operation.get", new JsonObject { ["operationId"] = approved.Str("operationId") });
    }

    private static string[] States(JsonObject op) => [.. op["steps"]!.AsArray().Select(s => s.Str("state"))];

    // ---------------------------------------------------------------- flag off = today's behaviour

    [Fact]
    public async Task FlagOff_AdminEntriesDisabled_SingleAndFixAdminStepsUnavailable_NoBrokerCall()
    {
        await using var r = await Start(admin: false);
        var row = (await r.C.Call("action.catalog"))["actions"]!.AsArray().Single(a => a.Str("actionId") == "service.disable")!;
        Assert.False((bool)row["enabled"]!);
        Assert.Equal(ActionService.AdminUnavailable, row.Str("reason"));

        var single = await r.C.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "c", ["value"] = "on" },
        });
        Assert.Equal("ineligible", single.Str("status"));
        Assert.Equal(ActionService.AdminUnavailable, single["ineligible"].Str("code"));

        var fix = await Propose(r, S("a"), S("c"));
        Assert.Equal("ineligible", fix.Str("status"));
        Assert.Equal(ActionService.AdminUnavailable, fix["ineligible"].Str("code"));
        var withService = await Propose(r, S("s1", "manual", "service.disable"));
        Assert.Equal(ActionService.AdminUnavailable, withService["ineligible"].Str("code"));
        Assert.Equal(0, r.Broker.Calls);
        Assert.DoesNotContain("service.disable", r.Actions.RequestableIds);
    }

    // ---------------------------------------------------------------- fix plans

    [Fact]
    public async Task FlagOn_UserStepsFirst_ThenAllAdminSteps_InOneBrokerSession_InThePlansOrder()
    {
        await using var r = await Start();
        Assert.True((bool)(await r.C.Call("action.catalog"))["actions"]!.AsArray().Single(a => a.Str("actionId") == "service.disable")!["enabled"]!);
        var plan = await Propose(r, S("s1", "manual", "service.disable"), S("a"), S("c"));
        Assert.Equal("awaiting_user", plan.Str("status"));
        Assert.Equal(["a", "s1", "c"], plan["steps"]!.AsArray().Select(s => s!["parameters"].Str("settingId")));
        Assert.Equal("admin", plan.Str("privilege"));

        var op = await Run(r, plan);
        Assert.Equal(["verified", "verified", "verified"], States(op));
        Assert.Equal("verified", op.Str("state"));
        var session = Assert.Single(r.Broker.Requests); // one UAC prompt, one broker window
        Assert.Equal([2, 3], session.Steps.Select(s => s.N));
        Assert.Equal(["service.disable", "setting.set"], session.Steps.Select(s => s.ActionId));
        Assert.Equal(plan.Str("planHash"), session.PlanHash);
        Assert.Equal(1, r.Pc.Writes); // only the user step ran in the host
        Assert.Equal("manual", r.Pc.Values["s1"]);
        Assert.Null(op["steps"]![0]!["receipt"]!["brokerReceiptId"]);
        Assert.Equal("rcpt-00000000000000b1", op["steps"]![1]!["receipt"].Str("brokerReceiptId"));
        SchemaTests.AssertValid("operation", op);
    }

    [Fact]
    public async Task BrokerFailure_StopsAtThatStep_LaterAdminStepsSkipped_EarlierStepsKeepReceipts()
    {
        await using var r = await Start();
        r.Pc.Fail["s1"] = "failed";
        var op = await Run(r, await Propose(r, S("a"), S("s1", "manual", "service.disable"), S("c")));
        Assert.Equal(["verified", "failed", "skipped"], States(op));
        Assert.Equal("partial", op.Str("state"));
        Assert.Equal(2, op["error"]!["n"]!.GetValue<int>());
        Assert.NotNull(op["steps"]![0]!["receipt"]);
        Assert.Equal("off", r.Pc.Values["c"]);
    }

    [Fact]
    public async Task UacCancelled_IsDeclined_NotFailed_WhenNothingWasWritten()
    {
        await using var r = await Start(broker: new CancelledUac());
        var plan = await Propose(r, S("s1", "manual", "service.disable"), S("c"));
        var approved = await Act.Approve(r.C, plan);
        await r.C.WaitForEvent(e => e.Str("type") == "action.plan" && e["payload"].Str("planId") == plan.Str("planId") && e["payload"].Str("status") == "declined");
        var op = await r.C.Call("operation.get", new JsonObject { ["operationId"] = approved.Str("operationId") });
        Assert.Equal(["failed", "failed"], States(op));
        Assert.Equal("uac_cancelled", op["steps"]![0]!["error"].Str("code"));
        Assert.Equal("declined", (await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));

        // After a verified user step the same cancel leaves the fix partial.
        var mixed = await Run(r, await Propose(r, S("a"), S("c")));
        Assert.Equal(["verified", "failed"], States(mixed));
        Assert.Equal("partial", mixed.Str("state"));
    }

    [Fact]
    public async Task BrokerWindowCancelled_IsDeclined()
    {
        var cancelled = new FakeElevation
        {
            Answer = req => new ElevatedResult([.. req.Steps.Select(s => (s.N, new ActionOutcome("declined", [], "Cancelled.", "broker_cancelled", "Cancelled in the administrator window.")))], null, true),
        };
        await using var r = await Start(broker: cancelled);
        var plan = await Propose(r, S("c"));
        await Act.Approve(r.C, plan);
        await r.C.WaitForEvent(e => e.Str("type") == "action.plan" && e["payload"].Str("planId") == plan.Str("planId") && e["payload"].Str("status") == "declined");
        Assert.Equal("broker_cancelled", (await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") }))["ineligible"].Str("code"));
    }

    [Fact]
    public async Task Stop_ReachesTheBrokerSession_AsItsStopSignal()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var r = await Start(make: pc => { var b = BrokerFor(pc); b.Gate = gate; return b; });
        var plan = await Propose(r, S("s1", "manual", "service.disable"), S("c"));
        var approved = await Act.Approve(r.C, plan);
        await r.C.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("state") == "attempted" && e["payload"]!["step"] is not null);
        for (var i = 0; i < 100 && r.Broker.Calls == 0; i++) await Task.Delay(20);
        Assert.False(r.Broker.LastStop.IsCancellationRequested);
        await r.C.Call("operation.stop", new JsonObject { ["operationId"] = approved.Str("operationId") });
        Assert.True(r.Broker.LastStop.IsCancellationRequested);
        gate.SetResult();
        await Finished(r.C, plan.Str("planId"));
    }

    [Fact]
    public async Task UndoOfAnAdminStep_NamesTheBrokersRecordOfTheOriginal()
    {
        await using var r = await Start();
        var op = await Run(r, await Propose(r, S("a"), S("c")));
        var undo = await r.C.Call("action.undo", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["operationId"] = op.Str("operationId") });
        Assert.Equal("awaiting_user", undo.Str("status"));
        var undone = await Run(r, undo);
        Assert.Equal(["verified", "verified"], States(undone));
        var session = r.Broker.Requests[^1];
        var step = Assert.Single(session.Steps);
        Assert.Equal(new ElevatedUndoOf("rcpt-00000000000000b1", 2), step.UndoOf);
        Assert.Equal("off", r.Pc.Values["c"]);
    }

    // ---------------------------------------------------------------- single administrator plans

    [Fact]
    public async Task SingleAdminPlan_RunsAsOneStepBrokerSession_AndKeepsTheBrokersReceipt()
    {
        await using var r = await Start();
        var plan = await r.C.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "c", ["value"] = "on" },
        });
        Assert.Equal("admin", plan.Str("privilege"));
        var op = await Act.Approve(r.C, plan);
        Assert.Equal("verified", op.Str("state"));
        Assert.Equal("rcpt-00000000000000b1", op["receipt"].Str("brokerReceiptId"));
        var step = Assert.Single(Assert.Single(r.Broker.Requests).Steps);
        Assert.Equal(1, step.N);
        Assert.Equal(0, r.Pc.Writes);
        SchemaTests.AssertValid("operation", op);
    }

    // ---------------------------------------------------------------- restart recovery (m9 §3.5)

    public static TheoryData<string, string[], string> Crashes => new()
    {
        { "never-prepared", ["verified", "skipped", "skipped"], "partial" },
        { "s2-prepared-after", ["verified", "verified", "skipped"], "partial" },
        { "s2-prepared-before", ["verified", "failed", "skipped"], "partial" },
        { "s2-prepared-other", ["verified", "indeterminate", "skipped"], "indeterminate" },
        { "journal-unreadable", ["verified", "verified", "verified"], "verified" },
    };

    [Theory]
    [MemberData(nameof(Crashes))]
    public async Task Restart_ClassifiesDispatchedAdminSteps_FromTheJournalAndLiveState(string crash, string[] expected, string state)
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a"), S("s1", "manual", "service.disable"), S("c"));
        var op = await Run(r, plan);
        await r.Host.StopAsync();

        // As if the host died after dispatching steps 2 and 3 to the broker.
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(r.Host.DataDir, Store.FileName)};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE operation_step_states SET state = 'attempted', receipt = NULL, observed = NULL, dispatched_at = '2026-11-12T00:00:00.000Z',
                  undo = '{"available":false,"reason":"not_verified"}' WHERE operation_id = $o AND n IN (2, 3);
                UPDATE operations SET state = 'attempted', body = json_set(body, '$.state', 'attempted', '$.error', json('null')) WHERE operation_id = $o;
                UPDATE action_plans SET status = 'running', body = json_set(body, '$.status', 'running') WHERE plan_id = $p;
                """;
            cmd.Parameters.AddWithValue("$o", op.Str("operationId"));
            cmd.Parameters.AddWithValue("$p", plan.Str("planId"));
            cmd.ExecuteNonQuery();
        }
        r.Broker.Records = crash == "never-prepared" ? new ElevatedJournal(null, [])
            : new ElevatedJournal("rcpt-00000000000000b1", [new JournalStep(2, "service.disable", true, null, null)]);
        r.Broker.JournalUnreadable = crash == "journal-unreadable";
        r.Pc.Values["s1"] = crash switch { "s2-prepared-before" or "never-prepared" => "auto", "s2-prepared-other" => "weird", _ => "manual" };
        r.Pc.Values["c"] = crash == "journal-unreadable" ? "on" : "off";
        await r.Host.RestartAsync();

        var c2 = await r.Host.ConnectAsync(minor: 3);
        var reconciled = await c2.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal(expected, States(reconciled));
        Assert.Equal(state, reconciled.Str("state"));
        Assert.Equal(1, r.Broker.Calls); // never resumed
        SchemaTests.AssertValid("operation", reconciled);
    }

    [Fact]
    public async Task Restart_SingleAdminPlan_NeverPreparedByTheBroker_IsFailed()
    {
        await using var r = await Start();
        var plan = await r.C.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "c", ["value"] = "on" },
        });
        var op = await Act.Approve(r.C, plan);
        await r.Host.StopAsync();
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(r.Host.DataDir, Store.FileName)};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE operations SET state = 'prepared', body = json_set(body, '$.state', 'prepared', '$.receipt', json('null')) WHERE operation_id = $o;
                UPDATE action_plans SET status = 'running', body = json_set(body, '$.status', 'running') WHERE plan_id = $p;
                """;
            cmd.Parameters.AddWithValue("$o", op.Str("operationId"));
            cmd.Parameters.AddWithValue("$p", plan.Str("planId"));
            cmd.ExecuteNonQuery();
        }
        r.Broker.Records = new ElevatedJournal(null, []);
        await r.Host.RestartAsync();
        var c2 = await r.Host.ConnectAsync(minor: 3);
        var reconciled = await c2.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("failed", reconciled.Str("state")); // live is "on", but the journal shows the broker never prepared it
        Assert.Equal("interrupted", reconciled["error"].Str("code"));
    }
}

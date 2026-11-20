using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>
/// M9 fix plans (contracts/m9-ai-powers.md §4, §4.1): propose_fix, the manifest and its hash, the per-step journal, the
/// executor (partial, stop, stale), restart reconcile per step and undo of steps. Fake handlers only: nothing here writes
/// to this PC.
/// </summary>
public class FixPlanTests
{
    private sealed record Rig(TestHost Host, FakeSettings Pc, Client C) : IAsyncDisposable
    {
        public ActionService Actions => Host.Server.Engine.Actions;
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> Start(bool agent = false)
    {
        var pc = new FakeSettings().Set("a", "off").Set("b", "off").Set("c", "off").Set("s1", "auto");
        var scheme = new FakePc();
        var host = TestHost.StartActions([new FakeActionModule(
                new FakeSettingHandler(pc), new FakeSettingHandler(pc, "service.disable", privilege: "admin"),
                new FakeScanHandler(scheme), new FakeSchemeHandler(scheme))],
            agent ? FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false) : null,
            agent: agent ? AgentSetup.Options(null, new FakeProvider("anthropic_api")) : null);
        return new Rig(host, pc, await host.ConnectAsync(minor: 3));
    }

    private static FixStepRequest S(string id, string value = "on", string? why = null, string actionId = "setting.set") =>
        new(actionId, new JsonObject { ["settingId"] = id, ["value"] = value }, why, null);

    private static Task<JsonObject> Propose(Rig r, params FixStepRequest[] steps) =>
        r.Actions.CreateFixAsync("Quieter startup", "Two settings slow it down.", "Claude", steps, null, RequestedBy.User);

    private static Task<JsonObject> Finished(Client c, string planId) =>
        c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == planId && e["payload"]!["step"] is null
            && e["payload"].Str("state") is "verified" or "failed" or "indeterminate" or "partial");

    private static async Task<(JsonObject Op, JsonObject Final)> Run(Rig r, JsonObject plan)
    {
        var approved = await Act.Approve(r.C, plan);
        Assert.Equal("fix", approved.Str("kind"));
        var final = await Finished(r.C, plan.Str("planId"));
        return (await r.C.Call("operation.get", new JsonObject { ["operationId"] = approved.Str("operationId") }), final);
    }

    private static string[] States(JsonObject op) => [.. op["steps"]!.AsArray().Select(s => s.Str("state"))];

    // ---------------------------------------------------------------- propose_fix gating

    private static JsonObject FixArgs(int steps = 2) => new()
    {
        ["title"] = "Quieter startup", ["why"] = "Two​ settings\nslow it down.",
        ["steps"] = new JsonArray([.. new[] { "a", "b", "c", "a", "b", "c", "a", "b" }.Take(steps).Select((id, i) =>
            (JsonNode?)new JsonObject { ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = id, ["value"] = i < 3 ? "on" : "x" + i } })]),
    };

    private static JsonObject GrantWith(string changes, int? fixSteps = null)
    {
        var g = AgentSetup.Grant([]);
        g["changes"] = changes;
        if (fixSteps is not null) g["limits"] = new JsonObject { ["fixSteps"] = fixSteps };
        return g;
    }

    [Fact]
    public async Task ProposeFix_NeedsMultiStepFixes_OnePendingPlan_AndTheStepCap()
    {
        await using var r = await Start(agent: true);
        var reportId = await AgentSetup.Report(r.C);
        await AgentSetup.SetKey(r.C);
        var agent = r.Host.Server.Engine.Agent;

        foreach (var level in new[] { "off", "ask" })
        {
            var t = (await AgentSetup.StartTask(r.C, reportId, grant: GrantWith(level))).Str("taskId");
            var refused = await agent.CallToolAsync(t, "propose_fix", FixArgs(), "in_app", null, default);
            Assert.Equal("not_allowed", refused["error"].Str("code"));
            var grant = Grant.FromJson((await r.C.Call("agent.get", new JsonObject { ["taskId"] = t }))["grant"]!);
            Assert.DoesNotContain(Diagnostics.Tools(grant), s => s.Name == "propose_fix");
        }

        var taskId = (await AgentSetup.StartTask(r.C, reportId, grant: GrantWith("fix"))).Str("taskId");
        Assert.Contains(Diagnostics.Tools(Grant.FromJson((await r.C.Call("agent.get", new JsonObject { ["taskId"] = taskId }))["grant"]!)), s => s.Name == "propose_fix");
        var proposed = await agent.CallToolAsync(taskId, "propose_fix", FixArgs(), "in_app", null, default);
        Assert.Equal("awaiting_user", proposed.Str("status"));
        Assert.Equal([1, 2], proposed["steps"]!.AsArray().Select(s => (int)s!["n"]!));
        Assert.All(proposed["steps"]!.AsArray(), s => Assert.Equal("ok", s.Str("status")));
        Assert.StartsWith("2 steps, waiting", proposed.Str("summary"));
        Assert.DoesNotContain("off", proposed["steps"]!.ToJsonString()); // row values stay in the app
        SchemaTests.AssertValid("tool-result", proposed);

        var plan = await r.C.Call("action.get", new JsonObject { ["planId"] = proposed.Str("planId") });
        Assert.Equal("fix", plan.Str("kind"));
        Assert.Equal("in_app", plan["requestedBy"].Str("kind"));
        Assert.Equal(new JsonObject { ["text"] = "Two settings slow it down.", ["by"] = "Claude" }.ToJsonString(), plan["why"]!.ToJsonString()); // sanitised
        Assert.Contains(r.C.Events, e => e.Str("type") == "agent.tool" && e["payload"].Str("tool") == "propose_fix" && e["payload"].Str("status") == "complete"
            && e["payload"].Str("planId") == proposed.Str("planId"));

        // One pending plan per task, whichever tool asks.
        Assert.Equal("not_allowed", (await agent.CallToolAsync(taskId, "propose_fix", FixArgs(), "in_app", null, default))["error"].Str("code"));
        Assert.Equal("not_allowed", (await agent.CallToolAsync(taskId, "request_action", new JsonObject { ["actionId"] = "power.select_scheme" }, "in_app", null, default))["error"].Str("code"));
        // get_operation {planId} works for a fix.
        var status = await agent.CallToolAsync(taskId, "get_operation", new JsonObject { ["planId"] = proposed.Str("planId") }, "in_app", null, default);
        Assert.Equal(2, (int)status["summary"]!["changes"]!);
        SchemaTests.AssertValid("tool-result", status);

        // The grant's fixSteps bounds the arguments; a grant above the dialog's 6 makes a 7-step fix too_large instead.
        var two = (await AgentSetup.StartTask(r.C, reportId, grant: GrantWith("fix", fixSteps: 2))).Str("taskId");
        Assert.Equal("invalid_arguments", (await agent.CallToolAsync(two, "propose_fix", FixArgs(3), "in_app", null, default))["error"].Str("code"));
        var eight = (await AgentSetup.StartTask(r.C, reportId, grant: GrantWith("fix", fixSteps: 8))).Str("taskId");
        var big = await agent.CallToolAsync(eight, "propose_fix", FixArgs(7), "in_app", null, default);
        Assert.Equal("ineligible", big.Str("status"));
        Assert.Empty(big["steps"]!.AsArray());
        var bigPlan = await r.C.Call("action.get", new JsonObject { ["planId"] = big.Str("planId") });
        Assert.Equal("too_large", bigPlan["ineligible"].Str("code"));
        Assert.Empty(bigPlan["ineligible"]!["steps"]!.AsArray());
        Assert.Empty(bigPlan["steps"]!.AsArray());
        SchemaTests.AssertValid("action-plan", bigPlan);

        // request_action gains why (plain, quoted with its author).
        var single = (await AgentSetup.StartTask(r.C, reportId, grant: GrantWith("ask"))).Str("taskId");
        var asked = await agent.CallToolAsync(single, "request_action",
            new JsonObject { ["actionId"] = "power.select_scheme", ["parameters"] = new JsonObject { ["scheme"] = "balanced" }, ["why"] = "Saves‮ battery." }, "in_app", null, default);
        var singlePlan = await r.C.Call("action.get", new JsonObject { ["planId"] = asked.Str("planId") });
        Assert.Equal("Saves battery.", singlePlan["why"].Str("text"));
        Assert.Equal("setting", singlePlan.Str("kind"));
        Assert.Null(singlePlan["title"]);
        Assert.Null(singlePlan["steps"]);
        SchemaTests.AssertValid("action-plan", singlePlan);
        Assert.Equal(0, r.Pc.Writes);
    }

    [Fact]
    public async Task FixPlan_LosesTheApproveRace_WhenTheTaskNoLongerHoldsFixes()
    {
        await using var r = await Start(agent: true);
        var reportId = await AgentSetup.Report(r.C);
        await AgentSetup.SetKey(r.C);
        var taskId = (await AgentSetup.StartTask(r.C, reportId, grant: GrantWith("fix"))).Str("taskId");
        var proposed = await r.Host.Server.Engine.Agent.CallToolAsync(taskId, "propose_fix", FixArgs(), "in_app", null, default);
        var plan = await r.C.Call("action.get", new JsonObject { ["planId"] = proposed.Str("planId") });
        // The task's grant narrows to single changes between the dialog and the approve.
        r.Host.Server.Engine.Store.Write(db => db.Exec("UPDATE agent_tasks SET body = json_set(body, '$.grant.changes', 'ask') WHERE task_id = $t", ("$t", taskId)));
        var nonce = await Act.Nonce(r.C, plan.Str("planId"));
        var refused = await r.C.Fails("action.approve", Act.ApproveParams(plan, nonce));
        Assert.Equal("cancelled", refused.ErrorData.Str("reason"));
        Assert.Equal("cancelled", (await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Equal(0, r.Pc.Writes);
    }

    // ---------------------------------------------------------------- manifest

    [Fact]
    public async Task Order_IsUserScopeFirst_AndAnAdministratorStepMakesThePlanUnavailable()
    {
        await using var r = await Start();
        var plan = await Propose(r, S("s1", "manual", "It reads the disk.", actionId: "service.disable"), S("a", why: "First user step."), S("b"));
        Assert.Equal(["setting.set", "setting.set", "service.disable"], plan["steps"]!.AsArray().Select(s => s.Str("actionId")));
        Assert.Equal([1, 2, 3], plan["steps"]!.AsArray().Select(s => (int)s!["n"]!));
        Assert.Equal(["none", "none", "admin"], plan["steps"]!.AsArray().Select(s => s.Str("privilege")));
        Assert.Equal("First user step.", plan["steps"]![0].Str("why"));
        Assert.Equal("It reads the disk.", plan["steps"]![2].Str("why"));
        Assert.Equal("ineligible", plan.Str("status"));
        Assert.Equal("admin", plan.Str("privilege"));
        Assert.Equal(ActionService.AdminUnavailable, plan["ineligible"].Str("code"));
        Assert.Equal("Step 3: Administrator steps aren't available in this version.", plan["ineligible"].Str("message"));
        Assert.Equal(new JsonArray(new JsonObject { ["n"] = 3, ["status"] = "ineligible", ["code"] = "admin_unavailable", ["message"] = "Administrator steps aren't available in this version." }).ToJsonString(),
            plan["ineligible"]!["steps"]!.ToJsonString());
        Assert.Empty(plan["resources"]!.AsArray());
        SchemaTests.AssertValid("action-plan", plan);
        Assert.Equal("ineligible", (await r.C.Fails("action.challenge", new JsonObject { ["planId"] = plan.Str("planId") })).ErrorData.Str("reason"));

        // A user-scope handler resolving one target as administrator is ordered (and refused) the same way.
        r.Pc.Privilege["a"] = "admin";
        var mixed = await Propose(r, S("a"), S("b"));
        Assert.Equal(["b", "a"], mixed["steps"]!.AsArray().Select(s => s!["parameters"].Str("settingId")));
        Assert.Equal(ActionService.AdminUnavailable, mixed["ineligible"].Str("code"));
        Assert.Equal(0, r.Pc.Writes);
    }

    [Fact]
    public async Task IneligibleSteps_KeepTheirPlace_WithNullContent_AndTheirReason()
    {
        await using var r = await Start();
        r.Pc.Values["b"] = "on";
        var plan = await Propose(r, S("a"), S("b"), new FixStepRequest("defender.quick_scan", null, null, null), new FixStepRequest("nope.never", null, null, null));
        Assert.Equal("ineligible", plan.Str("status"));
        var steps = plan["steps"]!.AsArray();
        Assert.Equal(4, steps.Count);
        Assert.Equal("already_set", plan["ineligible"].Str("code")); // the first blocked step's
        Assert.Equal(["already_set", "not_supported", "not_supported"], plan["ineligible"]!["steps"]!.AsArray().Select(s => s.Str("code")));
        var blocked = steps[1]!.AsObject();
        Assert.Null(blocked["target"]);
        Assert.Null(blocked["before"]);
        Assert.Empty(blocked["rows"]!.AsArray());
        Assert.False((bool)blocked["undo"]!["supported"]!);
        Assert.Equal("nope.never", steps[3].Str("label"));
        Assert.Null(steps[3]!["handlerVersion"]);
        SchemaTests.AssertValid("action-plan", plan);
    }

    [Fact]
    public async Task OverlappingResources_MarkTheLaterStep_AsConflict()
    {
        await using var r = await Start();
        var same = await Propose(r, S("a"), S("a", "x"));
        Assert.Equal("conflict", same["ineligible"].Str("code"));
        Assert.Equal("Step 2: Changes the same thing as step 1.", same["ineligible"].Str("message"));
        Assert.Equal(2, (int)same["ineligible"]!["steps"]![0]!["n"]!);
        // Hierarchical keys: a parent key conflicts with its child.
        r.Pc.Resources["b"] = [@"reg:64:HKCU\TEST"];
        var parent = await Propose(r, S("a"), S("b"));
        Assert.Equal("conflict", parent["ineligible"].Str("code"));
        r.Pc.Resources["b"] = [@"reg:64:HKCU\TESTING"];
        Assert.Equal("awaiting_user", (await Propose(r, S("a"), S("b"))).Str("status"));
    }

    [Fact]
    public async Task ContentOverACap_MakesThePlanTooLarge_NeverCut()
    {
        await using var r = await Start();
        r.Pc.Rows["a"] = 5;
        var rows = await Propose(r, S("a"), S("b"));
        Assert.Equal("too_large", rows["ineligible"].Str("code"));
        Assert.Equal(1, (int)rows["ineligible"]!["steps"]![0]!["n"]!);
        r.Pc.Rows["a"] = 4;
        Assert.Equal("awaiting_user", (await Propose(r, S("a"), S("b"))).Str("status"));

        var seven = await Propose(r, [.. Enumerable.Range(0, 7).Select(_ => S("a"))]);
        Assert.Equal("too_large", seven["ineligible"].Str("code"));
        Assert.Empty(seven["steps"]!.AsArray());
        var title = await r.Actions.CreateFixAsync(new string('t', 81), "why", "Claude", [S("a")], null, RequestedBy.User);
        Assert.Equal("too_large", title["ineligible"].Str("code"));
        var why = await r.Actions.CreateFixAsync("t", new string('w', 401), "Claude", [S("a")], null, RequestedBy.User);
        Assert.Equal("too_large", why["ineligible"].Str("code"));
        var stepWhy = await r.Actions.CreateFixAsync("t", "w", "Claude", [S("a", why: new string('s', 201))], null, RequestedBy.User);
        Assert.Equal("too_large", stepWhy["ineligible"].Str("code"));
        // Control and format characters do not count toward a cap: they are removed.
        var hidden = await r.Actions.CreateFixAsync(new string('t', 80) + "​‮", "w", "Claude", [S("a")], null, RequestedBy.User);
        Assert.Equal("awaiting_user", hidden.Str("status"));
        Assert.Equal(new string('t', 80), hidden.Str("title"));
    }

    public static readonly string[] StepFields =
        ["n", "actionId", "handlerVersion", "label", "what", "target", "before", "after", "parameters", "rows", "context", "tradeoffs",
         "privilege", "flags", "undo", "resources", "contentHash", "why"];

    [Fact]
    public async Task PlanHash_CoversEveryStepField_TheTitle_TheWhy_AndTheExpiry()
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a", why: "w"), S("b"));
        Assert.Equal(plan.Str("planHash"), ActionService.HashFix(plan));
        foreach (var field in StepFields)
        {
            var changed = (JsonObject)plan.DeepClone();
            changed["steps"]![1]![field] = field == "n" ? 9 : new JsonObject { ["changed"] = true };
            Assert.True(ActionService.HashFix(changed) != plan.Str("planHash"), field);
        }
        foreach (var field in new[] { "title", "why", "expiresAt" })
        {
            var changed = (JsonObject)plan.DeepClone();
            changed[field] = "changed";
            Assert.True(ActionService.HashFix(changed) != plan.Str("planHash"), field);
        }
        // contentHash: the same step resolves to the same hash; another target to another.
        var again = await Propose(r, S("a", why: "w"), S("b"));
        Assert.Equal(plan["steps"]![0].Str("contentHash"), again["steps"]![0].Str("contentHash"));
        Assert.NotEqual(plan["steps"]![0].Str("contentHash"), plan["steps"]![1].Str("contentHash"));
        Assert.NotEqual(plan.Str("planHash"), again.Str("planHash"));
        Assert.Equal(StepFields.Order(), plan["steps"]![0]!.AsObject().Select(p => p.Key).Order());
    }

    // ---------------------------------------------------------------- executor

    [Fact]
    public async Task AllStepsVerified_EachTransitionHasItsEvent_AndTheFinalEventHasNoStep()
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a"), S("b"));
        var (op, final) = await Run(r, plan);
        Assert.Equal(["verified", "verified"], States(op));
        Assert.Equal("verified", op.Str("state"));
        Assert.Null(op["error"]);
        Assert.True((bool)op["undo"]!["available"]!);
        Assert.Null(final["payload"]!["progress"]);
        var stepEvents = r.C.Events.Where(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == plan.Str("planId") && e["payload"]!["step"] is not null).ToList();
        Assert.Equal(["1:prepared", "1:attempted", "1:verified", "2:prepared", "2:attempted", "2:verified"],
            stepEvents.Select(e => $"{e["payload"]!["step"]!["n"]}:{e["payload"]!["step"].Str("state")}"));
        Assert.All(stepEvents, e => Assert.Matches(@"^Step [12] of 2$", e["payload"].Str("progress")));
        Assert.Equal("verified", (await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Equal(("on", "on", 2), (r.Pc.Values["a"], r.Pc.Values["b"], r.Pc.Writes));
        // The journal: one row per step, history of every transition.
        var rows = r.Host.Server.Engine.Store.Read(db => db.Query("SELECT state, preimage, dispatched_at FROM operation_step_states WHERE operation_id = $o ORDER BY n",
            x => (x.GetString(0), x.GetString(1), x.IsDBNull(2)), ("$o", op.Str("operationId"))));
        Assert.Equal([("verified", "{\"value\":\"off\"}", false), ("verified", "{\"value\":\"off\"}", false)], rows);
        Assert.Equal(["prepared", "prepared", "attempted", "verified", "prepared", "attempted", "verified", "verified"], op["history"]!.AsArray().Select(h => h.Str("state")));
        Assert.Equal(op.Str("operationId"), (await r.C.Call("action.approve", Act.ApproveParams(plan, "x"))).Str("operationId")); // a repeated approve replays
    }

    [Fact]
    public async Task AFailedMiddleStep_EndsPartial_KeepsEarlierReceipts_AndSkipsTheRest()
    {
        await using var r = await Start();
        r.Pc.Fail["b"] = "failed";
        var plan = await Propose(r, S("a"), S("b"), S("c"));
        var (op, _) = await Run(r, plan);
        Assert.Equal(["verified", "failed", "skipped"], States(op));
        Assert.Equal("partial", op.Str("state"));
        Assert.Equal("step_failed", op["error"].Str("code"));
        Assert.Equal(2, (int)op["error"]!["n"]!);
        Assert.NotNull(op["steps"]![0]!["receipt"]!["receiptId"]);
        Assert.True((bool)op["steps"]![0]!["undo"]!["available"]!);
        Assert.Equal("not_verified", op["steps"]![1]!["undo"].Str("reason"));
        Assert.Equal("write_failed", op["steps"]![1]!["error"].Str("code"));
        Assert.True((bool)op["undo"]!["available"]!);
        Assert.Equal("partial", (await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Equal(2, r.Pc.Writes);
        Assert.Equal("off", r.Pc.Values["c"]);
        Assert.Contains(r.C.Events, e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Step 2 of 3");

        // The first step failing: nothing written → failed.
        r.Pc.Fail.Clear();
        r.Pc.Fail["c"] = "failed";
        var first = await Propose(r, S("c"), S("b"));
        var (op2, _) = await Run(r, first);
        Assert.Equal(["failed", "skipped"], States(op2));
        Assert.Equal("failed", op2.Str("state"));
        Assert.Equal("not_verified", op2["undo"].Str("reason"));
    }

    [Fact]
    public async Task AnIndeterminateStep_StopsTheRun()
    {
        await using var r = await Start();
        r.Pc.Fail["b"] = "indeterminate";
        var (op, _) = await Run(r, await Propose(r, S("a"), S("b"), S("c")));
        Assert.Equal(["verified", "indeterminate", "skipped"], States(op));
        Assert.Equal("indeterminate", op.Str("state"));
        Assert.Equal("step_indeterminate", op["error"].Str("code"));
        Assert.Equal(2, r.Pc.Writes);
    }

    [Fact]
    public async Task Stop_LetsTheStepInFlightFinish_AndSkipsTheRest()
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a"), S("b"), S("c"));
        string? opId = null;
        var stopped = new TaskCompletionSource();
        r.Pc.DuringWrite = id =>
        {
            if (id != "a") return;
            // The card's "Stop after this step", while step 1 is being written.
            var view = r.Actions.OperationStop(new JsonObject { ["operationId"] = r.Host.Server.Engine.Store.Read(db => (string)db.Scalar("SELECT operation_id FROM operations WHERE plan_id = $p", ("$p", plan.Str("planId")))!) });
            opId = view.Str("operationId");
            stopped.TrySetResult();
        };
        var (op, _) = await Run(r, plan);
        await stopped.Task;
        Assert.Equal(op.Str("operationId"), opId);
        Assert.Equal(["verified", "skipped", "skipped"], States(op));
        Assert.Equal("partial", op.Str("state"));
        Assert.Null(op["error"]);
        Assert.Equal(1, r.Pc.Writes);

        // Stop during the last step: nothing left, so verified. Stop on a finished or single operation is a no-op.
        r.Pc.DuringWrite = null;
        var last = await Propose(r, S("c"));
        var (op2, _) = await Run(r, last);
        Assert.Equal("verified", (await r.C.Call("operation.stop", new JsonObject { ["operationId"] = op2.Str("operationId") })).Str("state"));
        var single = await Act.Approve(r.C, await Act.Preview(r.C));
        Assert.Equal(single.Str("state"), (await r.C.Call("operation.stop", new JsonObject { ["operationId"] = single.Str("operationId") })).Str("state"));
        Assert.Contains("operation.stop", AuditEngine.DesktopMethods);
    }

    public static TheoryData<string> DriftFields => ["handlerVersion", "target", "before", "after", "privilege", "flags", "resources"];

    private static void Drift(FakeSettings pc, string field, string id)
    {
        switch (field)
        {
            case "handlerVersion": pc.Version = "2.0.0"; break;
            case "target": pc.Labels[id] = "Renamed"; break;
            case "before": pc.Values[id] = "third"; break;
            case "after": pc.After[id] = "fourth"; break;
            case "privilege": pc.Privilege[id] = "admin"; break;
            case "flags": pc.Flags[id] = [ActionFlags.Restart]; break;
            case "resources": pc.Resources[id] = [@"reg:64:HKCU\OTHER"]; break;
        }
    }

    [Theory]
    [MemberData(nameof(DriftFields))]
    public async Task Drift_BeforeAnythingRan_IsStalePlan_AndMidRun_FailsTheStep(string field)
    {
        await using var r = await Start();
        // Before approval: the whole plan is stale and nothing is written.
        var plan = await Propose(r, S("a"), S("b"));
        Drift(r.Pc, field, "b");
        var stale = await r.C.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(r.C, plan.Str("planId"))));
        Assert.Equal(ErrorCodes.StaleRevision, stale.Code);
        Assert.Equal("stale_plan", stale.ErrorData.Str("reason"));
        Assert.Equal("stale_plan", (await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") }))["ineligible"].Str("code"));
        Assert.Equal(0, r.Pc.Writes);

        // During the run: step 1 is written, then step 2 no longer matches → it fails (stale_plan), the rest are skipped.
        await using var r2 = await Start();
        var plan2 = await Propose(r2, S("a"), S("b"), S("c"));
        r2.Pc.DuringWrite = id => { if (id == "a") Drift(r2.Pc, field, "b"); };
        var (op, _) = await Run(r2, plan2);
        Assert.Equal(["verified", "failed", "skipped"], States(op));
        Assert.Equal("stale_plan", op["steps"]![1]!["error"].Str("code"));
        Assert.Equal("partial", op.Str("state"));
        Assert.Equal(1, r2.Pc.Writes);
    }

    [Fact]
    public async Task ResolvingALaterStepAtApproval_ThatChangesTheFirstStep_WritesNothingForIt()
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a"), S("b"));
        var drifted = 0;
        // The approval recheck resolves a, then b; resolving b changes a (as another handler's read could).
        r.Pc.DuringResolve = id => { if (id == "b" && Interlocked.Exchange(ref drifted, 1) == 0) r.Pc.Values["a"] = "other"; };
        var (op, _) = await Run(r, plan);
        Assert.Equal(["failed", "skipped"], States(op));
        Assert.Equal("stale_plan", op["steps"]![0]!["error"].Str("code"));
        Assert.Equal("failed", op.Str("state"));
        Assert.Equal(0, r.Pc.Writes);
        Assert.Equal("other", r.Pc.Values["a"]);
    }

    // ---------------------------------------------------------------- reconcile

    public static TheoryData<string, string[], string> Crashes => new()
    {
        { "s2-prepared-before", ["verified", "failed", "skipped"], "partial" },
        { "s2-attempted-after", ["verified", "verified", "skipped"], "partial" },
        { "s2-attempted-other", ["verified", "indeterminate", "skipped"], "indeterminate" },
        { "s1-prepared-before", ["failed", "skipped", "skipped"], "failed" },
        { "s1-attempted-after", ["verified", "skipped", "skipped"], "partial" },
        { "none-prepared", ["skipped", "skipped", "skipped"], "failed" },
    };

    [Theory]
    [MemberData(nameof(Crashes))]
    public async Task Restart_ReconcilesEachStep_FromLiveState_WithoutResuming(string crash, string[] expected, string state)
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a"), S("b"), S("c"));
        var (op, _) = await Run(r, plan);
        Assert.Equal(3, r.Pc.Writes);
        await r.Host.StopAsync();

        // Rewrite the journal as if the host died at this transition, and set the live values.
        static (string[], string[]) T(string[] rows, string[] live) => (rows, live);
        var (rows, live) = crash switch
        {
            "s2-prepared-before" => T(["verified", "prepared", "pending"], ["on", "off", "off"]),
            "s2-attempted-after" => T(["verified", "attempted", "pending"], ["on", "on", "off"]),
            "s2-attempted-other" => T(["verified", "attempted", "pending"], ["on", "weird", "off"]),
            "s1-prepared-before" => T(["prepared", "pending", "pending"], ["off", "off", "off"]),
            "s1-attempted-after" => T(["attempted", "pending", "pending"], ["on", "off", "off"]),
            _ => T(["pending", "pending", "pending"], ["off", "off", "off"]),
        };
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(r.Host.DataDir, Store.FileName)};Pooling=False"))
        {
            conn.Open();
            for (var n = 1; n <= 3; n++)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = rows[n - 1] == "verified" ? "SELECT 1" : """
                    UPDATE operation_step_states SET state = $s, receipt = NULL, observed = NULL, undo = '{"available":false,"reason":"not_verified"}' WHERE operation_id = $o AND n = $n
                    """;
                cmd.Parameters.AddWithValue("$s", rows[n - 1]);
                cmd.Parameters.AddWithValue("$o", op.Str("operationId"));
                cmd.Parameters.AddWithValue("$n", n);
                cmd.ExecuteNonQuery();
            }
            using var opCmd = conn.CreateCommand();
            opCmd.CommandText = """
                UPDATE operations SET state = $st, body = json_set(body, '$.state', $st, '$.error', json('null')) WHERE operation_id = $o;
                UPDATE action_plans SET status = 'running', body = json_set(body, '$.status', 'running') WHERE plan_id = $p;
                """;
            opCmd.Parameters.AddWithValue("$st", rows.All(x => x == "pending") ? "prepared" : "attempted");
            opCmd.Parameters.AddWithValue("$o", op.Str("operationId"));
            opCmd.Parameters.AddWithValue("$p", plan.Str("planId"));
            opCmd.ExecuteNonQuery();
        }
        r.Pc.Values["a"] = live[0];
        r.Pc.Values["b"] = live[1];
        r.Pc.Values["c"] = live[2];
        await r.Host.RestartAsync();

        var c2 = await r.Host.ConnectAsync(minor: 3);
        var reconciled = await c2.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal(expected, States(reconciled));
        Assert.Equal(state, reconciled.Str("state"));
        Assert.True((bool)reconciled["reconciled"]!);
        Assert.Equal(state, (await c2.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        if (crash.EndsWith("-after", StringComparison.Ordinal)) Assert.Equal("authorship uncertain after interruption", reconciled.Str("note"));
        Assert.Equal(3, r.Pc.Writes); // never resumed or repeated
        Assert.Equal(live, new[] { r.Pc.Values["a"], r.Pc.Values["b"], r.Pc.Values["c"] });
        SchemaTests.AssertValid("operation", reconciled);
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public async Task Undo_OfSelectedSteps_AndUndoAll_AreNewFixPlans_InHostOrder()
    {
        await using var r = await Start();
        var plan = await Propose(r, S("a"), S("b"), S("c"));
        var (op, _) = await Run(r, plan);
        var opId = op.Str("operationId");

        var one = await r.C.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u1", ["operationId"] = opId, ["steps"] = new JsonArray(2) });
        Assert.Equal("fix", one.Str("kind"));
        Assert.Equal("awaiting_user", one.Str("status"));
        Assert.Equal(opId, one.Str("undoOf"));
        Assert.Null(one["title"]);
        Assert.Null(one["why"]);
        Assert.Equal("user", one["requestedBy"].Str("kind"));
        var undoStep = one["steps"]![0]!;
        Assert.Equal(1, (int)undoStep["n"]!);
        Assert.Equal("Undo: Change a Windows setting", undoStep.Str("label"));
        Assert.Equal("off", undoStep["after"].Str("value"));
        Assert.Equal(new JsonObject { ["operationId"] = opId, ["receiptId"] = op["steps"]![1]!["receipt"].Str("receiptId"), ["n"] = 2 }.ToJsonString(), undoStep["undoOf"]!.ToJsonString());
        Assert.Equal("off", undoStep["parameters"].Str("restore")); // the engine's restore data
        SchemaTests.AssertValid("action-plan", one);
        var (undone, _) = await Run(r, one);
        Assert.Equal("verified", undone.Str("state"));
        Assert.Equal("off", r.Pc.Values["b"]);
        var after = await r.C.Call("operation.get", new JsonObject { ["operationId"] = opId });
        Assert.Equal("undone", after["steps"]![1]!["undo"].Str("reason"));
        Assert.False((bool)after["steps"]![1]!["undo"]!["available"]!);
        Assert.True((bool)after["undo"]!["available"]!);

        // A step that is not verified and undoable → -32013 not_undoable with its n.
        var refused = await r.C.Fails("action.undo", new JsonObject { ["idempotencyKey"] = "u2", ["operationId"] = opId, ["steps"] = new JsonArray(2) });
        Assert.Equal(ErrorCodes.InvalidState, refused.Code);
        Assert.Equal("not_undoable", refused.ErrorData.Str("reason"));
        Assert.Equal(2, (int)refused.ErrorData!["n"]!);

        // Undo all: the remaining undoable steps; one drifted since → that step changed_since, the plan ineligible.
        var all = await r.C.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u3", ["operationId"] = opId });
        Assert.Equal(["a", "c"], all["steps"]!.AsArray().Select(s => s!["target"].Str("id")));
        Assert.Equal("awaiting_user", all.Str("status"));
        r.Pc.Values["c"] = "elsewhere";
        var drifted = await r.C.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u4", ["operationId"] = opId });
        Assert.Equal("changed_since", drifted["ineligible"].Str("code"));
        Assert.Equal(2, (int)drifted["ineligible"]!["steps"]![0]!["n"]!);
        Assert.Equal("Undo: Change a Windows setting", drifted["steps"]![1].Str("label"));

        // steps names a single operation → invalid params; nothing undoable at all → not_undoable.
        var single = await Act.Approve(r.C, await Act.Preview(r.C));
        Assert.Equal(ErrorCodes.InvalidParams, (await r.C.Fails("action.undo", new JsonObject { ["idempotencyKey"] = "u5", ["operationId"] = single.Str("operationId"), ["steps"] = new JsonArray(1) })).Code);
        r.Pc.Fail["a"] = "failed";
        r.Pc.Values["a"] = "off";
        var (failed, _) = await Run(r, await Propose(r, S("a")));
        Assert.Equal("not_undoable", (await r.C.Fails("action.undo", new JsonObject { ["idempotencyKey"] = "u6", ["operationId"] = failed.Str("operationId") })).ErrorData.Str("reason"));
    }

    // ---------------------------------------------------------------- shapes

    /// <summary>contracts/m9-ai-powers.md §4.1, the fix plan's top level as documented (and as the fixture builds it).</summary>
    public static readonly string[] PlanFields =
        ["planId", "kind", "actionId", "label", "handlerVersion", "findingId", "reportId", "title", "why", "flags", "steps", "target", "before", "after",
         "rows", "context", "tradeoffs", "privilege", "undo", "resources", "requestedBy", "createdAt", "expiresAt", "status", "ineligible", "undoOf", "planHash",
         "cardId", "attemptId"]; // m9 §17.3: the card and attempt a plan belongs to (null when none)

    /// <summary>§4.1 operation fields; the host adds its bookkeeping (listed) and nothing else.</summary>
    public static readonly string[] OperationFields = ["operationId", "planId", "actionId", "kind", "state", "receipt", "steps", "undo", "error"];
    public static readonly string[] OperationExtras = ["progress", "findingId", "requestedBy", "createdAt", "updatedAt", "history"];

    [Fact]
    public async Task HostShapes_MatchTheDocumentedFieldSets_AndTheSchemas()
    {
        await using var r = await Start();
        r.Pc.Fail["b"] = "failed";
        var plan = await Propose(r, S("a", why: "w"), S("b"), S("c"));
        var (op, _) = await Run(r, plan);
        var stored = await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") });
        stored.Remove("operation");

        Assert.Equal(PlanFields.Order(), stored.Select(p => p.Key).Order());
        Assert.Equal(PlanFields.Order(), plan.Select(p => p.Key).Order());
        foreach (var nulls in new[] { "actionId", "label", "handlerVersion", "target", "before", "after", "undoOf", "ineligible", "findingId", "reportId" })
            Assert.Null(stored[nulls]);
        foreach (var empty in new[] { "rows", "context", "tradeoffs" }) Assert.Empty(stored[empty]!.AsArray());
        Assert.Equal(["by", "text"], stored["why"]!.AsObject().Select(p => p.Key).Order());
        Assert.Null(stored["undo"]!["conditions"]);
        Assert.All(stored["steps"]!.AsArray(), s => Assert.Equal(StepFields.Order(), s!.AsObject().Select(p => p.Key).Order()));

        Assert.Equal(OperationFields.Concat(OperationExtras).Order(), op.Select(p => p.Key).Order());
        Assert.Null(op["actionId"]);
        Assert.Null(op["receipt"]);
        Assert.Equal(["code", "message", "n"], op["error"]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["n", "receipt", "state", "undo"], op["steps"]![0]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["error", "n", "receipt", "state", "undo"], op["steps"]![1]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["observed", "receiptId", "text", "verifiedAt"], op["steps"]![0]!["receipt"]!.AsObject().Select(p => p.Key).Order());
        Assert.Null(op["steps"]![2]!["receipt"]);

        SchemaTests.AssertValid("action-plan", stored);
        SchemaTests.AssertValid("operation", op);
        SchemaTests.AssertValid("action-plan", await r.C.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") }));
        foreach (var o in (await r.C.Call("operation.list"))["operations"]!.AsArray()) SchemaTests.AssertValid("operation", o);
        foreach (var e in r.C.Events) SchemaTests.AssertValid("event", e);
        Assert.Contains(r.C.Events, e => e.Str("type") == "operation.state" && e["payload"]!["step"] is not null);
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Tests.Scripts;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// contracts/m9-ai-powers.md §17.1: administrator rights as a permission. do.admin in the record (absent = false), grant.admin,
/// available.admin (the --enable-admin-actions flag), and the one enforcement rule for AI-requested plans by their resolved
/// privilege, at insert and again at approve, for single changes, fix plans and elevated scripts; fix steps blocked only by
/// administrator rights are left out. The broker and the script runners are fakes: nothing is elevated, nothing touches this PC.
/// </summary>
public sealed class AdminPermissionTests
{
    private static readonly CallContext Bridge = new("bridge-admin", "agent", Environment.ProcessId);

    private sealed record Rig(TestHost Host, FakeSettings Pc, FakeElevation Broker, Client C, string ReportId) : IAsyncDisposable
    {
        public AgentService Agent => Host.Server.Engine.Agent;
        public ActionService Actions => Host.Server.Engine.Actions;
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>A fake broker that applies each administrator step to the fake PC.</summary>
    private static FakeElevation BrokerFor(FakeSettings pc) => new()
    {
        Answer = req =>
        {
            var list = new List<(int, ActionOutcome)>();
            foreach (var s in req.Steps)
            {
                var id = s.Target["id"]!.GetValue<string>();
                pc.Values[id] = s.After["value"]!.GetValue<string>();
                list.Add((s.N, new ActionOutcome("verified", new JsonObject { ["value"] = pc.Values[id] }, "Done, read back.")));
            }
            return new ElevatedResult(list, "rcpt-00000000000000a1", true);
        },
    };

    /// <summary>Settings a, b (user scope) and c (a machine-scope target: administrator), with an in-app provider and a fake CLI.</summary>
    private static async Task<Rig> Start(bool flag, ScriptOptions? scripts = null, bool firewall = false)
    {
        var pc = new FakeSettings().Set("a", "off").Set("b", "off").Set("c", "off");
        pc.Privilege["c"] = "admin";
        var broker = BrokerFor(pc);
        var o = AgentSetup.Options(null, new FakeProvider("anthropic_api"));
        var agent = new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = new FakeCli(),
        };
        var host = TestHost.StartActions([new FakeActionModule(new FakeSettingHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), elevation: broker, agent: agent, scripts: scripts, enableAdmin: flag, enableFirewall: firewall);
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        return new Rig(host, pc, broker, c, reportId);
    }

    private static JsonObject GrantOf(string changes, bool? admin, bool checks = false, bool scripts = false)
    {
        var g = AgentSetup.Grant(checks ? null : []);
        g["changes"] = changes;
        if (scripts) g["scripts"] = true;
        if (admin is not null) g["admin"] = admin;
        return g;
    }

    private static async Task<string> NewTask(Rig r, JsonObject grant) => (await AgentSetup.StartTask(r.C, r.ReportId, grant: grant)).Str("taskId");

    private static JsonObject Set(string id, string value = "on") => new() { ["settingId"] = id, ["value"] = value };

    private static JsonObject RequestC() => new() { ["actionId"] = "setting.set", ["parameters"] = Set("c") };

    private static JsonObject FixArgs(params string[] ids) => new()
    {
        ["title"] = "Quieter startup", ["why"] = "Settings slow it down.",
        ["steps"] = new JsonArray([.. ids.Select(id => (JsonNode?)new JsonObject { ["actionId"] = "setting.set", ["parameters"] = Set(id) })]),
    };

    private static Task<JsonObject> Plan(Rig r, string planId) => r.C.Call("action.get", new JsonObject { ["planId"] = planId });

    private static JsonObject Perms(string changes = "fix", bool? admin = false)
    {
        var d = new JsonObject { ["checks"] = true, ["changes"] = changes, ["scripts"] = false, ["web"] = false };
        if (admin is not null) d["admin"] = admin;
        return new JsonObject { ["see"] = new JsonObject { ["specs"] = true, ["history"] = true, ["names"] = false }, ["do"] = d };
    }

    private static async Task<JsonObject> Save(Client c, JsonObject permissions)
    {
        var revision = (long)(await c.Call("ai.getPermissions"))["permissions"]!["revision"]!;
        return await c.Call("ai.setPermissions", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["revision"] = revision, ["permissions"] = permissions });
    }

    private static void EditGrant(Rig r, string taskId, Action<JsonObject> edit) => r.Host.Server.Engine.Store.Write(db =>
    {
        var task = AgentService.LoadTask(db, taskId);
        edit(task["grant"]!.AsObject());
        db.Exec("UPDATE agent_tasks SET body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", taskId));
    });

    /// <summary>Approving a plan whose approve recheck fails: -32013 cancelled, the plan cancelled with not_allowed, nothing written.</summary>
    private static async Task AssertCancelledNotAllowed(Rig r, JsonObject plan, string message)
    {
        var refused = await r.C.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(r.C, plan.Str("planId"))));
        Assert.Equal(ErrorCodes.InvalidState, refused.Code);
        Assert.Equal("cancelled", refused.ErrorData.Str("reason"));
        var after = await Plan(r, plan.Str("planId"));
        Assert.Equal("cancelled", after.Str("status"));
        Assert.Equal("not_allowed", after["ineligible"].Str("code"));
        Assert.Equal(message, after["ineligible"].Str("message"));
        SchemaTests.AssertValid("action-plan", after);
        Assert.Equal(0, r.Broker.Calls);
    }

    // ---------------------------------------------------------------- the record, the grant, available.admin

    [Fact]
    public void AbsentAdmin_ReadsFalse_InAStoredRecordAndAStoredOrGivenGrant()
    {
        var stored = Permissions.FromStored(Engine.Json.Parse("""{"version":1,"revision":4,"set":true,"see":{"specs":true,"history":true,"names":false},"do":{"checks":true,"changes":"fix","scripts":false,"web":false}}"""));
        Assert.Equal((4L, true, "fix", false), (stored.Revision, stored.Set, stored.Changes, stored.Admin));
        Assert.False(Permissions.Default.Admin);
        Assert.False((bool)Permissions.Default.ToJson()["do"]!["admin"]!);

        var oldGrant = Grant.FromJson(Engine.Json.Parse("""{"diagnostics":[],"share":{"names":false},"changes":"fix","limits":{},"idleExpirySeconds":900}""")!);
        Assert.False(oldGrant.Admin);
        Assert.False(Grant.Parse(new JsonObject { ["diagnostics"] = new JsonArray() }).Admin);
        Assert.True(Grant.Parse(new JsonObject { ["diagnostics"] = new JsonArray(), ["admin"] = true }).Admin);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<RpcError>(() => Grant.Parse(new JsonObject { ["diagnostics"] = new JsonArray(), ["admin"] = "yes" })).Code);
        Assert.True((bool)(oldGrant with { Admin = true }).ToJson()["admin"]!);

        // Narrowing administrator rights is a narrowing; turning them on only widens.
        var off = Permissions.Default with { Set = true };
        Assert.True((off with { Admin = true }).NarrowedBy(off));
        Assert.True(off.WidenedBy(off with { Admin = true }));
        Assert.False(off.NarrowedBy(off with { Admin = true }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SetPermissions_RequiresAdmin_AvailableAdminIsEitherFlag(bool flag, bool firewall)
    {
        // m9 §17.1 review: available.admin is true when either flag enables an administrator action (the firewall's own flag
        // included), so the row is never disabled while an administrator plan is approvable. Checks follow the admin flag only.
        await using var r = await Start(flag, firewall: firewall);
        var before = await r.C.Call("ai.getPermissions");
        SchemaTests.AssertValid("permissions", before);
        Assert.False((bool)before["permissions"]!["do"]!["admin"]!);     // never pre-selected
        Assert.Equal(flag || firewall, (bool)before["available"]!["admin"]!);
        Assert.Equal(flag, r.Actions.AdminChecksEnabled);

        var on = await Save(r.C, Perms(admin: true));
        SchemaTests.AssertValid("permissions", on);
        Assert.True((bool)on["permissions"]!["do"]!["admin"]!);          // stored whatever the flags say
        Assert.Equal(flag || firewall, (bool)on["available"]!["admin"]!);

        var off = await Save(r.C, Perms(admin: false));
        Assert.False((bool)off["permissions"]!["do"]!["admin"]!);
        // An incoming save must carry do.admin: missing, null or not a boolean → -32602 naming it, and nothing changes.
        var revision = (long)off["permissions"]!["revision"]!;
        foreach (var bad in new Func<JsonObject>[] { () => Perms(admin: null), () => Perms(admin: null).Also(p => p["do"]!["admin"] = null), () => Perms(admin: null).Also(p => p["do"]!["admin"] = 1) })
        {
            var refused = await r.C.Fails("ai.setPermissions", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["revision"] = revision, ["permissions"] = bad() });
            Assert.Equal(ErrorCodes.InvalidParams, refused.Code);
            Assert.Equal("permissions.do.admin", refused.ErrorData.Str("field"));
        }
        Assert.Equal(revision, (long)(await r.C.Call("ai.getPermissions"))["permissions"]!["revision"]!);
    }

    [Fact]
    public async Task TheGrantFollowsTheRecord_WideningUpdatesInApp_NeverAPairedTask_AndNarrowingEndsTasks()
    {
        await using var r = await Start(flag: true);
        await AgentSetup.Choose(r.C, "anthropic_api");
        await Save(r.C, Perms());
        var inApp = (await r.C.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["goal"] = "Help", ["recipientKind"] = "anthropic_api",
            ["scope"] = new JsonObject { ["reportId"] = r.ReportId, ["findingIds"] = null },
        })).Str("taskId");
        Assert.False((bool)(await r.C.Call("agent.get", new JsonObject { ["taskId"] = inApp }))["grant"]!["admin"]!);

        // A paired task's panel grant without admin reads false; it is never widened.
        var pair = await r.Host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge);
        await r.C.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        var paired = (await AgentSetup.StartTask(r.C, r.ReportId, "claude_code_mcp", GrantOf("fix", admin: null), pair.Str("pairingId"))).Str("taskId");
        var pairedTask = await r.C.Call("agent.get", new JsonObject { ["taskId"] = paired });
        Assert.False((bool)pairedTask["grant"]!["admin"]!);
        SchemaTests.AssertValid("agent-task", pairedTask);

        await Save(r.C, Perms(admin: true));
        var widened = await r.C.Call("agent.get", new JsonObject { ["taskId"] = inApp });
        Assert.Equal("active", widened.Str("status"));
        Assert.True((bool)widened["grant"]!["admin"]!);
        SchemaTests.AssertValid("agent-task", widened);
        var ev = await AgentSetup.TaskEvent(r.C, inApp, "agent.task", p => p["grant"]?["admin"]?.GetValue<bool>() == true);
        SchemaTests.AssertValid("event", ev);
        pairedTask = await r.C.Call("agent.get", new JsonObject { ["taskId"] = paired });
        Assert.Equal("active", pairedTask.Str("status"));
        Assert.False((bool)pairedTask["grant"]!["admin"]!);

        // Turning administrator rights off is a narrowing: every active task ends, its waiting plan is cancelled.
        var plan = await r.Agent.CallToolAsync(inApp, "request_action", RequestC(), "in_app", null, default);
        Assert.Equal("awaiting_user", plan.Str("status"));
        await Save(r.C, Perms(admin: false));
        Assert.Equal("stopped", (await r.C.Call("agent.get", new JsonObject { ["taskId"] = inApp })).Str("status"));
        Assert.Equal("stopped", (await r.C.Call("agent.get", new JsonObject { ["taskId"] = paired })).Str("status"));
        Assert.Equal("cancelled", (await Plan(r, plan.Str("planId"))).Str("status"));
    }

    // ---------------------------------------------------------------- the matrix: single changes

    public static TheoryData<bool, bool, string, bool> Matrix()
    {
        var data = new TheoryData<bool, bool, string, bool>();
        foreach (var flag in new[] { false, true })
            foreach (var admin in new[] { false, true })
                foreach (var changes in new[] { "off", "ask", "fix" })
                    foreach (var checks in new[] { false, true })
                        data.Add(flag, admin, changes, checks);
        return data;
    }

    /// <summary>The single-change and fix outcome at insert for every admin × changes × checks × flag combination.</summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Matrix_AtInsert_SingleAndFix(bool flag, bool admin, string changes, bool checks)
    {
        await using var r = await Start(flag);
        var single = await NewTask(r, GrantOf(changes, admin, checks));
        var asked = await r.Agent.CallToolAsync(single, "request_action", RequestC(), "in_app", null, default);
        SchemaTests.AssertValid("tool-result", asked);
        if (changes == "off")
            Assert.Equal("not_allowed", asked["error"].Str("code"));
        else
        {
            var plan = await Plan(r, asked.Str("planId"));
            SchemaTests.AssertValid("action-plan", plan);
            var (status, code) = !flag ? ("ineligible", ActionService.AdminUnavailable) : !admin ? ("ineligible", ActionService.AdminNotAllowed) : ("awaiting_user", null);
            Assert.Equal(status, plan.Str("status"));
            Assert.Equal(code, plan["ineligible"]?["code"]?.GetValue<string>());
            if (code == ActionService.AdminNotAllowed) Assert.Equal("Administrator rights are switched off under Permissions.", plan["ineligible"].Str("message"));
            Assert.Equal(code, asked["summary"]!["ineligible"]?["code"]?.GetValue<string>());
        }

        var fixTask = await NewTask(r, GrantOf(changes, admin, checks));
        var fix = await r.Agent.CallToolAsync(fixTask, "propose_fix", FixArgs("c", "a"), "in_app", null, default);
        SchemaTests.AssertValid("tool-result", fix);
        if (changes != "fix")
        {
            Assert.Equal("not_allowed", fix["error"].Str("code"));
            return;
        }
        var fixPlan = await Plan(r, fix.Str("planId"));
        SchemaTests.AssertValid("action-plan", fixPlan);
        Assert.Equal("awaiting_user", fixPlan.Str("status"));
        if (flag && admin)
        {
            Assert.Null(fixPlan["leftOut"]);
            Assert.Equal(["a", "c"], fixPlan["steps"]!.AsArray().Select(s => s!["parameters"].Str("settingId")));
            Assert.Equal("admin", fixPlan.Str("privilege"));
            Assert.All(fix["steps"]!.AsArray(), s => Assert.Equal("ok", s.Str("status")));
        }
        else
        {
            var code = flag ? ActionService.AdminNotAllowed : ActionService.AdminUnavailable;   // the flag wins
            Assert.Equal(["a"], fixPlan["steps"]!.AsArray().Select(s => s!["parameters"].Str("settingId")));
            Assert.Equal("none", fixPlan.Str("privilege"));
            var left = Assert.Single(fixPlan["leftOut"]!.AsArray())!;
            Assert.Equal(("setting.set", code), (left.Str("actionId"), left.Str("code")));
            Assert.False(string.IsNullOrEmpty(left.Str("label")));
            Assert.Equal(["ok", "left_out"], fix["steps"]!.AsArray().Select(s => s.Str("status")));
            Assert.Equal(code, fix["steps"]![1]!.Str("code"));
            Assert.Null(fix["steps"]![1]!["n"]);
            Assert.Contains("1 step was left out", fix.Str("summary"));
        }
        Assert.Equal(0, r.Pc.Writes);
        Assert.Equal(0, r.Broker.Calls);
    }

    [Fact]
    public async Task UserStartedPreviewsAndUndo_AreUnaffected()
    {
        await using var r = await Start(flag: true);
        await NewTask(r, GrantOf("fix", admin: false)); // an active task without administrator rights changes nothing for the user
        var preview = await r.C.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "setting.set", ["parameters"] = Set("c"),
        });
        Assert.Equal(("awaiting_user", "admin"), (preview.Str("status"), preview.Str("privilege")));
        var op = await Act.Approve(r.C, preview);
        Assert.Equal("verified", op.Str("state"));
        var undo = await r.C.Call("action.undo", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["operationId"] = op.Str("operationId") });
        Assert.Equal("awaiting_user", undo.Str("status"));
        Assert.Equal("verified", (await Act.Approve(r.C, undo)).Str("state"));
        // The user's own fix (no task) with an administrator step is a whole plan, never reduced.
        var own = await r.Actions.CreateFixAsync("t", "w", "Claude", [new FixStepRequest("setting.set", Set("a"), null, null), new FixStepRequest("setting.set", Set("c"), null, null)],
            null, RequestedBy.User);
        Assert.Equal("awaiting_user", own.Str("status"));
        Assert.Null(own["leftOut"]);
        Assert.Equal(2, own["steps"]!.AsArray().Count);
    }

    // ---------------------------------------------------------------- approve recheck

    [Fact]
    public async Task Approve_RechecksAdmin_SingleAndFix_PlansFromBeforeTheUpgradeAreCancelledNotAllowed()
    {
        await using var r = await Start(flag: true);
        // A plan proposed with administrator rights runs when they hold at approve.
        var ok = await NewTask(r, GrantOf("fix", admin: true));
        var okPlan = await Plan(r, (await r.Agent.CallToolAsync(ok, "request_action", RequestC(), "in_app", null, default)).Str("planId"));
        Assert.Equal("verified", (await Act.Approve(r.C, okPlan)).Str("state"));
        Assert.Equal(1, r.Broker.Calls);
        r.Pc.Values["c"] = "off";
        r.Broker.Calls = 0;

        // A pending plan stored before the upgrade: its task's grant has no admin field at all.
        var single = await NewTask(r, GrantOf("fix", admin: true));
        var singlePlan = await Plan(r, (await r.Agent.CallToolAsync(single, "request_action", RequestC(), "in_app", null, default)).Str("planId"));
        EditGrant(r, single, g => g.Remove("admin"));
        await AssertCancelledNotAllowed(r, singlePlan, ActionService.AdminNotAllowedMessage);
        Assert.Equal("off", r.Pc.Values["c"]);

        // A fix whose administrator rights went away between the dialog and the approve.
        var fixTask = await NewTask(r, GrantOf("fix", admin: true));
        var fixPlan = await Plan(r, (await r.Agent.CallToolAsync(fixTask, "propose_fix", FixArgs("a", "c"), "in_app", null, default)).Str("planId"));
        Assert.Equal("admin", fixPlan.Str("privilege"));
        EditGrant(r, fixTask, g => g["admin"] = false);
        await AssertCancelledNotAllowed(r, fixPlan, ActionService.AdminNotAllowedMessage);
        Assert.Equal(0, r.Pc.Writes);

        // A fix without administrator steps is unaffected by the grant's administrator rights.
        var userFixTask = await NewTask(r, GrantOf("fix", admin: false));
        var userFix = await Plan(r, (await r.Agent.CallToolAsync(userFixTask, "propose_fix", FixArgs("a", "b"), "in_app", null, default)).Str("planId"));
        var started = await Act.Approve(r.C, userFix);
        Assert.Equal("fix", started.Str("kind"));
    }

    // ---------------------------------------------------------------- scripts

    private static ScriptOptions ScriptWiring(FakeScriptRunner broker) => new()
    {
        Runner = new FakeScriptRunner(), ElevatedRunner = broker, EnableElevatedScripts = true, Parser = new FakeScriptParser(), Policy = new FakeScriptPolicy(),
        ProgressInterval = TimeSpan.FromMilliseconds(40), Identity = () => new ScrubIdentity(["Sam"], ["DESKTOP-SAM7"], []),
    };

    private static JsonObject ScriptArgs(bool elevated) => ScriptPlanTests.Args(elevated: elevated);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ElevatedScripts_AtInsert_FlagThenPermission(bool flag, bool admin)
    {
        var broker = new FakeScriptRunner { Lines = [(false, "as admin")] };
        await using var r = await Start(flag, ScriptWiring(broker));
        var task = await NewTask(r, GrantOf("fix", admin, scripts: true));
        var res = await r.Agent.CallToolAsync(task, "propose_script", ScriptArgs(elevated: true), "in_app", null, default);
        SchemaTests.AssertValid("tool-result", res);
        var plan = await Plan(r, res.Str("planId"));
        SchemaTests.AssertValid("action-plan", plan);
        var code = !flag ? ActionService.AdminUnavailable : !admin ? ActionService.AdminNotAllowed : null;
        Assert.Equal(code is null ? "awaiting_user" : "ineligible", res.Str("status"));
        if (code is not null)
        {
            Assert.Equal(code, res["guard"]!["refused"]![0].Str("rule"));
            Assert.Equal(code, plan["ineligible"].Str("code"));
            if (code == ActionService.AdminNotAllowed) Assert.Equal(ActionService.AdminNotAllowedMessage, plan["ineligible"].Str("message"));
        }
        // A user-scope script never needs administrator rights.
        var userTask = await NewTask(r, GrantOf("fix", admin, scripts: true));
        Assert.Equal("awaiting_user", (await r.Agent.CallToolAsync(userTask, "propose_script", ScriptArgs(elevated: false), "in_app", null, default)).Str("status"));
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ElevatedScript_Approve_RechecksAdmin_AndAPreUpgradePlanIsCancelledNotAllowed()
    {
        var broker = new FakeScriptRunner { Lines = [(false, "as admin")] };
        await using var r = await Start(flag: true, ScriptWiring(broker));
        var task = await NewTask(r, GrantOf("fix", admin: true, scripts: true));
        var res = await r.Agent.CallToolAsync(task, "propose_script", ScriptArgs(elevated: true), "in_app", null, default);
        Assert.Equal("awaiting_user", res.Str("status"));
        EditGrant(r, task, g => g.Remove("admin"));
        var options = new JsonObject { ["restorePoint"] = false, ["shareOutput"] = true };
        var nonce = (await r.C.Call("action.challenge", new JsonObject { ["planId"] = res.Str("planId"), ["options"] = options.DeepClone() })).Str("nonce");
        var current = await Plan(r, res.Str("planId"));
        var p = new JsonObject { ["planId"] = res.Str("planId"), ["planHash"] = current.Str("planHash"), ["nonce"] = nonce, ["options"] = options };
        var refused = await r.C.Fails("action.approve", p);
        Assert.Equal("cancelled", refused.ErrorData.Str("reason"));
        var after = await Plan(r, res.Str("planId"));
        Assert.Equal(("cancelled", "not_allowed"), (after.Str("status"), after["ineligible"].Str("code")));
        Assert.Equal(ActionService.AdminNotAllowedMessage, after["ineligible"].Str("message"));
        Assert.Equal(0, broker.Calls);
    }

    // ---------------------------------------------------------------- left out

    [Fact]
    public async Task LeftOut_IsBoundByTheHash_AllBlockedIsIneligible_AndAnotherIneligibleStepKeepsTheWholePlanIneligible()
    {
        await using var r = await Start(flag: true);
        var t1 = await NewTask(r, GrantOf("fix", admin: false));
        var reduced = await Plan(r, (await r.Agent.CallToolAsync(t1, "propose_fix", FixArgs("c", "a", "b"), "in_app", null, default)).Str("planId"));
        Assert.Equal(["a", "b"], reduced["steps"]!.AsArray().Select(s => s!["parameters"].Str("settingId")));
        Assert.Equal(ActionService.HashFix(reduced), reduced.Str("planHash"));
        var tampered = (JsonObject)reduced.DeepClone();
        tampered["leftOut"]![0]!["code"] = ActionService.AdminUnavailable;
        Assert.NotEqual(reduced.Str("planHash"), ActionService.HashFix(tampered));
        // The reduced plan approves and runs only its own steps.
        var op = await Act.Approve(r.C, reduced);
        await r.C.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("operationId") == op.Str("operationId") && e["payload"]!["step"] is null
            && e["payload"].Str("state") == "verified");
        Assert.Equal(("on", "on", "off"), (r.Pc.Values["a"], r.Pc.Values["b"], r.Pc.Values["c"]));
        Assert.Equal(0, r.Broker.Calls);

        // Every step blocked by administrator rights: ineligible with that code, nothing left out.
        var t2 = await NewTask(r, GrantOf("fix", admin: false));
        var all = await r.Agent.CallToolAsync(t2, "propose_fix", FixArgs("c"), "in_app", null, default);
        Assert.Equal("ineligible", all.Str("status"));
        Assert.Equal(ActionService.AdminNotAllowed, all["steps"]![0]!.Str("code"));
        var allPlan = await Plan(r, all.Str("planId"));
        Assert.Equal(ActionService.AdminNotAllowed, allPlan["ineligible"].Str("code"));
        Assert.Null(allPlan["leftOut"]);
        SchemaTests.AssertValid("action-plan", allPlan);

        // A step that can't be made for another reason (already set here): the whole plan stays ineligible, nothing left out.
        var t3 = await NewTask(r, GrantOf("fix", admin: false));
        var mixed = await r.Agent.CallToolAsync(t3, "propose_fix", FixArgs("c", "a", "a"), "in_app", null, default);
        Assert.Equal("ineligible", mixed.Str("status"));
        var mixedPlan = await Plan(r, mixed.Str("planId"));
        Assert.Null(mixedPlan["leftOut"]);
        var codes = mixedPlan["ineligible"]!["steps"]!.AsArray().Select(s => s.Str("code")).ToList();
        Assert.Contains("already_set", codes); // a was set on by the run above
        Assert.Contains(ActionService.AdminNotAllowed, codes);
        Assert.DoesNotContain(mixed["steps"]!.AsArray(), s => s.Str("status") == "left_out");
        SchemaTests.AssertValid("tool-result", mixed);
    }
}

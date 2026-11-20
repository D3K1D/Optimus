using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Tests.Scripts;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// contracts/m9-ai-powers.md §17.3 / §17.6 "Fix for me": fix asks and attempts, plans bound to cards, the shared slot rule,
/// action.narrow, reduced plans, note_card outcomes, card.fix and its events, fix.dismiss, the per-ask turn budget. Fake
/// providers, a fake CLI and fake handlers only: nothing here touches this PC or calls an AI.
/// </summary>
public sealed class FixForMeTests : IDisposable
{
    private static readonly CallContext Bridge = new("bridge-fix", "agent", 5151);
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "cli-" + Guid.NewGuid().ToString("N"));

    public FixForMeTests() => Directory.CreateDirectory(_bin);

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Rig(TestHost Host, FakeSettings Pc, Client C, string ReportId, SemaphoreSlim Release, FakeProvider Provider, FakeCli Cli) : IAsyncDisposable
    {
        public AgentService Agent => Host.Server.Engine.Agent;
        public ActionService Actions => Host.Server.Engine.Actions;
        public Store Store => Host.Server.Engine.Store;
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>A provider whose every turn waits for the test to release it, then answers "Done." (a turn the test controls).</summary>
    private static FakeProvider Gated(string kind, SemaphoreSlim release) =>
        new(kind, [.. Enumerable.Range(0, 60).Select(_ => (Func<ProviderRequest, CancellationToken, Task<ProviderTurn>>)(async (_, ct) =>
        {
            await release.WaitAsync(ct);
            return FakeProvider.Say("Done.");
        }))]);

    private static async Task<Rig> Start(FakeProvider? provider = null, string kind = "anthropic_api", bool flag = true)
    {
        var pc = new FakeSettings();
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f", "g", "h" }) pc.Set(id, "off");
        pc.Privilege["c"] = "admin";
        var release = new SemaphoreSlim(0);
        provider ??= Gated(kind, release);
        var cli = new FakeCli();
        var o = AgentSetup.Options(null, provider);
        var agent = new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay, McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        };
        var scripts = new ScriptOptions
        {
            Runner = new FakeScriptRunner(), Parser = new FakeScriptParser(), Policy = new FakeScriptPolicy(), ProgressInterval = TimeSpan.FromMilliseconds(40),
            Identity = () => new ScrubIdentity(["Sam"], ["DESKTOP-SAM7"], []),
        };
        var host = TestHost.StartActions([new FakeActionModule(new FakeSettingHandler(pc))], FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false),
            elevation: new FakeElevation(), agent: agent, scripts: scripts, enableAdmin: flag);
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        if (kind is "anthropic_api" or "openai_api") await AgentSetup.SetKey(c, kind);
        return new Rig(host, pc, c, reportId, release, provider, cli);
    }

    private static JsonObject GrantOf(string changes = "fix", bool admin = false, bool scripts = false)
    {
        var g = AgentSetup.Grant([]);
        g["changes"] = changes;
        g["admin"] = admin;
        if (scripts) g["scripts"] = true;
        return g;
    }

    private static async Task<string> NewTask(Rig r, JsonObject? grant = null, string kind = "anthropic_api") =>
        (await AgentSetup.StartTask(r.C, r.ReportId, kind, grant ?? GrantOf())).Str("taskId");

    /// <summary>The report's open cards with a finding, in deck order (and AI cards added by add_card when more are needed).</summary>
    private static List<(string CardId, string? FindingId)> Cards(Rig r, bool withFinding = true) =>
        r.Store.Read(db => Followups.AgentCards(db, r.ReportId, true, null))!.OfType<JsonObject>()
            .Where(c => c.Str("status") == "open" && (!withFinding || c["findingId"] is not null))
            .Select(c => (c.Str("cardId"), c["findingId"]?.GetValue<string>())).ToList();

    /// <summary>Adds an AI card (no finding) from <paramref name="taskId"/> and returns it.</summary>
    private static async Task<string> AiCard(Rig r, string taskId, string title)
    {
        var added = await r.Agent.CallToolAsync(taskId, "add_card", new JsonObject { ["title"] = title, ["why"] = "Because.", ["category"] = "misc" }, "in_app", null, default);
        return added.Str("cardId");
    }

    private static JsonObject FixArgs(params string[] ids) => new()
    {
        ["title"] = "Quieter startup", ["why"] = "Settings slow it down.",
        ["steps"] = new JsonArray([.. ids.Select(id => (JsonNode?)new JsonObject { ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = id, ["value"] = "on" } })]),
    };

    private static JsonObject WithCard(JsonObject args, string cardId)
    {
        args["cardRef"] = new JsonObject { ["cardId"] = cardId };
        return args;
    }

    private static async Task<string> AskFix(Rig r, string taskId, string cardId, string? text = null, string? revise = null)
    {
        var p = new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["intent"] = "fix", ["cardId"] = cardId };
        if (text is not null) p["text"] = text;
        if (revise is not null) p["revise"] = revise;
        return (await r.C.Call("agent.ask", p)).Str("turnId");
    }

    private static Task<JsonRpcException> AskFixFails(Rig r, string taskId, string? cardId, string? text = null, string? revise = null, bool fix = true)
    {
        var p = new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId };
        if (fix) p["intent"] = "fix";
        if (cardId is not null) p["cardId"] = cardId;
        if (text is not null) p["text"] = text;
        if (revise is not null) p["revise"] = revise;
        return r.C.Fails("agent.ask", p);
    }

    /// <summary>Lets the gated turn answer and waits until it has ended (its agent.message).</summary>
    private static async Task EndTurn(Rig r, string taskId, string turnId)
    {
        r.Release.Release();
        await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("turnId") == turnId);
        await Until(() => r.Store.Read(db => FixAttempts.OfTurn(db, turnId)) is null);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(20);
        }
    }

    private static JsonObject? Fix(Rig r, string cardId) => r.Store.Read(db => FixAttempts.Derive(db, cardId));
    private static string? State(Rig r, string cardId) => Fix(r, cardId)?.Str("state");
    private static Task<JsonObject> Plan(Rig r, string planId) => r.C.Call("action.get", new JsonObject { ["planId"] = planId });

    private static async Task<JsonObject> RunToEnd(Rig r, JsonObject plan)
    {
        var op = await Act.Approve(r.C, plan);
        await r.C.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("operationId") == op.Str("operationId") && e["payload"]!["step"] is null
            && e["payload"].Str("state") is "verified" or "failed" or "indeterminate" or "partial" or "completed");
        return await r.C.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
    }

    private static string CardStatus(Rig r, string cardId) =>
        r.Store.Read(db => Followups.AgentCards(db, r.ReportId, true, null))!.OfType<JsonObject>().Single(c => c.Str("cardId") == cardId).Str("status");

    // ---------------------------------------------------------------- the fix ask, end to end

    [Fact]
    public async Task FixAsk_StartsAnAttempt_WritesTheTurn_BindsThePlan_AndAVerifiedRunClosesTheCard()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var front = (await r.C.Call("followup.get", new JsonObject { ["reportId"] = r.ReportId }))["card"]!;
        var cardId = front.Str("id");
        Assert.NotNull(front["findingId"]);

        var turnId = await AskFix(r, taskId, cardId);
        var question = await AgentSetup.TaskEvent(r.C, taskId, "agent.question", p => p.Str("turnId") == turnId);
        SchemaTests.AssertValid("event", question);
        Assert.Equal(("fix", ""), (question["payload"].Str("intent"), question["payload"].Str("text")));
        var working = await r.C.WaitForEvent(e => e.Str("type") == "card.fix" && e["payload"].Str("cardId") == cardId && e["payload"]?["fix"]?.Str("state") == "working");
        SchemaTests.AssertValid("event", working);
        Assert.Equal(question["payload"].Str("attemptId"), working["payload"]!["fix"].Str("attemptId"));
        Assert.Equal("Claude", working["payload"]!["fix"].Str("by"));

        // The host wrote the turn: the card, the fix request, the permission line; no "User:" line without words.
        await Until(() => !r.Provider.Requests.IsEmpty);
        var prompt = ((UserText)r.Provider.Requests.First().Messages.OfType<UserText>().Last()).Text;
        Assert.Contains($"The user asks you to fix the issue on card {cardId}.", prompt);
        Assert.Contains("note_card on this card with an outcome", prompt);
        Assert.Contains($"The user is looking at card {cardId}", prompt);
        Assert.DoesNotContain("\nUser:", prompt);
        Assert.StartsWith("[prompt-v11]", r.Provider.Requests.First().System);

        // A plan proposed in the attempt's turn belongs to the card and the attempt (no cardRef needed).
        var proposed = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("a", "b"), "in_app", turnId, default);
        Assert.Equal("awaiting_user", proposed.Str("status"));
        var plan = await Plan(r, proposed.Str("planId"));
        SchemaTests.AssertValid("action-plan", plan);
        Assert.Equal((cardId, question["payload"].Str("attemptId")), (plan.Str("cardId"), plan.Str("attemptId")));
        Assert.Equal("working", State(r, cardId)); // the turn still runs
        await EndTurn(r, taskId, turnId);

        var view = await r.C.Call("followup.get", new JsonObject { ["reportId"] = r.ReportId });
        SchemaTests.AssertValid("followup", view);
        Assert.Equal("plan_ready", view["card"]!["fix"].Str("state"));
        Assert.Equal(plan.Str("planId"), view["card"]!["fix"].Str("planId"));
        SchemaTests.AssertValidRef("fix.schema.json", view["card"]!["fix"]);

        var op = await RunToEnd(r, plan);
        Assert.Equal("verified", op.Str("state"));
        var fixedNow = Fix(r, cardId)!;
        Assert.Equal(("fixed", op.Str("operationId")), (fixedNow.Str("state"), fixedNow.Str("operationId")));
        Assert.Equal("done", CardStatus(r, cardId)); // closed by plan.cardId
        foreach (var e in r.C.Events.Where(e => e.Str("type") == "card.fix")) SchemaTests.AssertValid("event", e);
    }

    // ---------------------------------------------------------------- refusals and revise

    [Fact]
    public async Task FixAsk_Refusals_ChangeNothing_AndReviseCancelsOnlyAfterAdmission()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var cards = Cards(r);
        var (cardA, _) = cards[0];
        var (cardB, _) = cards[1];

        Assert.Equal(ErrorCodes.InvalidParams, (await AskFixFails(r, taskId, null)).Code);                      // intent fix needs a card
        Assert.Equal(ErrorCodes.InvalidParams, (await AskFixFails(r, taskId, cardA, fix: false)).Code);         // text is required otherwise
        Assert.Equal(ErrorCodes.InvalidParams, (await AskFixFails(r, taskId, cardA, text: "x", revise: "plan-0000000000000000", fix: false)).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await AskFixFails(r, taskId, "card-0000000000000000")).Code);  // not in the task's report
        Assert.Empty(r.Store.Read(db => db.Query("SELECT attempt_id FROM fix_attempts", x => x.GetString(0))));

        var turn = await AskFix(r, taskId, cardA);
        var busy = await AskFixFails(r, taskId, cardB);
        Assert.Equal(("busy", ErrorCodes.InvalidState), (busy.ErrorData.Str("reason"), busy.Code));
        Assert.Null(Fix(r, cardB));
        var first = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("a"), "in_app", turn, default);
        await EndTurn(r, taskId, turn);
        Assert.Equal("plan_ready", State(r, cardA));

        // Something is waiting on the card: a new fix without revise is refused (fix_active).
        Assert.Equal("fix_active", (await AskFixFails(r, taskId, cardA)).ErrorData.Str("reason"));
        // revise must be the awaiting plan of this card's current attempt in this task.
        var loose = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("d"), "in_app", null, default); // a plan with no card
        Assert.Null((await Plan(r, loose.Str("planId")))["cardId"]);
        foreach (var wrong in new[] { "plan-0000000000000000", loose.Str("planId") })
        {
            var refused = await AskFixFails(r, taskId, cardA, text: "fewer", revise: wrong);
            Assert.Equal(ErrorCodes.InvalidParams, refused.Code);
        }
        Assert.Equal(ErrorCodes.InvalidParams, (await AskFixFails(r, taskId, cardB, text: "x", revise: first.Str("planId"))).Code); // another card
        Assert.Equal("awaiting_user", (await Plan(r, first.Str("planId"))).Str("status"));

        // A refused (busy) ask cancels nothing.
        await AgentSetup.Ask(r.C, taskId, "Something else?");
        var busyRevise = await AskFixFails(r, taskId, cardA, text: "fewer", revise: first.Str("planId"));
        Assert.Equal("busy", busyRevise.ErrorData.Str("reason"));
        Assert.Equal("awaiting_user", (await Plan(r, first.Str("planId"))).Str("status"));
        r.Release.Release();
        await Until(() => r.C.Events.Count(e => e.Str("type") == "agent.message" && e.Str("stream") == "task:" + taskId) >= 2);

        // Admitted: the plan is cancelled (revised) in the ask's transaction, a new attempt starts, the AI is told the old steps and the words.
        var oldAttempt = Fix(r, cardA)!.Str("attemptId");
        var requestsBefore = r.Provider.Requests.Count;
        var revised = await AskFix(r, taskId, cardA, text: "Leave the first one alone.", revise: first.Str("planId"));
        var cancelled = await Plan(r, first.Str("planId"));
        Assert.Equal(("cancelled", "revised"), (cancelled.Str("status"), cancelled["ineligible"].Str("code")));
        SchemaTests.AssertValid("action-plan", cancelled);
        Assert.NotEqual(oldAttempt, Fix(r, cardA)!.Str("attemptId"));
        Assert.Equal("working", State(r, cardA));
        await Until(() => r.Provider.Requests.Count > requestsBefore);
        var prompt = ((UserText)r.Provider.Requests.Last().Messages.OfType<UserText>().Last()).Text;
        Assert.Contains("The user asked to change your plan \"Quieter startup\" with steps 1. Change a Windows setting", prompt);
        Assert.Contains("User: Leave the first one alone.", prompt);
        await EndTurn(r, taskId, revised);
        Assert.Equal("no_fix", State(r, cardA)); // the turn ended with neither a plan nor an outcome
    }

    // ---------------------------------------------------------------- the turn budget (§17.6)

    [Fact]
    public async Task TurnBudget_IsPerAsk_8And12_WithA64TurnTaskBound()
    {
        var forever = new FakeProvider("anthropic_api", [.. Enumerable.Range(0, 200).Select(_ => FakeProvider.Step(FakeProvider.Call(("list_capabilities", new JsonObject()))))]);
        await using var r = await Start(forever);
        var taskId = await NewTask(r);
        await AgentSetup.Ask(r.C, taskId, "Look at everything.");
        var normal = await AgentSetup.TaskEvent(r.C, taskId, "agent.message");
        Assert.Equal(AgentService.AskBudgetUsed, normal["payload"].Str("text"));
        Assert.Equal(8, forever.Requests.Count);
        Assert.Equal("active", (await r.C.Call("agent.get", new JsonObject { ["taskId"] = taskId })).Str("status"));

        var (cardId, _) = Cards(r)[0];
        var turn = await AskFix(r, taskId, cardId);
        await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("turnId") == turn);
        Assert.Equal(20, forever.Requests.Count); // 12 more
        await Until(() => State(r, cardId) == "no_fix");

        // The per-task runaway bound: 64 round trips in all → exhausted, as before.
        r.Store.Write(db =>
        {
            var task = AgentService.LoadTask(db, taskId);
            task["used"]!["turns"] = 63;
            db.Exec("UPDATE agent_tasks SET body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", taskId));
        });
        await AgentSetup.Ask(r.C, taskId, "More?");
        await Until(() => r.C.Events.Any(e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.task" && e["payload"].Str("status") == "exhausted"));
        Assert.Equal(21, forever.Requests.Count);
        Assert.Contains("used all of its turns", (await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("text").Contains("its turns")))["payload"].Str("text"));
    }

    // ---------------------------------------------------------------- the card binding rechecked at insert (P2 review item 1)

    /// <summary>Holds the resolve of setting <paramref name="id"/> until the returned gate is set; entered is set when it is reached.</summary>
    private static (ManualResetEventSlim Entered, ManualResetEventSlim Gate) HoldResolve(Rig r, string id)
    {
        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        r.Pc.DuringResolve = s => { if (s == id) { entered.Set(); gate.Wait(TimeSpan.FromSeconds(20)); } };
        return (entered, gate);
    }

    [Fact]
    public async Task ALatePairedCardProposal_DoesNotReplaceTheFixTheUserStartedMeanwhile()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (a, _) = Cards(r)[0];
        var pair = await r.Host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge);
        await r.C.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        await AgentSetup.StartTask(r.C, r.ReportId, "claude_code_mcp", GrantOf(), pair.Str("pairingId"));
        var (entered, gate) = HoldResolve(r, "e");
        var late = Task.Run(async () => (JsonObject)(await r.Host.Server.Engine.InvokeAsync("tool.call",
            new JsonObject { ["name"] = "propose_fix", ["arguments"] = WithCard(FixArgs("e"), a) }, Bridge))!);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var turn = await AskFix(r, taskId, a); // the user's Fix for me on the same card, while the window's plan resolves
        var mine = r.Store.Read(db => FixAttempts.Current(db, a))!.Id;
        gate.Set();
        var result = await late;
        Assert.Equal(("not_allowed", ActionService.StaleCard), (result["error"].Str("code"), result["error"].Str("message")));
        Assert.Equal((mine, "working"), (r.Store.Read(db => FixAttempts.Current(db, a))!.Id, State(r, a)));
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM action_plans WHERE card_id = $c", ("$c", a))!));
        // The user's own turn still binds its plan to its attempt.
        r.Pc.DuringResolve = null;
        var own = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("a"), "in_app", turn, default);
        Assert.Equal(mine, (await Plan(r, own.Str("planId"))).Str("attemptId"));
        await EndTurn(r, taskId, turn);
    }

    [Fact]
    public async Task AChatTurnsCardProposal_ResolvingAfterTheTurnWasCancelled_StartsNoAttempt()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (a, _) = Cards(r)[0];
        var turn = (await AgentSetup.Ask(r.C, taskId, "Look at this card?")).Str("turnId");
        var (entered, gate) = HoldResolve(r, "e");
        var late = Task.Run(() => r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("e"), a), "in_app", turn, default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True((await r.C.Call("agent.cancel", new JsonObject { ["taskId"] = taskId }))["cancelled"]!.GetValue<bool>());
        gate.Set();
        var result = await late;
        Assert.Equal(("not_allowed", ActionService.StaleCard), (result["error"].Str("code"), result["error"].Str("message")));
        Assert.Null(r.Store.Read(db => FixAttempts.Current(db, a))); // no working attempt left behind
        Assert.Null(State(r, a));
    }

    // ---------------------------------------------------------------- restart

    [Fact]
    public async Task AWorkingAttemptFoundAtStartup_BecomesNoFix()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (cardId, _) = Cards(r)[0];
        r.Store.Write(db => FixAttempts.Start(db, taskId, r.ReportId, cardId, "turn-0000deadbeef", "Claude"));
        Assert.Equal("working", State(r, cardId));
        await r.Host.RestartAsync();
        Assert.Equal("no_fix", State(r, cardId));
    }

    // ---------------------------------------------------------------- card.fix precedence

    [Fact]
    public async Task CardFix_EveryState_InItsPrecedence_AndAnOpinionNeverWinsOverAHostState()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (cardId, findingId) = Cards(r)[0];
        var by = new RequestedBy("in_app", taskId, "call-000001");
        void SetStatus(string planId, string status, JsonObject? ineligible = null, bool reduced = false) => r.Store.Write(db =>
        {
            var body = ActionService.LoadPlan(db, planId);
            body["status"] = status;
            if (ineligible is not null) body["ineligible"] = ineligible;
            if (reduced) body["narrowedFrom"] = "plan-0000000000000000";
            db.Exec("UPDATE action_plans SET status = $s, body = $b WHERE plan_id = $p", ("$s", status), ("$b", Engine.Json.Write(body)), ("$p", planId));
        });
        async Task<string> NewPlan() =>
            (await r.Actions.CreateFixAsync("T", "W", "Claude", [new FixStepRequest("setting.set", new JsonObject { ["settingId"] = "a", ["value"] = "on" }, null, null)], null, by,
                new CardBinding(cardId, r.ReportId, null, "Claude", ExpectedAttempt: r.Store.Read(db => FixAttempts.Current(db, cardId)?.Id)))).Text("planId");

        Assert.Null(Fix(r, cardId)); // no attempt: normal
        var cases = new (string Status, bool Reduced, string? Expected)[]
        {
            ("awaiting_user", false, "plan_ready"), ("approved", false, "running"), ("running", false, "running"), ("verified", false, "fixed"),
            ("verified", true, "done_reduced"), ("partial", false, "partly_fixed"), ("completed", false, "finished"), ("failed", false, "failed"),
            ("stopped", false, "stopped"), ("timed_out", false, "stopped"), ("not_run", false, "failed"), ("indeterminate", false, "unclear"),
            ("declined", false, null), ("expired", false, null), ("cancelled", false, null),
        };
        foreach (var (status, reduced, expected) in cases)
        {
            var planId = await NewPlan();
            if (status != "awaiting_user") SetStatus(planId, status, reduced: reduced);
            var fix = Fix(r, cardId);
            Assert.Equal(expected, fix?.Str("state"));
            if (fix is not null)
            {
                SchemaTests.AssertValidRef("fix.schema.json", fix);
                Assert.Equal(planId, fix.Str("planId"));
            }
            if (status is "awaiting_user" or "approved" or "running") SetStatus(planId, "declined"); // a fix still in progress blocks a new binding
        }
        // blocked carries the ineligible plan's code.
        var blockedPlan = await NewPlan();
        SetStatus(blockedPlan, "ineligible", new JsonObject { ["code"] = "not_supported", ["message"] = "x" });
        Assert.Equal(("blocked", "not_supported"), (State(r, cardId), Fix(r, cardId)!.Str("code")));
        // An awaiting plan whose time passed reads as expired: normal.
        var stale = await NewPlan();
        r.Store.Write(db =>
        {
            var body = ActionService.LoadPlan(db, stale);
            body["expiresAt"] = "2020-01-01T00:00:00.000Z";
            db.Exec("UPDATE action_plans SET body = $b WHERE plan_id = $p", ("$b", Engine.Json.Write(body)), ("$p", stale));
        });
        Assert.Null(Fix(r, cardId));

        // working beats everything; then the opinion only where no host state applies; no_fix without either.
        var turnAttempt = r.Store.Write(db => FixAttempts.Start(db, taskId, r.ReportId, cardId, "turn-000000000aaa", "Claude"));
        Assert.Equal("working", State(r, cardId));
        r.Store.Write(db => FixAttempts.SetOpinion(db, FixAttempts.Get(db, turnAttempt)!, "needs_user", "Claude"));
        Assert.Equal("working", State(r, cardId));
        var ready = (await r.Actions.CreateFixAsync("T", "W", "Claude", [new FixStepRequest("setting.set", new JsonObject { ["settingId"] = "b", ["value"] = "on" }, null, null)],
            null, by, new CardBinding(cardId, r.ReportId, turnAttempt, "Claude"))).Text("planId");
        r.Store.Write(db => FixAttempts.EndTurn(db, "turn-000000000aaa"));
        Assert.Equal("plan_ready", State(r, cardId)); // a host state wins over the opinion, which stays attributed
        Assert.Equal("needs_user", Fix(r, cardId)!["opinion"].Str("outcome"));
        SetStatus(ready, "declined");
        var opinionAttempt = r.Store.Write(db => FixAttempts.Start(db, taskId, r.ReportId, cardId, "turn-000000000bbb", "Claude"));
        r.Store.Write(db => FixAttempts.SetOpinion(db, FixAttempts.Get(db, opinionAttempt)!, "needs_user", "Claude"));
        r.Store.Write(db => FixAttempts.EndTurn(db, "turn-000000000bbb"));
        Assert.Equal("needs_you", State(r, cardId));
        Assert.Equal("needs_user", Fix(r, cardId)!["opinion"].Str("outcome"));
        SchemaTests.AssertValidRef("fix.schema.json", Fix(r, cardId));
        // A plan can no longer bind to an attempt whose turn ended (P2 review item 1).
        var late = await Assert.ThrowsAsync<RpcError>(() => r.Actions.CreateFixAsync("T", "W", "Claude", [new FixStepRequest("setting.set", new JsonObject { ["settingId"] = "b", ["value"] = "on" }, null, null)],
            null, by, new CardBinding(cardId, r.ReportId, opinionAttempt, "Claude")));
        Assert.Equal(ActionService.StaleCard, late.Message);
        var nothing = r.Store.Write(db => FixAttempts.Start(db, taskId, r.ReportId, cardId, null, "Claude"));
        Assert.Null(Fix(r, cardId)!["opinion"]); // a new attempt clears the opinion
        Assert.Equal("no_fix", State(r, cardId));
        r.Store.Write(db => FixAttempts.SetOpinion(db, FixAttempts.Get(db, nothing)!, "nothing_to_fix", "Claude"));
        Assert.Equal("nothing_to_fix", State(r, cardId));
        // An opinion never changes the card's status.
        Assert.Equal("open", CardStatus(r, cardId));
        Assert.NotNull(findingId);
    }

    // ---------------------------------------------------------------- note_card outcome

    [Fact]
    public async Task NoteCardOutcome_IsAnAttributedOpinion_NeverTheCardsStatus_AndClearedByANewAttempt()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (cardId, _) = Cards(r)[0];
        var revision = (long)(await r.C.Call("followup.get", new JsonObject { ["reportId"] = r.ReportId }))["revision"]!;
        var turn = await AskFix(r, taskId, cardId);
        var bad = await r.Agent.CallToolAsync(taskId, "note_card", new JsonObject { ["cardId"] = cardId, ["text"] = "x", ["outcome"] = "fixed" }, "in_app", turn, default);
        Assert.Equal("invalid_arguments", bad["error"].Str("code")); // the AI can never set "fixed"
        var noted = await r.Agent.CallToolAsync(taskId, "note_card", new JsonObject { ["cardId"] = cardId, ["text"] = "Turn it on in the BIOS.", ["outcome"] = "needs_user" }, "in_app", turn, default);
        Assert.Equal("noted", noted.Str("status"));
        SchemaTests.AssertValid("tool-result", noted);
        await EndTurn(r, taskId, turn);
        var fix = Fix(r, cardId)!;
        Assert.Equal("needs_you", fix.Str("state"));
        Assert.Equal(("needs_user", "Claude", taskId), (fix["opinion"].Str("outcome"), fix["opinion"].Str("by"), fix["opinion"].Str("taskId")));
        Assert.Equal("open", CardStatus(r, cardId));
        var view = await r.C.Call("followup.get", new JsonObject { ["reportId"] = r.ReportId });
        Assert.Equal(revision + 1, (long)view["revision"]!); // the note's own write only; the opinion lives apart from the deck
        Assert.Empty(view["decisions"]!.AsArray());

        var again = await AskFix(r, taskId, cardId);
        Assert.Null(Fix(r, cardId)!["opinion"]);
        await EndTurn(r, taskId, again);
        // Outside a fix turn (a paired window's way): an opinion starts an ended attempt of its own.
        var (other, _) = Cards(r)[1];
        await r.Agent.CallToolAsync(taskId, "note_card", new JsonObject { ["cardId"] = other, ["text"] = "Nothing to do.", ["outcome"] = "nothing_to_fix" }, "in_app", null, default);
        Assert.Equal("nothing_to_fix", State(r, other));
    }

    // ---------------------------------------------------------------- findingRef / cardRef

    [Fact]
    public async Task InAFixTurn_CardRefAndFindingRefMustMatchTheCard_OutsideOne_ACardRefStartsTheAttempt()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var cards = Cards(r);
        var (cardA, findingA) = cards[0];
        var (cardB, findingB) = cards[1];
        var turn = await AskFix(r, taskId, cardA);
        Assert.Equal("invalid_arguments", (await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("a"), cardB), "in_app", turn, default))["error"].Str("code"));
        var wrongFinding = FixArgs("a");
        wrongFinding["findingRef"] = findingB;
        Assert.Equal("invalid_arguments", (await r.Agent.CallToolAsync(taskId, "propose_fix", wrongFinding, "in_app", turn, default))["error"].Str("code"));
        Assert.Equal("invalid_arguments", (await r.Agent.CallToolAsync(taskId, "request_action",
            new JsonObject { ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "a", ["value"] = "on" }, ["cardRef"] = new JsonObject { ["cardId"] = cardB } },
            "in_app", turn, default))["error"].Str("code"));
        var right = FixArgs("a");
        right["findingRef"] = findingA;
        var ok = await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(right, cardA), "in_app", turn, default);
        Assert.Equal("awaiting_user", ok.Str("status"));
        await EndTurn(r, taskId, turn);

        // Outside a fix turn: a cardRef to a card that is not open is not_allowed; to an open one, the insert starts an ended attempt.
        await r.C.Call("finding.setDisposition", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["reportId"] = r.ReportId, ["findingId"] = findingB, ["status"] = "ignored", ["scope"] = "finding" });
        Assert.Equal("not_allowed", (await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("b"), cardB), "in_app", null, default))["error"].Str("code"));
        var ai = await AiCard(r, taskId, "Check the fan");
        var bound = await r.Agent.CallToolAsync(taskId, "request_action",
            new JsonObject { ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "e", ["value"] = "on" }, ["cardRef"] = new JsonObject { ["cardId"] = ai } }, "in_app", null, default);
        var plan = await Plan(r, bound.Str("planId"));
        Assert.Equal(ai, plan.Str("cardId"));
        Assert.Equal("plan_ready", State(r, ai));
        // A single change verified on an AI card (no finding) closes that card by plan.cardId.
        Assert.Equal("verified", (await RunToEnd(r, plan)).Str("state"));
        Assert.Equal(("fixed", "done"), (State(r, ai), CardStatus(r, ai)));
    }

    // ---------------------------------------------------------------- the slot rule

    [Fact]
    public async Task Slots_OnePerCardAcrossTasks_ThreePerTask_OneWithoutACard_ApprovalWithOthersPending()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var cards = Cards(r);
        var (a, _) = cards[0];
        var (b, _) = cards[1];
        var c = await AiCard(r, taskId, "Third card");

        var onA = await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("a"), a), "in_app", null, default);
        Assert.Equal("awaiting_user", onA.Str("status"));
        var noCard = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("b"), "in_app", null, default);
        Assert.Equal("awaiting_user", noCard.Str("status"));
        var secondNoCard = await r.Agent.CallToolAsync(taskId, "request_action", new JsonObject { ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "d", ["value"] = "on" } }, "in_app", null, default);
        Assert.Equal(("not_allowed", ActionService.NoCardSlotTaken), (secondNoCard["error"].Str("code"), secondNoCard["error"].Str("message")));
        var sameCard = await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("d"), a), "in_app", null, default);
        Assert.Equal(("not_allowed", ActionService.CardSlotTaken), (sameCard["error"].Str("code"), sameCard["error"].Str("message")));
        var onB = await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("d"), b), "in_app", null, default);
        Assert.Equal("awaiting_user", onB.Str("status"));
        var fourth = await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("e"), c), "in_app", null, default);
        Assert.Equal(("not_allowed", ActionService.TaskSlotsFull), (fourth["error"].Str("code"), fourth["error"].Str("message")));

        // The same card from another task (a paired window): one pending plan per card across tasks.
        var pair = await r.Host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge);
        await r.C.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        await AgentSetup.StartTask(r.C, r.ReportId, "claude_code_mcp", GrantOf(), pair.Str("pairingId"));
        JsonObject Paired(string tool, JsonObject args) => (JsonObject)r.Host.Server.Engine.InvokeAsync("tool.call", new JsonObject { ["name"] = tool, ["arguments"] = args }, Bridge).GetAwaiter().GetResult()!;
        var fromWindow = Paired("propose_fix", WithCard(FixArgs("e"), a));
        Assert.Equal(("not_allowed", ActionService.CardSlotTaken), (fromWindow["error"].Str("code"), fromWindow["error"].Str("message")));
        // The paired window's cardRef: the host starts the attempt; the plan belongs to the card.
        var windowPlan = Paired("propose_fix", WithCard(FixArgs("e"), c));
        Assert.Equal("awaiting_user", windowPlan.Str("status"));
        Assert.Equal(c, (await Plan(r, windowPlan.Str("planId"))).Str("cardId"));
        Assert.Equal(("plan_ready", "ended"), (State(r, c), r.Store.Read(db => FixAttempts.Current(db, c))!.State));

        // Approving one plan while others wait succeeds (the approved plan is not counted against itself).
        var approved = await RunToEnd(r, await Plan(r, onA.Str("planId")));
        Assert.Equal("verified", approved.Str("state"));
        Assert.Equal("awaiting_user", (await Plan(r, onB.Str("planId"))).Str("status"));
        Assert.Equal("awaiting_user", (await Plan(r, noCard.Str("planId"))).Str("status"));
    }

    [Fact]
    public async Task Slots_Scripts_TheirOwnSlotReturnsTheSamePlanUncharged_AnyOtherIsNotAllowed()
    {
        await using var r = await Start();
        var taskId = await NewTask(r, GrantOf(scripts: true));
        var (a, _) = Cards(r)[0];
        int Charged() => r.Store.Read(db => AgentService.ScriptProposals(AgentService.LoadTask(db, taskId)));

        var first = await r.Agent.CallToolAsync(taskId, "propose_script", WithCard(ScriptPlanTests.Args(), a), "in_app", null, default);
        Assert.Equal("awaiting_user", first.Str("status"));
        Assert.Equal(1, Charged());
        var again = await r.Agent.CallToolAsync(taskId, "propose_script", WithCard(ScriptPlanTests.Args(), a), "in_app", null, default);
        Assert.Equal((first.Str("planId"), 1), (again.Str("planId"), Charged())); // its slot: the same plan, uncharged
        Assert.Equal(ActionService.CardSlotTaken, (await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("a"), a), "in_app", null, default))["error"].Str("message"));

        var noCardFix = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("a"), "in_app", null, default);
        Assert.Equal("awaiting_user", noCardFix.Str("status"));
        var scriptNoCard = await r.Agent.CallToolAsync(taskId, "propose_script", ScriptPlanTests.Args(), "in_app", null, default);
        Assert.Equal(("not_allowed", ActionService.NoCardSlotTaken), (scriptNoCard["error"].Str("code"), scriptNoCard["error"].Str("message")));
        Assert.Equal(1, Charged()); // refused before anything was charged
    }

    [Fact]
    public async Task Scripts_BudgetUsedUp_StillReturnsThePendingScriptPlan_ButRefusesANewOne()
    {
        // P2 review item 4: with limits.scripts = 1 used, the slot's own pending script comes back (uncharged); only a new proposal is refused.
        await using var r = await Start();
        var grant = GrantOf(scripts: true);
        grant["limits"] = new JsonObject { ["scripts"] = 1 };
        var taskId = await NewTask(r, grant);
        var (a, _) = Cards(r)[0];
        int Charged() => r.Store.Read(db => AgentService.ScriptProposals(AgentService.LoadTask(db, taskId)));

        var first = await r.Agent.CallToolAsync(taskId, "propose_script", WithCard(ScriptPlanTests.Args(), a), "in_app", null, default);
        Assert.Equal(("awaiting_user", 1), (first.Str("status"), Charged()));
        var again = await r.Agent.CallToolAsync(taskId, "propose_script", WithCard(ScriptPlanTests.Args(), a), "in_app", null, default);
        Assert.Null(again["error"]);
        Assert.Equal((first.Str("planId"), "awaiting_user", 1), (again.Str("planId"), again.Str("status"), Charged()));

        var other = await r.Agent.CallToolAsync(taskId, "propose_script", ScriptPlanTests.Args(), "in_app", null, default);
        Assert.Equal(("not_allowed", "This task has used all of its script proposals."), (other["error"].Str("code"), other["error"].Str("message")));
        Assert.Equal(1, Charged());
    }

    // ---------------------------------------------------------------- action.narrow

    private static Task<JsonObject> Narrow(Rig r, string planId, params int[] keep) =>
        r.C.Call("action.narrow", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["planId"] = planId, ["keep"] = new JsonArray([.. keep.Select(n => (JsonNode?)n)]) });

    private static Task<JsonRpcException> NarrowFails(Rig r, string planId, JsonNode? keep) =>
        r.C.Fails("action.narrow", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["planId"] = planId, ["keep"] = keep });

    private static Task<JsonObject> UserFix(Rig r, params string[] ids) =>
        r.Actions.CreateFixAsync("Quieter startup", "Three settings slow it down.", "Claude",
            [.. ids.Select(id => new FixStepRequest("setting.set", new JsonObject { ["settingId"] = id, ["value"] = "on" }, "Step " + id, null))], null, RequestedBy.User);

    [Fact]
    public async Task Narrow_KeepsStepsExactly_InOrder_WithTheParentsExpiry_AndCancelsTheParent()
    {
        await using var r = await Start();
        var parent = await UserFix(r, "a", "b", "d");
        var key = Guid.NewGuid().ToString();
        var p = new JsonObject { ["idempotencyKey"] = key, ["planId"] = parent.Str("planId"), ["keep"] = new JsonArray(1, 3) };
        var child = await r.C.Call("action.narrow", p);
        SchemaTests.AssertValid("action-plan", child);
        SchemaTests.AssertValidRef("fix.schema.json#/$defs/narrowParams", p);
        Assert.Equal("awaiting_user", child.Str("status"));
        Assert.Equal(parent.Str("planId"), child.Str("narrowedFrom"));
        Assert.Equal(parent.Str("expiresAt"), child.Str("expiresAt"));
        Assert.Equal([1, 2], child["steps"]!.AsArray().Select(s => (int)s!["n"]!));
        var parentSteps = parent["steps"]!.AsArray();
        Assert.Equal([parentSteps[0].Str("contentHash"), parentSteps[2].Str("contentHash")], child["steps"]!.AsArray().Select(s => s.Str("contentHash")));
        Assert.Equal(["Step a", "Step d"], child["steps"]!.AsArray().Select(s => s.Str("why")));
        Assert.Equal(["Change a Windows setting"], child["removed"]!.AsArray().Select(x => x.Str("label")));
        Assert.Equal(ActionService.HashFix(child), child.Str("planHash"));
        Assert.NotEqual(parent.Str("planHash"), child.Str("planHash"));
        var cancelled = await Plan(r, parent.Str("planId"));
        Assert.Equal(("cancelled", "replaced", child.Str("planId")), (cancelled.Str("status"), cancelled["ineligible"].Str("code"), cancelled.Str("replacedBy")));
        SchemaTests.AssertValid("action-plan", cancelled);
        // A replay returns the same plan; the same key with other parameters is refused.
        Assert.Equal(child.Str("planId"), (await r.C.Call("action.narrow", p)).Str("planId"));
        var mismatch = await r.C.Fails("action.narrow", new JsonObject { ["idempotencyKey"] = key, ["planId"] = parent.Str("planId"), ["keep"] = new JsonArray(1) });
        Assert.Equal(ErrorCodes.IdempotencyMismatch, mismatch.Code);

        // The parent can no longer be approved; the narrowed plan runs only the kept steps and keeps "removed" in its receipt.
        var lost = await r.C.Fails("action.challenge", new JsonObject { ["planId"] = parent.Str("planId") });
        Assert.Equal("cancelled", lost.ErrorData.Str("reason"));
        var op = await RunToEnd(r, child);
        Assert.Equal("verified", op.Str("state"));
        Assert.Equal(("on", "off", "on"), (r.Pc.Values["a"], r.Pc.Values["b"], r.Pc.Values["d"]));
        Assert.Equal(["Change a Windows setting"], op["removed"]!.AsArray().Select(x => x.Str("label")));
        SchemaTests.AssertValid("operation", op);

        // An undo plan is never narrowed.
        var undo = await r.C.Call("action.undo", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["operationId"] = op.Str("operationId") });
        Assert.Equal("not_narrowable", (await NarrowFails(r, undo.Str("planId"), new JsonArray(1))).ErrorData.Str("reason"));
    }

    [Fact]
    public async Task Narrow_Twice_CarriesTheEarlierRemovedStepsForward_IntoTheHashAndTheReceipt()
    {
        // P2 review item 3: A/B/C → B/C → C keeps removed [A, B] (the parent's first, then the new one), hashed in full.
        await using var r = await Start();
        var parent = await UserFix(r, "a", "b", "d");
        var first = await Narrow(r, parent.Str("planId"), 2, 3);
        Assert.Single(first["removed"]!.AsArray());
        var second = await Narrow(r, first.Str("planId"), 2);
        SchemaTests.AssertValid("action-plan", second);
        Assert.Equal(2, second["removed"]!.AsArray().Count);
        Assert.Equal(first["removed"]![0].Str("label"), second["removed"]![0].Str("label"));
        Assert.Equal(ActionService.HashFix(second), second.Str("planHash"));
        var dropped = (JsonObject)second.DeepClone();
        dropped["removed"]!.AsArray().RemoveAt(0);
        Assert.NotEqual(ActionService.HashFix(dropped), second.Str("planHash")); // the carried entry is covered by the hash
        Assert.Single(second["steps"]!.AsArray());

        var op = await RunToEnd(r, second);
        Assert.Equal("verified", op.Str("state"));
        Assert.Equal(("off", "off", "on"), (r.Pc.Values["a"], r.Pc.Values["b"], r.Pc.Values["d"]));
        Assert.Equal(second["removed"]!.AsArray().Select(x => x.Str("label")), op["removed"]!.AsArray().Select(x => x.Str("label")));
        SchemaTests.AssertValid("operation", op);
    }

    [Fact]
    public async Task Narrow_Refusals_BadKeep_ADriftedStep_AndRacesWithApproveExpiryAndTaskEnd()
    {
        await using var r = await Start();
        var plan = await UserFix(r, "a", "b");
        foreach (var bad in new JsonNode?[] { new JsonArray(), new JsonArray(2, 1), new JsonArray(1, 1), new JsonArray(1, 2), new JsonArray(9), JsonValue.Create("1"), null })
            Assert.Equal(ErrorCodes.InvalidParams, (await NarrowFails(r, plan.Str("planId"), bad)).Code);

        // A kept step that no longer resolves the same: stale_plan, and the parent ends as an approval finding drift ends it.
        r.Pc.Values["a"] = "half";
        var stale = await NarrowFails(r, plan.Str("planId"), new JsonArray(1));
        Assert.Equal((ErrorCodes.StaleRevision, "stale_plan"), (stale.Code, stale.ErrorData.Str("reason")));
        var ended = await Plan(r, plan.Str("planId"));
        Assert.Equal(("ineligible", "stale_plan"), (ended.Str("status"), ended["ineligible"].Str("code")));
        Assert.Equal(("ineligible", "stale_plan"), (stale.ErrorData!["current"].Str("status"), stale.ErrorData["current"]!["ineligible"].Str("code")));
        SchemaTests.AssertValid("action-plan", ended);
        // A dropped step may drift freely (it is not kept).
        var fresh = await UserFix(r, "a", "b");
        r.Pc.Values["a"] = "off";
        Assert.Equal("awaiting_user", (await Narrow(r, fresh.Str("planId"), 2)).Str("status"));

        // Narrow against approve: an approved plan is not narrowed.
        var approving = await UserFix(r, "d", "e");
        await RunToEnd(r, approving);
        Assert.Equal(ErrorCodes.InvalidState, (await NarrowFails(r, approving.Str("planId"), new JsonArray(1))).Code);

        // Expired while waiting: refused (expired), nothing stored.
        var expiring = await UserFix(r, "f", "g");
        r.Store.Write(db =>
        {
            var body = ActionService.LoadPlan(db, expiring.Str("planId"));
            body["expiresAt"] = "2020-01-01T00:00:00.000Z";
            db.Exec("UPDATE action_plans SET body = $b, expires_at = $e WHERE plan_id = $p", ("$b", Engine.Json.Write(body)), ("$e", "2020-01-01T00:00:00.000Z"), ("$p", expiring.Str("planId")));
        });
        Assert.Equal("expired", (await NarrowFails(r, expiring.Str("planId"), new JsonArray(1))).ErrorData.Str("reason"));

        // The requesting task ended: its plan was cancelled with it, so it is not narrowed.
        var taskId = await NewTask(r);
        var ai = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("g", "h"), "in_app", null, default);
        await r.C.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
        Assert.Equal("cancelled", (await NarrowFails(r, ai.Str("planId"), new JsonArray(1))).ErrorData.Str("reason"));
        Assert.Single(r.Store.Read(db => db.Query("SELECT plan_id FROM action_plans WHERE body LIKE '%narrowedFrom%'", x => x.GetString(0))));
    }

    [Fact]
    public async Task Narrow_FindingDrift_OnACardPlan_EndsIt_AndTheCardShowsBlockedStalePlan()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (cardId, _) = Cards(r)[0];
        var plan = (await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("a", "b"), cardId), "in_app", null, default)).Str("planId");
        Assert.Equal("plan_ready", State(r, cardId));
        r.Pc.Values["a"] = "half";
        Assert.Equal("stale_plan", (await NarrowFails(r, plan, new JsonArray(1))).ErrorData.Str("reason"));
        var fix = Fix(r, cardId)!;
        SchemaTests.AssertValidRef("fix.schema.json", fix);
        Assert.Equal(("blocked", "stale_plan", plan), (fix.Str("state"), fix.Str("code"), fix.Str("planId")));
        Assert.Contains(r.C.Events, e => e.Str("type") == "card.fix" && e["payload"]?["fix"]?["code"]?.GetValue<string>() == "stale_plan");
    }

    // ---------------------------------------------------------------- reduced plans (review item 1)

    [Fact]
    public async Task AReducedPlan_NeverClosesItsCard_NotByLeftOut_NotByNarrowing_NotAfterARestartReconcile()
    {
        await using var r = await Start();
        var taskId = await NewTask(r, GrantOf(admin: false));
        var cards = Cards(r);
        var (cardA, findingA) = cards[0];
        var (cardB, _) = cards[1];

        // leftOut: c needs administrator rights, which are off for this task; a and b run.
        var args = FixArgs("c", "a", "b");
        args["findingRef"] = findingA;
        var reduced = await Plan(r, (await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(args, cardA), "in_app", null, default)).Str("planId"));
        Assert.Single(reduced["leftOut"]!.AsArray());
        var op = await RunToEnd(r, reduced);
        Assert.Equal("verified", op.Str("state"));
        Assert.Single(op["leftOut"]!.AsArray()); // the receipt keeps it
        Assert.Equal(("done_reduced", "open"), (State(r, cardA), CardStatus(r, cardA)));
        Assert.DoesNotContain((await r.C.Call("followup.get", new JsonObject { ["reportId"] = r.ReportId }))["decisions"]!.AsArray(), d => d.Str("cardId") == cardA);

        // narrowedFrom: switched off by the user.
        var full = await r.Agent.CallToolAsync(taskId, "propose_fix", WithCard(FixArgs("d", "e"), cardB), "in_app", null, default);
        var narrowed = await Narrow(r, full.Str("planId"), 1);
        Assert.Equal(cardB, narrowed.Str("cardId"));
        Assert.Equal("plan_ready", State(r, cardB));
        var narrowedOp = await RunToEnd(r, narrowed);
        Assert.Equal(("done_reduced", "open"), (State(r, cardB), CardStatus(r, cardB)));

        // A restart reconcile of the reduced fix (its step attempted, the write landed) still leaves the card open.
        await r.Host.StopAsync();
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(r.Host.DataDir, Store.FileName)};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE operation_step_states SET state = 'attempted', receipt = NULL WHERE operation_id = $o;
                UPDATE operations SET state = 'attempted', body = json_set(body, '$.state', 'attempted') WHERE operation_id = $o;
                UPDATE action_plans SET status = 'running', body = json_set(body, '$.status', 'running') WHERE plan_id = $p;
                """;
            cmd.Parameters.AddWithValue("$o", op.Str("operationId"));
            cmd.Parameters.AddWithValue("$p", reduced.Str("planId"));
            cmd.ExecuteNonQuery();
        }
        await r.Host.RestartAsync();
        var c2 = await r.Host.ConnectAsync(minor: 5);
        var reconciled = await c2.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("verified", reconciled.Str("state"));
        Assert.True((bool)reconciled["reconciled"]!);
        Assert.Equal(("done_reduced", "open"), (State(r, cardA), CardStatus(r, cardA)));
        Assert.Equal("verified", narrowedOp.Str("state"));
    }

    // ---------------------------------------------------------------- fix.dismiss

    [Fact]
    public async Task FixDismiss_IsAllowed_OnceACardScriptPlanHasEnded_WhateverItsOutcome()
    {
        await using var r = await Start();
        var (a, _) = Cards(r)[0];
        var p = new JsonObject { ["cardId"] = a };
        // A script run's end sets its plan to the run's terminal state (ActionService.Script: SavePlan(plan, state)).
        foreach (var ended in ActionService.ScriptEnded)
        {
            var taskId = await NewTask(r, GrantOf(scripts: true));
            var planId = (await r.Agent.CallToolAsync(taskId, "propose_script", WithCard(ScriptPlanTests.Args(), a), "in_app", null, default)).Str("planId");
            void Set(string status) => r.Store.Write(db =>
            {
                var body = ActionService.LoadPlan(db, planId);
                body["status"] = status;
                db.Exec("UPDATE action_plans SET status = $s, body = $b WHERE plan_id = $p", ("$s", status), ("$b", Engine.Json.Write(body)), ("$p", planId));
            });
            Set("running");
            Assert.Equal("fix_active", (await r.C.Fails("fix.dismiss", p)).ErrorData.Str("reason")); // while it runs
            Set(ended);
            Assert.Contains(State(r, a), new[] { "finished", "failed", "stopped", "unclear" });
            Assert.Null((await r.C.Call("fix.dismiss", p))["fix"]); // ended: dismissed, back to normal
            Assert.Null(Fix(r, a));
        }
    }

    [Fact]
    public async Task FixDismiss_EndsAnEndedAttempt_ButNotOneThatIsActive()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var (a, _) = Cards(r)[0];
        var p = new JsonObject { ["cardId"] = a };
        SchemaTests.AssertValidRef("fix.schema.json#/$defs/dismissParams", p);
        Assert.Null((await r.C.Call("fix.dismiss", p))["fix"]); // nothing there: already normal

        var turn = await AskFix(r, taskId, a);
        Assert.Equal("fix_active", (await r.C.Fails("fix.dismiss", p)).ErrorData.Str("reason")); // working
        var proposed = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("a"), "in_app", turn, default);
        await EndTurn(r, taskId, turn);
        var refused = await r.C.Fails("fix.dismiss", p);
        Assert.Equal((ErrorCodes.InvalidState, "fix_active"), (refused.Code, refused.ErrorData.Str("reason"))); // plan_ready
        Assert.Equal("plan_ready", State(r, a));

        await r.C.Call("action.decline", new JsonObject { ["planId"] = proposed.Str("planId") });
        Assert.Null(Fix(r, a)); // declined: normal

        var noFix = await AskFix(r, taskId, a);
        await EndTurn(r, taskId, noFix);
        Assert.Equal("no_fix", State(r, a));
        var dismissed = await r.C.Call("fix.dismiss", p);
        SchemaTests.AssertValidRef("fix.schema.json#/$defs/dismissResult", dismissed);
        Assert.Null(Fix(r, a));
        Assert.Equal("dismissed", r.Store.Read(db => db.Scalar("SELECT state FROM fix_attempts ORDER BY rowid DESC LIMIT 1")) as string);
        var ev = await r.C.WaitForEvent(e => e.Str("type") == "card.fix" && e["payload"].Str("cardId") == a && e["payload"]!["fix"] is null);
        SchemaTests.AssertValid("event", ev);
        Assert.Equal("open", CardStatus(r, a)); // nothing else changes
    }

    // ---------------------------------------------------------------- every provider through the same path

    [Fact]
    public async Task OpenAi_InApp_FixAsk_IsTheSamePath()
    {
        await using var r = await Start(kind: "openai_api");
        var taskId = await NewTask(r, kind: "openai_api");
        var (a, _) = Cards(r)[0];
        var turn = await AskFix(r, taskId, a);
        await Until(() => !r.Provider.Requests.IsEmpty);
        Assert.Contains($"fix the issue on card {a}", ((UserText)r.Provider.Requests.First().Messages.OfType<UserText>().Last()).Text);
        var proposed = await r.Agent.CallToolAsync(taskId, "propose_fix", FixArgs("a"), "in_app", turn, default);
        Assert.Equal("ChatGPT", (await Plan(r, proposed.Str("planId")))["why"].Str("by"));
        Assert.Equal("ChatGPT", Fix(r, a)!.Str("by"));
        await EndTurn(r, taskId, turn);
        Assert.Equal("plan_ready", State(r, a));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadlessClaude_FixAsk_PassesItsTurnCap_OnlyWhenListed_AndItsBridgesPlanBelongsToTheCard(bool listed)
    {
        await using var r = await Start(kind: "claude_code_mcp");
        var claude = Path.Combine(_bin, "claude.cmd");
        File.WriteAllText(claude, "@echo off");
        var client = new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = claude, ["signedIn"] = true, ["version"] = "2.1.318" };
        if (listed) client["claudeFlags"] = new JsonArray("--max-turns");
        await r.C.Call("provider.setClient", client);
        var taskId = await NewTask(r, kind: "claude_code_mcp");
        var (a, _) = Cards(r)[0];
        var engine = r.Host.Server.Engine;
        var bridge = new CallContext("bridge-run", "agent", 6262);
        string? planId = null, stdin = null;
        r.Cli.Script = async (launch, line, ct) =>
        {
            stdin = launch.StandardInput;
            var session = launch.Arguments[^1];
            await engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code", ["runToken"] = FakeCli.Token(launch) }, bridge);
            var result = (JsonObject)(await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "propose_fix", ["arguments"] = FixArgs("a") }, bridge))!;
            planId = result.Str("planId");
            line(FakeCli.Claude("result", new JsonObject { ["subtype"] = "success", ["is_error"] = false, ["result"] = "Proposed.", ["session_id"] = session, ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } }));
            return new CliExit(0, "");
        };
        var turn = await AskFix(r, taskId, a);
        await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("turnId") == turn);
        var args = r.Cli.Launches.Single().Arguments;
        var at = args.ToList().IndexOf("--max-turns");
        if (listed) Assert.Equal("12", args[at + 1]); // Claude Code's own cap: the fix ask's budget
        else Assert.Equal(-1, at); // not listed by the probed help (2.1.318): the host deadline and per-task bound apply
        Assert.Contains($"fix the issue on card {a}", stdin);
        Assert.Equal(a, (await Plan(r, planId!)).Str("cardId"));
        await Until(() => State(r, a) == "plan_ready");
    }

    [Fact]
    public async Task HeadlessClaude_CountsTheRoundTripsItReports_ElseItsReservedAllowance_AndNeverPasses64()
    {
        // P2 review item 2: a CLI run counts its num_turns; one that reports none (or is cut off) keeps the launch allowance
        // reserved before it started; each launch gets min(the ask's budget, 64 - used); nothing left → exhausted, no launch.
        await using var r = await Start(kind: "claude_code_mcp");
        var claude = Path.Combine(_bin, "claude.cmd");
        File.WriteAllText(claude, "@echo off");
        await r.C.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = claude, ["signedIn"] = true, ["version"] = "2.1.318", ["claudeFlags"] = new JsonArray("--max-turns") });
        var taskId = await NewTask(r, kind: "claude_code_mcp");
        long? report = 5;
        long? seenDuringRun = null;
        r.Cli.Script = (launch, line, ct) =>
        {
            seenDuringRun = r.Store.Read(db => AgentService.LoadTask(db, taskId))["used"]!["turns"]!.GetValue<long>();
            var result = new JsonObject { ["subtype"] = "success", ["is_error"] = false, ["result"] = "Done.", ["session_id"] = launch.Arguments[^1], ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } };
            if (report is { } n) result["num_turns"] = n;
            line(FakeCli.Claude("result", result));
            return Task.FromResult(new CliExit(0, ""));
        };
        long Used() => r.Store.Read(db => AgentService.LoadTask(db, taskId))["used"]!["turns"]!.GetValue<long>();
        string MaxTurns(int i) { var a = r.Cli.Launches[i].Arguments.ToList(); return a[a.IndexOf("--max-turns") + 1]; }
        async Task AskOnce(string text)
        {
            var turn = (await AgentSetup.Ask(r.C, taskId, text)).Str("turnId");
            await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("turnId") == turn);
            await Until(() => !r.Agent.HasActiveWork);
        }

        await AskOnce("One?");
        Assert.Equal((8L, 5L, "8"), (seenDuringRun!.Value, Used(), MaxTurns(0))); // 8 reserved while it ran, then the 5 it reported
        report = null;
        await AskOnce("Two?");
        Assert.Equal(13, Used()); // nothing reported: the reservation stays
        r.Store.Write(db =>
        {
            var task = AgentService.LoadTask(db, taskId);
            task["used"]!["turns"] = 60;
            db.Exec("UPDATE agent_tasks SET body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", taskId));
        });
        await AskOnce("Three?");
        Assert.Equal(("4", 64L), (MaxTurns(2), Used())); // only what is left of 64
        await AgentSetup.Ask(r.C, taskId, "Four?");
        await Until(() => r.C.Events.Any(e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.task" && e["payload"].Str("status") == "exhausted"));
        Assert.Equal(3, r.Cli.Launches.Count); // never launched
    }
}

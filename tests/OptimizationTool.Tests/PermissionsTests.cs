using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// contracts/m9-ai-powers.md §1 (P1): the permissions record, ai.getPermissions / ai.setPermissions, grants built from
/// the record, the see/do tool gates, what a save does to running tasks, sticky exposure marks and the plan races.
/// Fake providers, fake CLI and fake action handlers only: nothing touches this PC.
/// </summary>
public sealed class PermissionsTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "perm-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude;
    private static readonly CallContext Bridge = new("bridge-perm", "agent", Environment.ProcessId);

    public PermissionsTests()
    {
        Directory.CreateDirectory(_bin);
        _claude = Path.Combine(_bin, "claude.cmd");
        File.WriteAllText(_claude, "@echo off");
    }

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<IAuditModule> Modules() =>
        [.. FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), new DiagnosticModule()];

    private static AgentOptions Agent(FakeCli? cli, params IProviderAdapter[] providers)
    {
        var o = AgentSetup.Options(null, providers);
        return new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        };
    }

    /// <summary>An action host with the fake power-plan and quick-scan handlers, an in-app provider and a fake CLI.</summary>
    private static async Task<(TestHost Host, FakePc Pc, Client C, string ReportId)> StartActions(FakeProvider? provider = null, IActionHandler? scheme = null)
    {
        var pc = new FakePc();
        var host = TestHost.StartActions([new FakeActionModule(scheme ?? new FakeSchemeHandler(pc), new FakeScanHandler(pc))], Modules(),
            agent: Agent(new FakeCli(), provider ?? new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        return (host, pc, c, reportId);
    }

    private static JsonObject Perms(bool specs = true, bool history = true, bool names = false, bool checks = true, string changes = "off",
        bool scripts = false, bool web = false) => new()
    {
        ["see"] = new JsonObject { ["specs"] = specs, ["history"] = history, ["names"] = names },
        ["do"] = new JsonObject { ["checks"] = checks, ["changes"] = changes, ["scripts"] = scripts, ["web"] = web, ["admin"] = false },
    };

    private static async Task<JsonObject> Save(Client c, JsonObject permissions, string? key = null)
    {
        var revision = (long)(await c.Call("ai.getPermissions"))["permissions"]!["revision"]!;
        return await c.Call("ai.setPermissions", new JsonObject
        {
            ["idempotencyKey"] = key ?? Guid.NewGuid().ToString(), ["revision"] = revision, ["permissions"] = permissions,
        });
    }

    /// <summary>agent.start without a grant: the host builds it from the permissions record.</summary>
    private static async Task<JsonObject> StartDefault(Client c, string reportId, string kind = "anthropic_api")
    {
        await AgentSetup.Choose(c, kind);
        return await c.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["goal"] = "Help", ["recipientKind"] = kind,
            ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = null },
        });
    }

    private async Task StartHeadlessClient(Client c) =>
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["version"] = "2.1.318" });

    /// <summary>A user-window (paired) Claude Code task with an explicit grant, as the pairing panel sends it.</summary>
    private static async Task<string> StartPaired(TestHost host, Client c, string reportId, JsonObject grant)
    {
        var pair = await host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge);
        await c.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        return (await AgentSetup.StartTask(c, reportId, "claude_code_mcp", grant, pair.Str("pairingId"))).Str("taskId");
    }

    private static async Task<JsonObject> GetTask(Client c, string taskId) => await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });

    private static JsonObject Request(string actionId, string? scheme = null)
    {
        var a = new JsonObject { ["actionId"] = actionId };
        if (scheme is not null) a["parameters"] = new JsonObject { ["scheme"] = scheme };
        return a;
    }

    private static List<string> Plans(TestHost host, string taskId) =>
        host.Server.Engine.Store.Read(db => db.Query("SELECT status FROM action_plans WHERE task_id = $t", r => r.GetString(0), ("$t", taskId)));

    /// <summary>Rewrites a stored task's grant (a state no method produces), to prove a recheck reads the grant itself.</summary>
    private static void EditGrant(TestHost host, string taskId, Action<JsonObject> edit) => host.Server.Engine.Store.Write(db =>
    {
        var task = AgentService.LoadTask(db, taskId);
        edit(task["grant"]!.AsObject());
        db.Exec("UPDATE agent_tasks SET body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", taskId));
    });

    /// <summary>A handler whose target resolution waits for the test.</summary>
    private sealed class GatedHandler(IActionHandler inner) : IActionHandler
    {
        public readonly SemaphoreSlim Entered = new(0);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ActionId => inner.ActionId;
        public string Version => inner.Version;
        public string Label => inner.Label;
        public string Privilege => inner.Privilege;
        public bool SupportsUndo => inner.SupportsUndo;
        public string Kind => inner.Kind;
        public string Group => inner.Group;
        public string What => inner.What;
        public IReadOnlyList<string> Flags => inner.Flags;
        public JsonObject? ParameterSchema => inner.ParameterSchema;
        public bool TakesLease => inner.TakesLease;
        public async Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
        {
            Entered.Release();
            await Release.Task.ConfigureAwait(false);
            return await inner.ResolveAsync(request, ct).ConfigureAwait(false);
        }
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => inner.ReadTargetAsync(target, ct);
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => inner.ExecuteAsync(execution, progress, ct);
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => inner.UndoRequest(target, before);
    }

    // ---------------------------------------------------------------- record and methods

    [Fact]
    public async Task Defaults_BuildTheGrant_AndASaveShapesTheNextTask()
    {
        var provider = new FakeProvider("anthropic_api");
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, provider), Modules());
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);

        var got = await c.Call("ai.getPermissions");
        SchemaTests.AssertValid("permissions", got);
        Assert.False((bool)got["permissions"]!["set"]!);
        Assert.Equal(0, (long)got["permissions"]!["revision"]!);
        Assert.Equal("off", got["permissions"]!["do"].Str("changes"));
        Assert.True((bool)got["available"]!["tools"]!);
        Assert.Equal("unknown", got["available"].Str("web"));
        Assert.False((bool)got["available"]!["scripts"]!);

        var available = (await c.Call("provider.list"))["diagnostics"]!.AsArray().Select(d => d.Str("id")).ToList();
        Assert.NotEmpty(available);
        var task = await StartDefault(c, reportId);
        SchemaTests.AssertValid("agent-task", task);
        var grant = task["grant"]!;
        Assert.Equal(available, Engine.Json.Strings(grant["diagnostics"]));
        Assert.False((bool)grant["share"]!["names"]!);
        Assert.True((bool)grant["see"]!["specs"]!);
        Assert.True((bool)grant["see"]!["history"]!);
        Assert.Equal("off", grant.Str("changes"));
        Assert.False((bool)grant["canRequestChanges"]!);
        Assert.False((bool)grant["scripts"]!);
        Assert.False((bool)grant["web"]!);
        Assert.Equal(0, (long)grant["permissionsRevision"]!);
        Assert.Equal(4, (int)grant["limits"]!["webLookups"]!);
        Assert.Equal(6, (int)grant["limits"]!["fixSteps"]!);
        Assert.Equal(3, (int)grant["limits"]!["scripts"]!);
        Assert.False((bool)task["everSharedNames"]!);

        // The saved record shapes the next task: no checks, names on, multi-step fixes and scripts.
        var saved = await Save(c, Perms(names: true, checks: false, changes: "fix", scripts: true, specs: false));
        SchemaTests.AssertValid("permissions", saved);
        Assert.True((bool)saved["permissions"]!["set"]!);
        Assert.Equal(1, (long)saved["permissions"]!["revision"]!);
        var next = await StartDefault(c, reportId); // the narrowing above already ended the first task
        var g2 = next["grant"]!;
        Assert.Empty(g2["diagnostics"]!.AsArray());
        Assert.True((bool)g2["share"]!["names"]!);
        Assert.False((bool)g2["see"]!["specs"]!);
        Assert.Equal("fix", g2.Str("changes"));
        Assert.True((bool)g2["canRequestChanges"]!);
        Assert.True((bool)g2["scripts"]!);
        Assert.Equal(1, (long)g2["permissionsRevision"]!);
        Assert.True((bool)next["everSharedNames"]!);
        SchemaTests.AssertValid("agent-task", next);
    }

    [Fact]
    public async Task SetPermissions_Validates_RefusesAStaleRevision_AndReplaysByKey()
    {
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 5);
        JsonObject Set(JsonNode permissions, long revision = 0, string? key = null) => new()
        {
            ["idempotencyKey"] = key ?? Guid.NewGuid().ToString(), ["revision"] = revision, ["permissions"] = permissions,
        };

        // Scripts need multi-step fixes; enums and booleans only; unknown and missing fields are refused.
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms(changes: "ask", scripts: true)))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms(changes: "always")))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms().Also(p => p["do"]!["checks"] = "yes")))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms().Also(p => p["see"]!["paths"] = true)))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms().Also(p => p["extra"] = 1)))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms().Also(p => p["do"]!.AsObject().Remove("web"))))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("ai.setPermissions", Set(Perms().Also(p => p["version"] = 2)))).Code);
        Assert.Equal(0, (long)(await c.Call("ai.getPermissions"))["permissions"]!["revision"]!); // nothing stored

        // Online lookups do not need names (§1.1); the full record may be echoed back.
        var echoed = Perms(web: true, changes: "fix").Also(p => { p["version"] = 1; p["revision"] = 0; p["set"] = false; });
        var saved = await c.Call("ai.setPermissions", Set(echoed.DeepClone(), key: "k1"));
        Assert.True((bool)saved["permissions"]!["do"]!["web"]!);
        Assert.False((bool)saved["permissions"]!["see"]!["names"]!);
        Assert.Equal(1, (long)saved["permissions"]!["revision"]!);
        var ev = await c.WaitForEvent(e => e.Str("type") == "ai.permissions");
        Assert.Equal("user", ev.Str("stream"));
        Assert.Equal(1, (long)ev["payload"]!["permissions"]!["revision"]!);
        SchemaTests.AssertValid("event", ev);

        // The same key replays; another payload under it is a mismatch; a stale revision is -32002 with the current record.
        Assert.Equal(1, (long)(await c.Call("ai.setPermissions", Set(echoed.DeepClone(), key: "k1")))["permissions"]!["revision"]!);
        Assert.Equal(ErrorCodes.IdempotencyMismatch, (await c.Fails("ai.setPermissions", Set(Perms(), key: "k1"))).Code);
        var stale = await c.Fails("ai.setPermissions", Set(Perms(), revision: 0));
        Assert.Equal(ErrorCodes.StaleRevision, stale.Code);
        SchemaTests.AssertValid("permissions", stale.ErrorData!["current"]);
        Assert.Equal(1, (long)stale.ErrorData!["current"]!["permissions"]!["revision"]!);
        Assert.Equal(2, (long)(await c.Call("ai.setPermissions", Set(Perms(), revision: 1)))["permissions"]!["revision"]!);
        Assert.Single(c.Events, e => e.Str("type") == "ai.permissions" && (long)e["payload"]!["permissions"]!["revision"]! == 1);
    }

    [Fact]
    public async Task PermissionMethods_AreDesktopOnly()
    {
        Assert.Contains("ai.getPermissions", AuditEngine.DesktopMethods);
        Assert.Contains("ai.setPermissions", AuditEngine.DesktopMethods);
        Assert.Contains("ai.setPermissions", OptimizationTool.Host.HostServer.Allowlists["desktop"]);
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var agent = await host.ConnectAsync(kind: "agent", minor: 5);
        Assert.Equal(ErrorCodes.MethodNotAllowed, (await agent.Fails("ai.getPermissions")).Code);
        Assert.Equal(ErrorCodes.MethodNotAllowed, (await agent.Fails("ai.setPermissions", new JsonObject { ["idempotencyKey"] = "k", ["revision"] = 0, ["permissions"] = Perms() })).Code);
    }

    // ---------------------------------------------------------------- gates

    [Fact]
    public async Task EachGate_RefusesItsTool_AndTheToolListOmitsIt()
    {
        var provider = new FakeProvider("anthropic_api", FakeProvider.Step(FakeProvider.Say("Hello.")));
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, provider), Modules());
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var agent = host.Server.Engine.Agent;

        await Save(c, Perms(specs: false, history: false)); // changes stay off
        var taskId = (await StartDefault(c, reportId)).Str("taskId");
        foreach (var (tool, args) in new[]
        {
            ("get_pc_specs", new JsonObject()), ("get_report_history", new JsonObject()), ("get_benchmarks", new JsonObject()),
            ("get_benchmarks", new JsonObject { ["runId"] = "bench-0000000000000000" }), ("request_action", Request("power.select_scheme", "balanced")),
        })
        {
            var refused = await agent.CallToolAsync(taskId, tool, args, "in_app", null, default);
            Assert.Equal("not_allowed", refused["error"].Str("code"));
            SchemaTests.AssertValid("tool-result", refused);
        }
        Assert.Contains(c.Events, e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.tool"
            && e["payload"].Str("tool") == "get_pc_specs" && e["payload"].Str("status") == "denied");
        Assert.Null((await agent.CallToolAsync(taskId, "get_cards", new JsonObject(), "in_app", null, default))["error"]); // the deck stays readable

        // The in-app tool list leaves out what the grant refuses.
        await AgentSetup.Ask(c, taskId);
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        var offered = provider.Requests.Single().Tools.Select(t => t.Name).ToList();
        Assert.DoesNotContain("get_pc_specs", offered);
        Assert.DoesNotContain("get_report_history", offered);
        Assert.DoesNotContain("get_benchmarks", offered);
        Assert.DoesNotContain("request_action", offered);
        Assert.Contains("get_cards", offered);
        Assert.Contains("run_diagnostic", offered);

        // The same from the stored grant; with everything on, all are offered.
        var narrow = Grant.FromJson((await GetTask(c, taskId))["grant"]!);
        Assert.DoesNotContain(Diagnostics.Tools(narrow, ["power.select_scheme"]), t => t.Name is "get_pc_specs" or "get_report_history" or "get_benchmarks" or "request_action");
        Assert.Contains(Diagnostics.Tools(narrow, ["power.select_scheme"], benchContext: true), t => t.Name == "get_benchmarks");
        var wide = Grant.Parse(AgentSetup.Grant().Also(g => g["changes"] = "ask"));
        Assert.Superset(new HashSet<string>(["get_pc_specs", "get_report_history", "get_benchmarks", "request_action"]),
            Diagnostics.Tools(wide, ["power.select_scheme"]).Select(t => t.Name).ToHashSet());
    }

    [Fact]
    public async Task HistoryOff_StillReadsTheBenchmarkRunTheAskIsAbout()
    {
        string? runId = null, otherId = null;
        var provider = new FakeProvider("anthropic_api",
            (_, _) => System.Threading.Tasks.Task.FromResult(FakeProvider.Call(
                ("get_benchmarks", new JsonObject { ["runId"] = runId }), ("get_benchmarks", new JsonObject { ["runId"] = otherId }),
                ("get_benchmarks", new JsonObject()), ("get_report_history", new JsonObject()))),
            FakeProvider.Step(FakeProvider.Say("Fine.")));
        var (options, _, _, _) = BenchTests.Fakes();
        var agentOptions = new AgentOptions { Providers = [provider], Credentials = new MemoryCredentialStore(), RetryDelay = TimeSpan.FromMilliseconds(20) };
        await using var host = TestHost.StartBench(options, agentOptions, [.. FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false)]);
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        otherId = (await BenchTests.RunToEnd(c)).Str("runId");
        runId = (await BenchTests.RunToEnd(c)).Str("runId");
        await AgentSetup.SetKey(c);
        await Save(c, Perms(history: false));
        var taskId = (await StartDefault(c, reportId)).Str("taskId");

        await c.Call("agent.ask", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["text"] = "Is this good?",
            ["context"] = new JsonObject { ["benchRunId"] = runId, ["part"] = "gpu" },
        });
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        var first = provider.Requests.First();
        Assert.Contains(first.Tools, t => t.Name == "get_benchmarks"); // offered for the run the ask is about
        Assert.DoesNotContain(first.Tools, t => t.Name == "get_report_history");
        var results = provider.Requests.Last().Messages.OfType<ToolResults>().Last().Results.Select(r => r.Content).ToList();
        Assert.Null(results[0]["error"]);
        Assert.Equal(runId, results[0]["runs"]![0].Str("runId"));
        Assert.Equal("not_allowed", results[1]["error"].Str("code")); // another run
        Assert.Equal("not_allowed", results[2]["error"].Str("code")); // the latest runs
        Assert.Equal("not_allowed", results[3]["error"].Str("code"));
    }

    // ---------------------------------------------------------------- saves and running tasks

    [Fact]
    public async Task Narrowing_EndsInAppHeadlessAndPairedTasks_CancelsThePendingPlan_AndLeavesTheApprovedOperation()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider("anthropic_api", async (_, ct) =>
        {
            running.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, ct);
            return FakeProvider.Say("Never.");
        });
        var (host, pc, c, reportId) = await StartActions(provider);
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        await Save(c, Perms(changes: "ask"));

        var inApp = (await StartDefault(c, reportId)).Str("taskId");
        var scan = await agent.CallToolAsync(inApp, "request_action", Request("defender.quick_scan"), "in_app", null, default);
        var op = await Act.Approve(c, await c.Call("action.get", new JsonObject { ["planId"] = scan.Str("planId") }));
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Scanning · quick");
        var pending = await agent.CallToolAsync(inApp, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        Assert.Equal("awaiting_user", pending.Str("status"));
        await AgentSetup.Ask(c, inApp);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Save(c, Perms(changes: "off")); // narrows: every active task ends

        Assert.Equal("stopped", (await GetTask(c, inApp)).Str("status"));
        await AgentSetup.TaskEvent(c, inApp, "agent.task", p => p.Str("status") == "stopped");
        await AgentSetup.TaskEvent(c, inApp, "agent.turn", p => p.Str("status") == "failed"); // the running turn was cancelled
        Assert.DoesNotContain(c.Events, e => e.Str("stream") == "task:" + inApp && e.Str("type") == "agent.message");
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = pending.Str("planId") })).Str("status"));
        Assert.Empty((await c.Call("agent.active"))["tasks"]!.AsArray());

        // A headless task ends the same way (on its own: only the chosen AI has in-app and headless tasks).
        await Save(c, Perms(changes: "ask"));
        await StartHeadlessClient(c);
        var headless = (await StartDefault(c, reportId, "claude_code_mcp")).Str("taskId");
        await Save(c, Perms(changes: "off"));
        Assert.Equal("stopped", (await GetTask(c, headless)).Str("status"));
        await AgentSetup.TaskEvent(c, headless, "agent.task", p => p.Str("status") == "stopped");
        Assert.Empty((await c.Call("agent.active"))["tasks"]!.AsArray());

        // A paired (user-window) task of the same kind ends the same way (one active task per kind, so it runs on its own).
        await Save(c, Perms(changes: "ask"));
        var paired = await StartPaired(host, c, reportId, AgentSetup.Grant().Also(g => g["changes"] = "ask"));
        await Save(c, Perms(changes: "off"));
        Assert.Equal("stopped", (await GetTask(c, paired)).Str("status"));
        await AgentSetup.TaskEvent(c, paired, "agent.task", p => p.Str("status") == "stopped");
        Assert.Empty((await c.Call("agent.active"))["tasks"]!.AsArray());

        // The approved operation is not the task's: it runs to its end.
        pc.ProviderGate.SetResult();
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("operationId") == op.Str("operationId") && e["payload"].Str("state") == "verified");
        Assert.Equal(1, pc.ProviderRuns);
        Assert.Equal(0, pc.Writes);
    }

    [Fact]
    public async Task Widening_UpdatesInAppAndHeadlessGrantsInPlace_AndNeverAPairedTask()
    {
        var (host, pc, c, reportId) = await StartActions();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        await Save(c, Perms(specs: false, checks: false));

        async Task Widened(string id, long revision)
        {
            var task = await GetTask(c, id);
            Assert.Equal("active", task.Str("status"));
            var g = task["grant"]!;
            Assert.True((bool)g["see"]!["specs"]!);
            Assert.True((bool)g["share"]!["names"]!);
            Assert.Equal("ask", g.Str("changes"));
            Assert.NotEmpty(g["diagnostics"]!.AsArray());
            Assert.Equal(revision, (long)g["permissionsRevision"]!);
            Assert.True((bool)task["everSharedNames"]!);
            var ev = await AgentSetup.TaskEvent(c, id, "agent.task", p => p["grant"] is not null);
            SchemaTests.AssertValid("event", ev);
            SchemaTests.AssertValid("agent-task", task);
        }

        var inApp = (await StartDefault(c, reportId)).Str("taskId");
        Assert.Equal("not_allowed", (await agent.CallToolAsync(inApp, "get_pc_specs", new JsonObject(), "in_app", null, default))["error"].Str("code"));
        await Save(c, Perms(names: true, changes: "ask")); // only adds: specs, checks, names, changes
        await Widened(inApp, 2);
        Assert.Null((await agent.CallToolAsync(inApp, "get_pc_specs", new JsonObject(), "in_app", null, default))["error"]);
        Assert.Equal("awaiting_user", (await agent.CallToolAsync(inApp, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default)).Str("status"));

        // A headless task widens the same way (on its own: only the chosen AI has in-app and headless tasks).
        await Save(c, Perms(specs: false, checks: false));
        await StartHeadlessClient(c);
        var headless = (await StartDefault(c, reportId, "claude_code_mcp")).Str("taskId");
        await Save(c, Perms(names: true, changes: "ask"));
        await Widened(headless, 4);

        // A paired task (same kind, so on its own) keeps what its panel granted when the permissions widen.
        await Save(c, Perms(specs: false, checks: false));
        var paired = await StartPaired(host, c, reportId, AgentSetup.Grant([]).Also(g => g["see"] = new JsonObject { ["specs"] = false, ["history"] = true }));
        await Save(c, Perms(names: true, changes: "ask"));
        var pairedTask = await GetTask(c, paired);
        Assert.Equal("active", pairedTask.Str("status"));
        Assert.False((bool)pairedTask["grant"]!["see"]!["specs"]!);
        Assert.False((bool)pairedTask["grant"]!["share"]!["names"]!);
        Assert.Equal("off", pairedTask["grant"].Str("changes"));
        Assert.False((bool)pairedTask["everSharedNames"]!);
        Assert.DoesNotContain(c.Events, e => e.Str("stream") == "task:" + paired && e.Str("type") == "agent.task" && e["payload"]!["grant"] is not null);
        Assert.Equal(0, pc.Writes);
    }

    [Fact]
    public async Task ANoteWrittenWithNames_StaysHiddenFromANamesOffTask_AfterNarrowing()
    {
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, new FakeProvider("anthropic_api")), Modules());
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var agent = host.Server.Engine.Agent;

        // A task that started without names gains them by widening, and writes a note.
        var writer = (await StartDefault(c, reportId)).Str("taskId");
        await Save(c, Perms(names: true));
        var cards = (await agent.CallToolAsync(writer, "get_cards", new JsonObject(), "in_app", null, default))["cards"]!.AsArray();
        var cardId = cards[0].Str("cardId");
        Assert.Equal("noted", (await agent.CallToolAsync(writer, "note_card", new JsonObject { ["cardId"] = cardId, ["text"] = "The fan runs at full speed." }, "in_app", null, default)).Str("status"));

        // Narrowing names ends it; a new names-off task does not get the note.
        await Save(c, Perms(names: false));
        Assert.Equal("stopped", (await GetTask(c, writer)).Str("status"));
        var reader = (await StartDefault(c, reportId)).Str("taskId");
        async Task<string?> Note(string taskId) => (await agent.CallToolAsync(taskId, "get_cards", new JsonObject(), "in_app", null, default))["cards"]!.AsArray()
            .Single(x => x.Str("cardId") == cardId)!["note"]?.GetValue<string>();
        Assert.Null(await Note(reader));

        // The sticky mark decides, not the stored grant: even a writer grant rewritten to names-off stays hidden.
        EditGrant(host, writer, g => g["share"] = new JsonObject { ["names"] = false });
        Assert.Null(await Note(reader));
        // Control: a writer that never saw names is shown to a names-off reader.
        host.Server.Engine.Store.Write(db =>
        {
            var task = AgentService.LoadTask(db, writer);
            task["everSharedNames"] = false;
            db.Exec("UPDATE agent_tasks SET body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", writer));
        });
        Assert.Equal("The fan runs at full speed.", await Note(reader));
    }

    // ---------------------------------------------------------------- legacy grants

    [Fact]
    public async Task LegacyGrantShapes_MapToChanges()
    {
        var legacy = Grant.Parse(AgentSetup.Grant().Also(g => g["canRequestChanges"] = true));
        Assert.Equal("ask", legacy.Changes);
        Assert.True(legacy.SeeSpecs && legacy.SeeHistory && !legacy.Scripts && !legacy.Web);
        Assert.Equal("off", Grant.Parse(AgentSetup.Grant().Also(g => { g["canRequestChanges"] = true; g["changes"] = "off"; })).Changes); // changes wins
        Assert.Equal("fix", Grant.Parse(AgentSetup.Grant().Also(g => { g["canRequestChanges"] = false; g["changes"] = "fix"; })).Changes);
        Assert.Equal("off", Grant.Parse(AgentSetup.Grant()).Changes);
        Assert.Throws<RpcError>(() => Grant.Parse(AgentSetup.Grant().Also(g => { g["changes"] = "ask"; g["scripts"] = true; })));
        Assert.Throws<RpcError>(() => Grant.Parse(AgentSetup.Grant().Also(g => g["changes"] = "sometimes")));
        Assert.Throws<RpcError>(() => Grant.Parse(AgentSetup.Grant().Also(g => g["see"] = new JsonObject { ["specs"] = "no" })));

        // A grant stored before m9 (no see, changes, scripts, web or new limits) reads as it behaved.
        var stored = Grant.FromJson(JsonNode.Parse("""
            {"diagnostics":["power.inventory"],"share":{"names":true},"limits":{"diagnosticCalls":10,"resultBytes":32768,"totalBytes":131072,"turns":8,"turnSeconds":90,"maxSampleSeconds":60},
             "idleExpirySeconds":900,"canRequestChanges":true}
            """)!);
        Assert.Equal("ask", stored.Changes);
        Assert.True(stored.SeeSpecs && stored.SeeHistory && stored.ShareNames);
        Assert.Equal(4, stored.Limits.WebLookups);
        var written = stored.ToJson();
        Assert.True((bool)written["canRequestChanges"]!);
        Assert.Equal("ask", written.Str("changes"));

        // Over the wire: an explicit old-shape grant still works.
        var (host, _, c, reportId) = await StartActions();
        await using var _h = host;
        var task = await AgentSetup.StartTask(c, reportId, grant: AgentSetup.Grant([]).Also(g => g["canRequestChanges"] = true));
        Assert.Equal("ask", task["grant"].Str("changes"));
        Assert.True((bool)task["grant"]!["canRequestChanges"]!);
        SchemaTests.AssertValid("agent-task", task);
    }

    // ---------------------------------------------------------------- races (§1.3)

    [Fact]
    public async Task APlanResolvingWhileANarrowingLands_IsNeverStored()
    {
        var pc0 = new FakePc();
        var gated = new GatedHandler(new FakeSchemeHandler(pc0));
        var (host, _, c, reportId) = await StartActions(scheme: gated);
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        await Save(c, Perms(changes: "ask"));
        var taskId = (await StartDefault(c, reportId)).Str("taskId");

        var call = agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await Save(c, Perms(changes: "off"));
        gated.Release.SetResult();
        Assert.Equal("revoked", (await call.WaitAsync(TimeSpan.FromSeconds(10)))["error"].Str("code"));
        Assert.Empty(Plans(host, taskId));
        Assert.DoesNotContain(c.Events, e => e.Str("type") == "action.plan" && e["payload"].Str("status") == "awaiting_user");
    }

    [Fact]
    public async Task TheInsertRechecksChangesAndThePendingPlan_InItsTransaction()
    {
        var pc0 = new FakePc();
        var gated = new GatedHandler(new FakeSchemeHandler(pc0));
        var (host, _, c, reportId) = await StartActions(scheme: gated);
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        await Save(c, Perms(changes: "ask"));
        var taskId = (await StartDefault(c, reportId)).Str("taskId");

        // Two requests pass the tool's own check together; only one plan is stored.
        var a = agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        var b = agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "power_saver"), "in_app", null, default);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        gated.Release.SetResult();
        var results = await System.Threading.Tasks.Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, r => r["status"]?.GetValue<string>() == "awaiting_user");
        Assert.Single(results, r => r["error"]?["code"]?.GetValue<string>() == "not_allowed");
        Assert.Equal(["awaiting_user"], Plans(host, taskId));

        // The grant itself is read at insert: a task whose stored grant lost changes stores nothing.
        await c.Call("action.decline", new JsonObject { ["planId"] = results.Single(r => r["planId"] is not null).Str("planId") });
        var second = new GatedHandler(new FakeSchemeHandler(pc0));
        var (host2, _, c2, reportId2) = await StartActions(scheme: second);
        await using var _2 = host2;
        await Save(c2, Perms(changes: "ask"));
        var task2 = (await StartDefault(c2, reportId2)).Str("taskId");
        var call = host2.Server.Engine.Agent.CallToolAsync(task2, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        await second.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        EditGrant(host2, task2, g => { g["changes"] = "off"; g["canRequestChanges"] = false; });
        second.Release.SetResult();
        Assert.Equal("not_allowed", (await call.WaitAsync(TimeSpan.FromSeconds(10)))["error"].Str("code"));
        Assert.Empty(Plans(host2, task2));
    }

    [Fact]
    public async Task ApprovingAPlanAfterItsTaskLostChanges_IsRefused_AndThePlanIsCancelled()
    {
        var (host, pc, c, reportId) = await StartActions();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        await Save(c, Perms(changes: "ask"));

        // A narrowing between the approval dialog's challenge and the approve: the plan was cancelled with its task.
        var taskId = (await StartDefault(c, reportId)).Str("taskId");
        var planId = (await agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default)).Str("planId");
        var plan = await c.Call("action.get", new JsonObject { ["planId"] = planId });
        var nonce = await Act.Nonce(c, planId);
        await Save(c, Perms(changes: "off"));
        var refused = await c.Fails("action.approve", Act.ApproveParams(plan, nonce));
        Assert.Equal(ErrorCodes.InvalidState, refused.Code);
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = planId })).Str("status"));

        // The approve's own recheck: a still-active task whose stored grant lost changes cannot have its plan approved.
        await Save(c, Perms(changes: "ask"));
        var task2 = (await StartDefault(c, reportId)).Str("taskId");
        var plan2Id = (await agent.CallToolAsync(task2, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default)).Str("planId");
        var plan2 = await c.Call("action.get", new JsonObject { ["planId"] = plan2Id });
        EditGrant(host, task2, g => { g["changes"] = "off"; g["canRequestChanges"] = false; });
        var refused2 = await c.Fails("action.approve", Act.ApproveParams(plan2, await Act.Nonce(c, plan2Id)));
        Assert.Equal("cancelled", refused2.ErrorData.Str("reason"));
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = plan2Id })).Str("status"));
        Assert.Equal(0, pc.Writes);
        Assert.Null(host.Server.Engine.Store.Read(db => db.Scalar("SELECT 1 FROM operations")));
    }

    [Fact]
    public async Task ConcurrentPreviewsWithOneKey_StoreOnePlan()
    {
        var pc0 = new FakePc();
        var gated = new GatedHandler(new FakeSchemeHandler(pc0));
        var (host, _, c, _) = await StartActions(scheme: gated);
        await using var _h = host;
        var engine = host.Server.Engine;
        JsonObject Params() => new() { ["idempotencyKey"] = "same", ["actionId"] = "power.select_scheme", ["parameters"] = new JsonObject { ["scheme"] = "balanced" } };

        var first = engine.InvokeAsync("action.preview", Params());
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var duplicate = engine.InvokeAsync("action.preview", Params()); // waits for the reservation's holder
        Assert.False(await gated.Entered.WaitAsync(TimeSpan.FromMilliseconds(200))); // it never resolves on its own
        gated.Release.SetResult();
        var results = await System.Threading.Tasks.Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(results[0].Str("planId"), results[1].Str("planId"));
        Assert.Equal(1L, engine.Store.Read(db => db.Scalar("SELECT COUNT(*) FROM action_plans")));
        Assert.Equal(1L, engine.Store.Read(db => db.Scalar("SELECT COUNT(*) FROM idempotency WHERE method = 'action.preview' AND key = 'same'")));
        Assert.Equal(ErrorCodes.IdempotencyMismatch, (await c.Fails("action.preview", Params().Also(p => p["parameters"]!["scheme"] = "power_saver"))).Code);
    }
}

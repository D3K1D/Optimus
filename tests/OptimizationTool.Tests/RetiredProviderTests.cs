using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>
/// M9 "Drop Codex": the product has one native client (Claude Code, shown as "Claude"). A Codex bridge is refused at
/// agent.pair; old data (profile, tasks, plans, events, notes, pairings, research availability) loads without error and
/// what was left active ends once on start-up. Fake providers, fake CLI and fake action handlers only.
/// </summary>
public sealed class RetiredProviderTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "retired-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude;
    private static readonly CallContext Bridge = new("bridge-retired", "agent", Environment.ProcessId);

    public RetiredProviderTests()
    {
        Directory.CreateDirectory(_bin);
        _claude = Path.Combine(_bin, "claude.cmd");
        File.WriteAllText(_claude, "@echo off");
    }

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AgentOptions Agent(FakeCli? cli)
    {
        var o = AgentSetup.Options(null, new FakeProvider("anthropic_api"));
        return new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        };
    }

    private static async Task<JsonObject> Entry(Client c, string kind) =>
        (await c.Call("provider.list"))["providers"]!.AsArray().Single(p => p.Str("kind") == kind)!.AsObject();

    // ---------------------------------------------------------------- pairing

    [Fact]
    public async Task Pair_RefusesACodexBridge_WithAMessage_AndLeavesNoTrace()
    {
        await using var host = TestHost.StartAgent(Agent(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        var before = await Entry(c, "claude_code_mcp");

        var refused = await Assert.ThrowsAsync<RpcError>(() =>
            host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "codex", ["clientVersion"] = "0.170.0" }, Bridge));
        Assert.Equal(-32013, refused.Code);
        Assert.Equal("client_not_supported", refused.ErrorData.Str("reason"));
        Assert.Equal("Optimus no longer works with Codex. Use Claude Code or the Optimus app.", refused.Message);
        // Also with a run token: there is no Codex run to bind to.
        Assert.Equal("client_not_supported", (await Assert.ThrowsAsync<RpcError>(() => host.Server.Engine.InvokeAsync("agent.pair",
            new JsonObject { ["client"] = "codex", ["runToken"] = new string('a', 32) }, Bridge))).ErrorData.Str("reason"));

        Assert.Null(host.Server.Engine.Store.Read(db => db.Scalar("SELECT 1 FROM agent_pairings")));
        await Task.Delay(50);
        Assert.DoesNotContain(c.Events, e => e.Str("type") is "agent.pairing" or "provider.status");
        Assert.Equal(before.Str("status"), (await Entry(c, "claude_code_mcp")).Str("status"));

        // Claude Code (and a client that did not name itself) still pair.
        var pair = (JsonObject)(await host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge))!;
        Assert.Matches("^[0-9]{4}$", pair.Str("code"));
        Assert.Equal("pending", (await Entry(c, "claude_code_mcp")).Str("status"));
    }

    // ---------------------------------------------------------------- what the host produces now

    [Fact]
    public async Task Providers_KindsAndLabels_AreClaudeOnly()
    {
        await using var host = TestHost.StartAgent(Agent(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        var list = await c.Call("provider.list");
        SchemaTests.AssertValid("provider-list", list);
        Assert.DoesNotContain(list["providers"]!.AsArray(), p => p.Str("kind") == "codex_mcp");
        Assert.Equal("Claude", (await Entry(c, "claude_code_mcp")).Str("label"));

        var connect = await c.Call("provider.connect", new JsonObject { ["kind"] = "claude_code_mcp" });
        Assert.Equal(["claude"], connect["registration"]!.AsObject().Select(x => x.Key));
        Assert.StartsWith("claude mcp add --scope user optimization-tool -- ", connect["registration"].Str("claude"));
        foreach (var method in new[] { "provider.connect", "provider.disconnect" })
            Assert.Equal(-32602, (await c.Fails(method, new JsonObject { ["kind"] = "codex_mcp" })).Code);

        var reportId = await AgentSetup.Report(c);
        Assert.Equal(-32602, (await c.Fails("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "k1", ["goal"] = "Chat", ["recipientKind"] = "codex_mcp",
            ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = null }, ["grant"] = AgentSetup.Grant(),
        })).Code);

        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["version"] = "2.1.318" });
        var task = await AgentSetup.StartTask(c, reportId, "claude_code_mcp");
        Assert.Equal("Claude", task["recipient"].Str("label"));
        SchemaTests.AssertValid("agent-task", task);
    }

    [Fact]
    public async Task SetClient_RefusesTheRetiredKind_AndItsFeatureList()
    {
        await using var host = TestHost.StartAgent(Agent(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        Assert.Equal("kind", (await c.Fails("provider.setClient", new JsonObject { ["kind"] = "codex_mcp", ["path"] = null, ["signedIn"] = null })).ErrorData.Str("field"));
        var features = await c.Fails("provider.setClient", new JsonObject
            { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["codexFeatures"] = new JsonArray("apps") });
        Assert.Equal(-32602, features.Code);
        Assert.Equal("codexFeatures", features.ErrorData.Str("field"));
        Assert.Equal("waiting", (await Entry(c, "claude_code_mcp")).Str("status")); // nothing was taken from the refused report
    }

    [Fact]
    public async Task Plans_FromAClaudeTask_QuoteClaude()
    {
        var pc = new FakePc();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), agent: Agent(new FakeCli()));
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["version"] = "2.1.318" });
        var grant = AgentSetup.Grant([]);
        grant["canRequestChanges"] = true;
        var taskId = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp", grant)).Str("taskId");
        var requested = await host.Server.Engine.Agent.CallToolAsync(taskId, "request_action", new JsonObject
        {
            ["actionId"] = "power.select_scheme", ["parameters"] = new JsonObject { ["scheme"] = "balanced" }, ["why"] = "Saves power.",
        }, "claude_code", null, default);
        var plan = await c.Call("action.get", new JsonObject { ["planId"] = requested.Str("planId") });
        Assert.Equal("claude_code", plan["requestedBy"].Str("kind"));
        Assert.Equal("Claude", plan["why"].Str("by")); // the native dialog's "From Claude: …"
        SchemaTests.AssertValid("action-plan", plan);
    }

    // ---------------------------------------------------------------- old data

    [Fact]
    public async Task Onboarding_AStoredCodexChoice_LoadsAsStored_AndCannotBeSavedAgain()
    {
        await using var host = TestHost.StartAgent(Agent(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        StoreProvider(host, "codex", "Codex");

        var got = await c.Call("onboarding.get");
        Assert.Equal("codex", got["profile"]!["provider"].Str("value")); // the UI's migration sees it and shows the AI picker
        SchemaTests.AssertValid("onboarding", got);

        var revision = (long)got["revision"]!;
        var refused = await c.Fails("onboarding.save", new JsonObject
            { ["expectedRevision"] = revision, ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = "codex" } } });
        Assert.Equal(-32602, refused.Code);
        Assert.Equal("patch.provider", refused.ErrorData.Str("field"));

        // Another field saves without touching the stored choice; a new choice replaces it, with the label "Claude".
        var saved = await c.Call("onboarding.save", new JsonObject { ["expectedRevision"] = revision, ["patch"] = new JsonObject { ["uses"] = new JsonArray("Gaming") } });
        Assert.Equal("codex", saved["profile"]!["provider"].Str("value"));
        saved = await c.Call("onboarding.save", new JsonObject
            { ["expectedRevision"] = (long)saved["revision"]!, ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = "claude-code" } } });
        Assert.Equal("Claude", saved["profile"]!["provider"].Str("label"));
    }

    [Fact]
    public async Task Startup_StopsActiveCodexTasks_ExpiresTheirPairings_DropsTheirResearchRows_AndKeepsHistoryReadable()
    {
        var pc = new FakePc();
        var log = new List<string>();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), agent: Agent(new FakeCli()), log: line => { lock (log) log.Add(line); });
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var engine = host.Server.Engine;

        // What an older version left: an active Codex task with a plan waiting for the user, its tool event (origin
        // "codex"), a note it wrote, a pending Codex pairing, research availability for the Codex CLI, and the profile choice.
        var grant = AgentSetup.Grant([]);
        grant["canRequestChanges"] = true;
        var taskId = (await AgentSetup.StartTask(c, reportId, grant: grant)).Str("taskId");
        var planId = (await engine.Agent.CallToolAsync(taskId, "request_action", new JsonObject
        {
            ["actionId"] = "power.select_scheme", ["parameters"] = new JsonObject { ["scheme"] = "balanced" }, ["why"] = "Saves power.",
        }, "codex", null, default)).Str("planId");
        var cardId = (await c.Call("followup.get", new JsonObject { ["reportId"] = reportId }))["card"].Str("id");
        engine.Store.Write(db =>
        {
            var task = AgentService.LoadTask(db, taskId);
            task["recipient"] = new JsonObject { ["kind"] = "codex_mcp", ["label"] = "Codex", ["model"] = null, ["mode"] = "headless" };
            task["clientSession"] = "01a1acef-e7fe-7660-8a5e-dbabb0764106";
            db.Exec("UPDATE agent_tasks SET recipient_kind = 'codex_mcp', body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", taskId));
            var plan = (JsonObject)Engine.Json.Parse((string)db.Scalar("SELECT body FROM action_plans WHERE plan_id = $p", ("$p", planId))!)!;
            plan["why"]!["by"] = "Codex";
            db.Exec("UPDATE action_plans SET body = $b WHERE plan_id = $p", ("$b", Engine.Json.Write(plan)), ("$p", planId));
            Assert.True(Followups.NoteCard(db, reportId, cardId, "Worth a look.", taskId, "Codex"));
            db.Exec("""
                INSERT INTO agent_pairings (pairing_id, client, client_version, companion_pid, code, status, connection_id, created_at, expires_at)
                VALUES ('pair-codex', 'codex', '0.167.0', 4242, '1234', 'pending', 'c-old', $t, $e)
                """, ("$t", Time.Format(DateTimeOffset.UtcNow)), ("$e", Time.Format(DateTimeOffset.UtcNow.AddMinutes(5))));
            Settings.Set(db, AgentService.ResearchWebKey, new JsonObject { ["codex_mcp|cli|0.167.0"] = "yes", ["claude_code_mcp|cli|2.1.318"] = "no" });
        });
        StoreProvider(host, "codex", "Codex");
        Assert.Equal("awaiting_user", (await c.Call("action.get", new JsonObject { ["planId"] = planId })).Str("status"));

        await host.RestartAsync();
        c = await host.ConnectAsync(minor: 5);
        engine = host.Server.Engine;

        // Ended once, with one info line.
        var task = await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });
        Assert.Equal("stopped", task.Str("status"));
        Assert.Equal("codex_mcp", task["recipient"].Str("kind")); // history keeps what it was written with
        Assert.Equal("Codex", task["recipient"].Str("label"));
        Assert.Contains(task["activity"]!.AsArray(), e => e.Str("type") == "agent.tool" && e!["payload"].Str("origin") == "codex");
        Assert.Contains(task["activity"]!.AsArray(), e => e.Str("type") == "agent.task" && e!["payload"].Str("status") == "stopped");
        SchemaTests.AssertValid("agent-task", task);
        foreach (var e in task["activity"]!.AsArray()) SchemaTests.AssertValid("event", e!.AsObject().Also(x => x["stream"] = "task:" + taskId));
        var plan = await c.Call("action.get", new JsonObject { ["planId"] = planId });
        Assert.Equal("cancelled", plan.Str("status"));
        Assert.Equal("codex", plan["requestedBy"].Str("kind"));
        Assert.Equal("Codex", plan["why"].Str("by"));
        SchemaTests.AssertValid("action-plan", plan);
        Assert.Equal("expired", engine.Store.Read(db => db.Scalar("SELECT status FROM agent_pairings WHERE pairing_id = 'pair-codex'")));
        var web = engine.Store.Read(db => Settings.Get(db, AgentService.ResearchWebKey))!.AsObject();
        Assert.Equal(["claude_code_mcp|cli|2.1.318"], web.Select(x => x.Key));
        var card = (await c.Call("followup.get", new JsonObject { ["reportId"] = reportId }))["card"]!;
        Assert.Equal("Codex", card.Str("noteBy"));
        Assert.Equal("codex", (await c.Call("onboarding.get"))["profile"]!["provider"].Str("value"));
        Assert.DoesNotContain((await c.Call("agent.active"))["tasks"]!.AsArray(), t => t.Str("taskId") == taskId);
        lock (log) Assert.Single(log, l => l == "retired provider: stopped 1 task(s), expired 1 pairing(s), dropped 1 research availability row(s)");

        // Nothing left to do on the next start: no second line.
        await host.RestartAsync();
        await host.ConnectAsync(minor: 5);
        lock (log) Assert.Single(log, l => l.StartsWith("retired provider:", StringComparison.Ordinal));
        Assert.Equal(0, pc.Writes);
    }

    private static void StoreProvider(TestHost host, string value, string label) => host.Server.Engine.Store.Write(db =>
    {
        var state = (JsonObject)Engine.Json.Parse((string)db.Scalar("SELECT state FROM onboarding WHERE id = 1")!)!;
        state["profile"]!["provider"] = new JsonObject { ["value"] = value, ["label"] = label };
        db.Exec("UPDATE onboarding SET state = $s WHERE id = 1", ("$s", Engine.Json.Write(state)));
    });
}

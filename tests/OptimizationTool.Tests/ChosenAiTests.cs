using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// The chosen AI is the one used: the onboarding profile's provider.kind, onboarding.save's kind check, -32013 not_chosen
/// for in-app and headless starts, asks and in-app reviews of another kind, and what a change of choice ends (other
/// kinds' in-app and headless tasks; never a paired window). Fake providers and a fake CLI only: nothing touches this PC.
/// </summary>
public sealed class ChosenAiTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "chosen-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude;
    private static readonly CallContext Bridge = new("bridge-chosen", "agent", Environment.ProcessId);

    public ChosenAiTests()
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

    private static async Task<(TestHost Host, Client C, string ReportId)> Start(params string[] keys)
    {
        var o = AgentSetup.Options(null, new FakeProvider("anthropic_api"), new FakeProvider("openai_api"));
        var host = TestHost.StartAgent(new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = new FakeCli(),
        }, [.. FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), new DiagnosticModule()]);
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        foreach (var kind in keys) await AgentSetup.SetKey(c, kind);
        return (host, c, reportId);
    }

    private async Task ConnectClaude(Client c) =>
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["version"] = "2.1.318" });

    private static Task<JsonObject> SaveProvider(Client c, long revision, JsonObject provider) =>
        c.Call("onboarding.save", new JsonObject { ["expectedRevision"] = revision, ["patch"] = new JsonObject { ["provider"] = provider } });

    private static async Task<JsonObject> Choose(Client c, string value, string? kind = null)
    {
        var provider = new JsonObject { ["value"] = value };
        if (kind is not null) provider["kind"] = kind;
        return await SaveProvider(c, (long)(await c.Call("onboarding.get"))["revision"]!, provider);
    }

    /// <summary>agent.start without a grant or pairing: an in-app (or, for Claude Code, headless) task.</summary>
    private static JsonObject StartParams(string reportId, string kind) => new()
    {
        ["idempotencyKey"] = Guid.NewGuid().ToString(), ["goal"] = "Help", ["recipientKind"] = kind,
        ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = null },
    };

    private static async Task<string> StartTask(Client c, string reportId, string kind) => (await c.Call("agent.start", StartParams(reportId, kind))).Str("taskId");

    private static void AssertNotChosen(JsonRpcException e, string? chosen)
    {
        Assert.Equal(ErrorCodes.InvalidState, e.Code);
        Assert.Equal("That AI is not the one chosen.", e.Message);
        Assert.Equal("not_chosen", e.ErrorData.Str("reason"));
        Assert.Equal(chosen, e.ErrorData!["chosen"]?.GetValue<string>());
        Assert.Equal(2, e.ErrorData!.AsObject().Count);
    }

    private static async Task<string> Status(Client c, string taskId) => (await c.Call("agent.get", new JsonObject { ["taskId"] = taskId })).Str("status");

    /// <summary>Rewrites the stored choice without onboarding.save (as a save landing between a task's start and its next ask).</summary>
    private static void StoreChoiceDirectly(TestHost host, JsonObject provider) => host.Server.Engine.Store.Write(db =>
    {
        var state = (JsonObject)JsonNode.Parse((string)db.Scalar("SELECT state FROM onboarding WHERE id = 1")!)!;
        state["profile"]!["provider"] = provider;
        db.Exec("UPDATE onboarding SET state = $s WHERE id = 1", ("$s", state.ToJsonString()));
    });

    // ---------------------------------------------------------------- the record

    [Fact]
    public async Task Save_StoresTheKind_AndRefusesAKindThatDoesNotMatchTheValue()
    {
        var (host, c, _) = await Start();
        await using var _h = host;

        var saved = await Choose(c, "api", "openai_api");
        Assert.Equal("openai_api", saved["profile"]!["provider"].Str("kind"));
        Assert.Equal("api", saved["profile"]!["provider"].Str("value"));
        SchemaTests.AssertValid("onboarding", saved);
        SchemaTests.AssertValid("onboarding", await Choose(c, "claude-code", "claude_code_mcp"));
        SchemaTests.AssertValid("onboarding", await Choose(c, "chatgpt", "chatgpt_plan"));
        var local = await Choose(c, "local");
        Assert.Null(local["profile"]!["provider"]!["kind"]);
        SchemaTests.AssertValid("onboarding", local);

        foreach (var (value, kind) in new[] { ("chatgpt", "openai_api"), ("api", "chatgpt_plan"), ("claude-code", "anthropic_api"), ("local", "anthropic_api"),
                     ("later", "claude_code_mcp"), ("api", "codex"), ("api", "") })
        {
            var e = await c.Fails("onboarding.save", new JsonObject
            {
                ["expectedRevision"] = (long)(await c.Call("onboarding.get"))["revision"]!,
                ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = value, ["kind"] = kind } },
            });
            Assert.Equal((ErrorCodes.InvalidParams, "patch.provider.kind"), (e.Code, e.ErrorData.Str("field")));
        }
        Assert.Null((await c.Call("onboarding.get"))["profile"]!["provider"]!["kind"]); // nothing saved
    }

    [Fact]
    public void TheRecipientAllowlists_AreUnchanged()
    {
        Assert.Equal(["anthropic_api", "openai_api", "chatgpt_plan", "claude_code_mcp"], AgentService.RecipientKinds);
        Assert.Equal(["anthropic_api", "openai_api", "chatgpt_plan"], AgentService.InAppKinds);
        Assert.Equal(["anthropic_api", "openai_api"], AgentService.ApiKeyKinds);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task BothKeys_OpenAiChosen_OpenAiStarts_AnthropicAndClaudeAreRefused()
    {
        var (host, c, reportId) = await Start("anthropic_api", "openai_api");
        await using var _h = host;
        await ConnectClaude(c);
        await Choose(c, "api", "openai_api");

        var openai = await StartTask(c, reportId, "openai_api");
        Assert.Equal("active", await Status(c, openai));
        AssertNotChosen(await c.Fails("agent.start", StartParams(reportId, "anthropic_api")), "openai_api");
        AssertNotChosen(await c.Fails("agent.start", StartParams(reportId, "claude_code_mcp")), "openai_api"); // headless
        Assert.Equal(["openai_api"], (await c.Call("agent.active"))["tasks"]!.AsArray().Select(t => t!["recipient"].Str("kind")));
        // ai.getPermissions.available reads the chosen kind too.
        Assert.True((await c.Call("ai.getPermissions"))["available"]!["tools"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("local")]
    [InlineData("later")]
    public async Task LocalOrLater_RefusesEveryInAppAndHeadlessStart(string value)
    {
        var (host, c, reportId) = await Start("anthropic_api");
        await using var _h = host;
        await ConnectClaude(c);
        await Choose(c, value);
        AssertNotChosen(await c.Fails("agent.start", StartParams(reportId, "anthropic_api")), null);
        AssertNotChosen(await c.Fails("agent.start", StartParams(reportId, "claude_code_mcp")), null);
        Assert.Empty((await c.Call("agent.active"))["tasks"]!.AsArray());
    }

    [Fact]
    public async Task AnOlderApiProfile_WithOneKey_IsThatKind_WithTwoKeys_AcceptsEither()
    {
        var (host, c, reportId) = await Start("anthropic_api");
        await using var _h = host;
        await ConnectClaude(c);
        await Choose(c, "api"); // saved before provider.kind existed: no kind
        Assert.Null((await c.Call("onboarding.get"))["profile"]!["provider"]!["kind"]);

        var anthropic = await StartTask(c, reportId, "anthropic_api");
        AssertNotChosen(await c.Fails("agent.start", StartParams(reportId, "claude_code_mcp")), "anthropic_api");

        await AgentSetup.SetKey(c, "openai_api");
        var openai = await StartTask(c, reportId, "openai_api");
        Assert.Equal(("active", "active"), (await Status(c, anthropic), await Status(c, openai)));
        AssertNotChosen(await c.Fails("agent.start", StartParams(reportId, "claude_code_mcp")), null); // either API kind: no single one
    }

    [Fact]
    public async Task AnAskOnATaskOfAKindNoLongerChosen_IsRefused()
    {
        var (host, c, reportId) = await Start("anthropic_api", "openai_api");
        await using var _h = host;
        await Choose(c, "api", "anthropic_api");
        var task = await StartTask(c, reportId, "anthropic_api");
        await AgentSetup.Ask(c, task);

        StoreChoiceDirectly(host, new JsonObject { ["value"] = "api", ["label"] = "API key", ["kind"] = "openai_api" });
        await Task.Delay(50);
        var e = await c.Fails("agent.ask", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = task, ["text"] = "And now?" });
        AssertNotChosen(e, "openai_api");
    }

    [Fact]
    public async Task AnInAppReview_OfAKindNotChosen_IsRefused_AtPreviewAndAtSubmit()
    {
        var (host, c, reportId) = await Start("anthropic_api", "openai_api");
        await using var _h = host;
        await Choose(c, "api", "openai_api");
        AssertNotChosen(await c.Fails("review.preview", new JsonObject { ["recipientKind"] = "anthropic_api", ["reportId"] = reportId }), "openai_api");

        var preview = await c.Call("review.preview", new JsonObject { ["recipientKind"] = "openai_api", ["reportId"] = reportId });
        await Choose(c, "api", "anthropic_api");
        var e = await c.Fails("review.submit", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["previewId"] = preview.Str("previewId") });
        AssertNotChosen(e, "anthropic_api");
        // Not consumed: refused before anything was sent.
        Assert.Null(host.Server.Engine.Store.Read(db => db.Scalar("SELECT consumed_at FROM reviews WHERE review_id = $r", ("$r", preview.Str("previewId")))) as string);
        // Export (paste anywhere) is not an in-app recipient: unaffected.
        await c.Call("review.preview", new JsonObject { ["recipientKind"] = "claude_code_mcp", ["reportId"] = reportId });
    }

    // ---------------------------------------------------------------- a change of choice

    [Fact]
    public async Task ChangingTheChoice_StopsOtherKindsTasks_AndLeavesAPairedWindowRunning()
    {
        var (host, c, reportId) = await Start("anthropic_api", "openai_api");
        await using var _h = host;
        await Choose(c, "api", "anthropic_api");
        var anthropic = await StartTask(c, reportId, "anthropic_api");

        var pair = await host.Server.Engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge);
        await c.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        var paired = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp", AgentSetup.Grant(), pair.Str("pairingId"))).Str("taskId");

        // Saving the same choice again (another field) ends nothing.
        await c.Call("onboarding.save", new JsonObject { ["expectedRevision"] = (long)(await c.Call("onboarding.get"))["revision"]!, ["patch"] = new JsonObject { ["uses"] = new JsonArray("Gaming") } });
        await Choose(c, "api", "anthropic_api");
        Assert.Equal(("active", "active"), (await Status(c, anthropic), await Status(c, paired)));

        await Choose(c, "api", "openai_api");
        Assert.Equal("stopped", await Status(c, anthropic));
        await AgentSetup.TaskEvent(c, anthropic, "agent.task", p => p.Str("status") == "stopped");
        Assert.Equal("active", await Status(c, paired));
        var openai = await StartTask(c, reportId, "openai_api");
        Assert.Equal(["claude_code_mcp", "openai_api"], (await c.Call("agent.active"))["tasks"]!.AsArray().Select(t => t!["recipient"].Str("kind")).Order());

        // Local only: the in-app task ends too; the paired window still runs.
        await Choose(c, "local");
        Assert.Equal("stopped", await Status(c, openai));
        Assert.Equal("active", await Status(c, paired));
    }

    [Fact]
    public async Task ChangingTheChoice_StopsAHeadlessTask()
    {
        var (host, c, reportId) = await Start("anthropic_api");
        await using var _h = host;
        await ConnectClaude(c);
        await Choose(c, "claude-code", "claude_code_mcp");
        var headless = await StartTask(c, reportId, "claude_code_mcp");
        Assert.Equal("headless", (await c.Call("agent.get", new JsonObject { ["taskId"] = headless }))["recipient"].Str("mode"));
        await Choose(c, "api", "anthropic_api");
        Assert.Equal("stopped", await Status(c, headless));
    }
}

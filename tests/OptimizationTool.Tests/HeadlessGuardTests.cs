using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>contracts/m6-benchmark.md §0: the runtime guard of an in-app Claude Code run (stream and host run).</summary>
public sealed class HeadlessGuardTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "guard-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude;

    public HeadlessGuardTests()
    {
        Directory.CreateDirectory(_bin);
        _claude = Path.Combine(_bin, "claude.exe");
        File.WriteAllText(_claude, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string ClaudeTool(string type, string name) =>
        new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Let me look." }, new JsonObject { ["type"] = type, ["id"] = "t1", ["name"] = name, ["input"] = new JsonObject() }) },
        }.ToJsonString();

    [Theory]
    [InlineData("tool_use", "Bash", "tool_use")]
    [InlineData("tool_use", "WebFetch", "tool_use")]
    [InlineData("tool_use", "mcp__other__read", "tool_use server other")]
    [InlineData("tool_use", "mcp__some_apps__web_run", "tool_use server some_apps")]
    [InlineData("tool_use", "mcp__optimization-tool", "tool_use")] // no tool part: not ours
    [InlineData("server_tool_use", "web_search", "server_tool_use")]
    public void ClaudeGuard_BlocksAnyToolButOurs(string type, string name, string logged)
    {
        var killed = 0;
        var stream = new HeadlessStream(() => killed++);
        stream.Feed(ClaudeTool(type, name));
        stream.Feed("""{"type":"result","is_error":false,"result":"Done.","session_id":"s"}""");
        Assert.Equal(1, killed);
        Assert.Equal(logged, stream.Blocked);
        Assert.Null(stream.Answer);
    }

    [Fact]
    public void ClaudeGuard_AllowsOurMcpTools()
    {
        var killed = 0;
        var stream = new HeadlessStream(() => killed++);
        stream.Feed(ClaudeTool("tool_use", "mcp__optimization-tool__get_cards"));
        stream.Feed("""{"type":"result","is_error":false,"result":"Done.","session_id":"s"}""");
        Assert.Equal(0, killed);
        Assert.Equal("Done.", stream.Answer);
    }

    [Theory]
    [InlineData("tool_use", "WebSearch", "tool_use")]
    [InlineData("mcp_tool_use", "read", "mcp_tool_use")]
    [InlineData("web_fetch_tool_use", "mcp__optimization-tool__x", "")] // a *_tool_use named like ours is still ours
    public void ClaudeGuard_BlocksAnyToolCallBlockNotNamedAsOurs(string type, string name, string logged)
    {
        var killed = 0;
        var stream = new HeadlessStream(() => killed++);
        stream.Feed(ClaudeTool(type, name));
        Assert.Equal(logged.Length > 0 ? 1 : 0, killed);
        if (logged.Length > 0) Assert.Equal(logged, stream.Blocked);
    }

    /// <summary>Content-free status events of Claude Code 2.1.318 and later (m9 fix): never a reason to fail a chat run.</summary>
    private static readonly string[] StatusLines =
    [
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1800000000,"unifiedWindows":{"five_hour":{"utilization":0.5}}},"uuid":"u","session_id":"s"}""",
        """{"type":"system","subtype":"thinking_tokens","thinking_tokens":282,"session_id":"s"}""",
        """{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","type":"auto"},"session_id":"s"}""",
        """{"type":"heartbeat","detail":{"type":"status","items":[1,2]}}""",
    ];

    public static TheoryData<string> StatusEvents => new(StatusLines);

    [Theory]
    [MemberData(nameof(StatusEvents))]
    public void ClaudeGuard_IgnoresContentFreeStatusEvents(string line)
    {
        var killed = 0;
        var stream = new HeadlessStream(() => killed++);
        stream.Feed("""{"type":"system","subtype":"init","session_id":"s","tools":["mcp__optimization-tool__get_cards"]}""");
        stream.Feed(line);
        stream.Feed(ClaudeTool("tool_use", "mcp__optimization-tool__get_cards"));
        stream.Feed(line);
        stream.Feed("""{"type":"result","is_error":false,"result":"Done.","session_id":"s"}""");
        Assert.Equal(0, killed);
        Assert.Null(stream.Blocked);
        Assert.Equal("Done.", stream.Answer);
    }

    public static TheoryData<string, string> SmugglingStatusEvents => new()
    {
        { """{"type":"system","subtype":"task_progress","detail":{"steps":[{"type":"tool_use","id":"t9","name":"Bash","input":{}}]}}""", "system task_progress" },
        { """{"type":"rate_limit_event","info":{"type":"server_tool_use","name":"web_search"}}""", "event rate_limit_event" },
        { """{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"mcp__optimization-tool__get_cards"}}}""", "event stream_event" },
        { """{"type":"assistant_partial","message":{"content":[{"type":"text","text":"x"}]}}""", "event assistant_partial" },
        { "{\"type\":\"rate_limit_event\",\"pad\":\"" + new string('x', StreamShape.MaxEventChars) + "\"}", "event rate_limit_event too large" },
    };

    [Theory]
    [MemberData(nameof(SmugglingStatusEvents))]
    public void ClaudeGuard_KillsOnAStatusEventThatCarriesACallOrContent(string line, string logged)
    {
        var killed = 0;
        var stream = new HeadlessStream(() => killed++);
        stream.Feed(line);
        stream.Feed("""{"type":"result","is_error":false,"result":"Done.","session_id":"s"}""");
        Assert.Equal(1, killed);
        Assert.Equal(logged, stream.Blocked);
        Assert.Null(stream.Answer);
    }

    [Fact]
    public async Task Run_WithStatusEvents_FinishesNormally()
    {
        var cli = new FakeCli();
        await using var host = TestHost.StartAgent(Options(cli));
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true });
        var taskId = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp")).Str("taskId");
        cli.Script = (launch, emit, _) =>
        {
            emit(FakeCli.Claude("system", new JsonObject { ["subtype"] = "init", ["session_id"] = launch.Arguments[^1] }));
            foreach (var l in StatusLines) emit(l);
            emit(FakeCli.Claude("result", new JsonObject { ["is_error"] = false, ["result"] = "Fine.", ["session_id"] = launch.Arguments[^1], ["usage"] = new JsonObject() }));
            return Task.FromResult(new CliExit(0, ""));
        };
        await AgentSetup.Ask(c, taskId, "Look");
        var turn = await AgentSetup.TaskEvent(c, taskId, "agent.turn", t => t.Str("status") is "finished" or "failed");
        Assert.Equal("finished", turn["payload"].Str("status"));
    }

    private static AgentOptions Options(FakeCli cli)
    {
        var o = AgentSetup.Options();
        return new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        };
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"WebFetch","input":{}}]}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__other__x","input":{}}]}}""")]
    public async Task Run_KillsTheProcess_AndFailsTheTurnAsToolBlocked(string line)
    {
        var cli = new FakeCli();
        await using var host = TestHost.StartAgent(Options(cli));
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true });
        var taskId = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp")).Str("taskId");
        var killed = false;
        cli.Script = async (launch, emit, ct) =>
        {
            emit("""{"type":"assistant","message":{"content":[{"type":"text","text":"Partial."}]}}""");
            emit(line);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { killed = true; throw; }
            return new CliExit(0, "");
        };
        await AgentSetup.Ask(c, taskId, "Search the web");
        var turn = await AgentSetup.TaskEvent(c, taskId, "agent.turn", t => t.Str("status") is "finished" or "failed");
        Assert.Equal("failed", turn["payload"].Str("status"));
        Assert.Equal("tool_blocked", turn["payload"].Str("code"));
        Assert.True(killed);
        Assert.DoesNotContain(c.Events, e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.message");
        SchemaTests.AssertValid("event", turn);
        Assert.Equal(8, /* m9 §17.6: a cut-off run keeps its reserved allowance */ (await c.Call("agent.get", new JsonObject { ["taskId"] = taskId }))["used"]!["turns"]!.GetValue<long>());
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>contracts/m6-benchmark.md §0: the Codex lock-down argv, the features probe parser and the runtime guard (both clients).</summary>
public sealed class CodexLockdownTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "lock-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude, _codex;
    private const string Thread = "01a1acef-e7fe-7660-8a5e-dbabb0764106";

    public CodexLockdownTests()
    {
        Directory.CreateDirectory(_bin);
        _claude = Path.Combine(_bin, "claude.exe");
        _codex = Path.Combine(_bin, "codex.exe");
        File.WriteAllText(_claude, "");
        File.WriteAllText(_codex, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- the fixed set and argv

    [Fact]
    public void FeatureSet_IsTheSameInShellAndHost() =>
        Assert.Equal(CodexLockdown.Features, HeadlessCommand.CodexLockdownFeatures);

    [Fact]
    public void Argv_FirstAndResume_DisableTheProbedFeatures_AndTheWebSearchSetting()
    {
        string[] probed = ["shell_tool", "apps", "multi_agent"];
        var first = HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, probed)!;
        var resume = HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), Thread, probed)!;
        string[] lockdown = ["-c", "web_search='disabled'", "--disable", "shell_tool", "--disable", "apps", "--disable", "multi_agent"];
        Assert.Equal(["exec", "--sandbox", "read-only", "-C", @"C:\run", "--json", "--skip-git-repo-check", "--ignore-user-config", .. lockdown], first.Take(16));
        Assert.Equal(["exec", "--sandbox", "read-only", "-C", @"C:\run", "resume", Thread, "--json", "--skip-git-repo-check", "--ignore-user-config", .. lockdown], resume.Take(18));
        Assert.Equal(3, first.Count(a => a == "--disable"));
        Assert.NotNull(ProcessCommand.ForCli(@"C:\Users\u\AppData\Roaming\npm\codex.cmd", first, @"C:\Windows")); // a .cmd line can carry it

        // Nothing probed (all absent or removed): only the setting.
        var none = HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, [])!;
        Assert.DoesNotContain("--disable", none);
        Assert.Contains("web_search='disabled'", none);
    }

    [Fact]
    public void Argv_RefusesANameOutsideTheSet_OrARepeat()
    {
        Assert.Null(HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, ["web_search_request"]));
        Assert.Null(HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, ["apps' -c evil='1"]));
        Assert.Null(HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, ["apps", "apps"]));
    }

    // ---------------------------------------------------------------- the probe parser

    private const string Listing = """
        apps                                     stable             true
        apps_mcp_path_override                   removed            false
        browser_use                              stable             true
        code_mode_host                           stable             true
        computer_use                             under development  false
        in_app_browser                           removed            false
        multi_agent                              experimental       true
        shell_tool                               stable             true
        unified_exec                             deprecated         false
        web_search_request                       deprecated         false
        """;

    [Fact]
    public void Features_ListedAreDisabled_RemovedAndAbsentAreSkipped()
    {
        var features = CodexLockdown.ParseFeatures(Listing.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(["shell_tool", "unified_exec", "apps", "browser_use", "computer_use", "multi_agent"], features);
    }

    [Fact]
    public void Features_RequiredMayBeRemoved()
    {
        var features = CodexLockdown.ParseFeatures("apps  removed  false\ncode_mode_host  removed  false\nplugins  stable  true\n");
        Assert.Equal(["plugins"], features);
    }

    [Theory]
    [InlineData("")]
    [InlineData("error: unrecognized subcommand 'features'")]
    [InlineData("plugins stable true\n")]                  // apps absent
    [InlineData("code_mode_host stable true\n")]           // apps absent (code_mode_host is not disabled: Codex routes MCP calls through it)
    [InlineData("apps stable maybe\ncode_mode_host stable true\n")]
    [InlineData("{\"apps\":true,\"code_mode_host\":true}")]
    [InlineData(null)]
    public void Features_GarbageOrMissingRequired_FailsClosed(string? output) =>
        Assert.Null(CodexLockdown.ParseFeatures(output));

    [Theory]
    [InlineData("codex-cli 0.167.0-alpha.3.2", "0.167.0", true)]
    [InlineData("0.167.0", "0.167.0", true)]
    [InlineData("codex-cli 0.168.1", "0.168.1", true)]
    [InlineData("codex-cli 1.0.0", "1.0.0", true)]
    [InlineData("codex-cli 0.166.9", "0.166.9", false)]
    [InlineData("codex-cli 0.98.0", "0.98.0", false)]
    [InlineData("codex-cli", null, false)]
    [InlineData("", null, false)]
    [InlineData(null, null, false)]
    public void Version_ParsesTheNumericPart_AndChecksTheMinimum(string? text, string? expected, bool supported)
    {
        Assert.Equal(expected, CodexLockdown.ParseVersion(text)?.ToString());
        Assert.Equal(supported, CodexLockdown.VersionSupported(text));
    }

    // ---------------------------------------------------------------- the runtime guard (stream)

    [Theory]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"web_search","query":"x"}}""", "web_search")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"dir"}}""", "command_execution")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"file_change","changes":[]}}""", "file_change")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","server":"codex_apps","tool":"web_run"}}""", "mcp_tool_call server codex_apps")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","server":"evil server; x","tool":"t"}}""", "mcp_tool_call server other")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","tool":"t"}}""", "mcp_tool_call server other")]
    [InlineData("""{"type":"item.completed","item":{"id":"i1","type":"web_search","query":"x"}}""", "web_search")] // no item.started
    public void CodexGuard_BlocksEachForbiddenItem(string line, string logged)
    {
        var killed = 0;
        var stream = new HeadlessStream(codex: true, () => killed++);
        stream.Feed("""{"type":"item.completed","item":{"id":"i0","type":"agent_message","text":"Partial."}}""");
        stream.Feed(line);
        stream.Feed("""{"type":"item.completed","item":{"id":"i2","type":"agent_message","text":"After."}}""");
        Assert.Equal(1, killed);
        Assert.Equal(logged, stream.Blocked);
        Assert.Null(stream.Answer); // the partial answer is dropped
    }

    [Theory]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","server":"optimization-tool","tool":"get_cards"}}""")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"collab_tool_call","tool":"spawn_agent"}}""")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"reasoning","text":"…"}}""")]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"todo_list","items":[]}}""")]
    public void CodexGuard_AllowsOurToolsAndSubAgents(string line)
    {
        var killed = 0;
        var stream = new HeadlessStream(codex: true, () => killed++);
        stream.Feed(line);
        stream.Feed(line.Replace("item.started", "item.completed", StringComparison.Ordinal));
        stream.Feed("""{"type":"item.completed","item":{"id":"i2","type":"agent_message","text":"Fine."}}""");
        Assert.Equal(0, killed);
        Assert.Null(stream.Blocked);
        Assert.Equal("Fine.", stream.Answer);
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
    [InlineData("tool_use", "mcp__codex_apps__web_run", "tool_use server codex_apps")]
    [InlineData("tool_use", "mcp__optimization-tool", "tool_use")] // no tool part: not ours
    [InlineData("server_tool_use", "web_search", "server_tool_use")]
    public void ClaudeGuard_BlocksAnyToolButOurs(string type, string name, string logged)
    {
        var killed = 0;
        var stream = new HeadlessStream(codex: false, () => killed++);
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
        var stream = new HeadlessStream(codex: false, () => killed++);
        stream.Feed(ClaudeTool("tool_use", "mcp__optimization-tool__get_cards"));
        stream.Feed("""{"type":"result","is_error":false,"result":"Done.","session_id":"s"}""");
        Assert.Equal(0, killed);
        Assert.Equal("Done.", stream.Answer);
    }

    // ---------------------------------------------------------------- the runtime guard (host run)

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
    [InlineData(true, """{"type":"item.started","item":{"id":"i1","type":"web_search","query":"x"}}""")]
    [InlineData(true, """{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"dir"}}""")]
    [InlineData(true, """{"type":"item.started","item":{"id":"i1","type":"file_change","changes":[]}}""")]
    [InlineData(true, """{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","server":"codex_apps","tool":"web_run"}}""")]
    [InlineData(false, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"WebFetch","input":{}}]}}""")]
    [InlineData(false, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__other__x","input":{}}]}}""")]
    public async Task Run_KillsTheProcess_AndFailsTheTurnAsToolBlocked(bool codex, string line)
    {
        var cli = new FakeCli();
        await using var host = TestHost.StartAgent(Options(cli));
        host.Server.Engine.Agent.CodexCredentialsStore = () => null;
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        var kind = codex ? "codex_mcp" : "claude_code_mcp";
        var p = new JsonObject { ["kind"] = kind, ["path"] = codex ? _codex : _claude, ["signedIn"] = true };
        if (codex) p["codexFeatures"] = new JsonArray("shell_tool", "apps");
        await c.Call("provider.setClient", p);
        var taskId = (await AgentSetup.StartTask(c, reportId, kind)).Str("taskId");
        var killed = false;
        cli.Script = async (launch, emit, ct) =>
        {
            if (codex) emit($$"""{"type":"thread.started","thread_id":"{{Thread}}"}""");
            emit(codex ? """{"type":"item.completed","item":{"id":"i0","type":"agent_message","text":"Partial."}}"""
                : """{"type":"assistant","message":{"content":[{"type":"text","text":"Partial."}]}}""");
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
        Assert.Equal(1, (await c.Call("agent.get", new JsonObject { ["taskId"] = taskId }))["used"]!["turns"]!.GetValue<long>());
    }

    // ---------------------------------------------------------------- provider.setClient codexFeatures

    [Fact]
    public async Task SetClient_CodexWithoutProbedFeatures_IsOutdated()
    {
        await using var host = TestHost.StartAgent(Options(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "codex_mcp", ["path"] = _codex, ["signedIn"] = true });
        var entry = (await c.Call("provider.list"))["providers"]!.AsArray().Single(x => x.Str("kind") == "codex_mcp")!;
        Assert.Equal("error", entry.Str("status"));
        Assert.Equal("unavailable", entry.Str("detail"));

        await c.Call("provider.setClient", new JsonObject { ["kind"] = "codex_mcp", ["path"] = _codex, ["signedIn"] = true, ["codexFeatures"] = new JsonArray() });
        entry = (await c.Call("provider.list"))["providers"]!.AsArray().Single(x => x.Str("kind") == "codex_mcp")!;
        Assert.Equal("connected", entry.Str("status"));
    }

    [Theory]
    [InlineData("""{"kind":"codex_mcp","path":null,"signedIn":null,"codexFeatures":["web_search_request"]}""")]
    [InlineData("""{"kind":"codex_mcp","path":null,"signedIn":null,"codexFeatures":["apps","apps"]}""")]
    [InlineData("""{"kind":"codex_mcp","path":null,"signedIn":null,"codexFeatures":"apps"}""")]
    [InlineData("""{"kind":"codex_mcp","path":null,"signedIn":null,"codexFeatures":[1]}""")]
    [InlineData("""{"kind":"claude_code_mcp","path":null,"signedIn":null,"codexFeatures":["apps"]}""")]
    public async Task SetClient_RefusesBadCodexFeatures(string json)
    {
        await using var host = TestHost.StartAgent(Options(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        Assert.Equal(-32602, (await c.Fails("provider.setClient", (JsonObject)JsonNode.Parse(json)!)).Code);
    }
}

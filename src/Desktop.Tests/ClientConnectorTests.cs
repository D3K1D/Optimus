using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Tests;

public sealed class ClientConnectorTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string Bridge = @"C:\Program Files\Optimization Tool\OptimizationTool.Mcp.exe";
    private const string NpmDir = @"C:\Users\u\AppData\Roaming\npm";
    private const string ClaudeCmd = NpmDir + @"\claude.cmd";
    private const string CodexExe = @"C:\Tools\codex\codex.exe";
    private const string SysRoot = @"C:\Windows";
    private const string Cmd = @"C:\Windows\System32\cmd.exe";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Bridge };
    private string _path = @"C:\Windows\system32;" + NpmDir + @";C:\Tools\codex";
    private readonly FakeRunner _runner = new();
    private readonly FakeConfirm _confirm = new();
    private readonly BridgeDispatcher _dispatcher;

    public ClientConnectorTests()
    {
        var connector = new ClientConnector(_runner, new CliLocator(() => _path, f => _files.Contains(f)), _confirm, Bridge,
            systemRoot: SysRoot, fileExists: f => _files.Contains(f));
        _dispatcher = new BridgeDispatcher(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled, clients: connector);
    }

    private sealed class NoBackend : IBackend
    {
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            throw new InvalidOperationException("shell.* must not be forwarded");
    }

    private sealed class FakeConfirm : IClientConfirmPrompt
    {
        public bool Answer = true;
        public readonly List<ClientConfirmView> Shown = [];
        public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct) { Shown.Add(view); return Task.FromResult(Answer); }
    }

    /// <summary>Simulates the CLI's registration state; records every command.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public readonly List<ProcessCommand> Runs = [];
        public string? Registered;          // command path in the CLI's config, null = no entry
        public string? RawGetOutput;        // overrides the get output
        public int? GetExit;
        public bool AddFails;
        public string Flavor = "claude";

        public Task<ProcessResult> RunAsync(ProcessCommand c, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add(c);
            var args = c.Argv.Skip(1).ToArray();
            ProcessResult r = args switch
            {
                ["--version"] => new(0, Flavor == "claude" ? "2.0.14 (Claude Code)\n" : "codex-cli 0.46.0\n", "", false),
                ["--help"] => new(0, CliHelp.Claude, "", false),
                ["exec", "--help"] => new(0, CliHelp.Codex, "", false),
                ["mcp", "get", "optimization-tool"] => Get(),
                ["mcp", "add", .., "--", var path] => Add(path),
                ["mcp", "remove", ..] => Remove(),
                _ => new(2, "", "unknown command", false),
            };
            return Task.FromResult(r);
        }

        private ProcessResult Get()
        {
            if (RawGetOutput is not null) return new(GetExit ?? 0, RawGetOutput, "", false);
            if (Registered is null)
                return Flavor == "claude"
                    ? new(1, "", "No MCP server found with name: optimization-tool\n", false)
                    : new(1, "", "Error: No MCP server named 'optimization-tool' found.\n", false);
            return Flavor == "claude"
                ? new(0, $"optimization-tool:\n  Scope: User config (available in all your projects)\n  Status: ✓ Connected\n  Type: stdio\n  Command: {Registered}\n  Args: \n  Environment:\n\nTo remove this server, run: claude mcp remove \"optimization-tool\" -s user\n", "", false)
                : new(0, $"optimization-tool\n  enabled: true\n  transport: stdio\n  command: {Registered}\n  args: -\n  cwd: -\n  env: -\n  remove: codex mcp remove optimization-tool\n", "", false);
        }

        private ProcessResult Add(string path)
        {
            if (AddFails) return new(1, "", "boom", false);
            if (Registered is not null) return new(1, "", "already exists", false);
            Registered = path;
            return new(0, "Added stdio MCP server optimization-tool\n", "", false);
        }

        private ProcessResult Remove()
        {
            if (Registered is null) return new(1, "", "not found", false);
            Registered = null;
            return new(0, "Removed\n", "", false);
        }
    }

    private async Task<JsonNode> Call(string method, string paramsJson, int id = 1) =>
        JsonNode.Parse((await _dispatcher.HandleAsync(
            $$$"""{"type":"rpc","id":{{{id}}},"method":"{{{method}}}","params":{{{paramsJson}}}}""", Doc, true, default))!)!;

    private static string[] Args(ProcessCommand c) => c.Argv.Skip(1).ToArray();

    /// <summary>Runs other than the --version identity check and the --help capability probe.</summary>
    private List<ProcessCommand> McpRuns => _runner.Runs.Where(r => Args(r) is not (["--version"] or ["--help"] or ["exec", "--help"])).ToList();

    // ---------- status ----------

    [Fact]
    public async Task Status_not_installed_runs_nothing()
    {
        _path = @"C:\Windows\system32";
        var r = (await Call("shell.clientStatus", """{"client":"claude_code"}"""))["result"]!;
        Assert.False(r["installed"]!.GetValue<bool>());
        Assert.Equal("no", r["registered"]!.GetValue<string>());
        Assert.Equal($"claude mcp add --scope user optimization-tool -- \"{Bridge}\"", r["command"]!.GetValue<string>());
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task Status_installed_not_registered()
    {
        _files.Add(ClaudeCmd);
        var r = (await Call("shell.clientStatus", """{"client":"claude_code"}"""))["result"]!;
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.Equal("2.0.14", r["version"]!.GetValue<string>());
        Assert.Equal("no", r["registered"]!.GetValue<string>());
        Assert.Equal([["--version"], ["--help"], ["mcp", "get", "optimization-tool"], ["auth", "status"]], _runner.Runs.Select(Args)); // capability probe; m5: then the sign-in check
    }

    [Theory]
    [InlineData("claude", Bridge, "yes")]
    [InlineData("claude", @"c:\program files\optimization tool\OPTIMIZATIONTOOL.MCP.EXE", "yes")]
    [InlineData("claude", @"C:\Old\OptimizationTool.Mcp.exe", "outdated")]
    [InlineData("codex", Bridge, "yes")]
    [InlineData("codex", @"D:\dev\OptimizationTool.Mcp.exe", "outdated")]
    [InlineData("codex", "OptimizationTool.Mcp.exe", "outdated")]
    public async Task Status_compares_the_registered_path(string flavor, string registered, string expected)
    {
        _files.Add(ClaudeCmd);
        _files.Add(CodexExe);
        _runner.Flavor = flavor;
        _runner.Registered = registered;
        var client = flavor == "claude" ? "claude_code" : "codex";
        var r = (await Call("shell.clientStatus", $$"""{"client":"{{client}}"}"""))["result"]!;
        Assert.Equal(expected, r["registered"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0, "something entirely different\n", "unknown")]
    [InlineData(0, "", "unknown")]
    [InlineData(3, "crashed\n", "unknown")]
    [InlineData(1, "Error: server \"optimization-tool\" not found\n", "no")]
    [InlineData(0, "{\"name\":\"optimization-tool\",\"command\":\"\"}", "unknown")]
    public async Task Status_is_robust_to_unexpected_get_output(int exit, string output, string expected)
    {
        _files.Add(ClaudeCmd);
        _runner.RawGetOutput = output;
        _runner.GetExit = exit;
        var r = (await Call("shell.clientStatus", """{"client":"claude_code"}"""))["result"]!;
        Assert.Equal(expected, r["registered"]!.GetValue<string>());
    }

    [Fact]
    public void Get_timeout_is_unknown() =>
        Assert.Equal(Registration.Unknown, ClientConnector.ParseGet(new ProcessResult(-1, "", "", true), Bridge));

    // ---------- register ----------

    [Fact]
    public async Task Register_claude_shim_runs_add_then_get_through_cmd_with_exact_argv()
    {
        _files.Add(ClaudeCmd);
        var r = (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!;
        Assert.Equal("""{"status":"added"}""", r.ToJsonString());

        var view = Assert.Single(_confirm.Shown);
        Assert.Equal("Add Optimus to Claude Code?", view.Title);
        Assert.Equal("Add", view.Verb);
        Assert.Equal(Bridge, view.BridgePath);
        Assert.Equal($"claude mcp add --scope user optimization-tool -- \"{Bridge}\"", view.Command);

        Assert.Equal(
            [["mcp", "get", "optimization-tool"],
             ["mcp", "add", "--scope", "user", "optimization-tool", "--", Bridge],
             ["mcp", "get", "optimization-tool"]],
            McpRuns.Select(Args));
        var add = McpRuns[1];
        Assert.Equal(Cmd, add.FileName);
        Assert.Empty(add.ArgumentList);
        Assert.Equal($"/d /s /c \"\"{ClaudeCmd}\" \"mcp\" \"add\" \"--scope\" \"user\" \"optimization-tool\" \"--\" \"{Bridge}\"\"", add.CmdArguments);
    }

    [Fact]
    public async Task Register_outdated_runs_exactly_remove_add_get_after_confirm()
    {
        _files.Add(ClaudeCmd);
        _runner.Registered = @"C:\Old\OptimizationTool.Mcp.exe";
        var r = (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!;
        Assert.Equal("added", r["status"]!.GetValue<string>());
        Assert.Equal(Bridge, _runner.Registered);
        Assert.Equal(
            [["mcp", "remove", "--scope", "user", "optimization-tool"],
             ["mcp", "add", "--scope", "user", "optimization-tool", "--", Bridge],
             ["mcp", "get", "optimization-tool"]],
            McpRuns.Skip(1).Select(Args)); // after the initial state check
    }

    [Fact]
    public async Task Register_codex_exe_uses_argument_list_without_shell()
    {
        _files.Add(CodexExe);
        _runner.Flavor = "codex";
        _runner.Registered = @"C:\Old\OptimizationTool.Mcp.exe";
        var r = (await Call("shell.registerClient", """{"client":"codex"}"""))["result"]!;
        Assert.Equal("added", r["status"]!.GetValue<string>());
        var after = McpRuns.Skip(1).ToList();
        Assert.Equal(
            [["mcp", "remove", "optimization-tool"], ["mcp", "add", "optimization-tool", "--", Bridge], ["mcp", "get", "optimization-tool"]],
            after.Select(Args));
        Assert.All(after, c => { Assert.Equal(CodexExe, c.FileName); Assert.Null(c.CmdArguments); });
        Assert.Equal(["mcp", "add", "optimization-tool", "--", Bridge], after[1].ArgumentList);
        Assert.Equal($"codex mcp add optimization-tool -- \"{Bridge}\"", Assert.Single(_confirm.Shown).Command);
    }

    [Fact]
    public async Task Register_cancel_runs_nothing_after_the_state_check()
    {
        _files.Add(ClaudeCmd);
        _confirm.Answer = false;
        var r = (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!;
        Assert.Equal("""{"status":"cancelled"}""", r.ToJsonString());
        Assert.Equal([["mcp", "get", "optimization-tool"]], McpRuns.Select(Args));
        Assert.Null(_runner.Registered);
    }

    [Fact]
    public async Task Register_not_installed_and_missing_bridge()
    {
        Assert.Equal("not_installed", (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!["status"]!.GetValue<string>());
        _files.Add(ClaudeCmd);
        _files.Remove(Bridge);
        Assert.Equal("""{"status":"failed","error":"bridge_missing"}""", (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!.ToJsonString());
        Assert.Empty(_runner.Runs);
        Assert.Empty(_confirm.Shown);
    }

    [Fact]
    public async Task Register_already_current_does_not_prompt_or_change()
    {
        _files.Add(ClaudeCmd);
        _runner.Registered = Bridge;
        Assert.Equal("added", (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!["status"]!.GetValue<string>());
        Assert.Empty(_confirm.Shown);
        Assert.Single(McpRuns);
    }

    [Fact]
    public async Task Register_failed_add_is_reported()
    {
        _files.Add(ClaudeCmd);
        _runner.AddFails = true;
        Assert.Equal("""{"status":"failed","error":"add_failed"}""", (await Call("shell.registerClient", """{"client":"claude_code"}"""))["result"]!.ToJsonString());
    }

    // ---------- unregister ----------

    [Fact]
    public async Task Unregister_confirms_then_removes()
    {
        _files.Add(ClaudeCmd);
        _runner.Registered = Bridge;
        var r = (await Call("shell.unregisterClient", """{"client":"claude_code"}"""))["result"]!;
        Assert.Equal("""{"status":"removed"}""", r.ToJsonString());
        Assert.Equal("Remove Optimus from Claude Code?", Assert.Single(_confirm.Shown).Title);
        Assert.Equal([["mcp", "remove", "--scope", "user", "optimization-tool"], ["mcp", "get", "optimization-tool"]], McpRuns.Select(Args));
        Assert.Null(_runner.Registered);
    }

    [Fact]
    public async Task Unregister_cancel_runs_nothing()
    {
        _files.Add(ClaudeCmd);
        _runner.Registered = Bridge;
        _confirm.Answer = false;
        Assert.Equal("cancelled", (await Call("shell.unregisterClient", """{"client":"claude_code"}"""))["result"]!["status"]!.GetValue<string>());
        Assert.Empty(McpRuns);
        Assert.Equal(Bridge, _runner.Registered);
    }

    // ---------- renderer cannot steer anything ----------

    [Theory]
    [InlineData("""{"client":"claude"}""")]
    [InlineData("""{"client":"Claude_Code"}""")]
    [InlineData("""{"client":"claude_code "}""")]
    [InlineData("""{"client":"claude_code & calc"}""")]
    [InlineData("""{"client":"..\\..\\evil"}""")]
    [InlineData("""{"client":"codex","path":"C:\\evil\\OptimizationTool.Mcp.exe"}""")]
    [InlineData("""{"client":"codex","args":["--","x"]}""")]
    [InlineData("""{"client":"claude_code","command":"C:\\evil.exe"}""")]
    [InlineData("""{"client":["codex"]}""")]
    [InlineData("""{"client":7}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Bad_params_are_rejected_without_running_anything(string paramsJson)
    {
        _files.Add(ClaudeCmd);
        _files.Add(CodexExe);
        foreach (var m in new[] { "shell.clientStatus", "shell.registerClient", "shell.unregisterClient" })
        {
            var reply = await Call(m, paramsJson);
            Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        }
        Assert.Empty(_runner.Runs);
        Assert.Empty(_confirm.Shown);
    }

    [Fact]
    public async Task Frames_and_other_origins_are_ignored()
    {
        _files.Add(ClaudeCmd);
        const string msg = """{"type":"rpc","id":1,"method":"shell.registerClient","params":{"client":"claude_code"}}""";
        Assert.Null(await _dispatcher.HandleAsync(msg, Doc, isTopLevel: false, default));
        Assert.Null(await _dispatcher.HandleAsync(msg, "https://evil.example/", isTopLevel: true, default));
        Assert.Empty(_runner.Runs);
    }

    // ---------- command building / PATH ----------

    [Theory]
    [InlineData(@"C:\a&b\OptimizationTool.Mcp.exe")]
    [InlineData(@"C:\a%PATH%\OptimizationTool.Mcp.exe")]
    [InlineData(@"C:\a""b\OptimizationTool.Mcp.exe")]
    [InlineData(@"C:\a^b\OptimizationTool.Mcp.exe")]
    [InlineData(@"C:\a|b\OptimizationTool.Mcp.exe")]
    [InlineData(@"C:\a<b\OptimizationTool.Mcp.exe")]
    [InlineData(@"C:\a!b\OptimizationTool.Mcp.exe")]
    public void Unsafe_tokens_are_refused_for_cmd_shims(string bridgePath) =>
        Assert.Null(ProcessCommand.ForCli(ClaudeCmd, ["mcp", "add", "--", bridgePath], SysRoot));

    [Theory]
    [InlineData(@"C:\npm&calc\claude.cmd")]
    [InlineData(@"claude.cmd")]
    [InlineData(@"C:\npm\claude.bat")]
    [InlineData(@"C:\npm\claude.ps1")]
    public void Unsafe_or_unsupported_cli_paths_are_refused(string cli) =>
        Assert.Null(ProcessCommand.ForCli(cli, ["mcp", "get", "optimization-tool"], SysRoot));

    [Fact]
    public async Task Install_path_with_cmd_metacharacters_fails_safely()
    {
        var bad = @"C:\Apps & Stuff\OptimizationTool.Mcp.exe";
        _files.Add(bad);
        _files.Add(ClaudeCmd);
        var connector = new ClientConnector(_runner, new CliLocator(() => _path, f => _files.Contains(f)), _confirm, bad,
            systemRoot: SysRoot, fileExists: f => _files.Contains(f));
        var d = new BridgeDispatcher(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled, clients: connector);
        var r = JsonNode.Parse((await d.HandleAsync("""{"type":"rpc","id":1,"method":"shell.registerClient","params":{"client":"claude_code"}}""", Doc, true, default))!)!;
        Assert.Equal("""{"status":"failed","error":"unsupported_path"}""", r["result"]!.ToJsonString());
        Assert.Empty(McpRuns);
        Assert.Empty(_confirm.Shown);
    }

    [Fact]
    public void Path_search_skips_relative_entries_and_prefers_exe()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(@".\claude.exe"), @"C:\a\claude.cmd", @"C:\a\claude.exe", @"C:\b\codex.cmd",
        };
        var locator = new CliLocator(() => @".;""C:\a"";;C:\b", files.Contains);
        Assert.Equal(@"C:\a\claude.exe", locator.Find("claude"));
        Assert.Equal(@"C:\b\codex.cmd", locator.Find("codex"));
        Assert.Null(locator.Find("other"));
    }
}

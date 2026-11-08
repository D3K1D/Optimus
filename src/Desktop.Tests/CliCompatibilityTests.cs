using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Tests;

/// <summary>Help text excerpts (as printed by Claude Code 2.1.318 and codex-cli 0.167) the capability probe accepts.</summary>
internal static class CliHelp
{
    public const string Claude = """
        Usage: claude [options] [command] [prompt]

        Options:
          --allowedTools, --allowed-tools <tools...>
          --append-system-prompt <prompt>       Append a system prompt to the default
          --mcp-config <configs...>             Load MCP servers from JSON files or
          --output-format <format>              Output format (only works with --print):
          -r, --resume [value]                  Resume a conversation by session ID or
          --session-id <uuid>                   Use a specific session ID for the
          --strict-mcp-config                   Only use MCP servers from --mcp-config,
          --tools <tools...>                    Specify the list of available tools from
          --verbose                             Override verbose mode setting from

        Commands:
          auth                                  Manage authentication
          mcp                                   Configure and manage MCP servers
        """;

    public const string Codex = """
        Run Codex non-interactively

        Usage: codex exec [OPTIONS] [PROMPT] [COMMAND]

        Commands:
          resume  Resume a previous session by id or pick the most recent with --last
          help    Print this message or the help of the given subcommand(s)

        Options:
          -c, --config <key=value>
              --disable <FEATURE>
          -s, --sandbox <SANDBOX_MODE>
          -C, --cd <DIR>
              --skip-git-repo-check
              --ignore-user-config
              --json
        """;
}

/// <summary>Cross-PC CLI handling: capability probe and outdated CLIs, blocked launches, Codex/Claude sign-in forms and auth kind, npm shims, re-sent reports.</summary>
public sealed class CliCompatibilityTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string Bridge = @"C:\Program Files\Optimization Tool\OptimizationTool.Mcp.exe";
    private const string OldCodex = @"C:\Old\codex.exe";
    private const string NewCodex = @"C:\New\codex.exe";
    private const string OldClaude = @"C:\Old\claude.exe";
    private const string NewClaude = @"C:\New\claude.exe";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Bridge };
    private readonly Runner _runner = new();
    private readonly Backend _backend = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private string _path = @"C:\Old;C:\New";

    private ClientConnector Connector(NpmShimEnvironment? npm = null) =>
        new(_runner, new CliLocator(() => _path, _files.Contains), new NoConfirm(), Bridge, systemRoot: @"C:\Windows",
            fileExists: _files.Contains, npm: npm ?? NpmShimEnvironment.None, environment: n => _env.GetValueOrDefault(n));

    private BridgeDispatcher Dispatcher(ClientConnector connector) => new(new BridgeRouter(), _backend, DesktopLog.Disabled, clients: connector);

    private sealed class NoConfirm : IClientConfirmPrompt
    {
        public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Backend : IBackend
    {
        public readonly List<(string Method, JsonObject? Params)> Calls = [];
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            return Task.FromResult(new JsonObject { ["result"] = new JsonObject() });
        }
    }

    /// <summary>Per-path answers: version, help and auth status; anything not set behaves like a current, signed-in CLI.</summary>
    private sealed class Runner : IProcessRunner
    {
        public readonly Dictionary<string, ProcessResult> Version = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ProcessResult> Help = new(StringComparer.OrdinalIgnoreCase);
        public ProcessResult? Auth;
        public readonly List<ProcessCommand> Runs = [];

        public Task<ProcessResult> RunAsync(ProcessCommand c, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add(c);
            var cli = c.Argv[0];
            var claude = Path.GetFileNameWithoutExtension(cli) == "claude";
            ProcessResult r = c.Argv.Skip(1).ToArray() switch
            {
                ["--version"] => Version.GetValueOrDefault(cli) ?? new(0, claude ? "2.1.318 (Claude Code)\n" : "codex-cli 0.167.0\n", "", false),
                ["--help"] or ["exec", "--help"] => Help.GetValueOrDefault(cli) ?? new(0, claude ? CliHelp.Claude : CliHelp.Codex, "", false),
                ["mcp", "get", _] => new(1, "", "No MCP server found with name: optimization-tool\n", false),
                ["auth", "status"] => Auth ?? new(0, """{"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty"}""", "", false),
                ["login", "status"] => Auth ?? new(0, "", "Logged in using ChatGPT\n", false),
                _ => new(2, "", "", false),
            };
            return Task.FromResult(r);
        }
    }

    private static async Task<JsonNode> Status(BridgeDispatcher d, string client) =>
        JsonNode.Parse((await d.HandleAsync($$$"""{"type":"rpc","id":1,"method":"shell.clientStatus","params":{"client":"{{{client}}}"}}""", Doc, true, default))!)!["result"]!;

    private JsonObject LastReport => _backend.Calls.Last().Params!;

    // ---------- 1. Codex login status forms ----------

    [Theory]
    [InlineData(0, "", "Logged in using ChatGPT\n", true, "subscription")]
    [InlineData(0, "", "Logged in using an API key - sk-proj-***ABCD\n", true, "api_key")]
    [InlineData(0, "", "API key configured - sk-proj-***ABCD\n", true, "api_key")]
    [InlineData(0, "API key configured - sk-***\n", "", true, "api_key")]
    [InlineData(0, "", "Logged in using workload identity\n", true, "other")]
    [InlineData(1, "", "Not logged in\n", false, null)]
    [InlineData(1, "", "Error checking login status: permission denied\n", null, null)]
    [InlineData(101, "", "thread 'main' panicked\n", null, null)]
    public void Codex_LoginStatus_AllSuccessForms(int exit, string stdout, string stderr, bool? signedIn, string? auth)
    {
        var r = new ProcessResult(exit, stdout, stderr, false);
        Assert.Equal(signedIn, ClientConnector.ParseSignedIn("codex", r));
        Assert.Equal(auth, signedIn == true ? ClientConnector.ParseAuth("codex", r) : null);
    }

    [Fact]
    public async Task Codex_ApiKeyConfigured_IsSignedIn_AndTheKeyFragmentIsNeverReported()
    {
        _path = @"C:\New";
        _files.Add(NewCodex);
        _runner.Auth = new ProcessResult(0, "", "API key configured - sk-proj-***WXYZ\n", false);
        var r = await Status(Dispatcher(Connector()), "codex");
        Assert.True(r["signedIn"]!.GetValue<bool>());
        Assert.Equal("api_key", r["auth"]!.GetValue<string>());
        Assert.DoesNotContain("sk-", r.ToJsonString(), StringComparison.Ordinal);
        Assert.True(LastReport["signedIn"]!.GetValue<bool>());
        Assert.Equal("api_key", LastReport["auth"]!.GetValue<string>());
        Assert.DoesNotContain("sk-", LastReport.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchFailure_OrTimeout_IsUnknown_NotSignedOut()
    {
        Assert.Null(ClientConnector.ParseSignedIn("codex", new ProcessResult(-1, "", "", false, LaunchFailure.Policy)));
        Assert.Null(ClientConnector.ParseSignedIn("claude_code", new ProcessResult(-1, "", "", false, LaunchFailure.AccessDenied)));
        Assert.Null(ClientConnector.ParseSignedIn("codex", new ProcessResult(-1, "", "", true)));
    }

    // ---------- 7. Claude auth kind ----------

    [Theory]
    [InlineData("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty"}""", "subscription")]
    [InlineData("""{"loggedIn":true,"authMethod":"oauth_token"}""", "subscription")]
    [InlineData("""{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty"}""", "api_key")]
    [InlineData("""{"loggedIn":true,"authMethod":"apiKeyHelper"}""", "api_key")]
    [InlineData("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"bedrock"}""", "other")]
    [InlineData("""{"loggedIn":true,"authMethod":"something_new"}""", "other")]
    [InlineData("""{"loggedIn":true}""", null)]
    public void Claude_AuthMethod_Maps(string json, string? expected) =>
        Assert.Equal(expected, ClientConnector.ParseAuth("claude_code", new ProcessResult(0, json, "", false)));

    [Fact]
    public async Task Claude_Subscription_ButAnApiKeyInTheEnvironment_ReportsApiKey()
    {
        _path = @"C:\New";
        _files.Add(NewClaude);
        var connector = Connector();
        var r = await Status(Dispatcher(connector), "claude_code");
        Assert.Equal("subscription", r["auth"]!.GetValue<string>());
        Assert.Equal("subscription", LastReport["auth"]!.GetValue<string>());

        _env["ANTHROPIC_API_KEY"] = "sk-ant-test";
        r = await Status(Dispatcher(connector), "claude_code");
        Assert.Equal("api_key", r["auth"]!.GetValue<string>());
        Assert.Equal("api_key", LastReport["auth"]!.GetValue<string>());
        Assert.Equal("sk-ant-test", _env["ANTHROPIC_API_KEY"]); // never removed

        _runner.Auth = new ProcessResult(1, """{"loggedIn": false}""", "", false);
        r = await Status(Dispatcher(connector), "claude_code");
        Assert.False(r["signedIn"]!.GetValue<bool>());
        Assert.Null(r["auth"]);
        Assert.False(LastReport.ContainsKey("auth"));
    }

    // ---------- 3. capability probe / outdated ----------

    [Fact]
    public void Capabilities_RequireEveryFlagTheAppPasses()
    {
        Assert.True(ClientConnector.HasCapabilities("claude_code", CliHelp.Claude));
        Assert.True(ClientConnector.HasCapabilities("codex", CliHelp.Codex));
        Assert.False(ClientConnector.HasCapabilities("claude_code", CliHelp.Claude.Replace("--strict-mcp-config", "", StringComparison.Ordinal)));
        Assert.False(ClientConnector.HasCapabilities("claude_code", CliHelp.Claude.Replace("  auth ", "  other ", StringComparison.Ordinal)));
        Assert.False(ClientConnector.HasCapabilities("codex", CliHelp.Codex.Replace("--ignore-user-config", "", StringComparison.Ordinal)));
        Assert.False(ClientConnector.HasCapabilities("codex", CliHelp.Codex.Replace("  resume ", "  other ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("codex", OldCodex, NewCodex)]
    [InlineData("claude_code", OldClaude, NewClaude)]
    public async Task AnOutdatedCli_LosesToAWorkingOne(string client, string old, string current)
    {
        _files.UnionWith([old, current]);
        _runner.Help[old] = new ProcessResult(0, client == "codex" ? "Usage: codex exec [OPTIONS]\n  --json\n" : "Usage: claude [options]\n  -p, --print\n", "", false);
        var r = await Status(Dispatcher(Connector()), client);
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.Null(r["outdated"]);
        Assert.Equal(current, LastReport["path"]!.GetValue<string>());
        Assert.False(LastReport.ContainsKey("compatible"));
    }

    [Fact]
    public async Task OnlyAnOutdatedCli_ReportsOutdated_IsNotCached_AndAnUpdateIsPickedUp()
    {
        _path = @"C:\Old";
        _files.Add(OldCodex);
        _runner.Version[OldCodex] = new ProcessResult(0, "codex-cli 0.30.0\n", "", false);
        _runner.Help[OldCodex] = new ProcessResult(0, "Usage: codex exec [OPTIONS]\n  --json\n", "", false);
        var connector = Connector();
        var r = await Status(Dispatcher(connector), "codex");
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.True(r["outdated"]!.GetValue<bool>());
        Assert.Equal("0.30.0", r["version"]!.GetValue<string>());
        Assert.Equal(OldCodex, LastReport["path"]!.GetValue<string>());
        Assert.False(LastReport["compatible"]!.GetValue<bool>());
        Assert.Null(connector.ResolvedFor("codex"));

        // The same version is not probed twice.
        _runner.Runs.Clear();
        await Status(Dispatcher(connector), "codex");
        Assert.DoesNotContain(_runner.Runs, c => c.Argv.Skip(1).SequenceEqual(["exec", "--help"]));

        // Updated in place: a new version is probed again and now qualifies.
        _runner.Version[OldCodex] = new ProcessResult(0, "codex-cli 0.167.0\n", "", false);
        _runner.Help.Remove(OldCodex);
        r = await Status(Dispatcher(connector), "codex");
        Assert.Null(r["outdated"]);
        Assert.False(LastReport.ContainsKey("compatible"));
        Assert.Equal(OldCodex, connector.ResolvedFor("codex")?.Path);
    }

    [Fact]
    public async Task ACachedCliThatBecomesOutdated_IsDropped_AndTheNextCandidateWins()
    {
        _files.UnionWith([OldCodex, NewCodex]);
        var connector = Connector();
        await Status(Dispatcher(connector), "codex");
        Assert.Equal(OldCodex, connector.ResolvedFor("codex")?.Path);

        _runner.Version[OldCodex] = new ProcessResult(0, "codex-cli 0.20.0\n", "", false); // downgraded
        _runner.Help[OldCodex] = new ProcessResult(0, "Usage: codex exec\n", "", false);
        await Status(Dispatcher(connector), "codex");
        Assert.Equal(NewCodex, connector.ResolvedFor("codex")?.Path);
        Assert.Equal(NewCodex, LastReport["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnInconclusiveProbe_DoesNotBlockTheCli()
    {
        _path = @"C:\New";
        _files.Add(NewClaude);
        _runner.Help[NewClaude] = new ProcessResult(-1, "", "", true);
        var r = await Status(Dispatcher(Connector()), "claude_code");
        Assert.Null(r["outdated"]);
        Assert.Equal(NewClaude, LastReport["path"]!.GetValue<string>());
    }

    // ---------- 10. blocked ----------

    [Theory]
    [InlineData(LaunchFailure.Policy, "", "policy")]
    [InlineData(LaunchFailure.AccessDenied, "", "access_denied")]
    [InlineData(LaunchFailure.Failed, "", "launch_failed")]
    [InlineData(null, "This program is blocked by group policy. For more information, contact your system administrator.\n", "policy")]
    [InlineData(null, "Access is denied.\n", "access_denied")]
    public async Task ACandidateThatCannotStart_IsBlocked_NotMissing(string? launch, string stderr, string reason)
    {
        _path = @"C:\New";
        _files.Add(NewCodex);
        _runner.Version[NewCodex] = new ProcessResult(launch is null ? 1 : -1, "", stderr, false, launch);
        var r = await Status(Dispatcher(Connector()), "codex");
        Assert.False(r["installed"]!.GetValue<bool>());
        Assert.True(r["blocked"]!.GetValue<bool>());
        Assert.Equal(reason, r["blockedReason"]!.GetValue<string>());
        Assert.DoesNotContain(@"C:\New", r.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(LastReport["path"]);
        Assert.True(LastReport["blocked"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ABlockedCandidate_DoesNotHideAWorkingOne()
    {
        _files.UnionWith([OldCodex, NewCodex]);
        _runner.Version[OldCodex] = new ProcessResult(-1, "", "", false, LaunchFailure.Policy);
        var r = await Status(Dispatcher(Connector()), "codex");
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.Null(r["blocked"]);
        Assert.False(LastReport.ContainsKey("blocked"));
    }

    [Fact]
    public async Task ANotACliCandidate_IsNotBlocked()
    {
        _path = @"C:\New";
        _files.Add(NewCodex);
        _runner.Version[NewCodex] = new ProcessResult(0, "something else 1.0\n", "", false);
        var r = await Status(Dispatcher(Connector()), "codex");
        Assert.False(r["installed"]!.GetValue<bool>());
        Assert.Null(r["blocked"]);
    }

    [Fact]
    public async Task AnUnsafeShimPath_WithoutItsPackage_IsBlockedAsUnsupportedPath()
    {
        const string npm = @"C:\Users\AT&T\AppData\Roaming\npm";
        _path = npm;
        _files.Add(npm + @"\claude.cmd");
        var r = await Status(Dispatcher(Connector()), "claude_code");
        Assert.True(r["blocked"]!.GetValue<bool>());
        Assert.Equal("unsupported_path", r["blockedReason"]!.GetValue<string>());
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task AnUnsafeShimPath_WithItsPackage_RunsDirectly()
    {
        const string npm = @"C:\Users\Tom!\AppData\Roaming\npm";
        const string exe = npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        _path = npm;
        _files.Add(npm + @"\claude.cmd");
        var shim = new NpmShimEnvironment
        {
            FileExists = f => f == exe,
            ReadText = f => f == npm + @"\node_modules\@anthropic-ai\claude-code\package.json" ? """{"bin":{"claude":"bin/claude.exe"}}""" : null,
            ChildPath = () => null,
        };
        var r = await Status(Dispatcher(Connector(shim)), "claude_code");
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.All(_runner.Runs, c => Assert.Equal(exe, c.FileName));
        Assert.All(_runner.Runs, c => Assert.Null(c.CmdArguments));
        Assert.Equal(npm + @"\claude.cmd", LastReport["path"]!.GetValue<string>()); // the host resolves the shim the same way
    }

    [Fact]
    public async Task AShimWhosePackageNeedsNode_WithNoNode_IsBlockedAsNodeMissing()
    {
        const string npm = @"C:\Users\A%B\AppData\Roaming\npm";
        const string js = npm + @"\node_modules\@openai\codex\bin\codex.js";
        _path = npm;
        _files.Add(npm + @"\codex.cmd");
        var shim = new NpmShimEnvironment
        {
            FileExists = f => f == js,
            ReadText = f => f.EndsWith(@"@openai\codex\package.json", StringComparison.Ordinal) ? """{"bin":{"codex":"bin/codex.js"}}""" : null,
            ChildPath = () => @"C:\Windows",
        };
        var r = await Status(Dispatcher(Connector(shim)), "codex");
        Assert.True(r["blocked"]!.GetValue<bool>());
        Assert.Equal("node_missing", r["blockedReason"]!.GetValue<string>());
    }

    // ---------- 8. re-send after a host restart ----------

    [Fact]
    public async Task ResendReports_SendsTheLastReportForEachClientAgain()
    {
        _path = @"C:\New";
        _files.UnionWith([NewClaude, NewCodex]);
        var connector = Connector();
        await Status(Dispatcher(connector), "claude_code");
        await Status(Dispatcher(connector), "codex");
        var sent = _backend.Calls.Select(c => c.Params!.ToJsonString()).ToList();

        var restarted = new Backend();
        await connector.ResendReportsAsync(restarted, DesktopLog.Disabled, default);
        Assert.Equal(["provider.setClient", "provider.setClient"], restarted.Calls.Select(c => c.Method));
        Assert.Equal(sent.Order(StringComparer.Ordinal), restarted.Calls.Select(c => c.Params!.ToJsonString()).Order(StringComparer.Ordinal));

        var empty = new Backend();
        await Connector().ResendReportsAsync(empty, DesktopLog.Disabled, default);
        Assert.Empty(empty.Calls);
    }

    // ---------- npm shim resolution ----------

    private const string Npm = @"C:\Users\u\AppData\Roaming\npm";

    private static NpmShimEnvironment Shim(string package, string? json, IEnumerable<string> files, string? path = null) => new()
    {
        FileExists = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase).Contains,
        ReadText = f => f == Npm + @"\node_modules\" + package + @"\package.json" ? json : null,
        ChildPath = () => path,
    };

    [Fact]
    public void NpmShim_ExeBin_RunsTheExe()
    {
        var exe = Npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        var r = NpmShim.Resolve(Npm + @"\claude.cmd", Shim(@"@anthropic-ai\claude-code", """{"bin":{"claude":"bin/claude.exe"}}""", [exe]));
        Assert.Equal(exe, r.Target?.FileName);
        Assert.Empty(r.Target!.Prefix);
    }

    [Fact]
    public void NpmShim_JsBin_RunsNode_FromTheShimFolderFirst_ThenPath()
    {
        var js = Npm + @"\node_modules\@openai\codex\bin\codex.js";
        const string json = """{"name":"@openai/codex","bin":{"codex":"bin/codex.js"}}""";
        var local = NpmShim.Resolve(Npm + @"\codex.cmd", Shim(@"@openai\codex", json, [js, Npm + @"\node.exe", @"C:\Program Files\nodejs\node.exe"], @"C:\Program Files\nodejs"));
        Assert.Equal(Npm + @"\node.exe", local.Target?.FileName);
        Assert.Equal([js], local.Target!.Prefix);

        var onPath = NpmShim.Resolve(Npm + @"\codex.cmd", Shim(@"@openai\codex", json, [js, @"C:\Program Files\nodejs\node.exe"], @"C:\Windows;C:\Program Files\nodejs"));
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", onPath.Target?.FileName);

        var none = NpmShim.Resolve(Npm + @"\codex.cmd", Shim(@"@openai\codex", json, [js], @"C:\Windows"));
        Assert.Null(none.Target);
        Assert.True(none.NodeMissing);
    }

    [Theory]
    [InlineData("""{"bin":{"claude":"../../../evil.exe"}}""")]
    [InlineData("""{"bin":{"claude":"C:\\evil.exe"}}""")]
    [InlineData("""{"bin":{"other":"cli.js"}}""")]
    [InlineData("""{"bin":{"claude":"cli.sh"}}""")]
    [InlineData("not json")]
    [InlineData(null)]
    public void NpmShim_RefusesAnythingElse(string? json)
    {
        var r = NpmShim.Resolve(Npm + @"\claude.cmd", Shim(@"@anthropic-ai\claude-code", json, [@"C:\evil.exe", Npm + @"\evil.exe", Npm + @"\node_modules\@anthropic-ai\claude-code\cli.sh"], @"C:\nodejs"));
        Assert.Null(r.Target);
        Assert.False(r.NodeMissing);
    }

    [Fact]
    public void NpmShim_StringBin_AndUnknownShims()
    {
        var js = Npm + @"\node_modules\@anthropic-ai\claude-code\cli.js";
        var env = Shim(@"@anthropic-ai\claude-code", """{"bin":"cli.js"}""", [js, Npm + @"\node.exe"]);
        Assert.Equal([js], NpmShim.Resolve(Npm + @"\claude.cmd", env).Target?.Prefix);
        Assert.Null(NpmShim.Resolve(Npm + @"\other.cmd", env).Target);
        Assert.Null(NpmShim.Resolve(Npm + @"\claude.exe", env).Target);
    }

    [Fact]
    public void ForCli_ResolvedShim_UsesAnArgumentList_EvenWithCmdMetacharacters()
    {
        const string npm = @"C:\Users\AT&T!%^\AppData\Roaming\npm";
        var exe = npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        var env = new NpmShimEnvironment
        {
            FileExists = f => f == exe,
            ReadText = f => f.EndsWith("package.json", StringComparison.Ordinal) ? """{"bin":{"claude":"bin/claude.exe"}}""" : null,
            ChildPath = () => null,
        };
        var mcp = @"C:\Users\AT&T!%^\AppData\Local\OptimizationTool\agent-runs\t\mcp.json";
        var c = ProcessCommand.ForCli(npm + @"\claude.cmd", ["--mcp-config", mcp], @"C:\Windows", env)!;
        Assert.Equal(exe, c.FileName);
        Assert.Null(c.CmdArguments);
        Assert.Equal(["--mcp-config", mcp], c.ArgumentList);
        Assert.Equal([npm + @"\claude.cmd", "--mcp-config", mcp], c.Argv);

        // Unresolved, the cmd.exe line still refuses the same tokens.
        Assert.Null(ProcessCommand.ForCli(npm + @"\claude.cmd", ["--version"], @"C:\Windows", NpmShimEnvironment.None));
    }

    [Fact]
    public void SignIn_ForAResolvedShim_InvokesWhatItRuns()
    {
        var exe = Npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        var connector = Connector(Shim(@"@anthropic-ai\claude-code", """{"bin":{"claude":"bin/claude.exe"}}""", [exe]));
        Assert.Equal($"& '{exe}' auth login", connector.SignInCommandFor("claude_code", Npm + @"\claude.cmd")!.ArgumentList[^1]);
        Assert.Equal(@"& 'C:\O''Brien\codex.exe' login", connector.SignInCommandFor("codex", @"C:\O'Brien\codex.exe")!.ArgumentList[^1]);
    }

    [Theory]
    [InlineData(1260, "policy")]
    [InlineData(4551, "policy")]
    [InlineData(5, "access_denied")]
    [InlineData(2, "launch_failed")]
    public void LaunchFailure_Codes(int native, string code)
    {
        Assert.Equal(code, LaunchFailure.Classify(native));
        Assert.Equal(code, LaunchFailure.Classify(new System.ComponentModel.Win32Exception(native)));
    }
}

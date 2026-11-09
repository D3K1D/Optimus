using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Tests;

/// <summary>m5-chat §2: clientStatus reports signedIn and tells the host (provider.setClient); signInClient opens the fixed sign-in.</summary>
public sealed class ClientSignInTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string Bridge = @"C:\Program Files\Optimus\OptimizationTool.Mcp.exe";
    private const string NpmDir = @"C:\Users\u\AppData\Roaming\npm";
    private const string ClaudeCmd = NpmDir + @"\claude.cmd";
    private const string CodexExe = @"C:\Tools\codex\codex.exe";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Bridge };
    private readonly Runner _runner = new();
    private readonly Backend _backend = new();
    private readonly Opener _opener = new();

    private BridgeDispatcher Dispatcher(bool opener = true)
    {
        var connector = new ClientConnector(_runner, new CliLocator(() => NpmDir + @";C:\Tools\codex", f => _files.Contains(f)), new NoConfirm(), Bridge,
            systemRoot: @"C:\Windows", fileExists: f => _files.Contains(f), opener: opener ? _opener : null);
        return new BridgeDispatcher(new BridgeRouter(), _backend, DesktopLog.Disabled, clients: connector);
    }

    private sealed class NoConfirm : IClientConfirmPrompt
    {
        public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Opener : IInstallLauncher
    {
        public readonly List<InstallCommand> Started = [];
        public bool IsRunning => false;
        public bool TryStart(InstallCommand command) { Started.Add(command); return true; }
    }

    /// <summary>Records what the shell sends the host itself.</summary>
    private sealed class Backend : IBackend
    {
        public readonly List<(string Method, JsonObject? Params)> Calls = [];
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            return Task.FromResult(new JsonObject { ["result"] = new JsonObject() });
        }
    }

    private sealed class Runner : IProcessRunner
    {
        public ProcessResult Auth = new(0, """{"loggedIn": true, "authMethod": "claude.ai"}""", "", false);
        public readonly List<ProcessCommand> Runs = [];

        public Task<ProcessResult> RunAsync(ProcessCommand c, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add(c);
            var claude = c.Argv[0].Contains("claude", StringComparison.OrdinalIgnoreCase);
            ProcessResult r = c.Argv.Skip(1).ToArray() switch
            {
                ["--version"] => new(0, claude ? "2.1.318 (Claude Code)\n" : "codex-cli 0.167.0\n", "", false),
                ["features", "list"] => new(0, CliHelp.CodexFeatures, "", false),
                ["mcp", "get", _] => new(1, "", "No MCP server found with name: optimization-tool\n", false),
                ["auth", "status"] or ["login", "status"] => Auth,
                _ => new(2, "", "unknown", false),
            };
            return Task.FromResult(r);
        }
    }

    private static async Task<JsonNode> Call(BridgeDispatcher d, string method, string client) =>
        JsonNode.Parse((await d.HandleAsync($$$"""{"type":"rpc","id":1,"method":"{{{method}}}","params":{"client":"{{{client}}}"}}""", Doc, true, default))!)!;

    [Theory]
    [InlineData("""{"loggedIn": true, "authMethod": "claude.ai"}""", 0, true)]
    [InlineData("""{"loggedIn": false}""", 1, false)]
    [InlineData("Warning: something\n{\n  \"loggedIn\": true\n}\n", 0, true)]
    [InlineData("not json", 0, null)]
    public async Task Claude_SignedInFromAuthStatusJson_AndReportedToTheHost(string stdout, int exit, bool? expected)
    {
        _files.Add(ClaudeCmd);
        _runner.Auth = new ProcessResult(exit, stdout, "", false);
        var r = (await Call(Dispatcher(), "shell.clientStatus", "claude_code"))["result"]!;
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.Equal(expected, r["signedIn"]?.GetValue<bool>());
        Assert.Equal(["auth", "status"], _runner.Runs.Last().Argv.Skip(1));

        var (method, p) = Assert.Single(_backend.Calls);
        Assert.Equal("provider.setClient", method);
        Assert.Equal("claude_code_mcp", p!["kind"]!.GetValue<string>());
        Assert.Equal(ClaudeCmd, p["path"]!.GetValue<string>());
        Assert.Equal(expected, p["signedIn"]?.GetValue<bool>());
        Assert.Equal("2.1.318", p["version"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0, "", "Logged in using ChatGPT\n", false, true)]   // Codex prints it to stderr
    [InlineData(0, "Logged in using an API key\n", "", false, true)]
    [InlineData(0, "", "API key configured - sk-proj-***ABCD\n", false, true)] // current Codex wording
    [InlineData(1, "", "Error checking login status: boom\n", false, null)] // a broken check is unknown, not signed out
    [InlineData(1, "", "Not logged in\n", false, false)]
    [InlineData(0, "", "Not logged in\n", false, false)]
    [InlineData(-1, "", "", true, null)]
    public async Task Codex_SignedInFromLoginStatus(int exit, string stdout, string stderr, bool timedOut, bool? expected)
    {
        _files.Add(CodexExe);
        _runner.Auth = new ProcessResult(exit, stdout, stderr, timedOut);
        var r = (await Call(Dispatcher(), "shell.clientStatus", "codex"))["result"]!;
        Assert.Equal(expected, r["signedIn"]?.GetValue<bool>());
        Assert.Equal(["login", "status"], _runner.Runs.Last().Argv.Skip(1));
        Assert.Equal("codex_mcp", _backend.Calls.Single().Params!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task NotInstalled_ReportsANullPath()
    {
        var r = (await Call(Dispatcher(), "shell.clientStatus", "claude_code"))["result"]!;
        Assert.False(r["installed"]!.GetValue<bool>());
        Assert.Null(r["signedIn"]);
        var p = _backend.Calls.Single().Params!;
        Assert.Null(p["path"]);
        Assert.Null(p["signedIn"]);
    }

    [Theory]
    [InlineData("claude_code", ClaudeCmd, "auth login")]
    [InlineData("codex", CodexExe, "login")]
    public async Task SignInClient_OpensTheFixedSignInCommand(string client, string cli, string verb)
    {
        _files.Add(cli);
        var d = Dispatcher();
        Assert.Equal("started", (await Call(d, "shell.signInClient", client))["result"]!["status"]!.GetValue<string>());
        var started = Assert.Single(_opener.Started);
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", started.FileName);
        Assert.Equal(["-NoProfile", "-NoExit", "-Command", $"& '{cli}' {verb}"], started.ArgumentList);
        Assert.Empty(_backend.Calls); // nothing is forwarded
    }

    [Fact]
    public async Task SignInClient_NotInstalledOrUnavailable_Fails()
    {
        var r = (await Call(Dispatcher(), "shell.signInClient", "claude_code"))["result"]!;
        Assert.Equal("failed", r["status"]!.GetValue<string>());
        Assert.Equal("not_installed", r["error"]!.GetValue<string>());
        _files.Add(ClaudeCmd);
        r = (await Call(Dispatcher(opener: false), "shell.signInClient", "claude_code"))["result"]!;
        Assert.Equal("unavailable", r["error"]!.GetValue<string>());
        Assert.Empty(_opener.Started);
    }

    [Fact]
    public void Router_SignInIsShellHandled_SetClientIsNeverRendererCallable()
    {
        var router = new BridgeRouter();
        Assert.IsType<BridgeDecision.Shell>(router.Evaluate("""{"type":"rpc","id":1,"method":"shell.signInClient","params":{"client":"codex"}}""", Doc, true));
        var setClient = router.Evaluate("""{"type":"rpc","id":2,"method":"provider.setClient","params":{"kind":"codex_mcp","path":"C:\\x.exe","signedIn":true}}""", Doc, true);
        Assert.Equal("method_not_allowed", Assert.IsType<BridgeDecision.Reply>(setClient).Reason);
    }
}

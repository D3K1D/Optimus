using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// Result of one child process run. Output is for parsing only and is never logged. <see cref="LaunchError"/>: the
/// process could not be started (a <see cref="LaunchFailure"/> code).
/// </summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? LaunchError = null);

/// <summary>Runs a resolved absolute executable with an exact argument list (no shell), no window, environment unchanged except PATH refreshed from the registry.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Asks the user to confirm adding/removing the registration or running an installer. True only on the explicit verb.</summary>
public interface IClientConfirmPrompt
{
    Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct);
}

/// <summary><paramref name="BridgePath"/> is shown under "Path": the MCP bridge, or powershell.exe for an install.</summary>
public sealed record ClientConfirmView(string Title, string Verb, string Command, string BridgePath);

/// <summary>A visible, non-elevated console for an official installer. Fixed argv only.</summary>
public sealed record InstallCommand(string FileName, IReadOnlyList<string> ArgumentList);

/// <summary>Starts the installer console. Production: <c>ConsoleInstallLauncher</c>.</summary>
public interface IInstallLauncher
{
    /// <summary>True while a previously started installer window is still open.</summary>
    bool IsRunning { get; }

    /// <summary>False when the process could not be started.</summary>
    bool TryStart(InstallCommand command);
}

/// <summary>What `… mcp get optimization-tool` says about the registration.</summary>
public enum Registration { Yes, No, Outdated, Unknown }

/// <summary>
/// Shell-handled <c>shell.clientStatus</c>, <c>shell.registerClient</c>, <c>shell.unregisterClient</c>
/// and <c>shell.installClient</c> (contracts/onboarding.md §2). The renderer only picks the client from a fixed enum; the CLI is
/// resolved by <see cref="CliLocator"/> plus a <c>--version</c> identity check, and the
/// registered bridge path is always this install's own OptimizationTool.Mcp.exe.
/// </summary>
public sealed partial class ClientConnector
{
    public const string StatusMethod = "shell.clientStatus";
    public const string RegisterMethod = "shell.registerClient";
    public const string UnregisterMethod = "shell.unregisterClient";
    public const string InstallMethod = "shell.installClient";
    public const string OpenMethod = "shell.openClient";
    public const string SignInMethod = "shell.signInClient";
    /// <summary>Shell-only, sent by the shell after every status (m5-chat §2); never forwarded from the renderer.</summary>
    public const string SetClientMethod = "provider.setClient";
    /// <summary>The fixed first question typed into the client when the app opens it; its tool call starts pairing.</summary>
    public const string StarterPrompt = "Use Optimus to check my PC.";
    public const string ServerName = "optimization-tool";
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ChangeTimeout = TimeSpan.FromSeconds(15);

    public static readonly FrozenSet<string> Methods = new[] { StatusMethod, RegisterMethod, UnregisterMethod, InstallMethod, OpenMethod, SignInMethod }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The official install one-liners (fixed; nothing from the renderer is ever added).</summary>
    public static readonly FrozenDictionary<string, string> InstallScripts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude_code"] = "irm https://claude.ai/install.ps1 | iex",
        ["codex"] = "irm https://chatgpt.com/codex/install.ps1 | iex",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// AuthArgs: the fixed sign-in check (m5 §2). SignInArgs: the fixed sign-in command shown in a console. Kind: the
    /// provider kind. HelpArgs / HelpTokens / HelpCommand: the capability probe — the help text must list every flag
    /// the app passes (sign-in check and headless run) and the subcommand it needs.
    /// </summary>
    private sealed record Client(string Id, string Name, string Exe, string[] ScopeArgs, Regex Identity, string[] AuthArgs, string SignInArgs, string Kind,
        string[] HelpArgs, string[] HelpTokens, Regex HelpCommand);

    private static readonly FrozenDictionary<string, Client> Clients = new Dictionary<string, Client>(StringComparer.Ordinal)
    {
        ["claude_code"] = new("claude_code", "Claude Code", "claude", ["--scope", "user"], ClaudeIdentity(), ["auth", "status"], "auth login", "claude_code_mcp",
            ["--help"], ["--output-format", "--strict-mcp-config", "--mcp-config", "--allowedTools", "--append-system-prompt", "--session-id", "--resume", "--tools"],
            ClaudeAuthCommand()),
        ["codex"] = new("codex", "Codex", "codex", [], CodexIdentity(), ["login", "status"], "login", "codex_mcp",
            ["exec", "--help"], ["--json", "--skip-git-repo-check", "--ignore-user-config", "--sandbox", "--disable", "--cd", "--config"],
            CodexResumeCommand()),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IProcessRunner _runner;
    private readonly CliLocator _locator;
    private readonly IClientConfirmPrompt _prompt;
    private readonly IInstallLauncher? _installer;
    private readonly IInstallLauncher? _opener;
    private readonly string _bridgePath;
    private readonly string _systemRoot;
    private readonly Func<string, bool> _fileExists;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    /// <summary>Resolved CLI per client for the window's lifetime; dropped when the file is gone or fails the identity or capability check.</summary>
    private readonly ConcurrentDictionary<string, CliCandidate> _resolved = new(StringComparer.Ordinal);
    /// <summary>Conclusive capability probes per CLI path and version (Capable = has everything the app passes).</summary>
    private readonly ConcurrentDictionary<(string Path, string Version), Probe> _capable = new();
    /// <summary>The last provider.setClient sent per client, re-sent when the host connection comes back (m5 §2).</summary>
    private readonly ConcurrentDictionary<string, JsonObject> _reports = new(StringComparer.Ordinal);
    private readonly NpmShimEnvironment _npm;
    private readonly Func<string, string?> _environment;

    /// <param name="bridgePath">This install's OptimizationTool.Mcp.exe (production: next to OptimizationTool.exe).</param>
    /// <param name="environment">This process's environment (inherited by every CLI it starts). Tests replace it.</param>
    public ClientConnector(IProcessRunner runner, CliLocator locator, IClientConfirmPrompt prompt, string bridgePath,
        string? systemRoot = null, Func<string, bool>? fileExists = null, IInstallLauncher? installer = null, IInstallLauncher? opener = null,
        NpmShimEnvironment? npm = null, Func<string, string?>? environment = null)
    {
        _npm = npm ?? NpmShimEnvironment.System;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _opener = opener;
        _runner = runner;
        _locator = locator;
        _prompt = prompt;
        _installer = installer;
        _bridgePath = Path.GetFullPath(bridgePath);
        _systemRoot = systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary><c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -Command &lt;script&gt;</c>.</summary>
    public InstallCommand InstallCommandFor(string clientId) => new(
        Path.Combine(_systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-Command", InstallScripts[clientId]]);

    /// <summary>
    /// <c>powershell.exe -NoProfile -NoExit -Command "&amp; '&lt;cli&gt;' '&lt;starter prompt&gt;'"</c>: the resolved CLI in a visible
    /// window, starting with the fixed question. Null when the path cannot be quoted safely.
    /// </summary>
    public InstallCommand? OpenCommandFor(string cliPath)
    {
        if (Invocation(cliPath) is not { } call) return null;
        var line = call + " '" + StarterPrompt + "'";
        return new(Path.Combine(_systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NoExit", "-Command", line]);
    }

    /// <summary>
    /// <c>&amp; '&lt;cli&gt;'</c> for a PowerShell line, single quotes doubled; a known npm shim is invoked as what it runs
    /// (<c>&amp; '&lt;node.exe&gt;' '&lt;entry&gt;'</c> or its exe), like every other start. Null when the path is unusable.
    /// </summary>
    private string? Invocation(string cliPath)
    {
        if (!Path.IsPathFullyQualified(cliPath) || cliPath.Any(char.IsControl)) return null;
        IEnumerable<string> parts = NpmShim.Resolve(cliPath, _npm).Target is { } t ? [t.FileName, .. t.Prefix] : [cliPath];
        return "& " + string.Join(' ', parts.Select(p => "'" + p.Replace("'", "''", StringComparison.Ordinal) + "'"));
    }

    public static string DefaultBridgePath => Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Mcp.exe");

    public async Task<string> HandleAsync(string method, JsonNode id, JsonObject? parameters, DesktopLog log, CancellationToken ct, IBackend? backend = null)
    {
        if (parameters is null || parameters.Count != 1
            || parameters["client"] is not JsonValue v || !v.TryGetValue<string>(out var clientId) || !Clients.TryGetValue(clientId, out var client))
        {
            log.Warn($"shell: refused {method} (invalid params)");
            return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = "client" });
        }

        // The installer has its own one-at-a-time gate so its confirm dialog does not block status checks.
        // Status checks queue instead (the chooser asks about both clients at once); a change while another runs is refused.
        var gate = method is InstallMethod or OpenMethod or SignInMethod ? _installGate : _gate;
        if (method == StatusMethod) await gate.WaitAsync(ct);
        else if (!await gate.WaitAsync(0, ct))
            return BridgeMessages.Error(id, -32013, "Busy.", new JsonObject { ["reason"] = "busy" });
        try
        {
            var result = method switch
            {
                StatusMethod => await StatusAsync(client, log, ct, backend),
                RegisterMethod => await RegisterAsync(client, log, ct),
                InstallMethod => await InstallAsync(client, log, ct),
                OpenMethod => await OpenAsync(client, log, ct),
                SignInMethod => await SignInAsync(client, log, ct),
                _ => await UnregisterAsync(client, log, ct),
            };
            return BridgeMessages.Result(id, result);
        }
        catch (OperationCanceledException)
        {
            return BridgeMessages.Result(id, new JsonObject { ["status"] = "failed", ["error"] = "cancelled" });
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- resolution ----

    /// <summary>The CLI resolved for a client in this window, if any (diagnostics and live checks).</summary>
    internal CliCandidate? ResolvedFor(string clientId) => _resolved.GetValueOrDefault(clientId);

    /// <summary>
    /// <see cref="Cli"/>: the CLI to use (when <see cref="Outdated"/>, an identified CLI that lacks a capability the app
    /// needs; never cached). <see cref="Blocked"/>: no CLI could be used, but a candidate exists that could not be started
    /// (a fixed reason code).
    /// </summary>
    private sealed record Resolution(CliCandidate? Cli, string? Version, bool Outdated = false, string? Blocked = null, IReadOnlyList<string>? CodexFeatures = null);

    /// <summary>A capability probe: Capable, and (Codex, capable only) the §0 features this Codex has, to disable.</summary>
    private sealed record Probe(bool Capable, IReadOnlyList<string>? CodexFeatures = null);

    /// <summary>
    /// The cached CLI when its file still exists (re-identified and re-probed only when <paramref name="identify"/>),
    /// otherwise the first candidate whose <c>--version</c> output identifies the client and whose help lists every
    /// capability the app needs. An outdated CLI is used only when no candidate qualifies; it is never cached, so a later
    /// status finds a newer install.
    /// </summary>
    private async Task<Resolution> ResolveAsync(Client c, bool identify, DesktopLog log, CancellationToken ct)
    {
        if (_resolved.TryGetValue(c.Id, out var cached))
        {
            if (_fileExists(cached.Path))
            {
                if (!identify) return new(cached, null);
                if ((await IdentifyAsync(c, cached.Path, log, ct)).Version is { } v && await CapableAsync(c, cached.Path, v, log, ct) is { Capable: true } p)
                    return new(cached, v, CodexFeatures: p.CodexFeatures);
            }
            _resolved.TryRemove(c.Id, out _);
            log.Info($"shell: {c.Id} cached CLI dropped, resolving again");
        }

        (CliCandidate Cli, string Version)? outdated = null;
        string? blocked = null;
        foreach (var candidate in _locator.Candidates(c.Id))
        {
            var (version, launch) = await IdentifyAsync(c, candidate.Path, log, ct);
            if (version is null)
            {
                blocked ??= launch;
                log.Info($"shell: {c.Id} skipped a {candidate.Source} candidate ({launch ?? "not the CLI"})");
                continue;
            }
            var probe = await CapableAsync(c, candidate.Path, version, log, ct);
            if (!probe.Capable)
            {
                outdated ??= (candidate, version);
                log.Info($"shell: {c.Id} skipped a {candidate.Source} candidate (outdated)");
                continue;
            }
            _resolved[c.Id] = candidate;
            log.Info($"shell: {c.Id} resolved ({candidate.Source})");
            return new(candidate, version, CodexFeatures: probe.CodexFeatures);
        }
        if (outdated is { } o) return new(o.Cli, o.Version, Outdated: true);
        return new(null, null, Blocked: blocked);
    }

    /// <summary>
    /// Version: the version text ("" if none) when <c>--version</c> identifies the client's CLI, else null. Blocked: why
    /// it could not run at all (policy | access_denied | node_missing | unsupported_path | launch_failed), else null.
    /// </summary>
    private async Task<(string? Version, string? Blocked)> IdentifyAsync(Client c, string cli, DesktopLog log, CancellationToken ct)
    {
        var r = await RunAsync(c, cli, ["--version"], QueryTimeout, "version", log, ct);
        if (r is null) return (null, NodeMissing(cli) ? "node_missing" : "unsupported_path");
        if (r.LaunchError is { } launch) return (null, launch);
        if (r is not { TimedOut: false, ExitCode: 0 } || !c.Identity.IsMatch(r.Stdout))
            return (null, r.TimedOut ? null : BlockedOutput(r) ?? (NodeMissing(cli) ? "node_missing" : null));
        return (VersionPattern().Match(r.Stdout) is { Success: true } m ? m.Value : "", null);
    }

    private bool NodeMissing(string cli) => NpmShim.Resolve(cli, _npm).NodeMissing;

    /// <summary>cmd.exe's own words when a policy or the file system refused what a .cmd shim started.</summary>
    internal static string? BlockedOutput(ProcessResult r)
    {
        if (r.ExitCode == 0) return null;
        var text = r.Stdout + "\n" + r.Stderr;
        if (PolicyPattern().IsMatch(text)) return LaunchFailure.Policy;
        return AccessDeniedPattern().IsMatch(text) ? LaunchFailure.AccessDenied : null;
    }

    /// <summary>
    /// The capability probe (cached per path and version when conclusive). Help: not capable only when the help text was
    /// printed (exit 0) and misses a flag or subcommand the app passes; a help probe that fails or times out does not
    /// block the CLI. Codex also needs (m6 §0, fail closed) a version of at least <see cref="CodexLockdown.CodexMinVersion"/>
    /// and a <c>features list</c> that names the required features; a features probe that fails or times out makes the
    /// CLI outdated (not cached, so the next status tries again).
    /// </summary>
    private async Task<Probe> CapableAsync(Client c, string cli, string version, DesktopLog log, CancellationToken ct)
    {
        if (_capable.TryGetValue((cli, version), out var known)) return known;
        var codex = c.Id == "codex";
        if (codex && !CodexLockdown.VersionSupported(version))
        {
            log.Info($"shell: {c.Id} version too old or unreadable");
            return _capable[(cli, version)] = new(false);
        }
        var r = await RunAsync(c, cli, c.HelpArgs, QueryTimeout, "help", log, ct);
        var helpConclusive = r is { TimedOut: false, ExitCode: 0, LaunchError: null } && r.Stdout.Length > 0;
        if (helpConclusive && !HasCapabilities(c.Id, r!.Stdout)) return _capable[(cli, version)] = new(false);
        if (!codex)
        {
            Probe probe = new(true);
            if (helpConclusive) _capable[(cli, version)] = probe;
            return probe;
        }

        var f = await RunAsync(c, cli, CodexFeaturesArgs, QueryTimeout, "features", log, ct);
        if (f is not { TimedOut: false, ExitCode: 0, LaunchError: null }) return new(false);
        if (CodexLockdown.ParseFeatures(f.Stdout) is not { } features)
        {
            log.Info($"shell: {c.Id} features list lacks a required feature");
            return _capable[(cli, version)] = new(false);
        }
        Probe found = new(true, features);
        if (helpConclusive) _capable[(cli, version)] = found;
        return found;
    }

    /// <summary><c>codex features list</c> (m6 §0 probe).</summary>
    private static readonly string[] CodexFeaturesArgs = ["features", "list"];

    /// <summary>True when <paramref name="help"/> lists every flag and the subcommand the app passes to that client.</summary>
    public static bool HasCapabilities(string clientId, string help)
    {
        var c = Clients[clientId];
        return c.HelpTokens.All(t => help.Contains(t, StringComparison.Ordinal)) && c.HelpCommand.IsMatch(help);
    }

    // ---- argv (fixed; the only variable token is this install's bridge path) ----

    private static string[] GetArgs() => ["mcp", "get", ServerName];
    private static string[] RemoveArgs(Client c) => ["mcp", "remove", .. c.ScopeArgs, ServerName];
    private string[] AddArgs(Client c) => ["mcp", "add", .. c.ScopeArgs, ServerName, "--", _bridgePath];

    /// <summary>The manual fallback line, e.g. <c>claude mcp add --scope user optimization-tool -- "C:\…\OptimizationTool.Mcp.exe"</c>.</summary>
    public string ManualCommand(string clientId)
    {
        var c = Clients[clientId];
        return string.Join(' ', new[] { c.Exe }.Concat(AddArgs(c)[..^1])) + $" \"{_bridgePath}\"";
    }

    // ---- methods ----

    private async Task<JsonObject> StatusAsync(Client c, DesktopLog log, CancellationToken ct, IBackend? backend)
    {
        var result = new JsonObject { ["installed"] = false, ["registered"] = "no", ["command"] = ManualCommand(c.Id) };
        var resolution = await ResolveAsync(c, identify: true, log, ct);
        if (resolution.Cli is not { } resolved)
        {
            if (resolution.Blocked is { } reason)
            {
                result["blocked"] = true;
                result["blockedReason"] = reason;
            }
            // The desktop app bundles Claude Code only after its Code tab is first opened.
            if (c.Id == "claude_code" && _locator.ClaudeAppInstalled()) result["app"] = true;
            await ReportClientAsync(c, new JsonObject { ["path"] = null, ["signedIn"] = null, ["blocked"] = resolution.Blocked is null ? null : true }, backend, log, ct);
            return result;
        }
        var cli = resolved.Path;
        var version = resolution.Version;
        result["installed"] = true;
        result["source"] = resolved.Source;
        if (!string.IsNullOrEmpty(version)) result["version"] = version;
        if (resolution.Outdated) result["outdated"] = true;

        result["registered"] = Name(await RegistrationRepairedAsync(c, cli, log, ct));
        var (signedIn, auth) = await SignedInAsync(c, cli, log, ct);
        result["signedIn"] = signedIn;
        if (auth is not null) result["auth"] = auth;

        var report = new JsonObject { ["path"] = cli, ["signedIn"] = signedIn };
        if (!string.IsNullOrEmpty(version)) report["version"] = version;
        if (auth is not null) report["auth"] = auth;
        if (resolution.Outdated) report["compatible"] = false;
        else if (resolution.CodexFeatures is { } features) report["codexFeatures"] = new JsonArray([.. features.Select(f => (JsonNode?)JsonValue.Create(f))]);
        await ReportClientAsync(c, report, backend, log, ct);
        return result;
    }

    /// <summary>
    /// m5 §2: Claude <c>auth status</c> JSON loggedIn; Codex <c>login status</c> (see <see cref="ParseSignedIn"/>). Null:
    /// the check failed for another reason. Auth (signed in only): subscription | api_key | other.
    /// </summary>
    private async Task<(bool? SignedIn, string? Auth)> SignedInAsync(Client c, string cli, DesktopLog log, CancellationToken ct)
    {
        var r = await RunAsync(c, cli, c.AuthArgs, QueryTimeout, "auth", log, ct);
        if (r is null) return (null, null);
        var signedIn = ParseSignedIn(c.Id, r);
        if (signedIn != true) return (signedIn, null);
        // Claude Code bills an ANTHROPIC_API_KEY it inherits (always, in a headless run) instead of the subscription.
        if (c.Id == "claude_code" && !string.IsNullOrEmpty(_environment("ANTHROPIC_API_KEY")) && ParseAuth(c.Id, r) != "other") return (true, "api_key");
        return (true, ParseAuth(c.Id, r));
    }

    /// <summary>
    /// Claude: <c>auth status</c> JSON <c>loggedIn</c> (null when there is no JSON). Codex (it prints to stderr): exit 0
    /// and a success line — "Logged in using ChatGPT", "Logged in using an API key - …", "Logged in using workload
    /// identity", or (newer) "API key configured - …" → true; "Not logged in" → false; anything else (an error, a crash,
    /// a launch failure) → null, so a broken check never reads as signed out.
    /// </summary>
    public static bool? ParseSignedIn(string clientId, ProcessResult r)
    {
        if (r.TimedOut || r.LaunchError is not null) return null;
        if (clientId == "codex")
        {
            var text = r.Stdout + "\n" + r.Stderr;
            if (r.ExitCode == 0 && CodexSignedInPattern().IsMatch(text)) return true;
            return NotLoggedInPattern().IsMatch(text) ? false : null;
        }
        return ClaudeStatus(r)?["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    /// <summary>
    /// How a signed-in CLI bills, from its status output: <c>subscription</c> (Claude <c>authMethod</c> claude.ai or an
    /// OAuth token; Codex "using ChatGPT"), <c>api_key</c> (an API key), <c>other</c> (anything else, e.g. a cloud
    /// provider or workload identity). Null when unknown. The key itself is never read out.
    /// </summary>
    public static string? ParseAuth(string clientId, ProcessResult r)
    {
        if (clientId == "codex")
        {
            var text = r.Stdout + "\n" + r.Stderr;
            if (!CodexSignedInPattern().IsMatch(text)) return null;
            if (text.Contains("ChatGPT", StringComparison.Ordinal)) return "subscription";
            return text.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "api_key" : "other";
        }
        if (ClaudeStatus(r) is not { } status) return null;
        if (status["apiProvider"] is JsonValue p && p.TryGetValue<string>(out var provider) && provider != "firstParty") return "other";
        if (status["authMethod"] is not JsonValue m || !m.TryGetValue<string>(out var method)) return null;
        var normalized = method.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (normalized is "claude.ai" or "claudeai" or "oauthtoken" or "oauth" or "subscription") return "subscription";
        return normalized.Contains("apikey", StringComparison.Ordinal) ? "api_key" : "other";
    }

    private static JsonObject? ClaudeStatus(ProcessResult r)
    {
        var start = r.Stdout.IndexOf('{');
        var end = r.Stdout.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(r.Stdout[start..(end + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// After every status the shell tells the host about the CLI (provider.setClient, shell-only) and keeps that report,
    /// so it can be sent again when the host restarts (<see cref="ResendReportsAsync"/>). Failures are logged and ignored.
    /// </summary>
    private async Task ReportClientAsync(Client c, JsonObject fields, IBackend? backend, DesktopLog log, CancellationToken ct)
    {
        var p = new JsonObject { ["kind"] = c.Kind };
        foreach (var (key, value) in fields)
            if (value is not null || key is "path" or "signedIn") p[key] = value?.DeepClone();
        _reports[c.Id] = (JsonObject)p.DeepClone();
        if (backend is not null) await SendReportAsync(c.Id, p, backend, log, ct);
    }

    private static async Task SendReportAsync(string clientId, JsonObject p, IBackend backend, DesktopLog log, CancellationToken ct)
    {
        try
        {
            var response = await backend.CallAsync(SetClientMethod, p, ct);
            if (response["error"] is not null) log.Warn($"shell: {clientId} setClient refused");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Info($"shell: {clientId} setClient not delivered ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// The host keeps setClient reports in memory only: after it restarts (the connection is ready again) the shell sends
    /// its last report for each client again. Waits for any status in progress so an older report never wins.
    /// </summary>
    public async Task ResendReportsAsync(IBackend backend, DesktopLog log, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var (clientId, report) in _reports.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                await SendReportAsync(clientId, (JsonObject)report.DeepClone(), backend, log, ct);
                log.Info($"shell: {clientId} setClient re-sent");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>powershell.exe -NoProfile -NoExit -Command "&amp; '&lt;cli&gt;' auth login"</c> (Codex: <c>login</c>): the fixed sign-in
    /// command in a visible window. Null when the path cannot be quoted safely.
    /// </summary>
    public InstallCommand? SignInCommandFor(string clientId, string cliPath)
    {
        if (Invocation(cliPath) is not { } call) return null;
        var line = call + " " + Clients[clientId].SignInArgs;
        return new(Path.Combine(_systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NoExit", "-Command", line]);
    }

    /// <summary>shell.signInClient: opens the client's sign-in in a console (no confirm: nothing on the PC changes).</summary>
    private async Task<JsonObject> SignInAsync(Client c, DesktopLog log, CancellationToken ct)
    {
        if (_opener is null) return Status("failed", "unavailable");
        var cli = (await ResolveAsync(c, identify: false, log, ct)).Cli?.Path;
        if (cli is null) return Status("failed", "not_installed");
        if (SignInCommandFor(c.Id, cli) is not { } command || !_opener.TryStart(command))
        {
            log.Warn($"shell: sign-in {c.Id} could not start");
            return Status("failed", "start_failed");
        }
        log.Info($"shell: sign-in {c.Id} started");
        return Status("started");
    }


    private async Task<JsonObject> InstallAsync(Client c, DesktopLog log, CancellationToken ct)
    {
        if (_installer is null) return Status("failed", "unavailable");
        if (_installer.IsRunning) return Status("failed", "already_running");

        var command = InstallCommandFor(c.Id);
        var confirmed = await _prompt.ConfirmAsync(new ClientConfirmView(
            $"Install {c.Name}?", "Install", InstallScripts[c.Id], command.FileName), ct);
        if (!confirmed)
        {
            log.Info($"shell: install {c.Id} cancelled");
            return Status("cancelled");
        }
        if (!_installer.TryStart(command))
        {
            log.Warn($"shell: install {c.Id} could not start");
            return Status("failed", "start_failed");
        }
        _resolved.TryRemove(c.Id, out _);
        log.Info($"shell: install {c.Id} started");
        return Status("started");
    }

    /// <summary>Opens the client in a terminal with the starter question (no confirm: nothing on the PC changes).</summary>
    private async Task<JsonObject> OpenAsync(Client c, DesktopLog log, CancellationToken ct)
    {
        if (_opener is null) return Status("failed", "unavailable");
        var cli = (await ResolveAsync(c, identify: false, log, ct)).Cli?.Path;
        if (cli is null) return Status("not_installed");
        if (OpenCommandFor(cli) is not { } command || !_opener.TryStart(command))
        {
            log.Warn($"shell: open {c.Id} could not start");
            return Status("failed", "start_failed");
        }
        log.Info($"shell: opened {c.Id}");
        return Status("opened");
    }

    private async Task<JsonObject> RegisterAsync(Client c, DesktopLog log, CancellationToken ct)
    {
        if (!_fileExists(_bridgePath)) return Status("failed", "bridge_missing");
        var cli = (await ResolveAsync(c, identify: false, log, ct)).Cli?.Path;
        if (cli is null) return Status("not_installed");

        var add = Build(cli, AddArgs(c));
        if (add is null) return Status("failed", "unsupported_path");

        var before = await GetRegistrationAsync(c, cli, log, ct);
        if (before == Registration.Yes) return Status("added");

        var confirmed = await _prompt.ConfirmAsync(new ClientConfirmView(
            $"Add Optimus to {c.Name}?", "Add", ManualCommand(c.Id), _bridgePath), ct);
        if (!confirmed)
        {
            log.Info($"shell: register {c.Id} cancelled");
            return Status("cancelled");
        }

        // An entry under our name that points elsewhere (or could not be read) is replaced.
        if (before is Registration.Outdated or Registration.Unknown)
            await RunAsync(c, cli, RemoveArgs(c), ChangeTimeout, "remove", log, ct);

        var added = await RunAsync(c, cli, AddArgs(c), ChangeTimeout, "add", log, ct);
        if (added is null) return Status("failed", "unsupported_path");
        if (added.TimedOut) return Status("failed", "timeout");
        if (added.ExitCode != 0) return Status("failed", "add_failed");

        return await GetRegistrationAsync(c, cli, log, ct) == Registration.Yes
            ? Status("added")
            : Status("failed", "verify_failed");
    }

    private async Task<JsonObject> UnregisterAsync(Client c, DesktopLog log, CancellationToken ct)
    {
        var cli = (await ResolveAsync(c, identify: false, log, ct)).Cli?.Path;
        if (cli is null) return Status("not_installed");

        var confirmed = await _prompt.ConfirmAsync(new ClientConfirmView(
            $"Remove Optimus from {c.Name}?", "Remove",
            string.Join(' ', new[] { c.Exe }.Concat(RemoveArgs(c))), _bridgePath), ct);
        if (!confirmed) return Status("cancelled");

        var removed = await RunAsync(c, cli, RemoveArgs(c), ChangeTimeout, "remove", log, ct);
        if (removed is null) return Status("failed", "unsupported_path");
        if (removed.TimedOut) return Status("failed", "timeout");

        var after = await GetRegistrationAsync(c, cli, log, ct);
        return after == Registration.No ? Status("removed") : Status("failed", "remove_failed");
    }

    // ---- process + parsing ----

    private ProcessCommand? Build(string cli, IReadOnlyList<string> args) => ProcessCommand.ForCli(cli, args, _systemRoot, _npm);

    private async Task<ProcessResult?> RunAsync(Client c, string cli, IReadOnlyList<string> args, TimeSpan timeout, string what, DesktopLog log, CancellationToken ct)
    {
        var command = Build(cli, args);
        if (command is null)
        {
            log.Warn($"shell: {c.Id} {what} refused (unsafe path or argument)");
            return null;
        }
        var r = await _runner.RunAsync(command, timeout, ct);
        log.Info($"shell: {c.Id} {what} exit={(r.LaunchError ?? (r.TimedOut ? "timeout" : r.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
        return r;
    }

    private async Task<Registration> GetRegistrationAsync(Client c, string cli, DesktopLog log, CancellationToken ct) =>
        (await GetRegistrationWithCommandAsync(c, cli, log, ct)).State;

    private async Task<(Registration State, string? Command)> GetRegistrationWithCommandAsync(Client c, string cli, DesktopLog log, CancellationToken ct)
    {
        var r = await RunAsync(c, cli, GetArgs(), QueryTimeout, "get", log, ct);
        return r is null ? (Registration.Unknown, null) : (ParseGet(r, _bridgePath), RegisteredCommand(r));
    }

    /// <summary>
    /// The registration for a status check, repaired when the install moved (onboarding §2): an entry that points at
    /// an <c>OptimizationTool.Mcp.exe</c> which no longer exists (e.g. the old <c>Program Files\Optimization Tool</c>
    /// folder an upgrade removed) is the user's earlier approved registration of this app, so it is replaced with this
    /// install's bridge without asking again. Any other entry elsewhere stays Outdated (the user reconnects).
    /// </summary>
    private async Task<Registration> RegistrationRepairedAsync(Client c, string cli, DesktopLog log, CancellationToken ct)
    {
        var (state, command) = await GetRegistrationWithCommandAsync(c, cli, log, ct);
        if (state != Registration.Outdated || !IsMovedBridge(command) || !_fileExists(_bridgePath)) return state;

        log.Info($"shell: {c.Id} registration points at a removed bridge, re-registering");
        await RunAsync(c, cli, RemoveArgs(c), ChangeTimeout, "remove", log, ct);
        await RunAsync(c, cli, AddArgs(c), ChangeTimeout, "add", log, ct);
        return await GetRegistrationAsync(c, cli, log, ct); // whatever the CLI now holds: yes, or no/outdated to reconnect
    }

    /// <summary>True for a fully qualified path to an OptimizationTool.Mcp.exe that is not there any more.</summary>
    private bool IsMovedBridge(string? command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        try
        {
            return Path.IsPathFullyQualified(command)
                && string.Equals(Path.GetFileName(command), Path.GetFileName(_bridgePath), StringComparison.OrdinalIgnoreCase)
                && !_fileExists(command);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The registered command path from <c>mcp get</c> output (quotes trimmed), or null.</summary>
    internal static string? RegisteredCommand(ProcessResult r)
    {
        if (r.TimedOut || r.ExitCode != 0) return null;
        var m = CommandLinePattern().Match(r.Stdout);
        if (!m.Success) return null;
        var command = m.Groups["cmd"].Value.Trim().Trim('"', '\'').Trim();
        return command.Length == 0 ? null : command;
    }

    /// <summary>
    /// Reads the registered command from <c>claude mcp get</c> / <c>codex mcp get</c> output ("Command: …" or
    /// "command: …" line). Not found → No; same path as this install's bridge → Yes; another path → Outdated;
    /// anything unrecognised → Unknown.
    /// </summary>
    public static Registration ParseGet(ProcessResult r, string bridgePath)
    {
        if (r.TimedOut) return Registration.Unknown;
        var text = r.Stdout + "\n" + r.Stderr;
        if (r.ExitCode != 0)
            return NotFoundPattern().IsMatch(text) ? Registration.No : Registration.Unknown;

        var m = CommandLinePattern().Match(r.Stdout);
        if (!m.Success)
            return NotFoundPattern().IsMatch(text) ? Registration.No : Registration.Unknown;

        var command = m.Groups["cmd"].Value.Trim().Trim('"', '\'').Trim();
        if (command.Length == 0) return Registration.Unknown;
        try
        {
            if (!Path.IsPathFullyQualified(command)) return Registration.Outdated;
            return string.Equals(Path.GetFullPath(command), Path.GetFullPath(bridgePath), StringComparison.OrdinalIgnoreCase)
                ? Registration.Yes
                : Registration.Outdated;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Registration.Unknown;
        }
    }

    private static string Name(Registration r) => r switch
    {
        Registration.Yes => "yes",
        Registration.No => "no",
        Registration.Outdated => "outdated",
        _ => "unknown",
    };

    private static JsonObject Status(string status, string? error = null)
    {
        var o = new JsonObject { ["status"] = status };
        if (error is not null) o["error"] = error;
        return o;
    }

    [GeneratedRegex(@"^[ \t]*command[ \t]*:[ \t]*(?<cmd>\S.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CommandLinePattern();

    [GeneratedRegex(@"no (mcp )?server (found )?(named|with name)|not found|does not exist|no such server", RegexOptions.IgnoreCase)]
    private static partial Regex NotFoundPattern();

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?([-+][0-9A-Za-z.-]{1,32})?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\(Claude Code\)")]
    private static partial Regex ClaudeIdentity();

    /// <summary>Every success form of <c>codex login status</c> (case-sensitive, so "Not logged in" never matches).</summary>
    [GeneratedRegex(@"\bLogged in\b|\bAPI key configured\b")]
    private static partial Regex CodexSignedInPattern();

    [GeneratedRegex(@"\bnot logged in\b|\blogged out\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotLoggedInPattern();

    [GeneratedRegex(@"blocked by (your )?(group policy|(system )?administrator)|disabled by your administrator|application control policy", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyPattern();

    [GeneratedRegex(@"^\s*Access is denied\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AccessDeniedPattern();

    /// <summary>The <c>auth</c> subcommand line in <c>claude --help</c>.</summary>
    [GeneratedRegex(@"^[ \t]+auth\b", RegexOptions.Multiline)]
    private static partial Regex ClaudeAuthCommand();

    /// <summary>The <c>resume</c> subcommand line in <c>codex exec --help</c>.</summary>
    [GeneratedRegex(@"^[ \t]+resume\b", RegexOptions.Multiline)]
    private static partial Regex CodexResumeCommand();

    [GeneratedRegex(@"\bcodex-cli\b")]
    private static partial Regex CodexIdentity();
}

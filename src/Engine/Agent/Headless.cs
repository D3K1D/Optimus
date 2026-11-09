using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

// M5 headless runs (contracts/m5-chat.md §3): the host runs Claude Code or Codex in the background under the user's
// own plan. The engine builds the argument list (fixed tokens, app-made paths and IDs only), writes the prompt to
// stdin and reads the CLI's JSON stream. The process itself is started by the host (src/Host/CliRunner.cs) through
// the shared safe command builder (OptimizationTool.Transport.ProcessCommand.ForCli).

/// <summary>One headless CLI run. <see cref="StandardInput"/> carries every piece of user text; arguments never do.</summary>
public sealed record CliLaunch(string CliPath, IReadOnlyList<string> Arguments, string StandardInput, string WorkingDirectory);

/// <summary>How a run ended. <see cref="Started"/> false: the command was refused (unsafe token) or could not start.
/// StandardError is for auth detection only and is never logged. <see cref="LaunchError"/> (not started only) is a fixed
/// code: unsupported_path | policy | access_denied | launch_failed.</summary>
public sealed record CliExit(int ExitCode, string StandardError, bool Started = true, string? LaunchError = null);

/// <summary>Starts a headless CLI. Production: the host's CliRunner. Tests replay recorded output.</summary>
public interface ICliRunner
{
    /// <summary>
    /// Starts the CLI with no window and the refreshed PATH, writes <see cref="CliLaunch.StandardInput"/> (UTF-8) and closes
    /// stdin, and passes each stdout line to <paramref name="onLine"/> as it arrives. Cancelling kills the whole process
    /// tree and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<CliExit> RunAsync(CliLaunch launch, Action<string> onLine, CancellationToken ct);
}

/// <summary>The fixed argument lists (§3 table). Every variable token is an app-made path, a UUID or a hex token.</summary>
public static partial class HeadlessCommand
{
    public const string ServerName = "optimization-tool";
    public const string McpConfigFile = "mcp.json", SystemFile = "system.md";

    /// <summary>
    /// Claude Code: <c>-p --output-format stream-json --verbose --tools= --strict-mcp-config --mcp-config &lt;runDir&gt;\mcp.json
    /// --allowedTools mcp__optimization-tool --append-system-prompt-file &lt;runDir&gt;\system.md</c> plus <c>--session-id</c> (first
    /// ask) or <c>--resume</c> (later asks). <c>--tools=</c> is the one-token form of <c>--tools ""</c> (no built-in tools):
    /// an empty token cannot be passed safely through a .cmd shim.
    /// </summary>
    public static IReadOnlyList<string> Claude(string runDir, Guid session, bool resume) =>
    [
        "-p", "--output-format", "stream-json", "--verbose", "--tools=", "--strict-mcp-config",
        "--mcp-config", Path.Combine(runDir, McpConfigFile), "--allowedTools", "mcp__" + ServerName,
        "--append-system-prompt-file", Path.Combine(runDir, SystemFile),
        resume ? "--resume" : "--session-id", session.ToString("D"),
    ];

    /// <summary>
    /// Every Codex feature an in-app run may turn off (contracts/m6-benchmark.md §0), in its order. The shell reports which
    /// of them this Codex has (provider.setClient codexFeatures); a name outside this set is never passed.
    /// </summary>
    public static readonly IReadOnlyList<string> CodexLockdownFeatures =
    [
        "shell_tool", "unified_exec", "apps", "browser_use", "browser_use_external", "computer_use", "in_app_browser",
        "image_generation", "plugins", "remote_plugin", "tool_suggest", "skill_mcp_dependency_install", "multi_agent",
    ];

    /// <summary>
    /// Codex: <c>exec --sandbox read-only -C &lt;runDir&gt; --json --skip-git-repo-check --ignore-user-config -c
    /// web_search='disabled' --disable &lt;feature&gt;… -c mcp_servers.optimization-tool.command='&lt;bridge&gt;' -c
    /// mcp_servers.optimization-tool.env.OT_RUN_TOKEN='&lt;token&gt;' -</c>, with one <c>--disable</c> per probed §0
    /// feature in <paramref name="disable"/> (m6 §0: no web, browser, apps or shell tool). A later ask resumes the thread:
    /// <c>exec --sandbox read-only -C &lt;runDir&gt; resume &lt;threadId&gt; --json …same… -</c> (the resume subcommand has no
    /// --sandbox/-C of its own, so they go on exec before it). Values are TOML single-quoted literals (no double quotes,
    /// which a cmd.exe line cannot carry); a bridge path with an apostrophe is written as a TOML basic string instead
    /// (fine for an .exe or a resolved npm shim; a plain .cmd refuses it). <paramref name="credentialsStore"/> (the user's
    /// <c>cli_auth_credentials_store</c>, see <see cref="CodexConfig"/>) is passed on as
    /// <c>-c cli_auth_credentials_store='…'</c>, because <c>--ignore-user-config</c> would otherwise drop it and the run
    /// would not find a keyring sign-in. Null when the bridge path or a token cannot be written safely, or a feature is
    /// outside <see cref="CodexLockdownFeatures"/>.
    /// </summary>
    public static IReadOnlyList<string>? Codex(string runDir, string bridgePath, string runToken, string? threadId, IReadOnlyList<string> disable, string? credentialsStore = null)
    {
        if (bridgePath.Contains('"') || bridgePath.Any(char.IsControl) || !TokenPattern().IsMatch(runToken)) return null;
        if (threadId is not null && !ThreadPattern().IsMatch(threadId)) return null;
        if (credentialsStore is not null && !CodexConfig.CredentialStores.Contains(credentialsStore)) return null;
        if (disable.Any(f => !CodexLockdownFeatures.Contains(f, StringComparer.Ordinal)) || disable.Distinct(StringComparer.Ordinal).Count() != disable.Count) return null;
        var command = bridgePath.Contains('\'') ? "\"" + bridgePath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"" : $"'{bridgePath}'";
        List<string> common = ["--json", "--skip-git-repo-check", "--ignore-user-config", "-c", "web_search='disabled'"];
        foreach (var feature in disable) common.AddRange(["--disable", feature]);
        common.AddRange(
        [
            "-c", $"mcp_servers.{ServerName}.command={command}",
            "-c", $"mcp_servers.{ServerName}.env.OT_RUN_TOKEN='{runToken}'",
        ]);
        if (credentialsStore is not null) common.AddRange(["-c", $"cli_auth_credentials_store='{credentialsStore}'"]);
        return threadId is null
            ? ["exec", "--sandbox", "read-only", "-C", runDir, .. common, "-"]
            : ["exec", "--sandbox", "read-only", "-C", runDir, "resume", threadId, .. common, "-"];
    }

    /// <summary>mcp.json for Claude Code: our bridge only, with this run's token in its environment.</summary>
    public static string ClaudeMcpConfig(string bridgePath, string runToken) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["command"] = bridgePath, ["args"] = new JsonArray(), ["env"] = new JsonObject { ["OT_RUN_TOKEN"] = runToken },
            },
        },
    }.ToJsonString();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    internal static partial Regex TokenPattern();

    [GeneratedRegex("^[0-9A-Za-z-]{1,64}$")]
    private static partial Regex ThreadPattern();
}

/// <summary>
/// The one non-secret setting read from the user's Codex config (<c>%CODEX_HOME%\config.toml</c>, else
/// <c>%USERPROFILE%\.codex\config.toml</c>): top-level <c>cli_auth_credentials_store</c>, accepted only as one of
/// <see cref="CredentialStores"/>. A simple line reader: keys inside a [table] are ignored, as is anything malformed.
/// </summary>
public static partial class CodexConfig
{
    public static readonly IReadOnlySet<string> CredentialStores = new HashSet<string>(StringComparer.Ordinal) { "file", "keyring", "auto", "ephemeral" };
    private const int MaxBytes = 1024 * 1024;

    /// <summary>The config path from CODEX_HOME (when fully qualified) or the user profile; null when neither is usable.</summary>
    public static string? ConfigPath(string? codexHome, string? userProfile)
    {
        if (!string.IsNullOrWhiteSpace(codexHome) && Path.IsPathFullyQualified(codexHome)) return Path.Combine(codexHome, "config.toml");
        return !string.IsNullOrWhiteSpace(userProfile) && Path.IsPathFullyQualified(userProfile) ? Path.Combine(userProfile, ".codex", "config.toml") : null;
    }

    /// <summary>The validated <c>cli_auth_credentials_store</c> value in <paramref name="toml"/>, or null.</summary>
    public static string? CredentialsStore(string? toml)
    {
        if (toml is null) return null;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) return null; // the first table: top-level keys are over
            var m = StoreLine().Match(line);
            if (m.Success) return CredentialStores.Contains(m.Groups["v"].Value) ? m.Groups["v"].Value : null;
        }
        return null;
    }

    /// <summary>Reads the user's setting from disk (the host's own environment); any failure means "not set".</summary>
    public static string? ReadCredentialsStore()
    {
        try
        {
            if (ConfigPath(Environment.GetEnvironmentVariable("CODEX_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is not { } path) return null;
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? CredentialsStore(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    [GeneratedRegex("""^cli_auth_credentials_store[ \t]*=[ \t]*(?:"(?<v>[a-z]+)"|'(?<v>[a-z]+)')[ \t]*(?:#.*)?$""")]
    private static partial Regex StoreLine();
}

/// <summary>
/// Reads one run's JSON stream. Claude Code (stream-json): assistant text blocks and the final result event (result,
/// session_id, usage, is_error). Codex (JSONL): thread.started, item.completed agent_message (the last one is the
/// answer), turn.completed usage, turn.failed / error. Lines that are not JSON objects, or whose fields have unexpected
/// types, are ignored.
/// </summary>
public sealed partial class HeadlessStream(bool codex, Action? onBlocked = null)
{
    /// <summary>Codex item types the runtime guard stops (m6 §0). collab_tool_call (sub-agents) is allowed.</summary>
    private static readonly string[] BlockedCodexItems = ["web_search", "command_execution", "file_change"];
    private const string OwnToolPrefix = "mcp__" + HeadlessCommand.ServerName + "__";

    private readonly List<string> _errors = [];
    private string? _lastText;
    private string? _result;

    public string? Session { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public bool Failed { get; private set; }

    /// <summary>
    /// Set by the runtime guard (m6 §0) when the run tried a tool it must not have: a loggable description (the item type,
    /// plus the MCP server name when it is a plain identifier; never arguments or text). The run is to be killed; later
    /// lines are ignored and there is no answer.
    /// </summary>
    public string? Blocked { get; private set; }

    /// <summary>The final answer, or null when the run produced none.</summary>
    public string? Answer => Failed || Blocked is not null ? null : !string.IsNullOrWhiteSpace(_result) ? _result : !string.IsNullOrWhiteSpace(_lastText) ? _lastText : null;

    public void Feed(string line)
    {
        if (Blocked is not null || line.Length == 0 || line[0] != '{') return;
        JsonObject? e;
        try { e = JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (e is null) return;
        // Valid JSON of an unexpected shape (a string where an object was expected, duplicate keys, a huge number) is
        // ignored like a non-JSON line: one odd line must not end the run.
        try { if (codex) FeedCodex(e); else FeedClaude(e); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException or System.Text.Json.JsonException) { }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long Num(JsonNode? n) => n is JsonValue v && Json.Number(v) is double d && d >= 0 ? (long)d : 0;

    private void Block(string what)
    {
        Blocked = what;
        onBlocked?.Invoke();
    }

    /// <summary>A name safe to log (a plain identifier), else "other".</summary>
    private static string LogName(string? name) => name is not null && LogNamePattern().IsMatch(name) ? name : "other";

    private void FeedClaude(JsonObject e)
    {
        switch (Str(e["type"]))
        {
            case "system" when Str(e["subtype"]) == "init":
                Session ??= Str(e["session_id"]);
                break;
            case "assistant":
                var content = e["message"]?["content"] as JsonArray ?? [];
                // Runtime guard (m6 §0): any tool but ours (built-in, server-side or another MCP server's) ends the run.
                var foreign = content.FirstOrDefault(b => Str(b?["type"]) is "tool_use" or "server_tool_use"
                    && !(Str(b?["name"]) ?? "").StartsWith(OwnToolPrefix, StringComparison.Ordinal));
                if (foreign is not null)
                {
                    var name = Str(foreign["name"]) ?? "";
                    var server = name.StartsWith("mcp__", StringComparison.Ordinal) && name.IndexOf("__", 5, StringComparison.Ordinal) is var end and > 5
                        ? LogName(name[5..end]) : null;
                    Block(Str(foreign["type"]) + (server is null ? "" : " server " + server));
                    return;
                }
                var texts = content.Where(b => Str(b?["type"]) == "text").Select(b => Str(b?["text"])).OfType<string>().ToList();
                if (texts.Count > 0) _lastText = string.Join("\n", texts);
                break;
            case "result":
                Session = Str(e["session_id"]) ?? Session;
                var usage = e["usage"];
                InputTokens += Num(usage?["input_tokens"]) + Num(usage?["cache_creation_input_tokens"]) + Num(usage?["cache_read_input_tokens"]);
                OutputTokens += Num(usage?["output_tokens"]);
                if (e["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var isError) && isError)
                {
                    Failed = true;
                    _errors.Add(Str(e["result"]) ?? Str(e["subtype"]) ?? "error");
                }
                else _result = Str(e["result"]);
                break;
        }
    }

    private void FeedCodex(JsonObject e)
    {
        switch (Str(e["type"]))
        {
            case "thread.started":
                Session = Str(e["thread_id"]) ?? Session;
                break;
            case "item.started" or "item.updated" or "item.completed" when GuardCodex(e["item"]):
                return;
            case "item.completed":
                var item = e["item"];
                if (Str(item?["type"]) == "agent_message" && Str(item?["text"]) is { } text) _lastText = text;
                else if (Str(item?["type"]) == "error") _errors.Add(Str(item?["message"]) ?? "error");
                break;
            case "turn.completed":
                InputTokens += Num(e["usage"]?["input_tokens"]);
                OutputTokens += Num(e["usage"]?["output_tokens"]);
                break;
            case "turn.failed":
                Failed = true;
                _errors.Add((e["error"] as JsonObject is { } err ? Str(err["message"]) : Str(e["error"])) ?? "turn failed");
                break;
            case "error":
                Failed = true;
                _errors.Add(Str(e["message"]) ?? "error");
                break;
        }
    }

    /// <summary>
    /// Runtime guard (m6 §0): a web_search, command_execution or file_change item, or an mcp_tool_call to a server other
    /// than ours, blocks the run at its first event (item.started, else item.completed). True when blocked.
    /// </summary>
    private bool GuardCodex(JsonNode? item)
    {
        var type = Str(item?["type"]);
        if (type is null) return false;
        if (BlockedCodexItems.Contains(type, StringComparer.Ordinal)) Block(type);
        else if (type == "mcp_tool_call" && Str(item?["server"]) is var server && server != HeadlessCommand.ServerName) Block(type + " server " + LogName(server));
        return Blocked is not null;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex LogNamePattern();

    /// <summary>Error text (stream errors plus stderr) that looks like a sign-in problem: login, not logged in, 401.</summary>
    public bool LooksLikeAuth(string stderr) => AuthPattern().IsMatch(string.Join("\n", _errors) + "\n" + stderr);

    [GeneratedRegex(@"\blog\s?in\b|not logged in|logged out|\b401\b|unauthori[sz]ed|authenticat", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPattern();
}

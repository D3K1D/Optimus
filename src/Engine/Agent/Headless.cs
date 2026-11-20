using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

// M5 headless runs (contracts/m5-chat.md §3): the host runs Claude Code in the background under the user's
// own plan. The engine builds the argument list (fixed tokens, app-made paths and IDs only), writes the prompt to
// stdin and reads the CLI's JSON stream. The process itself is started by the host (src/Host/CliRunner.cs) through
// the shared safe command builder (OptimizationTool.Transport.ProcessCommand.ForCli).

/// <summary>
/// One headless CLI run. <see cref="StandardInput"/> carries every piece of user text; arguments never do.
/// <see cref="Environment"/>: null (chat runs) inherits the host's environment with PATH refreshed; a dictionary (research
/// runs, m9 §6.3) is the child's WHOLE environment, built from an allowlist, with only PATH refreshed by the runner.
/// </summary>
public sealed record CliLaunch(string CliPath, IReadOnlyList<string> Arguments, string StandardInput, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

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
    /// an empty token cannot be passed safely through a .cmd shim. <paramref name="settingSources"/> (m9 §6.3 chat
    /// hardening, when the probed help lists the flag): <c>--setting-sources=</c> right after <c>--tools=</c>, the one-token
    /// empty value, so user, project and local settings (and their hooks) are not loaded. <c>--safe-mode</c> is never
    /// added here: it would also drop the explicitly passed MCP server until a live run shows otherwise. Research runs
    /// have their own builder (<see cref="ResearchCommand"/>); the two never share code. <paramref name="maxTurns"/> (m9 §17.6):
    /// <c>--max-turns n</c>, Claude Code's own cap on agentic turns in print mode, set to the ask's budget.
    /// </summary>
    public static IReadOnlyList<string> Claude(string runDir, Guid session, bool resume, bool settingSources = false, int? maxTurns = null) =>
    [
        "-p", "--output-format", "stream-json", "--verbose", "--tools=", .. settingSources ? new[] { "--setting-sources=" } : [], "--strict-mcp-config",
        "--mcp-config", Path.Combine(runDir, McpConfigFile), "--allowedTools", "mcp__" + ServerName,
        "--append-system-prompt-file", Path.Combine(runDir, SystemFile),
        .. maxTurns is { } turns ? new[] { "--max-turns", turns.ToString(System.Globalization.CultureInfo.InvariantCulture) } : [],
        resume ? "--resume" : "--session-id", session.ToString("D"),
    ];

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
}

/// <summary>
/// Reads one Claude Code run's JSON stream (stream-json): assistant text blocks and the final result event (result,
/// session_id, usage, is_error). Lines that are not JSON objects, or whose fields have unexpected types, are ignored.
/// Status events (any type but assistant, user and result, and system subtypes but init) are ignored when
/// <see cref="StreamShape.Event"/> finds no tool call, tool result or message content in them; otherwise the guard kills.
/// </summary>
public sealed partial class HeadlessStream(Action? onBlocked = null)
{
    private const string OwnToolPrefix = "mcp__" + HeadlessCommand.ServerName + "__";

    private readonly List<string> _errors = [];
    private string? _lastText;
    private string? _result;

    public string? Session { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public bool Failed { get; private set; }

    /// <summary>The model round trips the run reports in its result event (num_turns), or null when it reports none (m9 §17.6).</summary>
    public long? NumTurns { get; private set; }

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
        try { FeedClaude(e, line.Length); }
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

    private void FeedClaude(JsonObject e, int length)
    {
        var type = Str(e["type"]);
        switch (type)
        {
            case "system" when Str(e["subtype"]) == "init":
                Session ??= Str(e["session_id"]);
                break;
            case "assistant":
                var content = e["message"]?["content"] as JsonArray ?? [];
                // Runtime guard (m6 §0): any tool but ours (built-in, server-side or another MCP server's) ends the run.
                var foreign = content.FirstOrDefault(b => StreamShape.IsToolCall(Str(b?["type"]))
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
                if (e["num_turns"] is JsonValue nt && Json.Number(nt) is double turns && turns >= 0 && turns == Math.Floor(turns) && turns < 1e6)
                    NumTurns = (NumTurns ?? 0) + (long)turns;
                if (e["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var isError) && isError)
                {
                    Failed = true;
                    _errors.Add(Str(e["result"]) ?? Str(e["subtype"]) ?? "error");
                }
                else _result = Str(e["result"]);
                break;
            case "user":
                break; // the results of our own tools
            default:
                // A status event (rate_limit_event, system thinking_tokens, unknown types and subtypes): ignored when small
                // and content-free; one that carries a call, a result or a message is outside what this guard reads.
                var what = type == "system" ? "system " + LogName(Str(e["subtype"])) : "event " + LogName(type);
                switch (StreamShape.Event(e, length))
                {
                    case StreamShape.Carries.ToolOrContent: Block(what); break;
                    case StreamShape.Carries.TooLarge: Block(what + " too large"); break;
                }
                break;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex LogNamePattern();

    /// <summary>Error text (stream errors plus stderr) that looks like a sign-in problem: login, not logged in, 401.</summary>
    public bool LooksLikeAuth(string stderr) => AuthPattern().IsMatch(string.Join("\n", _errors) + "\n" + stderr);

    [GeneratedRegex(@"\blog\s?in\b|not logged in|logged out|\b401\b|unauthori[sz]ed|authenticat", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPattern();
}

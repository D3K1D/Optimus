using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Agent;

// M9 §6.3 research through the headless CLI (contracts/m9-ai-powers.md): Claude Code runs ONE research call per
// subject, each in a new empty folder, with its own argv (never the chat builder), a reduced environment, no MCP server,
// no run token, no session resume and a closed guard: any event or block outside the profile kills the
// process tree (tool_blocked) and nothing is stored.
//
// The accepted-shape profile below is written from a captured trace of Claude Code 2.1.318 (the product's exact argv and
// environment, scrubbed: tests/OptimizationTool.Tests/Traces/claude-research-2.1.318.jsonl). Everything fails closed: a
// version outside the profile's range has no research, model content or a tool outside the profile kills the call, and
// only small content-free status events (rate_limit_event, system thinking_tokens, …) are ignored.

/// <summary>The research argument lists (m9 §6.3 table). Separate from <see cref="HeadlessCommand"/> by design.</summary>
public static class ResearchCommand
{
    public const string McpConfigFile = "empty.json", SystemFile = "research.md";

    /// <summary>The MCP config a Claude research call loads (with --strict-mcp-config): no server at all.</summary>
    public const string EmptyMcpConfig = """{"mcpServers":{}}""";

    /// <summary>The one tool a Claude research call has.</summary>
    public const string ClaudeTool = "WebSearch";

    /// <summary>
    /// Claude Code: <c>-p --output-format stream-json --verbose --safe-mode --setting-sources= --tools WebSearch
    /// --allowedTools WebSearch --strict-mcp-config --mcp-config &lt;dir&gt;\empty.json --append-system-prompt-file
    /// &lt;dir&gt;\research.md --session-id &lt;new uuid&gt;</c>. Never <c>--resume</c>; the prompt goes on stdin.
    /// </summary>
    public static IReadOnlyList<string> Claude(string runDir, Guid session) =>
    [
        "-p", "--output-format", "stream-json", "--verbose", "--safe-mode", "--setting-sources=",
        "--tools", ClaudeTool, "--allowedTools", ClaudeTool, "--strict-mcp-config",
        "--mcp-config", Path.Combine(runDir, McpConfigFile), "--append-system-prompt-file", Path.Combine(runDir, SystemFile),
        "--session-id", session.ToString("D"),
    ];
}

/// <summary>
/// The environment of a research call (m9 §6.3), built from scratch: only what the CLI needs to start and authenticate
/// (system and profile locations, PATH, TEMP, proxies, and the CLI's own config/auth home variables). Nothing else is
/// inherited: no NODE_OPTIONS, no other tool's variables, no user-defined settings.
/// </summary>
public static class ResearchEnvironment
{
    /// <summary>System and profile locations, PATH, TEMP and the network proxy.</summary>
    public static readonly IReadOnlyList<string> Common =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "ProgramData", "ALLUSERSPROFILE", "PUBLIC", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "USERNAME", "USERDOMAIN", "PATH", "TEMP", "TMP", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY",
    ];

    /// <summary>Claude Code's own config home and API-key sign-in.</summary>
    public static readonly IReadOnlyList<string> Claude = ["CLAUDE_CONFIG_DIR", "ANTHROPIC_API_KEY"];

    private const int MaxValue = 32767;

    /// <summary>The allowlisted variables that are set (non-empty, no NUL, within Windows' size), read through <paramref name="read"/>.</summary>
    public static Dictionary<string, string> Build(Func<string, string?> read)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Common.Concat(Claude))
            if (read(name) is { Length: > 0 and <= MaxValue } value && !value.Contains('\0')) env[name] = value;
        return env;
    }
}

/// <summary>
/// Claude Code research profile (stream-json), from a captured trace of Claude Code 2.1.318. The first event is
/// system/init with tools exactly [WebSearch] and no MCP server. In assistant and user messages, the blocks of the message
/// content (and of a tool_result's content list) must be listed here, with tool_use named exactly
/// <see cref="ResearchCommand.ClaudeTool"/> and tool_result only for a WebSearch call seen in this call. Every other event
/// type (system subtypes, rate_limit_event, unknown types) is ignored when <see cref="StreamShape.Event"/> finds no tool
/// call, tool result or message content in it, and kills the call otherwise.
/// </summary>
public static class ClaudeResearchProfile
{
    /// <summary>The profile version: research-2 is the captured-trace profile (research-1 was written from documentation).</summary>
    public const string Profile = "claude-code.stream-json/research-2";
    /// <summary>
    /// The oldest CLI version this profile covers; every newer version is covered too (no upper bound). An upper bound would
    /// silently turn research off for every user on the next CLI update, while a newer CLI whose stream changes shape is
    /// already stopped by the structural checks (init, block allowlist, walk of every other field), which fail closed as
    /// tool_blocked. A newer CLI without --safe-mode or --setting-sources is no_capability from the probed flags.
    /// </summary>
    public static readonly Version Min = new(2, 1, 0);
    /// <summary>Blocks of an assistant message: text, thinking, and tool_use named exactly WebSearch.</summary>
    public static readonly IReadOnlySet<string> AssistantBlocks = new HashSet<string>(StringComparer.Ordinal) { "text", "thinking", "redacted_thinking", "tool_use" };
    /// <summary>Blocks of a user message: only the tool_result of a WebSearch tool_use seen in this call.</summary>
    public static readonly IReadOnlySet<string> UserBlocks = new HashSet<string>(StringComparer.Ordinal) { "tool_result" };
    /// <summary>Blocks inside a tool_result's content list.</summary>
    public static readonly IReadOnlySet<string> ToolResultBlocks = new HashSet<string>(StringComparer.Ordinal) { "text" };
    /// <summary>The id prefix of the server-side searches listed inside a WebSearch result (tool_use_result.results[].tool_use_id).</summary>
    public const string ServerSearchIdPrefix = "srvtoolu_";
}

/// <summary>Which profile covers a CLI version (null: none, so research is unavailable for that CLI). Claude Code: any version
/// at or above <see cref="ClaudeResearchProfile.Min"/>; an unparsable version has no profile.</summary>
public static class ResearchProfiles
{
    public static string? For(string kind, string? version)
    {
        if (ParseVersion(version) is not { } v) return null;
        return kind switch
        {
            "claude_code_mcp" when v >= ClaudeResearchProfile.Min => ClaudeResearchProfile.Profile,
            _ => null,
        };
    }

    /// <summary>The first a.b.c in a version text ("2.1.318 (Claude Code)"), or null.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (text is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,6})\.(\d{1,6})\.(\d{1,6})", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return m.Success ? new Version(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
    }
}

/// <summary>
/// Reads one research call's stream through its closed guard and collects the answer text, the OBSERVED sources (from
/// tool results or result items only, never from the model's text), the number of searches and usage. A line outside
/// the profile sets <see cref="Blocked"/> (a loggable description: a type name, never content) and calls the kill.
/// </summary>
public sealed class ResearchStream(Action? onBlocked = null, int killAboveSearches = 0)
{
    /// <summary>Set when the call was killed for searching more than its limit (m9 §6.8: Claude Code cannot cap searches).</summary>
    public bool OverSearchLimit { get; private set; }

    public const int MaxObserved = 50, MaxLinksPerResult = 20;

    private readonly HashSet<string> _searchIds = new(StringComparer.Ordinal);
    private readonly List<ObservedSource> _observed = [];
    private readonly List<string> _errors = [];
    private string? _lastText, _result;
    private bool _init;

    public string? Blocked { get; private set; }
    public bool Failed { get; private set; }
    public bool Ended { get; private set; }
    public int Searches { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public IReadOnlyList<ObservedSource> Observed => Blocked is null ? _observed : [];

    /// <summary>The model's final text (the result event, else the last assistant text).</summary>
    public string? Answer => Failed || Blocked is not null ? null : !string.IsNullOrWhiteSpace(_result) ? _result : !string.IsNullOrWhiteSpace(_lastText) ? _lastText : null;

    public void Feed(string line)
    {
        if (Blocked is not null) return;
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonObject? e;
        try { e = line.TrimStart()[0] == '{' ? JsonNode.Parse(line) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { e = null; }
        if (e is null) { Block("malformed line"); return; } // a stream of a research call is JSON lines only
        try { FeedClaude(e, line.Length); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException or System.Text.Json.JsonException)
        {
            Block("malformed event");
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long Num(JsonNode? n) => n is JsonValue v && Json.Number(v) is double d && d >= 0 ? (long)Math.Min(d, int.MaxValue) : 0;

    private void Block(string what)
    {
        if (Blocked is not null) return;
        Blocked = what;
        onBlocked?.Invoke();
    }

    /// <summary>A type name safe to log: a short plain identifier, else "other".</summary>
    private static string Name(string? s) => s is { Length: > 0 and <= 64 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? s : "other";

    // ---------------------------------------------------------------- Claude Code (stream-json)

    private void FeedClaude(JsonObject e, int length)
    {
        var type = Str(e["type"]);
        var init = type == "system" && Str(e["subtype"]) == "init";
        if (!_init && !init) { Block("first event " + Name(type)); return; } // the call must open with its init
        if (init) { Init(e); return; }
        switch (type)
        {
            case "assistant": Assistant(e); return;
            case "user": User(e); return;
            case "result":
                Ended = true;
                InputTokens += Num(e["usage"]?["input_tokens"]) + Num(e["usage"]?["cache_creation_input_tokens"]) + Num(e["usage"]?["cache_read_input_tokens"]);
                OutputTokens += Num(e["usage"]?["output_tokens"]);
                if (e["is_error"] is JsonValue iv && iv.TryGetValue<bool>(out var isError) && isError)
                {
                    Failed = true;
                    _errors.Add(Str(e["result"]) ?? Str(e["subtype"]) ?? "error");
                }
                else _result = Str(e["result"]);
                return;
            default:
                // Any other event (system subtypes, rate_limit_event, unknown types) is a status event: ignored when small
                // and content-free, killed otherwise (it may carry a call or model text this guard does not read).
                var what = type == "system" ? "system " + Name(Str(e["subtype"])) : "event " + Name(type);
                switch (StreamShape.Event(e, length))
                {
                    case StreamShape.Carries.ToolOrContent: Block(what); return;
                    case StreamShape.Carries.TooLarge: Block(what + " too large"); return;
                }
                return;
        }
    }

    /// <summary>system/init: the call's tools are exactly [WebSearch] and it has no MCP server. Agents, skills, plugins and
    /// slash commands may be listed: they are not callable without the Agent or Skill tool, which tools would list.</summary>
    private void Init(JsonObject e)
    {
        if (e["tools"] is not JsonArray tools || tools.Count != 1 || Str(tools[0]) != ResearchCommand.ClaudeTool) { Block("init tools"); return; }
        if (e["mcp_servers"] is not JsonArray { Count: 0 }) { Block("init mcp_servers"); return; }
        _init = true;
    }

    /// <summary>A field outside the message content carries no call, result or message (<see cref="StreamShape.Walk"/>); else it kills.</summary>
    private bool Plain(string where, JsonNode? field)
    {
        if (StreamShape.Walk(field) == StreamShape.Carries.Nothing) return true;
        Block(where);
        return false;
    }

    private void Assistant(JsonObject e)
    {
        if (e["message"] is not JsonObject message || message["content"] is not JsonArray content) { Block("assistant without content"); return; }
        var texts = new List<string>();
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in content)
        {
            var bt = Str(node?["type"]);
            if (node is not JsonObject b || bt is null || !ClaudeResearchProfile.AssistantBlocks.Contains(bt)) { Block("block " + Name(bt)); return; }
            if (bt == "tool_use")
            {
                if (Str(b["name"]) != ResearchCommand.ClaudeTool) { Block("tool_use " + Name(Str(b["name"]))); return; }
                if (Str(b["id"]) is not { Length: > 0 } id) { Block("tool_use without id"); return; }
                calls.Add(id);
                if (_searchIds.Add(id)) Searches++;
                if (killAboveSearches > 0 && Searches > killAboveSearches)
                {
                    OverSearchLimit = true;
                    Block("search limit");
                    return;
                }
            }
            else if (bt == "text" && Str(b["text"]) is { } t) texts.Add(t);
        }
        foreach (var (key, value) in message)
            if (key != "content" && !Plain("assistant message " + Name(key), value)) return;
        foreach (var (key, value) in e)
        {
            if (key is "type" or "message") continue;
            // wire_tool_inputs: the inputs of this message's calls, keyed by tool_use id; only WebSearch calls of this message.
            if (key == "wire_tool_inputs" && value is not null)
            {
                if (value is not JsonObject wire) { Block("wire_tool_inputs"); return; }
                foreach (var (id, _) in wire)
                    if (!calls.Contains(id)) { Block("wire_tool_inputs of another call"); return; }
            }
            if (!Plain("assistant " + Name(key), value)) return;
        }
        if (texts.Count > 0) _lastText = string.Join("\n", texts);
    }

    private void User(JsonObject e)
    {
        if (e["message"] is not JsonObject message || message["content"] is not JsonArray results) { Block("user without tool results"); return; }
        var anyError = false;
        var answered = 0;
        foreach (var node in results)
        {
            var bt = Str(node?["type"]);
            if (node is not JsonObject b || bt is null || !ClaudeResearchProfile.UserBlocks.Contains(bt)) { Block("block " + Name(bt)); return; }
            if (Str(b["tool_use_id"]) is not { } useId || !_searchIds.Contains(useId)) { Block("tool_result of another tool"); return; }
            var errored = b["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var err) && err;
            anyError |= errored;
            answered++;
            if (!ToolResultSources(b["content"], errored)) return;
        }
        foreach (var (key, value) in message)
            if (key != "content" && !Plain("user message " + Name(key), value)) return;
        JsonObject? structured = null;
        foreach (var (key, value) in e)
        {
            if (key is "type" or "message" || value is null) continue;
            switch (key)
            {
                case "tool_use_result":
                    // The structured copy of this event's WebSearch result: an object (results of srvtoolu_ searches, or
                    // text), or an error string. Never without a WebSearch tool_result in the same event.
                    if (answered == 0) { Block("tool_use_result without a tool_result"); return; }
                    if (value is JsonValue v && v.TryGetValue<string>(out _)) continue;
                    if (value is not JsonObject o) { Block("tool_use_result"); return; }
                    if (o["results"] is not null and not JsonArray) { Block("tool_use_result results"); return; }
                    foreach (var item in o["results"] as JsonArray ?? [])
                        if (item is JsonObject io && io["tool_use_id"] is { } tid && !SearchResultId(Str(tid)))
                        {
                            Block("tool_use_result of another call");
                            return;
                        }
                    structured = o;
                    break;
                case "tool_result_meta":
                    // Per result: the permission decision, which must be an accept of a WebSearch call seen in this call.
                    if (value is not JsonArray meta) { Block("tool_result_meta"); return; }
                    foreach (var m in meta)
                    {
                        if (m is not JsonObject mo || Str(mo["id"]) is not { } mid || !_searchIds.Contains(mid)) { Block("tool_result_meta of another call"); return; }
                        if (mo["permission_decision"] is { } pd && Str(pd["decision"]) != "accept") { Block("tool_result_meta decision"); return; }
                    }
                    break;
            }
            if (!Plain("user " + Name(key), value)) return;
        }
        // A search that reported an error establishes no source, in either copy.
        if (!anyError && structured is not null) StructuredSources(structured);
    }

    /// <summary>An id inside a WebSearch result: a server-side search (srvtoolu_…) or a WebSearch call seen in this call.</summary>
    private bool SearchResultId(string? id) =>
        id is { Length: > 0 } && (id.StartsWith(ClaudeResearchProfile.ServerSearchIdPrefix, StringComparison.Ordinal) || _searchIds.Contains(id));

    /// <summary>A WebSearch tool_result's content: a string, or a list of text blocks. False when it blocked.</summary>
    private bool ToolResultSources(JsonNode? content, bool isError)
    {
        var texts = new List<string>();
        switch (content)
        {
            case null: break;
            case JsonValue v when v.TryGetValue<string>(out var s): texts.Add(s); break;
            case JsonArray blocks:
                foreach (var b in blocks)
                {
                    var bt = Str(b?["type"]);
                    if (bt is null || !ClaudeResearchProfile.ToolResultBlocks.Contains(bt)) { Block("tool_result block " + Name(bt)); return false; }
                    if (Str(b!["text"]) is { } t) texts.Add(t);
                }
                break;
            default: Block("tool_result content"); return false;
        }
        if (!isError) foreach (var t in texts) LinksFromText(t);
        return true;
    }

    /// <summary>
    /// The WebSearch result text lists its results as a line <c>Links: [{"title":…,"url":…}, …]</c>. Only the FIRST such
    /// line that starts a line is read, as JSON; result snippets after it are never searched for URLs.
    /// </summary>
    internal void LinksFromText(string text)
    {
        var at = text.StartsWith("Links: [", StringComparison.Ordinal) ? 0 : text.IndexOf("\nLinks: [", StringComparison.Ordinal) is var i and >= 0 ? i + 1 : -1;
        if (at < 0) return;
        var start = at + "Links: ".Length;
        var end = ArrayEnd(text, start);
        if (end < 0) return;
        JsonArray? links;
        try { links = JsonNode.Parse(text[start..(end + 1)]) as JsonArray; }
        catch (System.Text.Json.JsonException) { return; }
        foreach (var link in (links ?? []).Take(MaxLinksPerResult).OfType<JsonObject>()) AddSource(Str(link["url"]), Str(link["title"]));
    }

    /// <summary>tool_use_result {results:[{content:[{title,url}]} | "text"]} (structured, when present).</summary>
    private void StructuredSources(JsonObject structured)
    {
        if (structured["results"] is not JsonArray results) return;
        foreach (var r in results)
        {
            if (r is not JsonObject ro || ro["content"] is not JsonArray items) continue; // a text item (the CLI adds the result text) has no links
            foreach (var item in items.Take(MaxLinksPerResult).OfType<JsonObject>()) AddSource(Str(item["url"]), Str(item["title"]));
        }
    }

    /// <summary>Index of the ']' that closes the JSON array opening at <paramref name="start"/>, honouring strings; -1 when it doesn't close.</summary>
    private static int ArrayEnd(string s, int start)
    {
        int depth = 0;
        var inString = false;
        for (var i = start; i < s.Length && i < start + 64 * 1024; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}')
            {
                depth--;
                if (depth == 0) return c == ']' ? i : -1;
            }
        }
        return -1;
    }

    private void AddSource(string? url, string? title)
    {
        if (_observed.Count >= MaxObserved || UrlHygiene.Clean(url) is not { } clean) return;
        if (_observed.Any(o => o.Url == clean)) return;
        _observed.Add(new ObservedSource(clean, title is { Length: > 0 and <= 1000 } ? title : null));
    }

    /// <summary>Error text (stream errors plus stderr) that looks like a sign-in problem.</summary>
    public bool LooksLikeAuth(string stderr) => new HeadlessStream().LooksLikeAuth(string.Join("\n", _errors) + "\n" + stderr);
}

/// <summary>What the host knows about an installed CLI for research (from provider.setClient).</summary>
internal sealed record ResearchClient(string Path, string? Version, bool? SignedIn, IReadOnlyList<string> ClaudeFlags);

/// <summary>
/// <see cref="IWebResearch"/> for the headless kind (claude_code_mcp): one CLI process per call, in a new
/// empty folder (user-only ACL, deleted afterwards), with the research argv, the allowlisted environment and the closed
/// guard of the profile. Cancelling (the caller's token, the guard) kills the process tree through the runner.
/// </summary>
internal sealed class HeadlessResearcher(ICliRunner runner, string dataDir, Func<string, ResearchClient?> clientOf,
    Func<string, string?> environment, Action<string> log) : IWebResearch
{
    public static readonly string[] ClaudeFlagsNeeded = ["--safe-mode", "--setting-sources"];

    /// <summary>Why research cannot run with this CLI (no_client | no_profile | no_capability), or null when it can.</summary>
    public static string? Unavailable(string kind, ResearchClient? c)
    {
        if (c is null) return "no_client";
        if (ResearchProfiles.For(kind, c.Version) is null) return "no_profile";
        return kind switch
        {
            "claude_code_mcp" when ClaudeFlagsNeeded.All(f => c.ClaudeFlags.Contains(f, StringComparer.Ordinal)) => null,
            _ => "no_capability",
        };
    }

    public async Task<ResearchAnswer> ResearchAsync(ProviderCredential credential, ResearchCall call, CancellationToken ct)
    {
        var kind = credential.Kind;
        var client = clientOf(kind);
        if (Unavailable(kind, client) is { } why)
        {
            Warn($"unavailable reason={why} kind={LogToken(kind)}", ErrorSites.ResearchCliUnavailable);
            throw new WebResearchUnsupportedException("not_available_yet"); // capability changed since the run began
        }
        if (client!.SignedIn == false)
        {
            Warn($"not signed in kind={LogToken(kind)}", ErrorSites.ResearchCliNotSignedIn);
            throw new ProviderException("auth", "Not signed in.", false);
        }

        var runDir = Path.Combine(dataDir, "research-runs", "r-" + Ids.RandomHex(8));
        try
        {
            Directory.CreateDirectory(runDir);
            try { Acl.RestrictDirectory(runDir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) 
            {
                log($"research run folder ACL: {ex.GetType().Name}");
                Warn($"run folder ACL failed {ex.GetType().Name}", ErrorSites.ResearchCliFolderAcl);
            }

            File.WriteAllText(Path.Combine(runDir, ResearchCommand.McpConfigFile), ResearchCommand.EmptyMcpConfig);
            File.WriteAllText(Path.Combine(runDir, ResearchCommand.SystemFile), call.System);
            var args = ResearchCommand.Claude(runDir, Guid.NewGuid());
            var stdin = call.Subject;

            using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stream = new ResearchStream(() => { try { guard.Cancel(); } catch (ObjectDisposedException) { } }, call.KillAboveSearches);
            var env = ResearchEnvironment.Build(environment);
            CliExit? exit = null;
            try
            {
                exit = await runner.RunAsync(new CliLaunch(client.Path, args, stdin, runDir, env), stream.Feed, guard.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stream.Blocked is not null && !ct.IsCancellationRequested) { }
            if (stream.OverSearchLimit)
            {
                log($"research call stopped: more than {call.KillAboveSearches} searches");
                Warn($"call stopped reason=search_limit searches={stream.Searches} limit={call.KillAboveSearches}", ErrorSites.ResearchCliSearchLimit);
                throw new ProviderException("search_limit", "The research call searched more than allowed.", false);
            }
            if (stream.Blocked is { } blocked)
            {
                log($"research call stopped: tool blocked ({blocked})");
                Warn($"call stopped reason=tool_blocked what={LogToken(blocked)}", ErrorSites.ResearchCliToolBlocked);
                throw new ProviderException("tool_blocked", "The research call used something it must not have.", false);
            }
            if (exit is { Started: false })
            {
                Warn($"CLI did not start reason={LaunchCode(exit.LaunchError)}", ErrorSites.ResearchCliNotStarted);
                throw new ProviderException("unavailable", "The CLI did not start.", false);
            }
            var answer = stream.Answer;
            if (answer is null)
            {
                // The exit code, the stderr length and whether it looks like a sign-in problem: never the text itself.
                var stderr = exit?.StandardError ?? "";
                var auth = stream.LooksLikeAuth(stderr);
                var code = exit is null ? "none" : exit.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Warn($"no answer exit={code} stderr={stderr.Length}ch auth_like={(auth ? "yes" : "no")}",
                    auth ? ErrorSites.ResearchCliNotSignedIn : ErrorSites.ResearchCliNoAnswer);
                if (auth) throw new ProviderException("auth", "Not signed in.", false);
                throw new ProviderException("client_error", "The CLI gave no answer.", true);
            }
            return new ResearchAnswer(answer, stream.Observed, stream.Searches,
                new ProviderUsage((int)Math.Min(stream.InputTokens, int.MaxValue), (int)Math.Min(stream.OutputTokens, int.MaxValue)));
        }
        finally
        {
            try { if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) 
            {
                log($"research run cleanup: {ex.GetType().Name}");
                Warn($"run folder cleanup failed {ex.GetType().Name}", ErrorSites.ResearchCliCleanup);
            }
        }
    }

    /// <summary>The developer log's "why" line (docs/error-logging.md): fixed words, codes, counts and type names only.</summary>
    private static void Warn(string message, string site,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        AppLog.Current.Write(LogLevel.Warn, "research: headless " + message, site, null, file, line, member);

    /// <summary>A launch error is one of the fixed codes of <see cref="CliExit.LaunchError"/>; anything else logs as "other".</summary>
    internal static string LaunchCode(string? code) =>
        code is null ? "launch_failed" : code is "unsupported_path" or "policy" or "access_denied" or "launch_failed" ? code : "other";

    /// <summary>A short plain token (letters, digits, space, <c>_ . -</c>) safe to log; anything else logs as "other".</summary>
    internal static string LogToken(string? s) =>
        s is { Length: > 0 and <= 80 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or ' ') ? s : "other";
}

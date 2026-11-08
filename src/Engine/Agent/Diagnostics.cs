using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

/// <summary>One model-callable diagnostic (contracts/m3-agent.md §3.3): which collectors it runs and how arguments map to options.</summary>
public sealed record DiagnosticDef(string Id, string Description, string[] CollectorIds, int DurationSeconds, bool TakesWindow, bool TakesTarget)
{
    /// <summary>Collector IDs for these arguments (events.recent picks win.events.&lt;target&gt;).</summary>
    public string[] Resolve(string? target) => TakesTarget ? [$"win.events.{target}"] : CollectorIds;
}

/// <summary>The §3.3 table, the four tool specs and their argument validation (shared by the in-app loop and tool.call).</summary>
public static class Diagnostics
{
    public static readonly string[] EventTargets = ["system", "application", "update", "defender"];

    /// <summary>network.listeners: exposed (default) = a summary plus what other devices can reach; all = every endpoint row.</summary>
    public static readonly string[] ListenerTargets = ["exposed", "all"];
    public static readonly string[] AllTargets = [.. EventTargets, .. ListenerTargets];
    public const string Listeners = "network.listeners";
    public const int DefaultSampleSeconds = 15, MinSampleSeconds = 5, DefaultLookbackDays = 7, MaxLookbackDays = 7;

    /// <summary>A diagnostic expected to run longer than this returns an operation ID instead of waiting.</summary>
    public const int OperationThresholdSeconds = 10;

    public static readonly IReadOnlyList<DiagnosticDef> All =
    [
        new("power.inventory", "Power schemes, the active scheme and battery state.", ["win.system.power"], 2, false, false),
        new("process.sample", "Samples CPU, memory and disk use and the busiest processes over a window of seconds.", ["win.perf.sample"], DefaultSampleSeconds, true, false),
        new("startup.list", "Apps, tasks and services that start with Windows or at sign-in.", ["win.security.startup"], 5, false, false),
        new("events.recent", "Grouped counts of recent Windows event log errors for one log, over a window of days.", [], 5, true, true),
        new("storage.volumes", "Capacity and free space of fixed volumes.", ["win.system.volumes"], 2, false, false),
        new("network.listeners", "Programs that accept network connections. target exposed (default): a summary and what other devices can reach; all: every endpoint.", ["win.security.listeners"], 3, false, false),
        new("display.modes", "Each active display's current and maximum refresh rate.", ["win.custom.display"], 2, false, false),
        new("devices.problems", "Devices that report a problem code.", ["win.devices.pnp"], 4, false, false),
        new("security.status", "Antivirus, Defender and firewall status.", ["win.security.wsc", "win.security.defender", "win.security.firewall"], 4, false, false),
    ];

    public static DiagnosticDef? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    public const string ListCapabilities = "list_capabilities", GetFindings = "get_findings", RunDiagnostic = "run_diagnostic", GetOperation = "get_operation";
    public const string RequestAction = "request_action";

    /// <summary>M5 (contracts/m5-chat.md §5): always allowed, read-only or deck-only, no diagnostic budget.</summary>
    public const string GetCards = "get_cards", GetPcSpecs = "get_pc_specs", GetReportHistory = "get_report_history", NoteCard = "note_card", AddCard = "add_card";
    public static readonly string[] ContextTools = [GetCards, GetPcSpecs, GetReportHistory, NoteCard, AddCard];
    public static readonly string[] ToolNames = [ListCapabilities, GetFindings, RunDiagnostic, GetOperation, RequestAction, .. ContextTools];

    public const int MaxNoteChars = 600, MaxCardTitle = 120, MaxCardWhy = 400, MaxCardRefs = 5, MaxHistory = 10, DefaultHistory = 5;

    public static readonly string[] Severities = ["info", "low", "medium", "high", "critical"];
    public static readonly string[] Categories = ["security", "performance", "storage", "privacy_telemetry", "diagnostics", "customization", "misc"];

    /// <summary>The tools for a grant: run_diagnostic's enum lists only granted diagnostics; request_action only with canRequestChanges.</summary>
    public static IReadOnlyList<ToolSpec> Tools(Grant grant, IReadOnlyList<string>? actionIds = null)
    {
        static JsonArray Enum(IEnumerable<string> values) => Json.Array(values);
        return
        [
            new(ListCapabilities, "Lists the diagnostics you may run and the remaining budget.", new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false,
            }),
            new(GetFindings, "Returns findings from the user's report (20 per page), each with its evidence.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["category"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(Categories) },
                    ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(Severities) },
                    ["findingIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 50 },
                    ["cursor"] = new JsonObject { ["type"] = "string" },
                },
                ["additionalProperties"] = false,
            }),
            new(RunDiagnostic, "Runs one read-only diagnostic on this PC. Long runs return an operationId; poll get_operation.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["diagnosticId"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(grant.Diagnostics) },
                    ["window"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = Math.Max(grant.Limits.MaxSampleSeconds, MaxLookbackDays),
                        ["description"] = $"process.sample: seconds {MinSampleSeconds}-{grant.Limits.MaxSampleSeconds}; events.recent: days 1-{MaxLookbackDays}.",
                    },
                    ["target"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(AllTargets), ["description"] = "events.recent: which log (required). network.listeners: exposed (default) or all." },
                },
                ["required"] = new JsonArray("diagnosticId"),
                ["additionalProperties"] = false,
            }),
            new(GetOperation, "Returns the status and result of a long-running diagnostic (operationId) or of a requested change (planId). Send exactly one.", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operationId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^op-" },
                    ["planId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^plan-" },
                },
                ["additionalProperties"] = false,
            }),
            .. grant.CanRequestChanges ? new[] { RequestActionSpec(actionIds ?? []) } : [],
            .. ContextToolSpecs(),
        ];
    }

    private static JsonObject Empty() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };

    private static JsonObject IdList(string pattern) => new()
    {
        ["type"] = "array", ["maxItems"] = MaxCardRefs, ["items"] = new JsonObject { ["type"] = "string", ["pattern"] = pattern },
    };

    /// <summary>The §5 tools (m5-chat): the deck, PC specs and report history, plus notes and AI cards on the deck.</summary>
    public static IReadOnlyList<ToolSpec> ContextToolSpecs() =>
    [
        new(GetCards, "Returns the suggestion cards the user sees, in order, with their status and any AI note.", Empty()),
        new(GetPcSpecs, "Returns this PC's specs from the onboarding inventory (CPU, GPU, memory, storage, Windows, displays).", Empty()),
        new(GetReportHistory, "Returns recent reports with their counts, and what changed between the current report and the previous one.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxHistory, ["default"] = DefaultHistory } },
            ["additionalProperties"] = false,
        }),
        new(NoteCard, "Sets a short plain-text note on one suggestion card, shown to the user under the card. Replaces your earlier note on it.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["cardId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^card-" },
                ["text"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxNoteChars },
            },
            ["required"] = new JsonArray("cardId", "text"),
            ["additionalProperties"] = false,
        }),
        new(AddCard, "Adds one suggestion card to the user's deck (at most 5 per task). It cannot change anything; fixes still go through request_action.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxCardTitle },
                ["why"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxCardWhy },
                ["category"] = new JsonObject { ["type"] = "string", ["enum"] = Json.Array(Categories) },
                ["findingIds"] = IdList("^fnd-"),
                ["evidenceIds"] = IdList("^ev-"),
            },
            ["required"] = new JsonArray("title", "why", "category"),
            ["additionalProperties"] = false,
        }),
    ];

    /// <summary>request_action (M4): asks the user to approve one catalog change. It never approves anything itself.</summary>
    private static ToolSpec RequestActionSpec(IReadOnlyList<string> actionIds) => new(RequestAction,
        "Asks the user to approve one change from the app's catalog. The app shows the exact change; only the user can approve it. " +
        "Poll get_operation with the returned planId.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["actionId"] = new JsonObject { ["type"] = "string", ["enum"] = Json.Array(actionIds) },
                ["targetRef"] = new JsonObject { ["type"] = "object" },
                ["parameters"] = new JsonObject { ["type"] = "object" },
                ["findingRef"] = new JsonObject { ["type"] = "string", ["pattern"] = "^fnd-" },
            },
            ["required"] = new JsonArray("actionId"),
            ["additionalProperties"] = false,
        });

    /// <summary>Validated arguments, or an error message. Unknown properties, wrong types and out-of-range values are rejected.</summary>
    public static (JsonObject? Args, string? Error) Validate(string tool, JsonObject? raw, Grant grant)
    {
        raw ??= [];
        string[] allowed = tool switch
        {
            ListCapabilities => [],
            GetFindings => ["category", "severity", "findingIds", "cursor"],
            RunDiagnostic => ["diagnosticId", "window", "target"],
            GetOperation => ["operationId", "planId"],
            RequestAction => ["actionId", "targetRef", "parameters", "findingRef"],
            GetReportHistory => ["limit"],
            NoteCard => ["cardId", "text"],
            AddCard => ["title", "why", "category", "findingIds", "evidenceIds"],
            _ => [],
        };
        if (!ToolNames.Contains(tool)) return (null, $"Unknown tool {tool}.");
        var extra = raw.Select(p => p.Key).FirstOrDefault(k => !allowed.Contains(k));
        if (extra is not null) return (null, $"Unknown argument {extra}.");

        var args = new JsonObject();
        string? Str(string key, int max, string[]? values = null)
        {
            if (raw[key] is null) return null;
            if (raw[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > max) throw new ArgumentException($"{key} must be a short string.");
            if (values is not null && !values.Contains(s)) throw new ArgumentException($"{key} must be one of: {string.Join(", ", values)}.");
            return s;
        }
        // Free text from the model (note_card, add_card): plain, control/format characters stripped, non-empty, capped.
        string? Text(string key, int max)
        {
            if (raw[key] is null) return null;
            if (raw[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > max) throw new ArgumentException($"{key} must be text of at most {max} characters.");
            var clean = new string(s.Where(ch => char.GetUnicodeCategory(ch) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format) || ch == '\n').ToArray()).Trim();
            if (clean.Length == 0) throw new ArgumentException($"{key} must not be empty.");
            return clean;
        }
        int? Int(string key)
        {
            if (raw[key] is null) return null;
            if (raw[key] is JsonValue v && Json.Number(v) is double d && d == Math.Floor(d) && Math.Abs(d) < 1e6) return (int)d;
            throw new ArgumentException($"{key} must be an integer.");
        }

        try
        {
            switch (tool)
            {
                case GetFindings:
                    if (Str("category", 32, Categories) is { } c) args["category"] = c;
                    if (Str("severity", 16, Severities) is { } s) args["severity"] = s;
                    if (Str("cursor", 16) is { } cur)
                    {
                        if (!int.TryParse(cur, out var n) || n < 0) throw new ArgumentException("cursor is not valid.");
                        args["cursor"] = cur;
                    }
                    if (raw["findingIds"] is { } ids)
                    {
                        if (ids is not JsonArray a || a.Count > 50 || a.Any(x => x is not JsonValue xv || !xv.TryGetValue<string>(out var xs) || xs.Length > 64))
                            throw new ArgumentException("findingIds must be up to 50 IDs.");
                        args["findingIds"] = a.DeepClone();
                    }
                    break;
                case RunDiagnostic:
                    var id = Str("diagnosticId", 64) ?? throw new ArgumentException("diagnosticId is required.");
                    var def = Find(id) ?? throw new ArgumentException($"Unknown diagnostic {id}.");
                    args["diagnosticId"] = id;
                    var window = Int("window");
                    var target = Str("target", 16, AllTargets);
                    if (!def.TakesWindow && window is not null) throw new ArgumentException($"{id} takes no window.");
                    if (!def.TakesTarget && def.Id != Listeners && target is not null) throw new ArgumentException($"{id} takes no target.");
                    if (def.Id == Listeners)
                    {
                        if (target is not null && !ListenerTargets.Contains(target)) throw new ArgumentException("target must be exposed or all.");
                        args["target"] = target ?? "exposed";
                    }
                    if (def.TakesTarget && target is not null && !EventTargets.Contains(target)) throw new ArgumentException($"target must be one of: {string.Join(", ", EventTargets)}.");
                    if (def.Id == "process.sample")
                    {
                        var w = window ?? DefaultSampleSeconds;
                        if (w < MinSampleSeconds || w > grant.Limits.MaxSampleSeconds)
                            throw new ArgumentException($"window must be {MinSampleSeconds}-{grant.Limits.MaxSampleSeconds} seconds.");
                        args["window"] = w;
                    }
                    if (def.TakesTarget)
                    {
                        args["target"] = target ?? throw new ArgumentException("target is required for events.recent.");
                        var days = window ?? DefaultLookbackDays;
                        if (days is < 1 or > MaxLookbackDays) throw new ArgumentException($"window must be 1-{MaxLookbackDays} days.");
                        args["window"] = days;
                    }
                    break;
                case GetOperation:
                    var op = Str("operationId", 64);
                    var plan = Str("planId", 64);
                    if ((op is null) == (plan is null)) throw new ArgumentException("Send exactly one of operationId or planId.");
                    if (op is not null) args["operationId"] = op; else args["planId"] = plan;
                    break;
                case RequestAction:
                    args["actionId"] = Str("actionId", 64) ?? throw new ArgumentException("actionId is required.");
                    foreach (var key in new[] { "targetRef", "parameters" })
                        if (raw[key] is not null)
                        {
                            if (raw[key] is not JsonObject o || Json.Write(o).Length > 2048) throw new ArgumentException($"{key} must be a small object.");
                            args[key] = o.DeepClone();
                        }
                    if (Str("findingRef", 64) is { } fnd)
                    {
                        if (!fnd.StartsWith("fnd-", StringComparison.Ordinal)) throw new ArgumentException("findingRef must be a finding ID.");
                        args["findingRef"] = fnd;
                    }
                    break;
                case GetReportHistory:
                    var limit = Int("limit") ?? DefaultHistory;
                    if (limit is < 1 or > MaxHistory) throw new ArgumentException($"limit must be 1-{MaxHistory}.");
                    args["limit"] = limit;
                    break;
                case NoteCard:
                    var cardId = Str("cardId", 64) ?? throw new ArgumentException("cardId is required.");
                    if (!cardId.StartsWith("card-", StringComparison.Ordinal)) throw new ArgumentException("cardId must be a card ID.");
                    args["cardId"] = cardId;
                    args["text"] = Text("text", MaxNoteChars) ?? throw new ArgumentException("text is required.");
                    break;
                case AddCard:
                    args["title"] = Text("title", MaxCardTitle) ?? throw new ArgumentException("title is required.");
                    args["why"] = Text("why", MaxCardWhy) ?? throw new ArgumentException("why is required.");
                    args["category"] = Str("category", 32, Categories) ?? throw new ArgumentException("category is required.");
                    foreach (var (key, prefix) in new[] { ("findingIds", "fnd-"), ("evidenceIds", "ev-") })
                    {
                        if (raw[key] is not { } list) continue;
                        if (list is not JsonArray a || a.Count > MaxCardRefs
                            || a.Any(x => x is not JsonValue xv || !xv.TryGetValue<string>(out var xs) || xs.Length > 64 || !xs.StartsWith(prefix, StringComparison.Ordinal)))
                            throw new ArgumentException($"{key} must be up to {MaxCardRefs} IDs starting with {prefix}.");
                        args[key] = Json.Array(Json.Strings(a).Distinct());
                    }
                    break;
            }
        }
        catch (ArgumentException ex) { return (null, ex.Message); }
        return (args, null);
    }
}

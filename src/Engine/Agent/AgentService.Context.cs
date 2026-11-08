using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M5 (contracts/m5-chat.md §4, §5): persisted in-app transcripts and the context tools — the deck (get_cards,
// note_card, add_card), the onboarding specs (get_pc_specs) and the report history (get_report_history). Everything
// leaves through the same Release path as the m3 tools (resultBytes/totalBytes, untrusted marker, names rule).
public sealed partial class AgentService
{
    public const int TranscriptMessages = 40, TranscriptToolResultBytes = 4096;

    // ---------------------------------------------------------------- transcripts (§4)

    /// <summary>The stored conversation of an in-app task, or null when it has none yet (the next ask is its first).</summary>
    internal static List<ProviderMessage>? LoadTranscript(Db db, string taskId)
    {
        if (db.Scalar("SELECT body FROM agent_transcripts WHERE task_id = $t", ("$t", taskId)) is not string body) return null;
        var messages = new List<ProviderMessage>();
        try
        {
            foreach (var m in Json.Parse(body) as JsonArray ?? [])
            {
                switch (m.Text("type"))
                {
                    case "user":
                        messages.Add(new UserText(m.Text("text")));
                        break;
                    case "assistant":
                        var calls = (m!["calls"] as JsonArray ?? []).Select(c => new ToolCall(c.Text("callId"), c.Text("name"), c!["arguments"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : [])).ToList();
                        messages.Add(new AssistantTurn(m!["text"]?.GetValue<string>(), calls, m["state"]?.DeepClone()));
                        break;
                    case "tool_results":
                        messages.Add(new ToolResults((m!["results"] as JsonArray ?? []).Select(r => new ToolResult(r.Text("callId"), r!["content"]?.DeepClone() ?? new JsonObject(), r["isError"]?.GetValue<bool>() ?? false)).ToList()));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return []; }
        return Normalize(messages);
    }

    /// <summary>Keeps the last 40 messages from a user message on, with each tool result cut to 4 KB.</summary>
    internal static void SaveTranscript(Db db, string taskId, IReadOnlyList<ProviderMessage> transcript)
    {
        var start = Math.Max(0, transcript.Count - TranscriptMessages);
        while (start < transcript.Count && transcript[start] is not UserText) start++;
        var body = new JsonArray();
        foreach (var message in transcript.Skip(start))
        {
            body.Add(message switch
            {
                UserText u => new JsonObject { ["type"] = "user", ["text"] = u.Text },
                AssistantTurn a => new JsonObject
                {
                    ["type"] = "assistant", ["text"] = a.Text,
                    ["calls"] = new JsonArray(a.Calls.Select(c => (JsonNode?)new JsonObject { ["callId"] = c.CallId, ["name"] = c.Name, ["arguments"] = c.Arguments.DeepClone() }).ToArray()),
                    ["state"] = a.ProviderState?.DeepClone(),
                },
                ToolResults r => new JsonObject
                {
                    ["type"] = "tool_results",
                    ["results"] = new JsonArray(r.Results.Select(x => (JsonNode?)new JsonObject { ["callId"] = x.CallId, ["content"] = CutResult(x.Content), ["isError"] = x.IsError }).ToArray()),
                },
                _ => null,
            });
        }
        db.Exec("INSERT INTO agent_transcripts (task_id, body, updated_at) VALUES ($t, $b, $n) ON CONFLICT(task_id) DO UPDATE SET body = $b, updated_at = $n",
            ("$t", taskId), ("$b", Json.Write(body)), ("$n", db.Now()));
    }

    /// <summary>A tool result over 4 KB is replaced by its first part, marked truncated (the model can call the tool again).</summary>
    private static JsonNode CutResult(JsonNode content)
    {
        var text = Json.Write(content);
        if (Encoding.UTF8.GetByteCount(text) <= TranscriptToolResultBytes) return content.DeepClone();
        var keep = text[..Math.Min(text.Length, TranscriptToolResultBytes / 2)];
        return new JsonObject
        {
            ["untrusted"] = UntrustedNote, ["truncated"] = true,
            ["note"] = "Cut from the conversation history; call the tool again for the full result.", ["partial"] = keep,
        };
    }

    /// <summary>A stored history must replay: an unanswered tool call at the end is dropped, and consecutive user messages merge.</summary>
    private static List<ProviderMessage> Normalize(List<ProviderMessage> messages)
    {
        if (messages.Count > 0 && messages[^1] is AssistantTurn { Calls.Count: > 0 }) messages.RemoveAt(messages.Count - 1);
        var result = new List<ProviderMessage>();
        foreach (var m in messages)
        {
            if (m is UserText u && result.Count > 0 && result[^1] is UserText previous) result[^1] = new UserText(previous.Text + "\n\n" + u.Text);
            else result.Add(m);
        }
        return result;
    }

    // ---------------------------------------------------------------- context tools (§5)

    /// <summary>Phase 3 of a context tool: the result body and its main array, or an error result.</summary>
    private (JsonObject? Body, string ArrayKey, JsonObject? Error) ContextTool(Db db, JsonObject task, Grant grant, string tool, JsonObject args)
    {
        var reportId = task["scope"].Text("reportId");
        var taskId = task.Text("taskId");
        var label = task["recipient"].Text("label");
        switch (tool)
        {
            case Diagnostics.GetCards:
                var cards = Followups.AgentCards(db, reportId, grant.ShareNames, taskId);
                return cards is null
                    ? (null, "", ToolError("unavailable", "The report for this task has no suggestion cards."))
                    : (new JsonObject { ["reportId"] = reportId, ["cards"] = cards }, "cards", null);

            case Diagnostics.GetPcSpecs:
                return (new JsonObject { ["facts"] = PcSpecs(db, reportId) }, "facts", null);

            case Diagnostics.GetReportHistory:
                return ReportHistory(db, reportId, grant.ShareNames, args["limit"]!.GetValue<int>()) is { } history
                    ? (history, "reports", null)
                    : (null, "", ToolError("unavailable", "The report for this task is not readable."));

            case Diagnostics.NoteCard:
                return Followups.NoteCard(db, reportId, args.Text("cardId"), args.Text("text"), taskId, label)
                    ? (new JsonObject { ["cardId"] = args.Text("cardId"), ["status"] = "noted" }, "", null)
                    : (null, "", ToolError("invalid_arguments", "Unknown cardId for this report."));

            default: // add_card
                JsonObject report;
                try { report = ReportService.Load(db, reportId); }
                catch (RpcError) { return (null, "", ToolError("unavailable", "The report for this task is not readable.")); }
                var findingIds = report["findings"]!.AsArray().Select(f => f.Text("id")).ToHashSet();
                var evidenceIds = report["evidence"]!.AsArray().Select(e => e.Text("id"))
                    .Concat(db.Query("SELECT evidence_id FROM agent_released WHERE task_id = $t", r => r.GetString(0), ("$t", taskId))).ToHashSet();
                var wantFindings = Json.Strings(args["findingIds"]);
                var wantEvidence = Json.Strings(args["evidenceIds"]);
                var keptFindings = wantFindings.Where(findingIds.Contains).ToList();
                var keptEvidence = wantEvidence.Where(evidenceIds.Contains).ToList();
                var dropped = wantFindings.Count - keptFindings.Count + wantEvidence.Count - keptEvidence.Count;
                if (db.Scalar("SELECT 1 FROM followups WHERE report_id = $r", ("$r", reportId)) is null)
                    return (null, "", ToolError("unavailable", "The report for this task has no suggestion cards."));
                var cardId = Followups.AddAiCard(db, reportId, taskId, args.Text("title"), args.Text("why"), args.Text("category"), keptFindings, keptEvidence, label);
                if (cardId is null) return (null, "", ToolError("not_allowed", $"This task already added {Followups.MaxAiCards} cards."));
                var added = new JsonObject { ["cardId"] = cardId, ["status"] = "added" };
                if (dropped > 0) added["dropped"] = dropped;
                return (added, "", null);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex WindowsVersion = new(
        @"^windows\s+(?<v>10|11|server\s+(?:2016|2019|2022|2025))(?![0-9a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Edition words get_pc_specs may name, most specific first.</summary>
    private static readonly string[] WindowsEditions =
    [
        "Pro for Workstations", "Pro Education", "Home Single Language", "IoT Enterprise", "Enterprise LTSC", "Enterprise",
        "Education", "Pro", "Home", "SE", "Team", "Datacenter", "Standard", "Essentials",
    ];

    /// <summary>
    /// The registry's ProductName mapped to a fixed vocabulary: "Windows 10|11|Server 20xx" plus a known edition
    /// (Home, Pro, Enterprise, Education, …). Anything else in the string never leaves; null when it is not Windows.
    /// </summary>
    internal static string? WindowsEdition(string productName)
    {
        var folded = Agent.Sanitize.Fold(productName);
        if (!folded.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) return null;
        var m = WindowsVersion.Match(folded);
        if (!m.Success) return "Windows";
        var version = System.Text.RegularExpressions.Regex.Replace(m.Groups["v"].Value, @"\s+", " ");
        version = version.StartsWith("server", StringComparison.OrdinalIgnoreCase) ? "Server" + version[6..] : version;
        var rest = " " + folded[m.Length..].ToLowerInvariant() + " ";
        var edition = WindowsEditions.FirstOrDefault(e => rest.Contains(" " + e.ToLowerInvariant() + " ", StringComparison.Ordinal));
        return edition is null ? $"Windows {version}" : $"Windows {version} {edition}";
    }

    /// <summary>
    /// get_pc_specs: the latest onboarding inventory's typed facts (CPU, memory, Windows, battery, graphics, displays),
    /// re-rendered from allowlisted fields only, plus fixed-drive capacity from the task's report. Nothing names a
    /// device, user, path or serial.
    /// </summary>
    private static JsonArray PcSpecs(Db db, string reportId)
    {
        var facts = new JsonArray();
        void Add(string id, string label, string value) => facts.Add(new JsonObject { ["id"] = id, ["label"] = label, ["value"] = Clean(value) });

        var inventoryId = db.Scalar("SELECT inventory_id FROM inventories WHERE status IN ('complete','partial') ORDER BY started_at DESC, rowid DESC LIMIT 1") as string;
        if (inventoryId is not null)
        {
            var items = db.Query("SELECT subject, body FROM inventory_items WHERE inventory_id = $i ORDER BY ord", r => (Subject: r.GetString(0), Body: r.GetString(1)), ("$i", inventoryId));
            foreach (var (subject, body) in items)
            {
                if (Json.Parse(body)?["value"] is not JsonObject v) continue;
                long? N(string key) => v[key] is JsonValue x && Json.Number(x) is double d ? (long)d : null;
                bool? B(string key) => v[key] is JsonValue x && x.TryGetValue<bool>(out var b) ? b : null;
                switch (subject)
                {
                    case "inventory.specs.cpu" when N("physicalCores") is { } cores:
                        Add("cpu", "CPU", N("logicalCores") is { } threads ? $"{cores} cores, {threads} threads" : $"{cores} cores");
                        break;
                    case "inventory.specs.cpu" when N("logicalCores") is { } logical:
                        Add("cpu", "CPU", $"{logical} logical processors");
                        break;
                    case "inventory.specs.memory" when N("installedGb") is { } gb:
                        Add("memory", "Memory", $"{gb} GB");
                        break;
                    case "inventory.specs.os" when v["edition"] is JsonValue ev && ev.TryGetValue<string>(out var edition) && WindowsEdition(edition) is { } name:
                        Add("os", "Windows", N("build") is { } build ? $"{name} (build {build})" : name);
                        break;
                    case "inventory.specs.battery" when B("present") is { } battery:
                        Add("battery", "Battery", battery ? "Present" : "None");
                        break;
                    case "inventory.graphics.gpu" when N("count") is { } gpus:
                        Add("gpu", "Graphics adapters", gpus.ToString(CultureInfo.InvariantCulture));
                        break;
                    case "inventory.graphics.driver" when B("found") is { } driver:
                        Add("gpu_driver", "Display driver", driver ? "Installed" : "Not found");
                        break;
                    case "inventory.displays.count" when N("count") is { } displays:
                        Add("displays", "Displays", displays.ToString(CultureInfo.InvariantCulture));
                        break;
                    case "inventory.displays.refresh" when N("maxCurrentHz") is { } hz:
                        Add("refresh", "Highest refresh rate", $"{hz} Hz");
                        break;
                }
            }
        }

        try
        {
            var report = ReportService.Load(db, reportId);
            foreach (var e in report["evidence"]!.AsArray().Where(e => e.Text("collectorId") == "win.system.volumes"))
            {
                if (e!["value"] is not JsonObject v || v["letter"]?.GetValue<string>() is not { Length: 1 } letter || !char.IsAsciiLetter(letter[0])) continue;
                double? D(string key) => v[key] is JsonValue x ? Json.Number(x) : null;
                if (D("capacityBytes") is not { } capacity || D("freeBytes") is not { } free) continue;
                var media = v["mediaType"]?.GetValue<string>() is "ssd" or "hdd" ? " " + v.Text("mediaType").ToUpperInvariant() : "";
                var system = v["isSystem"] is JsonValue s && s.TryGetValue<bool>(out var isSystem) && isSystem ? ", system drive" : "";
                Add("storage." + letter.ToLowerInvariant(), $"Drive {letter.ToUpperInvariant()}:",
                    string.Create(CultureInfo.InvariantCulture, $"{free / (1L << 30):0.0} GiB free of {capacity / (1L << 30):0.0} GiB{media}{system}"));
            }
        }
        catch (RpcError) { }
        return facts;
    }

    /// <summary>get_report_history: recent reports (newest first) with counts, and new/resolved findings of the current report against the one before it.</summary>
    private static JsonObject? ReportHistory(Db db, string reportId, bool shareNames, int limit)
    {
        JsonObject current;
        try { current = ReportService.Load(db, reportId); }
        catch (RpcError) { return null; }
        var rows = db.Query("SELECT report_id, generated_at, body FROM reports ORDER BY rowid DESC LIMIT $l",
            r => (Id: r.GetString(0), At: r.GetString(1), Body: r.GetString(2)), ("$l", limit));
        var reports = new JsonArray();
        foreach (var (id, at, body) in rows)
        {
            var counts = (Json.Parse(body) as JsonObject)?["counts"] as JsonObject;
            reports.Add(new JsonObject
            {
                ["reportId"] = id, ["generatedAt"] = at,
                ["counts"] = new JsonObject { ["findings"] = counts?["findings"]?.DeepClone() ?? 0, ["gaps"] = counts?["gaps"]?.DeepClone() ?? 0 },
            });
        }

        var changes = new JsonObject { ["new"] = new JsonArray(), ["resolved"] = new JsonArray() };
        var previousId = db.Scalar("SELECT report_id FROM reports WHERE rowid < (SELECT rowid FROM reports WHERE report_id = $r) ORDER BY rowid DESC LIMIT 1", ("$r", reportId)) as string;
        if (previousId is not null)
        {
            try
            {
                var (added, resolved, _) = ReportBuilder.Compare(current, ReportService.Load(db, previousId));
                JsonNode? Item(JsonObject f) => new JsonObject
                {
                    ["findingId"] = f.Text("id"), ["title"] = FindingTitles.For(f.Text("ruleId"), f.Text("category"), f.Text("title"), shareNames),
                };
                changes["new"] = new JsonArray(added.Select(Item).ToArray());
                changes["resolved"] = new JsonArray(resolved.Select(Item).ToArray());
            }
            catch (RpcError) { }
        }
        changes["previousReportId"] = previousId;
        return new JsonObject { ["reportId"] = reportId, ["reports"] = reports, ["changes"] = changes };
    }
}

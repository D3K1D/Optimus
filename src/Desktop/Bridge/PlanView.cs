using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// What the native dialog shows of an approved script (m9-ai-powers.md §5.2): every line as approved (tabs as four spaces),
/// the AI's explanation of each part, the guard's warnings as "Line n: …", the AI's name for the fixed labels, whether a
/// restore point can be made (and why not), the policy line and whether it runs as administrator.
/// </summary>
public sealed record ScriptView(IReadOnlyList<string> Lines, IReadOnlyList<(int From, int To, string Explains)> Parts, IReadOnlyList<string> Warnings,
    string AiName, bool RestorePointAvailable, string? RestorePointReason, bool BypassesPolicy, bool Elevated)
{
    /// <summary>"Lines 4–6" or "Line 4".</summary>
    public static string Range(int from, int to) => from == to ? $"Line {from}" : $"Lines {from}–{to}";

    /// <summary>The header over a part's explanation (design v57): "Lines 4–6 · Claude’s explanation, not checked by Optimus".</summary>
    public string PartHeader(int from, int to) => Range(from, to) + " · " + PlanView.ExplanationLabel(AiName);

    /// <summary>
    /// The script box's text, line-numbered, each part's explanation under its last line with the fixed label. Plain text only
    /// (the box renders no markup); nothing folded or cut.
    /// </summary>
    public string BoxText()
    {
        var width = Lines.Count.ToString(CultureInfo.InvariantCulture).Length;
        var sb = new StringBuilder();
        for (var i = 0; i < Lines.Count; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append("  ").Append(Lines[i]).Append("\r\n");
            foreach (var (from, to, explains) in Parts.Where(p => p.To == i + 1))
            {
                sb.Append(new string(' ', width + 2)).Append("    ").Append(PartHeader(from, to)).Append("\r\n");
                sb.Append(new string(' ', width + 2)).Append("    ").Append(explains).Append("\r\n");
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// One step as the dialog shows it (m9-ai-powers.md §4; a single change is one step, design v53/v57/v62). <see cref="Undo"/> is
/// the step's undo sentence ("You can undo this afterwards.", the host's full condition sentence, or "This can’t be undone."
/// with <see cref="UndoRed"/>; empty for a step with nothing to undo); <see cref="Irreversible"/> / <see cref="DataLoss"/>
/// feed the red block; <see cref="RestorePoint"/> marks a step that makes a restore point; <see cref="Tradeoffs"/> are the
/// step's cleaned trade-offs, each its own entry (shown after the rows on every step that has them, design v66), or null.
/// </summary>
public sealed record PlanStepView(int N, string Label, string What, IReadOnlyList<(string Label, string Before, string After)> Rows,
    IReadOnlyList<string> Badges, string? Why, string Undo = "", bool UndoRed = false, bool Irreversible = false, bool DataLoss = false,
    bool RestorePoint = false, IReadOnlyList<string>? Tradeoffs = null);

/// <summary>
/// What the approval dialog shows, built only from host-provided plan fields (m4-actions.md §3, §4; m9-ai-powers.md §4).
/// Every string is untrusted: control, format (bidi, zero-width) and line-break characters are removed, whitespace is
/// collapsed and lengths are capped. The dialog renders these as plain labels (no mnemonics, no markup). A fix plan whose
/// content would not fit the dialog's caps (6 steps, 4 rows per step, 400 characters per AI text) is refused (null):
/// the host already makes such a plan too_large, so nothing is ever cut silently.
/// </summary>
public sealed record PlanView
{
    public const int MaxSteps = 6;
    public const int MaxStepRows = 4;
    public const int MaxAiText = 400;
    public const int MaxAiName = 40;
    public static readonly TimeSpan ApproveDelay = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan FlaggedApproveDelay = TimeSpan.FromSeconds(3);

    /// <summary>Badge text per flag, in display order (m9 §3.1 closed set; the cards' words); privilege admin adds "Admin" first.</summary>
    public static readonly IReadOnlyList<(string Flag, string Text)> FlagBadges =
    [
        ("irreversible", "Can’t be undone"), ("data_loss", "May lose unsaved work"), ("runs_code", "Runs a program"),
        ("restart", "Restart needed"), ("sign_out", "Sign-out needed"), ("network", "Changes network"), ("long", "Takes a while"),
    ];
    public const string AdminBadge = "Admin";
    public const string TickText = "I understand this can’t be undone";
    /// <summary>The step undo sentences (design v57 "Windows dialogs"); a condition sentence comes from the host as it is.</summary>
    public const string UndoAfterwards = "You can undo this afterwards.";
    public const string CantUndo = "This can’t be undone.";
    /// <summary>The action that makes a restore point: its step brings "A restore point is made first."</summary>
    public const string RestorePointActionId = "restore_point.create";

    public const int MaxTitle = 80;
    public const int MaxLine = 160;
    public const int MaxCell = 60;
    public const int MaxRows = 8;
    public const int MaxContext = 6;
    public const int MaxTradeoffs = 6;

    public required string Title { get; init; }
    public required IReadOnlyList<(string Label, string Before, string After)> Rows { get; init; }
    public required IReadOnlyList<string> Context { get; init; }
    public required IReadOnlyList<string> Tradeoffs { get; init; }
    public required bool NeedsAdmin { get; init; }
    /// <summary>
    /// Who asked, as the cards name it ("Asked by Claude", "Asked by the assistant"): never free text (see <see cref="AskerName"/>).
    /// Null when the user started the change from a card: the line is left out.
    /// </summary>
    public required string? RequestedBy { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>The handler's one plain sentence (single plans); empty when the plan has none.</summary>
    public string What { get; init; } = "";
    /// <summary>Badges from the plan's flags and privilege, in display order.</summary>
    public IReadOnlyList<string> Badges { get; init; } = [];
    /// <summary>The AI's reason as a quote, "From &lt;AI&gt;: …", or null.</summary>
    public string? Why { get; init; }
    /// <summary>A fix plan: several numbered steps approved once.</summary>
    public bool IsFix { get; init; }
    /// <summary>The steps the dialog shows: a fix's steps, or a single change as its one step (as the card does).</summary>
    public IReadOnlyList<PlanStepView> Steps { get; init; } = [];
    /// <summary>
    /// m9 §17.3: the steps of a fix that do not run, each as its whole dialog line: the ones the user switched off (removed), then
    /// the ones left out for administrator rights (leftOut), in the host's order. Empty when nothing was left out.
    /// </summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];
    /// <summary>Any irreversible or data_loss flag (plan or step): red banner, required tick, longer Approve delay.</summary>
    public bool NeedsTick { get; init; }
    public TimeSpan Delay => NeedsTick ? FlaggedApproveDelay : ApproveDelay;

    /// <summary>An approved-script plan (m9 §5.2): the exact text and what the dialog must show around it; null for other plans.</summary>
    public ScriptView? Script { get; init; }
    public bool IsScript => Script is not null;

    /// <summary>Builds the view, or returns null when a field the dialog depends on is missing or malformed.</summary>
    public static PlanView? From(JsonObject plan)
    {
        if (!TryTime(plan["expiresAt"], out var expiresAt)) return null;

        // Title = the plan's action label; without one, the action id plus the target label.
        var label = Str(plan["label"]);
        if (label is null)
        {
            var targetLabel = Str(plan["target"]?["label"]);
            label = Str(plan["actionId"]) is { } actionId
                ? (targetLabel is null ? actionId : $"{actionId} · {targetLabel}")
                : null;
        }
        var fix = Str(plan["kind"]) == "fix";
        if (Str(plan["kind"]) == "script") return FromScript(plan, expiresAt);
        if (fix)
        {
            // §4.1: a fix has no handler label; its title is the AI's (or, for an undo, a fixed one).
            var fixTitle = Str(plan["title"]);
            if (fixTitle is not null && Clean(fixTitle, int.MaxValue).Length > MaxAiText) return null;
            label = fixTitle ?? (Str(plan["undoOf"]) is not null ? "Undo changes" : "Fix");
        }
        var title = Clean(label, MaxTitle);
        if (title.Length == 0) return null;
        if (!TryWhy(plan["why"], out var why)) return null;

        var steps = new List<PlanStepView>();
        var needsTick = Ticks(plan["flags"]);
        if (fix)
        {
            if (plan["steps"] is not JsonArray stepArray || stepArray.Count is 0 or > MaxSteps) return null;
            foreach (var node in stepArray)
            {
                if (node is not JsonObject step || step["n"] is not JsonValue nv || !nv.TryGetValue<int>(out var n)) return null;
                var stepRows = new List<(string, string, string)>();
                if (step["rows"] is JsonArray sr)
                {
                    if (sr.Count > MaxStepRows) return null;
                    foreach (var r in sr)
                        if (r is JsonObject row)
                            stepRows.Add((Clean(Str(row["label"]), MaxCell), Clean(Str(row["before"]), MaxCell), Clean(Str(row["after"]), MaxCell)));
                }
                var stepWhy = Str(step["why"]);
                if (stepWhy is not null && Clean(stepWhy, int.MaxValue).Length > MaxAiText) return null;
                needsTick |= Ticks(step["flags"]);
                var (undo, red) = UndoSentence(step["undo"], step["flags"]);
                steps.Add(new PlanStepView(n, Clean(Str(step["label"]) ?? Str(step["actionId"]), MaxTitle), Clean(Str(step["what"]), MaxLine), stepRows,
                    BadgesOf(step["flags"], Str(step["privilege"]) == "admin"), stepWhy is null ? null : Clean(stepWhy, MaxAiText),
                    undo, red, Has(step["flags"], "irreversible"), Has(step["flags"], "data_loss"), Str(step["actionId"]) == RestorePointActionId,
                    TradeoffsOf(step["tradeoffs"])));
            }
        }

        var rows = new List<(string, string, string)>();
        if (plan["rows"] is JsonArray rowArray)
        {
            foreach (var r in rowArray.Take(MaxRows))
                if (r is JsonObject row)
                    rows.Add((Clean(Str(row["label"]), MaxCell), Clean(Str(row["before"]), MaxCell), Clean(Str(row["after"]), MaxCell)));
        }

        var context = new List<string>();
        if (plan["context"] is JsonArray contextArray)
        {
            foreach (var c in contextArray.Take(MaxContext))
            {
                var line = c switch
                {
                    JsonArray pair => string.Join(": ", pair.Take(2).Select(p => Clean(Str(p), MaxCell)).Where(s => s.Length > 0)),
                    _ => Clean(Str(c), MaxLine),
                };
                if (line.Length > 0) context.Add(Cap(line, MaxLine));
            }
        }

        var tradeoffs = TradeoffsOf(plan["tradeoffs"]);

        var what = Clean(Str(plan["what"]), MaxLine);
        var badges = BadgesOf(plan["flags"], Str(plan["privilege"]) == "admin");
        if (!fix)
        {
            // A single change is one step, as the card shows it (design v53, v62): the change rows, then each context pair as
            // a name row ("<label>: <value>"), "Trade-off: <text>", the plan's flags as badges and its undo sentence.
            var stepRows = new List<(string, string, string)>(rows);
            if (plan["context"] is JsonArray ctx)
                foreach (var c in ctx.Take(MaxContext))
                    if (c is JsonArray pair && Clean(Str(pair.Count > 0 ? pair[0] : null), MaxCell) is { Length: > 0 } k)
                    {
                        var v = Clean(Str(pair.Count > 1 ? pair[1] : null), MaxCell);
                        stepRows.Add((k, v, v));
                    }

            var (undo, red) = UndoSentence(plan["undo"], plan["flags"]);
            steps.Add(new PlanStepView(1, title, what, stepRows, badges, null, undo, red, Has(plan["flags"], "irreversible"), Has(plan["flags"], "data_loss"),
                Str(plan["actionId"]) == RestorePointActionId, tradeoffs));
        }

        return new PlanView
        {
            Title = title,
            Rows = rows,
            Context = context,
            Tradeoffs = tradeoffs,
            NeedsAdmin = Str(plan["privilege"]) == "admin",
            RequestedBy = AskerName(plan),
            ExpiresAt = expiresAt,
            What = what,
            Badges = badges,
            Why = why,
            IsFix = fix,
            Steps = steps,
            NeedsTick = needsTick,
            LeftOut = fix ? LeftOutLines(plan) : [],
        };
    }

    /// <summary>The dialog lines of a fix's steps that do not run (m9 §17.3, Design System v80): removed, then leftOut.</summary>
    private static List<string> LeftOutLines(JsonObject plan)
    {
        var lines = new List<string>();
        foreach (var r in (plan["removed"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxSteps))
            if (Clean(Str(r["label"]), MaxTitle) is { Length: > 0 } label) lines.Add(ApprovalDialogText.LeftOutByYou(label));
        foreach (var l in (plan["leftOut"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxSteps))
            if (Clean(Str(l["label"]) ?? Str(l["actionId"]), MaxTitle) is { Length: > 0 } label)
                lines.Add(Str(l["code"]) == "admin_not_allowed" ? ApprovalDialogText.LeftOutAdminOff(label) : ApprovalDialogText.LeftOutAdminUnavailable(label));
        return lines;
    }

    // ---------------------------------------------------------------- approved scripts (m9 §5.2)

    public const string ScriptNotice = "Optimus can’t check what a script does and can’t undo it. No warning here doesn’t mean it is safe.";
    public const string ScriptPolicyLine = "Runs even though scripts are normally off on this PC.";
    public const string ScriptAdminLine = "Runs as administrator.";
    public const string RestorePointText = "Make a restore point first";
    public const string RestorePointNote = "Covers Windows files and settings, not your files.";
    public const string RestorePointOff = "Restore points are off on this PC";
    public const string RestorePointUnsupported = "Not available in this version";
    public const string ShareNote = "Paths and names are removed where recognised.";
    /// <summary>The label of a step's one trade-off ("Trade-off: …", design v66).</summary>
    public const string TradeoffLabel = "Trade-off";
    public const int MaxScriptLines = 200, MaxScriptLine = 200, MaxScriptParts = 12, MaxExplains = 200;

    /// <summary>"Let &lt;AI&gt; see the last lines of output" (pre-ticked in the dialog).</summary>
    public static string ShareText(string ai) => $"Let {ai} see the last lines of output";

    /// <summary>The fixed label over each part's explanation: the AI's words, not Optimus's.</summary>
    public static string ExplanationLabel(string ai) => $"{ai}’s explanation, not checked by Optimus";

    /// <summary>
    /// A script plan's view (m9 §5.2, §5.7). The text must be exactly what the host serialised (printable ASCII, tab, CRLF;
    /// at most 200 lines of 200 characters); anything else refuses the view (null), so nothing on screen can differ from
    /// what runs. Tabs are shown as four spaces, nothing is cut. Always flagged: the banner, the tick and the 3 s delay.
    /// </summary>
    private static PlanView? FromScript(JsonObject plan, DateTimeOffset expiresAt)
    {
        if (plan["script"] is not JsonObject s || Str(s["text"]) is not { } text) return null;
        foreach (var c in text)
            if (!(c is '\t' or '\r' or '\n' || (c >= 32 && c <= 126))) return null;
        var raw = text.Split("\r\n").ToList();
        if (raw.Count > 1 && raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);
        if (raw.Count is 0 or > MaxScriptLines || raw.Any(l => l.Length > MaxScriptLine || l.Contains('\r') || l.Contains('\n'))) return null;
        if (s["lines"] is JsonValue lv && lv.TryGetValue<int>(out var declared) && declared != raw.Count) return null;
        var lines = raw.Select(l => l.Replace("\t", "    ", StringComparison.Ordinal)).ToList();

        if (!TryWhy(plan["why"], out var why)) return null;
        var asker = AskerName(plan);
        var ai = asker ?? Unnamed; // the fixed labels always name someone ("the assistant’s explanation, not checked by Optimus")
        var parts = new List<(int From, int To, string Explains)>();
        if (s["parts"] is not JsonArray partArray || partArray.Count is 0 or > MaxScriptParts) return null;
        foreach (var node in partArray)
        {
            if (node is not JsonObject p || p["fromLine"] is not JsonValue fv || !fv.TryGetValue<int>(out var from)
                || p["toLine"] is not JsonValue tv || !tv.TryGetValue<int>(out var to) || from < 1 || to < from || to > lines.Count
                || (parts.Count > 0 && from <= parts[^1].To)) return null;
            var explains = Clean(Str(p["explains"]), int.MaxValue);
            if (explains.Length is 0 or > MaxExplains) return null;
            parts.Add((from, to, explains));
        }
        var warnings = new List<string>();
        if (s["guard"]?["warnings"] is JsonArray w)
            foreach (var node in w)
            {
                if (node?["line"] is not JsonValue wl || !wl.TryGetValue<int>(out var line)) return null;
                var said = Clean(Str(node["text"]), 120);
                if (said.Length == 0) return null;
                warnings.Add($"Line {line}: {char.ToLowerInvariant(said[0])}{said[1..]}");
            }
        var available = s["restorePoint"]?["available"] is JsonValue av && av.TryGetValue<bool>(out var a) && a;
        // As the card (fix-plan.js scriptView): flag_off is "not in this version"; any other reason means they are off.
        var reason = available ? null : Str(s["restorePoint"]?["reason"]) == "flag_off" ? RestorePointUnsupported : RestorePointOff;
        var elevated = s["elevated"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e;
        var title = Clean(Str(plan["title"]), MaxTitle);
        return new PlanView
        {
            Title = title.Length > 0 ? title : "Run a script",
            Rows = [], Context = [], Tradeoffs = [],
            NeedsAdmin = elevated || Str(plan["privilege"]) == "admin",
            RequestedBy = asker,
            ExpiresAt = expiresAt,
            Badges = BadgesOf(plan["flags"], Str(plan["privilege"]) == "admin"),
            Why = why,
            NeedsTick = true, // a script is always irreversible and runs code (m9 §5.1 rule 5), whatever the flags say
            Script = new ScriptView(lines, parts, warnings, ai, available, reason, s["bypassesPolicy"] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b, elevated),
        };
    }

    /// <summary>The badges for a flag list and privilege, in display order; unknown flags are ignored.</summary>
    public static IReadOnlyList<string> BadgesOf(JsonNode? flags, bool admin)
    {
        var set = (flags as JsonArray ?? []).Select(Str).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var badges = new List<string>();
        if (admin) badges.Add(AdminBadge);
        badges.AddRange(FlagBadges.Where(b => set.Contains(b.Flag)).Select(b => b.Text));
        return badges;
    }

    /// <summary>True when the flags hold irreversible or data_loss (the tick rule, m9 §0.3).</summary>
    public static bool Ticks(JsonNode? flags) =>
        (flags as JsonArray ?? []).Select(Str).Any(f => f is "irreversible" or "data_loss");

    /// <summary>
    /// The AI's reason {text, by} as "From &lt;by&gt;: &lt;text&gt;". Absent or null → no quote. Text over the cap, or a
    /// malformed value, refuses the view (false).
    /// </summary>
    private static bool TryWhy(JsonNode? node, out string? why)
    {
        why = null;
        if (node is null) return true;
        if (node is not JsonObject o || Str(o["text"]) is not { } text) return false;
        var clean = Clean(text, int.MaxValue);
        if (clean.Length == 0) return true;
        if (clean.Length > MaxAiText) return false;
        var by = Clean(Str(o["by"]), MaxAiName);
        why = $"From {(by.Length == 0 ? "the AI" : by)}: {clean}";
        return true;
    }

    /// <summary>A plan's or step's host <c>tradeoffs</c>, each cleaned, empty ones dropped (at most <see cref="MaxTradeoffs"/>).</summary>
    private static List<string> TradeoffsOf(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray array)
            foreach (var t in array.Take(MaxTradeoffs))
                if (Clean(Str(t), MaxLine) is { Length: > 0 } s) list.Add(s);
        return list;
    }

    /// <summary>True when the flag list holds <paramref name="flag"/>.</summary>
    private static bool Has(JsonNode? flags, string flag) => (flags as JsonArray ?? []).Select(Str).Contains(flag);

    /// <summary>
    /// A step's undo sentence (design v57, v62): supported → the host's full condition sentence, else "You can undo this
    /// afterwards."; nothing to undo (the host's <c>nothingToUndo: true</c> with supported false, on a step not flagged
    /// irreversible or data_loss) → no line; otherwise not supported → "This can’t be undone." (red). Never "Undo: yes ·".
    /// </summary>
    internal static (string Text, bool Red) UndoSentence(JsonNode? undo, JsonNode? flags)
    {
        var supported = undo?["supported"] is JsonValue u && u.TryGetValue<bool>(out var ub) && ub;
        if (!supported && NothingToUndo(undo, flags)) return ("", false);
        if (!supported) return (CantUndo, true);
        var conditions = Clean(Str(undo?["conditions"]), MaxLine);
        return (conditions.Length > 0 ? conditions : UndoAfterwards, false);
    }

    /// <summary>
    /// The host's "nothing to undo" (design v62, fix-plan.js nothingToUndo): <c>nothingToUndo: true</c> with supported false.
    /// A step flagged irreversible or data_loss changes something, so it never counts (the red line and the tick stay).
    /// </summary>
    public static bool NothingToUndo(JsonNode? undo, JsonNode? flags) =>
        undo?["nothingToUndo"] is JsonValue n && n.TryGetValue<bool>(out var nb) && nb
        && undo["supported"] is JsonValue s && s.TryGetValue<bool>(out var sb) && !sb
        && !Ticks(flags);

    /// <summary>The AI names the host gives a plan's why.by (AgentService.AiName). Anything else is never shown as the asker.</summary>
    private static readonly HashSet<string> KnownAiNames = new(StringComparer.Ordinal) { "Claude", "ChatGPT" };

    /// <summary>Who asked when no AI is named (design v62): "Asked by the assistant".</summary>
    public const string Unnamed = "the assistant";

    /// <summary>
    /// Who asked, as the cards name it (fix-plan.js requesterName, design v62): a change the user started from a card is null
    /// (the line is left out); else the host's AI name on the reason when it is one of the known names; else the requester
    /// kind's fixed name. Free text never becomes the asker.
    /// </summary>
    public static string? AskerName(JsonObject plan)
    {
        var kind = Str(plan["requestedBy"]?["kind"]);
        if (kind == "user") return null;
        if (Str(plan["why"]?["by"]) is { } by && KnownAiNames.Contains(by)) return by;
        return RequesterName(kind);
    }

    /// <summary>Fixed shell wording; the host only picks one of the known kinds (the cards' names). The user: null.</summary>
    public static string? RequesterName(string? kind) => kind switch
    {
        "user" => null,
        "claude_code" => "Claude",
        _ => Unnamed,
    };

    /// <summary>Removes control/format/separator characters (incl. bidi overrides, zero-width), collapses whitespace, caps length.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var limit = max >= int.MaxValue / 2 ? int.MaxValue : max * 2;
        var sb = new StringBuilder(Math.Min(s.Length, limit));
        var space = false;
        foreach (var rune in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
            {
                // A tab/newline separates words; everything else in these classes is dropped.
                if (rune.Value is '\t' or '\n' or '\r') space = sb.Length > 0;
                continue;
            }
            if (Rune.IsWhiteSpace(rune)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(rune.ToString());
            if (sb.Length > limit) break;
        }
        return Cap(sb.ToString(), max);
    }

    private static string Cap(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut].TrimEnd() + "…";
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool TryTime(JsonNode? n, out DateTimeOffset t)
    {
        t = default;
        return Str(n) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t);
    }
}

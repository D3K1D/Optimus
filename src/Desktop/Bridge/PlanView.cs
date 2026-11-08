using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// What the approval dialog shows, built only from host-provided plan fields (m4-actions.md §3, §4).
/// Every string is untrusted: control, format (bidi, zero-width) and line-break characters are removed, whitespace is
/// collapsed and lengths are capped. The dialog renders these as plain labels (no mnemonics, no markup).
/// </summary>
public sealed record PlanView
{
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
    public required string UndoLine { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

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
        var title = Clean(label, MaxTitle);
        if (title.Length == 0) return null;

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

        var tradeoffs = new List<string>();
        if (plan["tradeoffs"] is JsonArray tArray)
            foreach (var t in tArray.Take(MaxTradeoffs))
                if (Clean(Str(t), MaxLine) is { Length: > 0 } s) tradeoffs.Add(s);

        var undoSupported = plan["undo"]?["supported"] is JsonValue u && u.TryGetValue<bool>(out var ub) && ub;
        var undoConditions = Clean(Str(plan["undo"]?["conditions"]), MaxLine);
        var undoLine = undoSupported
            ? (undoConditions.Length > 0 ? $"Undo: yes · {undoConditions}" : "Undo: yes")
            : "Undo: no";

        return new PlanView
        {
            Title = title,
            Rows = rows,
            Context = context,
            Tradeoffs = tradeoffs,
            NeedsAdmin = Str(plan["privilege"]) == "admin",
            UndoLine = Cap(undoLine, MaxLine),
            RequestedBy = RequesterName(Str(plan["requestedBy"]?["kind"])),
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Fixed shell wording; the host only picks one of the known kinds.</summary>
    public static string RequesterName(string? kind) => kind switch
    {
        "user" => "You",
        "claude_code" => "Claude Code",
        "codex" => "Codex",
        "in_app" => "the in-app assistant",
        _ => "Unknown",
    };

    /// <summary>Removes control/format/separator characters (incl. bidi overrides, zero-width), collapses whitespace, caps length.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max * 2));
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
            if (sb.Length > max * 2) break;
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

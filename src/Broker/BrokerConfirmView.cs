using System.Globalization;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;

namespace OptimizationTool.Broker;

/// <summary>
/// One step on the broker's window: all of it from the broker's own handler and resolution, never host text.
/// <see cref="Irreversible"/> / <see cref="DataLoss"/> feed the red line; <see cref="Repair"/> marks a long repair tool.
/// </summary>
public sealed record BrokerConfirmStep(int N, string Label, string Target, IReadOnlyList<PlanRow> Rows, IReadOnlyList<string> Badges,
    bool Irreversible = false, bool DataLoss = false, bool Repair = false);

/// <summary>How a line on the broker's windows is drawn: the words come from the design; only the look varies.</summary>
public enum BrokerLineKind { Heading, Explanation, StepName, Target, Row, Badges, Note, Notice, FoundBy, Warning, Red }

/// <summary>One line of a broker window, in the order it is shown.</summary>
public sealed record BrokerLine(BrokerLineKind Kind, string Text);

/// <summary>
/// What the broker's own confirmation window shows (m9 §3.5; design README "Windows dialogs", "Administrator changes", v57):
/// the heading and its explanation, per step the handler's label, "Changes: &lt;target&gt;", the before → after rows, the badge
/// words and, for a repair, the long-time line; the red line and the never-pre-ticked tick when any step is irreversible or
/// data_loss. Content over a cap is not cut: <see cref="Fits"/> is false and the broker refuses the request (too_large).
/// </summary>
public sealed record BrokerConfirmView(IReadOnlyList<BrokerConfirmStep> Steps, bool NeedsTick)
{
    public const string TitleBar = "Optimus · Administrator changes";
    public const string Heading = "Make these changes as administrator?";
    /// <summary>The heading with one change (design v65); the step's name follows it without a number.</summary>
    public const string HeadingOne = "Make this change as administrator?";
    public const string Explanation = "You approved these in Optimus. Windows asked for permission because they need administrator rights.";
    public const string RepairLine = "This can take a long time. You can keep using your PC, and stop it from Optimus.";
    public const string TickText = "I understand this can’t be undone";
    public const string CancelText = "Cancel";
    public const string ApplyText = "Apply";
    public const string AdminBadge = "Admin";
    public const int MaxSteps = 6, MaxRows = 4, MaxText = 200;
    public static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(800), FlaggedApplyDelay = TimeSpan.FromSeconds(3);

    /// <summary>The cards' badge words (the shell dialog's PlanView.FlagBadges), in the same order.</summary>
    private static readonly (string Flag, string Text)[] BadgeText =
    [
        (ActionFlags.Irreversible, "Can’t be undone"), (ActionFlags.DataLoss, "May lose unsaved work"), (ActionFlags.RunsCode, "Runs a program"),
        (ActionFlags.Restart, "Restart needed"), (ActionFlags.SignOut, "Sign-out needed"), (ActionFlags.Network, "Changes network"), (ActionFlags.Long, "Takes a while"),
    ];

    public static BrokerConfirmView Build(IReadOnlyList<(int N, IBrokerHandler Handler, ActionResolution Resolution)> steps)
    {
        var list = new List<BrokerConfirmStep>();
        var tick = false;
        foreach (var (n, h, r) in steps)
        {
            var flags = ActionFlags.Union(h.Flags, r.Flags) ?? [];
            var irreversible = flags.Contains(ActionFlags.Irreversible);
            var dataLoss = flags.Contains(ActionFlags.DataLoss);
            tick |= irreversible || dataLoss;
            var target = r.Target["label"]?.GetValue<string>() ?? r.Target["id"]?.ToString() ?? "";
            list.Add(new BrokerConfirmStep(n, h.Label, target, r.Rows, [.. BadgeText.Where(b => flags.Contains(b.Flag)).Select(b => b.Text)],
                irreversible, dataLoss, Repair: h.Deadline is not null));
        }
        return new BrokerConfirmView(list, tick);
    }

    public bool Fits =>
        Steps.Count is > 0 and <= MaxSteps
        && Steps.All(s => s.Rows.Count <= MaxRows && Ok(s.Label) && Ok(s.Target) && s.Rows.All(r => Ok(r.Label) && Ok(r.Before) && Ok(r.After)));

    public TimeSpan Delay => NeedsTick ? FlaggedApplyDelay : ApplyDelay;

    /// <summary>
    /// The window's lines in order (design items 1–3, v65): the heading ("Make this change as administrator?" with one step,
    /// "Make these changes …" with several), the explanation, then per step its name (numbered unless it is the only one),
    /// "Changes: &lt;target&gt;", the rows, the badge words ("Admin" first) and the repair line; then the red line.
    /// </summary>
    public IReadOnlyList<BrokerLine> Lines()
    {
        var single = Steps.Count == 1;
        var lines = new List<BrokerLine> { new(BrokerLineKind.Heading, single ? HeadingOne : Heading), new(BrokerLineKind.Explanation, Explanation) };
        foreach (var s in Steps)
        {
            lines.Add(new(BrokerLineKind.StepName, single ? s.Label : s.N.ToString(CultureInfo.InvariantCulture) + ". " + s.Label));
            if (s.Target.Length > 0) lines.Add(new(BrokerLineKind.Target, "Changes: " + s.Target));
            foreach (var r in s.Rows) lines.Add(new(BrokerLineKind.Row, Row(r.Label, r.Before, r.After)));
            lines.Add(new(BrokerLineKind.Badges, string.Join(" · ", new[] { AdminBadge }.Concat(s.Badges))));
            if (s.Repair) lines.Add(new(BrokerLineKind.Note, RepairLine));
        }
        if (RedLine(Steps.Select(s => (s.N, s.Irreversible, s.DataLoss)).ToList()) is { } red) lines.Add(new(BrokerLineKind.Red, red));
        return lines;
    }

    /// <summary>"&lt;label&gt;: &lt;before&gt; → &lt;after&gt;", or "&lt;label&gt;: &lt;value&gt;" when they are the same.</summary>
    public static string Row(string label, string before, string after) =>
        string.IsNullOrEmpty(after) || before == after ? $"{label}: {before}" : $"{label}: {before} → {after}";

    /// <summary>
    /// The red line, worded as the card and the shell dialog: "Step 4 can’t be undone." (one step: "This can’t be undone.";
    /// several: "Steps 2 and 4", "Steps 1, 2, 4") and "… may lose unsaved work.". Null when no step is flagged.
    /// </summary>
    public static string? RedLine(IReadOnlyList<(int N, bool Irreversible, bool DataLoss)> steps)
    {
        var single = steps.Count == 1;
        var risky = steps.Where(s => s.Irreversible).Select(s => s.N).ToList();
        var lose = steps.Where(s => s.DataLoss).Select(s => s.N).ToList();
        var parts = new List<string>();
        if (risky.Count > 0) parts.Add(StepWord(risky, single) + " can’t be undone.");
        if (lose.Count > 0) parts.Add(StepWord(lose, single) + " may lose unsaved work.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string StepWord(IReadOnlyList<int> list, bool single)
    {
        if (single) return "This";
        var n = list.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        return (n.Count == 1 ? "Step " : "Steps ") + string.Join(n.Count == 2 ? " and " : ", ", n);
    }

    /// <summary>Plain text within the cap: no control or format characters (bidi, zero-width) that could hide or reorder it.</summary>
    private static bool Ok(string s) =>
        s.Length <= MaxText && !s.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
}

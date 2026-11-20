using System.Globalization;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>How a dialog line is drawn: the words come from the design; only the look varies.</summary>
public enum DialogLineKind { Heading, AskedBy, Quote, StepName, What, Row, Tradeoff, Badges, Why, Undo, UndoRed, RestorePoint, Notice, FoundBy, Warning, LeftOut }

/// <summary>
/// One line of a native approval dialog, in the order it is shown. <paramref name="Indent"/> is the list level: 0 for every
/// line but the "• …" bullets under "Trade-offs:" (design v67), which are 1 (drawn about 12 px in, with no extra space above).
/// </summary>
public sealed record DialogLine(DialogLineKind Kind, string Text, int Indent = 0);

/// <summary>
/// The words and their order in the two shell approval dialogs (design README "Windows dialogs", v57), built only from a
/// <see cref="PlanView"/> (the host's plan, already cleaned). The windows draw these lines as plain labels and add nothing,
/// so tests check the wording and order here without showing a window.
/// </summary>
public static class ApprovalDialogText
{
    public const string PlanTitleBar = "Optimus · Approve a change";
    public const string ScriptTitleBar = "Optimus · Approve a script";
    public const string Decline = "Decline";
    public const string Approve = "Approve";
    public const string Close = "Close";
    public const string RestorePointFirst = "A restore point is made first.";
    /// <summary>m9 §17.3 (Design System v80): the line of a step that does not run.</summary>
    public static string LeftOutByYou(string label) => label + " · Left out by you";
    public static string LeftOutAdminOff(string label) => label + " · Left out: needs administrator rights, which are off.";
    public static string LeftOutAdminUnavailable(string label) => label + " · Left out: needs administrator rights, not available in this version yet.";
    public const string Ended = "This request ended. Nothing was changed.";
    public const string FoundBy = "Found by Optimus:";
    public const string ReadToEnd = "Read to the end to approve";
    /// <summary>The script dialog's red line before the tick.</summary>
    public const string ScriptRed = "Can’t be undone";

    public static string AskedBy(string name) => "Asked by " + name;

    /// <summary>"This request ends in 4:52. If it ends, nothing changes." or, once it has ended, "This request ended. Nothing was changed."</summary>
    public static string Expiry(TimeSpan left)
    {
        if (left <= TimeSpan.Zero) return Ended;
        var whole = TimeSpan.FromSeconds(Math.Floor(left.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"This request ends in {(int)whole.TotalMinutes}:{whole.Seconds:00}. If it ends, nothing changes.");
    }

    /// <summary>"&lt;label&gt;: &lt;before&gt; → &lt;after&gt;", or "&lt;label&gt;: &lt;value&gt;" when they are the same (or there is no after).</summary>
    public static string Row(string label, string before, string after) =>
        after.Length == 0 || before == after ? $"{label}: {before}" : $"{label}: {before} → {after}";

    /// <summary>The step's name, numbered (several steps only: one step has no name, the heading says it).</summary>
    public static string StepName(PlanStepView step) => step.N.ToString(CultureInfo.InvariantCulture) + ". " + step.Label;

    /// <summary>The trade-off line after a step's rows: "Trade-off: &lt;text&gt;".</summary>
    public static string Tradeoff(string text) => PlanView.TradeoffLabel + ": " + text;

    /// <summary>The heading line over several trade-offs (design v66): "Trade-offs:".</summary>
    public const string TradeoffsHeading = "Trade-offs:";

    /// <summary>One of several trade-offs, on its own line under "Trade-offs:": "• &lt;text&gt;".</summary>
    public static string TradeoffBullet(string text) => "• " + text;

    /// <summary>
    /// A step's trade-off lines (design v66), the same for a single change and for every step of a fix: none → no line; one →
    /// "Trade-off: &lt;text&gt;"; two or more → "Trade-offs:" then one "• &lt;text&gt;" line each, in the host's order, each at
    /// indent 1 under the label (design v67); the label itself stays at indent 0, aligned with the step's other lines.
    /// </summary>
    public static IEnumerable<DialogLine> TradeoffLines(IReadOnlyList<string>? tradeoffs)
    {
        var list = (tradeoffs ?? []).Where(t => t.Length > 0).ToList();
        if (list.Count == 1) { yield return new(DialogLineKind.Tradeoff, Tradeoff(list[0])); yield break; }
        if (list.Count == 0) yield break;
        yield return new(DialogLineKind.Tradeoff, TradeoffsHeading);
        foreach (var t in list) yield return new(DialogLineKind.Tradeoff, TradeoffBullet(t), Indent: 1);
    }

    /// <summary>The heading, then "Asked by …" unless the user started the change from a card (design v62: the line is left out).</summary>
    private static List<DialogLine> Head(PlanView view)
    {
        var lines = new List<DialogLine> { new(DialogLineKind.Heading, view.Title) };
        if (view.RequestedBy is { } by) lines.Add(new(DialogLineKind.AskedBy, AskedBy(by)));
        return lines;
    }

    /// <summary>
    /// "Approve a change", items 1–4 (design v62, v66): the heading, who asked, the AI's reason, every step (its numbered name
    /// when there are several, what it does, rows, its trade-off lines, badges, why, undo sentence unless there is nothing to
    /// undo) and, when a step makes one, the restore-point line.
    /// </summary>
    public static IReadOnlyList<DialogLine> PlanBody(PlanView view)
    {
        var lines = Head(view);
        if (view.Why is { } why) lines.Add(new(DialogLineKind.Quote, why));
        var single = view.Steps.Count == 1;
        foreach (var s in view.Steps)
        {
            if (!single) lines.Add(new(DialogLineKind.StepName, StepName(s)));
            if (s.What.Length > 0) lines.Add(new(DialogLineKind.What, s.What));
            foreach (var (label, before, after) in s.Rows) lines.Add(new(DialogLineKind.Row, Row(label, before, after)));
            lines.AddRange(TradeoffLines(s.Tradeoffs));
            if (s.Badges.Count > 0) lines.Add(new(DialogLineKind.Badges, string.Join(" · ", s.Badges)));
            if (s.Why is { } stepWhy) lines.Add(new(DialogLineKind.Why, "Why: " + stepWhy));
            if (s.Undo.Length > 0) lines.Add(new(s.UndoRed ? DialogLineKind.UndoRed : DialogLineKind.Undo, s.Undo));
        }
        // m9 §17.3 / Design System v80: one unnumbered, plain (not bold) line per step that does not run, after the steps and
        // before the restore-point line: the steps the user switched off, then those left out for administrator rights.
        foreach (var left in view.LeftOut) lines.Add(new(DialogLineKind.LeftOut, left));
        if (view.Steps.Any(s => s.RestorePoint)) lines.Add(new(DialogLineKind.RestorePoint, RestorePointFirst));
        return lines;
    }

    /// <summary>
    /// Item 5's red block: "Step 4 can’t be undone." (one step: "This can’t be undone."; several: "Steps 2 and 4", "Steps 1, 2, 4")
    /// and "… may lose unsaved work.", as the card words it. Null when no step is flagged.
    /// </summary>
    public static string? RedBlock(PlanView view) =>
        RedBlock(view.Steps.Select(s => (s.N, s.Irreversible, s.DataLoss)).ToList());

    public static string? RedBlock(IReadOnlyList<(int N, bool Irreversible, bool DataLoss)> steps)
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

    /// <summary>
    /// "Approve a script", items 1–3: heading, who asked, the AI's reason, the fixed line, then (when they apply) the
    /// administrator and policy lines, then "Found by Optimus:" and each warning.
    /// </summary>
    public static IReadOnlyList<DialogLine> ScriptHeader(PlanView view)
    {
        var script = view.Script ?? throw new ArgumentException("Not a script plan.", nameof(view));
        var lines = Head(view);
        if (view.Why is { } why) lines.Add(new(DialogLineKind.Quote, why));
        lines.Add(new(DialogLineKind.Notice, PlanView.ScriptNotice));
        if (script.Elevated || view.NeedsAdmin) lines.Add(new(DialogLineKind.Notice, PlanView.ScriptAdminLine));
        if (script.BypassesPolicy) lines.Add(new(DialogLineKind.Notice, PlanView.ScriptPolicyLine));
        if (script.Warnings.Count > 0)
        {
            lines.Add(new(DialogLineKind.FoundBy, FoundBy));
            foreach (var w in script.Warnings) lines.Add(new(DialogLineKind.Warning, w));
        }
        return lines;
    }

    /// <summary>The restore-point tick's sub-line: the note when available, else the reason it isn't.</summary>
    public static string RestorePointSubLine(ScriptView script) =>
        script.RestorePointAvailable ? PlanView.RestorePointNote : script.RestorePointReason ?? PlanView.RestorePointUnsupported;
}

using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>
/// display.below_max_refresh: an active display runs ≥ 5 Hz below the highest rate offered at its current resolution
/// (ignores 59/60-style rounding). A preference, not a problem: low severity.
/// </summary>
public sealed class DisplayRefreshRule : IRule
{
    public const int MinGapHz = 5;

    public string Id => "display.below_max_refresh";
    public string Version => "1.0.0";
    public string Category => "customization";
    public IReadOnlyList<string> Inputs { get; } = ["win.custom.display"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        var displays = ctx.WithPrefix("display.").Where(e => e.CollectorId == "win.custom.display").OrderBy(e => e.Subject, StringComparer.Ordinal).ToList();
        if (displays.Count == 0) return RuleResult.Inapplicable("No active display reported.");

        var findings = new List<FindingDraft>();
        var readable = 0;
        foreach (var d in displays)
        {
            var current = RuleKit.Long(d.Value["currentHz"]);
            var max = RuleKit.Long(d.Value["maxHzAtResolution"]);
            if (current is null || max is null) continue;
            readable++;
            if (max - current < MinGapHz) continue;
            var label = RuleKit.Str(d.Value["label"]) ?? "Your display";
            findings.Add(new FindingDraft(d.Subject, $"{label} below its refresh rate", "low", "high", [d.Id],
                new CardDraft($"{label} runs at {current} Hz but supports {max} Hz. Would you like to review it?", RuleKit.Options,
                    ["Open Advanced display.", "Pick the display.", $"Choose {max} Hz under refresh rate."], "ms-settings:display-advanced")));
        }
        if (readable == 0) return RuleResult.Insufficient("Refresh rates unreadable.");
        return findings.Count > 0 ? RuleResult.Found([.. findings]) : RuleResult.Clear();
    }
}

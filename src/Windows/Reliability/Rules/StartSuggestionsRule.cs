using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>
/// start.suggestions_on: Start shows tips and recommendations. Info only. Confidence high when the setting is
/// read, medium when absent and the Windows default (on) is assumed.
/// </summary>
public sealed class StartSuggestionsRule : IRule
{
    public string Id => "start.suggestions_on";
    public string Version => "1.0.0";
    public string Category => "misc";
    public IReadOnlyList<string> Inputs { get; } = ["win.custom.start"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        var e = ctx.Get("start.suggestions");
        if (e is null) return RuleResult.Insufficient("Start setting missing.");
        if (!RuleKit.Bool(e.Value["suggestionsOn"])) return RuleResult.Clear();
        var confidence = RuleKit.Str(e.Value["confidence"]) == "high" ? "high" : "medium";
        return RuleResult.Found(new FindingDraft("start.suggestions", "Start suggestions are on", "info", confidence, [e.Id],
            new CardDraft("Start shows suggested apps and tips. Would you like to hide them?", RuleKit.Options,
                ["Open Start settings.", "Turn off recommendations for tips and new apps."], "ms-settings:personalization-start")));
    }
}

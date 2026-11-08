using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>privacy.optional_diag: optional diagnostic data is on and the setting is not managed by policy.</summary>
public sealed class OptionalDiagRule : IRule
{
    public string Id => "privacy.optional_diag";
    public string Version => "1.0.0";
    public string Category => "privacy_telemetry";
    public IReadOnlyList<string> Inputs { get; } = ["win.privacy.diagdata"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        var e = ctx.Get("privacy.diagdata");
        if (e is null) return RuleResult.Insufficient("Diagnostic data setting missing.");
        var state = RuleKit.Str(e.Value["state"]);
        if (state is null or "unknown") return RuleResult.Insufficient("Diagnostic data setting unknown.");
        if (RuleKit.Str(e.Value["effective"]) != "optional") return RuleResult.Clear();
        if (state == "managed") return RuleResult.Inapplicable("Managed by policy.");
        return RuleResult.Found(new FindingDraft("privacy.diagdata", "Optional diagnostic data is on", "low", "high", [e.Id],
            new CardDraft("Windows is sending optional diagnostic data. Would you like to review it?", RuleKit.Options,
                ["Open Diagnostics & feedback.", "Turn off Send optional diagnostic data."], "ms-settings:privacy-feedback")));
    }
}

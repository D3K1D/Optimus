using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain.Rules;

/// <summary>On battery with a High/Ultimate performance scheme active while Balanced is installed.</summary>
internal sealed class HighPerfOnBatteryRule : IRule
{
    public const string ActionId = "power.select_scheme";
    public static readonly IReadOnlyList<string> PreviewOptions = ["Preview change", "Ask about this", "Ignore"];
    /// <summary>Action parameters the card's Preview change sends (a fresh object per finding).</summary>
    public static global::System.Text.Json.Nodes.JsonObject ActionParameters() => new() { ["personality"] = "balanced" };

    public string Id => "power.high_perf_on_battery";
    public string Version => "1.0.0";
    public string Category => "performance";
    public IReadOnlyList<string> Inputs { get; } = ["win.system.power"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.Unusable(ctx, "win.system.power") is { } why) return RuleResult.Insufficient(why);
        var source = ctx.Get("power.source");
        var active = ctx.Get("power.active_scheme");
        var schemes = ctx.Get("power.schemes");
        if (source is null || active is null || schemes is null) return RuleResult.Insufficient("power evidence missing");

        if (source.Bool("hasBattery") == false) return RuleResult.Inapplicable("no battery");
        if (source.Str("source") != "battery") return RuleResult.Clear();
        if (active.Str("personality") != "high_performance") return RuleResult.Clear();
        // Single-plan machines (or Balanced removed): nothing to switch to here.
        if (schemes.Bool("hasBalanced") != true) return RuleResult.Inapplicable("Balanced plan not available");

        var plan = active.Bool("ultimate") == true ? "Ultimate Performance" : "High performance";
        return RuleResult.Found(new FindingDraft(
            Subject: "power.active_scheme",
            Title: $"{plan} on battery",
            Severity: "low",
            Confidence: "high",
            EvidenceIds: [active.Id, source.Id, schemes.Id],
            // M4: previewable through power.select_scheme with Parameters {personality:"balanced"}. Guide stays as the
            // manual fallback.
            Card: new CardDraft(
                $"Your PC is on battery with the {plan} plan. Would you like to try Balanced?",
                PreviewOptions,
                ["Open Control Panel › Power Options.", "Choose Balanced.", "Keep Power mode in Settings as you like it."],
                "ms-settings:powersleep",
                ActionId,
                ActionParameters()),
            ActionEligibility: "preview_available"));
    }
}

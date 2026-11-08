using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>reliability.app_crashes: one finding per app with ≥ 3 Application Error 1000 events for the same faulting app.</summary>
public sealed class AppCrashesRule : IRule
{
    public const string Prefix = "events.application.application-error.1000.";
    public const int Threshold = 3;

    public string Id => "reliability.app_crashes";
    public string Version => "1.0.0";
    public string Category => "diagnostics";
    public IReadOnlyList<string> Inputs { get; } = ["win.events.application"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        if (ctx.Get("events.application.coverage") is null) return RuleResult.Insufficient("Application event coverage missing.");

        var findings = new List<FindingDraft>();
        foreach (var e in ctx.WithPrefix(Prefix).OrderBy(e => e.Subject, StringComparer.Ordinal))
        {
            var count = RuleKit.Int(e.Value["count"]);
            var app = RuleKit.Str(e.Value["appName"]);
            if (count < Threshold || string.IsNullOrEmpty(app)) continue;
            var window = RuleKit.Window(e.Value["lookbackSeconds"]);
            findings.Add(new FindingDraft(e.Subject, $"{app} keeps crashing", count >= 10 ? "medium" : "low", "high", [e.Id],
                new CardDraft($"{app} closed unexpectedly {count} times {window}. Would you like to see what might help?", RuleKit.Options,
                    ["Update the app or reinstall it.", "Install pending updates in Windows Update.", "If it continues, contact the app maker."],
                    "ms-settings:appsfeatures")));
        }
        return findings.Count > 0 ? RuleResult.Found([.. findings]) : RuleResult.Clear();
    }
}

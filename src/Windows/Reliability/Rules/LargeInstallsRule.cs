using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>
/// apps.large_installs: info item listing up to 5 apps whose publisher-reported size is ≥ 2 GB.
/// Sizes are estimates; runtimes are left out; nothing is claimed about whether an app is used.
/// </summary>
public sealed class LargeInstallsRule : IRule
{
    public const long ThresholdBytes = 2L * 1024 * 1024 * 1024;
    public const int Top = 5;

    public string Id => "apps.large_installs";
    public string Version => "1.0.0";
    public string Category => "storage";
    public IReadOnlyList<string> Inputs { get; } = ["win.apps.installed"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        if (ctx.Get("apps.installed.summary") is not { } summary) return RuleResult.Insufficient("App summary missing.");

        var large = ctx.WithPrefix("apps.installed.")
            .Where(e => e.Subject != "apps.installed.summary" && RuleKit.Str(e.Value["kind"]) == "app")
            .Select(e => (e, bytes: RuleKit.Long(e.Value["sizeBytes"]) ?? 0))
            .Where(x => x.bytes >= ThresholdBytes)
            .OrderByDescending(x => x.bytes).ThenBy(x => x.e.Subject, StringComparer.Ordinal)
            .Take(Top).ToList();
        if (large.Count == 0) return RuleResult.Clear();

        var names = large.Select(x => $"{RuleKit.Str(x.e.Value["name"])} ({AppsNormalizer.Size(x.bytes)})").ToList();
        var text = large.Count == 1
            ? $"{names[0]} is your largest reported app. Would you like to review installed apps?"
            : $"{large.Count} apps report over 2 GB each, led by {names[0]}. Would you like to review installed apps?";
        return RuleResult.Found(new FindingDraft("apps.large_installs", "Large apps", "info", "low",
            large.Select(x => x.e.Id).Append(summary.Id).ToList(),
            new CardDraft(text, RuleKit.Options, ["Open Installed apps.", "Sort by size.", "Uninstall apps you no longer need."], "ms-settings:appsfeatures")));
    }
}

using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>
/// reliability.update_failures: ≥ 2 Windows Update install failures (WindowsUpdateClient 20) in the window → finding
/// (medium when none succeeded afterwards, low otherwise). A single failure → info with the last successful install (19).
/// Current Windows logs 19/20 in the System log; older builds also used WindowsUpdateClient/Operational, so both are read.
/// Uses locally logged history only; whether updates are pending now needs an online check (not done).
/// </summary>
public sealed class UpdateFailuresRule : IRule
{
    public static readonly string[] Channels = ["system", "update"];

    public string Id => "reliability.update_failures";
    public string Version => "1.0.0";
    public string Category => "diagnostics";
    public IReadOnlyList<string> Inputs { get; } = ["win.events.system", "win.events.update"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        // Either channel is enough to evaluate; insufficient only when neither was read.
        var read = Channels.Where(c => ctx.CoverageOf($"win.events.{c}") is "complete" or "partial" && ctx.Get($"events.{c}.coverage") is not null).ToList();
        if (read.Count == 0) return RuleResult.Insufficient("No update history was read.");
        var coverage = ctx.Get($"events.{read[0]}.coverage")!;

        var failures = read.SelectMany(c => ctx.WithPrefix($"events.{c}.windowsupdateclient.20.")).OrderBy(e => e.Subject, StringComparer.Ordinal).ToList();
        var total = failures.Sum(f => RuleKit.Int(f.Value["count"]));
        if (total == 0) return RuleResult.Clear();

        var successes = read.Select(c => ctx.Get($"events.{c}.windowsupdateclient.19")).OfType<EvidenceItem>().ToList();
        var lastSuccess = successes.Select(s => RuleKit.Time(s.Value["lastAt"])).Max();
        var lastFailure = failures.Select(f => RuleKit.Time(f.Value["lastAt"])).Max();
        var window = RuleKit.Window(coverage.Value["lookbackSeconds"]);
        var evidence = failures.Select(f => f.Id).Concat(successes.Select(s => s.Id)).ToList();
        var since = lastSuccess is { } ls ? $" The last successful install was on {ls.UtcDateTime:yyyy-MM-dd}." : "";

        if (total < 2)
        {
            return RuleResult.Found(new FindingDraft("reliability.update_failures", "An update failed to install", "info", "high", evidence,
                new CardDraft($"One Windows update failed to install {window}.{since} Would you like to review update history?", RuleKit.Options,
                    ["Open Windows Update.", "Select Check for updates.", "Restart if asked."], "ms-settings:windowsupdate-history")));
        }

        var recovered = lastSuccess is { } s && lastFailure is { } f && s > f;
        return RuleResult.Found(new FindingDraft("reliability.update_failures", "Updates keep failing", recovered ? "low" : "medium", "high", evidence,
            new CardDraft($"Windows updates failed to install {total} times {window}.{since} Would you like to review update history?", RuleKit.Options,
                ["Restart your PC, then open Windows Update.", "Select Check for updates.", "If it fails again, run the Windows Update troubleshooter."],
                "ms-settings:windowsupdate-history")));
    }
}

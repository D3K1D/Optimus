using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>
/// devices.problem_code: one finding per present device with a nonzero Device Manager problem code.
/// Code 22 (disabled on purpose) is not a problem. Code 28 (no driver) is low; others medium.
/// </summary>
public sealed class DeviceProblemRule : IRule
{
    public const int Disabled = 22;
    public const int NoDriver = 28;

    public string Id => "devices.problem_code";
    public string Version => "1.0.0";
    public string Category => "diagnostics";
    public IReadOnlyList<string> Inputs { get; } = ["win.devices.pnp"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        if (ctx.Get("devices.pnp.summary") is null) return RuleResult.Insufficient("Device summary missing.");

        var findings = new List<FindingDraft>();
        foreach (var d in ctx.WithPrefix("devices.pnp.").Where(e => e.Subject != "devices.pnp.summary").OrderBy(e => e.Subject, StringComparer.Ordinal))
        {
            var code = RuleKit.Int(d.Value["problemCode"]);
            if (code is 0 or Disabled) continue;
            var name = RuleKit.Str(d.Value["name"]) is { Length: > 0 } n ? n : "A device";
            var guide = code == NoDriver
                ? new List<string> { "Install pending updates in Windows Update.", "Install the driver from the device or PC maker.", "Restart your PC." }
                : ["Restart your PC.", "Install pending updates in Windows Update.", "Update or reinstall the driver from the device or PC maker."];
            findings.Add(new FindingDraft(d.Subject, $"{name} has a problem", code == NoDriver ? "low" : "medium", "high", [d.Id],
                new CardDraft($"{name} isn't working correctly (code {code}). Would you like to see steps?", RuleKit.Options, guide, "ms-settings:troubleshoot")));
        }
        return findings.Count > 0 ? RuleResult.Found([.. findings]) : RuleResult.Clear();
    }
}

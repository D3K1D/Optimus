using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability.Rules;

/// <summary>
/// reliability.kernel_power: ≥ 2 Kernel-Power 41 (unclean shutdowns) in the window, excluding ones where the power button was held.
/// Cites nearby bugchecks (1001 within 30 minutes, or a stop code inside event 41) and WHEA hardware errors.
/// Guidance comes from that evidence: stop code / WHEA → driver and hardware steps; none → power, cooling, updates.
/// Graphics drivers are named only when the stop code is a display-driver one.
/// </summary>
public sealed class KernelPowerRule : IRule
{
    public const string SubjectKey = "events.system.kernel-power.41";
    private static readonly TimeSpan Nearby = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> GraphicsStopCodes = ["0x116", "0x117", "0x119", "0x10e", "0x113"];

    public string Id => "reliability.kernel_power";
    public string Version => "1.0.0";
    public string Category => "diagnostics";
    public IReadOnlyList<string> Inputs { get; } = ["win.events.system"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.MissingInput(ctx, Inputs) is { } why) return RuleResult.Insufficient(why);
        if (ctx.Get("events.system.coverage") is null) return RuleResult.Insufficient("System event coverage missing.");
        var kp = ctx.Get(SubjectKey);
        if (kp is null) return RuleResult.Clear();

        var total = RuleKit.Int(kp.Value["count"]);
        var forced = RuleKit.Int(kp.Value["powerButtonCount"]);
        var unexpected = total - forced;
        if (unexpected < 2) return RuleResult.Clear();

        var times = (kp.Value["times"]?.AsArray() ?? []).Select(RuleKit.Time).OfType<DateTimeOffset>().ToList();
        var evidence = new List<string> { kp.Id };
        var stopCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in kp.Value["bugcheckCodes"]?.AsObject().Select(kv => kv.Key) ?? []) stopCodes.Add(code);

        foreach (var bc in ctx.WithPrefix("events.system.wer-systemerrorreporting.1001.").Concat(ctx.WithPrefix("events.system.bugcheck.1001.")))
        {
            var bcTimes = (bc.Value["times"]?.AsArray() ?? []).Select(RuleKit.Time).OfType<DateTimeOffset>();
            if (bcTimes.Any(t => times.Any(k => (t - k).Duration() <= Nearby)))
            {
                evidence.Add(bc.Id);
                if (RuleKit.Str(bc.Value["bugcheckCode"]) is { } c && c != "unknown") stopCodes.Add(c);
            }
        }
        // Corrected-error warnings are common; only fatal/uncorrected (error/critical) hardware reports count.
        var whea = ctx.WithPrefix("events.system.whea-logger.").Where(w => RuleKit.Str(w.Value["level"]) is "error" or "critical").ToList();
        evidence.AddRange(whea.Select(w => w.Id));

        var window = RuleKit.Window(kp.Value["lookbackSeconds"]);
        var text = $"Your PC restarted unexpectedly {unexpected} times {window}. Would you like to see what usually causes this?";
        List<string> guide;
        string confidence;
        if (whea.Count > 0)
        {
            confidence = "high";
            guide = ["Install pending updates in Windows Update.", "Update chipset and BIOS from your PC maker.", "Check cooling and fans.", "If it continues, ask your PC maker to test the hardware."];
        }
        else if (stopCodes.Count > 0)
        {
            confidence = "high";
            guide = stopCodes.Overlaps(GraphicsStopCodes)
                ? ["Update the graphics driver from your GPU maker.", "Install pending updates in Windows Update.", "If it continues, check cooling and power."]
                : ["Install pending updates in Windows Update.", "Update drivers from your PC maker.", "Run Windows Memory Diagnostic."];
        }
        else
        {
            confidence = "medium";
            guide = ["Check the power cable, outlet or battery.", "Make sure vents are clear and fans spin.", "Install pending updates in Windows Update."];
        }

        var severity = unexpected >= 5 || whea.Count > 0 ? "high" : "medium";
        return RuleResult.Found(new FindingDraft(SubjectKey, "Unexpected restarts", severity, confidence, evidence,
            new CardDraft(text, RuleKit.Options, guide, "ms-settings:windowsupdate-history")));
    }
}

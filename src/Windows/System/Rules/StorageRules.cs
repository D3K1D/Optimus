using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain.Rules;

/// <summary>System volume under 10% or under 15 GiB free.</summary>
internal sealed class SystemVolumeLowRule : IRule
{
    public string Id => "storage.system_low";
    public string Version => "1.0.0";
    public string Category => "storage";
    public IReadOnlyList<string> Inputs { get; } = ["win.system.volumes"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.Unusable(ctx, "win.system.volumes") is { } why) return RuleResult.Insufficient(why);
        var vol = ctx.WithPrefix("volume.").FirstOrDefault(e => e.Bool("isSystem") == true);
        if (vol is null) return RuleResult.Insufficient("system volume not measured");
        if (vol.Num("freeBytes") is not { } free || vol.Num("capacityBytes") is not { } cap || cap <= 0)
            return RuleResult.Insufficient("system volume size missing");

        var pct = 100 * free / cap;
        if (pct >= 10 && free >= 15 * RuleKit.GiB) return RuleResult.Clear();

        // Severity tracks consequence: under 5 GiB, updates and paging start failing; a big drive under 10% but
        // with tens of GiB left is a heads-up.
        var severity = free < 5 * RuleKit.GiB ? "high" : free < 50 * RuleKit.GiB ? "medium" : "low";
        return RuleResult.Found(new FindingDraft(
            Subject: vol.Subject,
            Title: "System drive space is running low",
            Severity: severity,
            Confidence: "high",
            EvidenceIds: [vol.Id],
            Card: new CardDraft(
                $"The system drive has {RuleKit.Gib(free)} GiB free. Would you like to review storage usage?",
                RuleKit.Options,
                ["Open Settings › System › Storage.", "Review Temporary files and Cleanup recommendations.", "Remove what you don’t need."],
                "ms-settings:storagesense")));
    }
}

/// <summary>Other fixed volumes under 10% free (tiny partitions under 1 GiB are ignored).</summary>
internal sealed class VolumeLowRule : IRule
{
    public string Id => "storage.volume_low";
    public string Version => "1.0.0";
    public string Category => "storage";
    public IReadOnlyList<string> Inputs { get; } = ["win.system.volumes"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.Unusable(ctx, "win.system.volumes") is { } why) return RuleResult.Insufficient(why);
        var others = ctx.WithPrefix("volume.").Where(e => e.Bool("isSystem") == false).ToList();
        if (others.Count == 0) return RuleResult.Inapplicable("no other fixed volumes");

        var findings = new List<FindingDraft>();
        foreach (var v in others)
        {
            if (v.Num("freeBytes") is not { } free || v.Num("capacityBytes") is not { } cap || cap < RuleKit.GiB) continue;
            if (100 * free / cap >= 10) continue;
            var letter = v.Str("letter") ?? "?";
            findings.Add(new FindingDraft(
                Subject: v.Subject,
                Title: $"Drive {letter}: is nearly full",
                Severity: "low",
                Confidence: "high",
                EvidenceIds: [v.Id],
                Card: new CardDraft(
                    $"Drive {letter}: has {RuleKit.Gib(free)} GiB free. Would you like to review what’s on it?",
                    RuleKit.Options,
                    ["Open Settings › System › Storage.", $"Choose Storage used on other drives, then {letter}:.", "Move or remove what you don’t need."],
                    "ms-settings:storagesense")));
        }
        return findings.Count == 0 ? RuleResult.Clear() : RuleResult.Found([.. findings]);
    }
}

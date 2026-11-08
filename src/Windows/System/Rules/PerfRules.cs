using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain.Rules;

/// <summary>
/// A background process above 25% CPU (share of all logical processors), or keeping ≥ 0.9 logical processors busy
/// (single-thread runaway), for more than half of the sample intervals. One finding per process either way.
/// Excludes this tool and the foreground app. Defender is reported as info. Never a malware claim.
/// </summary>
internal sealed class SustainedCpuRule : IRule
{
    public const double ThresholdPercent = 25;
    public const int MinIntervals = 3;
    /// <summary>Logical processors kept busy per interval for the single-thread trigger.</summary>
    public const double CoreThreshold = 0.9;

    public string Id => "perf.sustained_cpu";
    public string Version => "1.0.0";
    public string Category => "performance";
    public IReadOnlyList<string> Inputs { get; } = ["win.perf.sample"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.Unusable(ctx, "win.perf.sample") is { } why) return RuleResult.Insufficient(why);
        var sample = ctx.Get("perf.sample");
        if (sample is null) return RuleResult.Insufficient("sample missing");
        var total = (int)(sample.Num("intervals") ?? 0);
        if (total < MinIntervals) return RuleResult.Insufficient("too few samples");
        var procs = ctx.WithPrefix("perf.process.").ToList();
        if (procs.Count == 0) return RuleResult.Insufficient("process list unavailable");
        double? logical = sample.Num("logicalProcessors") is { } n && n > 0 ? n : null;

        var findings = new List<FindingDraft>();
        foreach (var p in procs)
        {
            if (p.Bool("isThisTool") == true) continue;
            var samples = p.Numbers("cpuPercentSamples");
            var above = samples.Count(s => s > ThresholdPercent);
            // Second trigger: a single-thread runaway (≈ one core) that stays under 25% of a many-core machine.
            var coreBusy = logical is { } lp ? samples.Count(s => s / 100 * lp >= CoreThreshold) : 0;
            var machineWide = above * 2 > total;
            if (!machineWide && coreBusy * 2 <= total) continue;
            var valid = samples.Count;
            if ((p.Num("foregroundIntervals") ?? 0) * 2 > valid) continue; // the app the user is working in
            var image = p.Str("image") ?? "A process";
            var avg = p.Num("cpuAvgPercent") ?? samples.Average();
            var defender = p.Bool("isDefender") == true;
            var evidence = new List<string> { p.Id, sample.Id };
            if (ctx.Get("perf.cpu") is { } cpu) evidence.Add(cpu.Id);
            findings.Add(new FindingDraft(
                Subject: BaseSubject(p.Subject),
                Title: $"{image} kept the CPU busy",
                Severity: defender ? "info" : "low",
                Confidence: "medium",
                EvidenceIds: evidence,
                Card: new CardDraft(
                    machineWide
                        ? $"{image} used {avg:0.#}% CPU on average in the background, above {ThresholdPercent:0}% in {above} of {total} samples. Would you like to review it?"
                        : $"{image} kept one CPU core busy in the background ({coreBusy} of {total} samples). Would you like to review it?",
                    RuleKit.Options,
                    ["Open Task Manager (Ctrl+Shift+Esc).", "Sort Processes by CPU.", $"Check whether {image} is doing work you expect."]),
                Counterevidence: defender ? ["Microsoft Defender can use CPU while it scans; this usually passes."] : null));
        }
        return findings.Count == 0 ? RuleResult.Clear() : RuleResult.Found([.. findings]);
    }

    /// <summary>"perf.process.ab12cd34.2" → "perf.process.ab12cd34" so the fingerprint is per image.</summary>
    private static string BaseSubject(string subject)
    {
        var parts = subject.Split('.');
        return parts.Length > 3 ? string.Join('.', parts.Take(3)) : subject;
    }
}

/// <summary>Available memory under 10% for most samples while committed memory is near its limit (≥ 90%).</summary>
internal sealed class MemoryPressureRule : IRule
{
    public const double AvailableThreshold = 10;
    public const double CommitNearLimit = 90;

    public string Id => "perf.memory_pressure";
    public string Version => "1.0.0";
    public string Category => "performance";
    public IReadOnlyList<string> Inputs { get; } = ["win.perf.sample"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.Unusable(ctx, "win.perf.sample") is { } why) return RuleResult.Insufficient(why);
        var mem = ctx.Get("perf.memory");
        if (mem is null) return RuleResult.Insufficient("memory not sampled");
        var avail = mem.Numbers("availablePercentSamples");
        if (avail.Count < 3) return RuleResult.Insufficient("too few samples");
        if (mem.Num("commitMaxPercent") is not { } commit) return RuleResult.Insufficient("commit charge missing");

        var low = avail.Count(a => a < AvailableThreshold);
        if (low * 2 <= avail.Count || commit < CommitNearLimit) return RuleResult.Clear();

        var evidence = new List<string> { mem.Id };
        var largest = ctx.WithPrefix("perf.process.")
            .Where(p => p.Num("memoryRank") is not null && p.Bool("isThisTool") != true)
            .OrderBy(p => p.Num("memoryRank")).FirstOrDefault();
        if (largest is not null) evidence.Add(largest.Id);
        var who = largest?.Str("image") is { } img ? $" The largest user was {img}." : "";
        return RuleResult.Found(new FindingDraft(
            Subject: "perf.memory",
            Title: "Memory was nearly full",
            Severity: "medium",
            Confidence: "medium",
            EvidenceIds: evidence,
            Card: new CardDraft(
                $"Available memory stayed under {AvailableThreshold:0}% for {low} of {avail.Count} samples.{who} Would you like to review what’s using memory?",
                RuleKit.Options,
                ["Open Task Manager (Ctrl+Shift+Esc).", "Sort Processes by Memory.", "Close apps you don’t need right now."])));
    }
}

/// <summary>Info: Windows has been running for more than 14 days.</summary>
internal sealed class UptimeLongRule : IRule
{
    public const double Days = 14;

    public string Id => "perf.uptime_long";
    public string Version => "1.0.0";
    public string Category => "performance";
    public IReadOnlyList<string> Inputs { get; } = ["win.system.os"];

    public RuleResult Evaluate(RuleContext ctx)
    {
        if (RuleKit.Unusable(ctx, "win.system.os") is { } why) return RuleResult.Insufficient(why);
        var up = ctx.Get("system.uptime");
        if (up?.Num("uptimeSeconds") is not { } seconds) return RuleResult.Insufficient("uptime missing");
        var days = seconds / 86400;
        if (days <= Days) return RuleResult.Clear();
        return RuleResult.Found(new FindingDraft(
            Subject: "system.uptime",
            Title: "No restart in a while",
            Severity: "info",
            Confidence: "high",
            EvidenceIds: [up.Id],
            Card: new CardDraft(
                $"Windows hasn’t restarted in {Math.Floor(days):0} days. Would you like to restart when it suits you?",
                RuleKit.Options,
                ["Save your work.", "Open Start › Power › Restart."]),
            Counterevidence: ["Shut down with Fast startup doesn’t reset this; Restart does."]));
    }
}

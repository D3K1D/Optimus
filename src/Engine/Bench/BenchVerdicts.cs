using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Bench;

/// <summary>
/// What the host saw of one part while it ran: the 1 Hz sensor samples and whether the thermal stop rule tripped.
/// Verdicts are decided from this alone, so they can be tested from synthetic telemetry.
/// </summary>
public sealed class PartTelemetry(string part)
{
    public string Part { get; } = part;
    public List<SensorSample> Samples { get; } = [];
    public double SampleSeconds { get; init; } = 1;

    /// <summary>Slot ids whose sensor source stopped answering during the part (guarded by the Samples lock).</summary>
    public HashSet<string> FailedSlots { get; } = new(StringComparer.Ordinal);

    /// <summary>The part was stopped by the thermal stop rule (§6.1): unscored and never repeated.</summary>
    public bool ThermalStop { get; set; }

    /// <summary>The longest run of consecutive samples with NVML HW/SW thermal slowdown active.</summary>
    public int LongestThermalSlowdown
    {
        get
        {
            int best = 0, run = 0;
            foreach (var s in Samples)
            {
                run = s.Gpu is { ThermalSlowdown: true } ? run + 1 : 0;
                best = Math.Max(best, run);
            }
            return best;
        }
    }

    public int ThermalSlowdownSamples => Samples.Count(s => s.Gpu is { ThermalSlowdown: true });
    public int PowerCapSamples => Samples.Count(s => s.Gpu is { PowerCap: true });

    /// <summary>A metric's values (slot id "temp" or "gpu.temp"), skipping unread samples.</summary>
    public List<double> Values(string metric) =>
        Samples.Select(s => BenchTelemetry.Value(s, Part, metric)).OfType<double>().Where(double.IsFinite).ToList();
}

public static class BenchTelemetry
{
    /// <summary>A sample's value for a metric, whether the hub keyed it "temp" or "gpu.temp".</summary>
    public static double? Value(SensorSample sample, string part, string metric) =>
        sample.Values.TryGetValue(metric, out var v) ? v : sample.Values.TryGetValue(part + "." + metric, out var w) ? w : null;
}

/// <summary>
/// The GPU thermal stop rule (§6.1): GPU temperature at the NVML slowdown temperature minus 3 °C or more for 2
/// consecutive samples. Feed it every sample of the GPU part; it trips once.
/// </summary>
public sealed class ThermalStopRule
{
    public const double MarginC = 3;
    public const int ConsecutiveSamples = 2;
    private int _consecutive;

    public bool Tripped { get; private set; }

    /// <summary>True on the sample that trips the rule (and stays tripped).</summary>
    public bool Observe(double? tempC, double? slowdownTempC)
    {
        if (Tripped) return true;
        if (tempC is { } t && slowdownTempC is { } s && double.IsFinite(t) && double.IsFinite(s) && s > 0 && t >= s - MarginC) _consecutive++;
        else _consecutive = 0;
        Tripped = _consecutive >= ConsecutiveSamples;
        return Tripped;
    }
}

/// <summary>How the slower_than_before confirmation went: a regressed subtest repeated once for 10 s (§6 rule 3).</summary>
public enum RepeatOutcome { NotNeeded, Confirmed, Unconfirmed, NotRepeatable }

/// <summary>One part's verdict: id, reason (unmeasured), why codes and neutral observations.</summary>
public sealed record PartVerdict(string Verdict, string? Reason, IReadOnlyList<string> Why, IReadOnlyList<JsonObject> Observations);

/// <summary>Everything a verdict looks at for one part.</summary>
public sealed record VerdictInput(
    string Part, string State, string? Reason, double? Score, IReadOnlyDictionary<string, double> Subtests,
    PartTelemetry Telemetry, IReadOnlyList<double> HistoryScores, RepeatOutcome Repeat, BenchHardware? Hardware);

/// <summary>
/// Verdicts (§6), host only and cautious; first match wins: running_hot (specific thermal evidence only), unmeasured,
/// slower_than_before (confirmed by one repeat), no_issue. Observations are neutral and never verdicts.
/// </summary>
public static class BenchVerdicts
{
    public const string RunningHot = "running_hot", Unmeasured = "unmeasured", SlowerThanBefore = "slower_than_before", NoIssue = "no_issue";

    /// <summary>NVML thermal slowdown must be active this long to count as running hot.</summary>
    public const double ThermalSlowdownSeconds = 5;

    /// <summary>Typical sequential read floor per disk media (MB/s): below it the neutral disk observation is shown.</summary>
    public static readonly IReadOnlyDictionary<string, double> TypicalSeqReadMBs = new Dictionary<string, double>
    {
        ["nvme"] = 1000, ["ssd"] = 300, ["hdd"] = 80,
    };

    public static PartVerdict Decide(VerdictInput input)
    {
        var observations = Observations(input);
        var t = input.Telemetry;

        // 1. running_hot: the thermal stop rule, or NVML HW/SW thermal slowdown for ≥ 5 s. Never without a sensor.
        if (t.ThermalStop) return new(RunningHot, null, ["thermal_stop"], observations);
        if (t.LongestThermalSlowdown * t.SampleSeconds >= ThermalSlowdownSeconds) return new(RunningHot, null, ["thermal_slowdown"], observations);

        // 2. unmeasured: skipped, failed or cancelled.
        if (input.State != "done" || input.Score is null) return new(Unmeasured, input.Reason ?? input.State, [], observations);

        // 3. slower_than_before: below 85% of the median of the last 3 comparable runs, confirmed by one repeat.
        if (IsRegressed(input.Score, input.HistoryScores))
        {
            if (input.Repeat == RepeatOutcome.Confirmed) return new(SlowerThanBefore, null, ["below_recent_median", "repeat_confirmed"], observations);
            observations = [.. observations, new JsonObject { ["id"] = "lower_than_before_unconfirmed" }];
        }
        return new(NoIssue, null, [], observations);
    }

    /// <summary>Score below <see cref="BenchScoring.RegressionRatio"/> × median of up to the last 3 comparable part scores.</summary>
    public static bool IsRegressed(double? score, IReadOnlyList<double> history) =>
        score is { } s && history.Count > 0 && s < BenchScoring.RegressionRatio * BenchScoring.Median([.. history.Take(BenchScoring.HistoryRuns)]);

    /// <summary>The subtest that regressed most against its own history median (direction-aware), or null.</summary>
    public static string? RegressedSubtest(string part, IReadOnlyDictionary<string, double> subtests, IReadOnlyList<IReadOnlyDictionary<string, double>> history)
    {
        string? worst = null;
        var worstRatio = double.MaxValue;
        foreach (var (id, value) in subtests)
        {
            var past = history.Take(BenchScoring.HistoryRuns).Select(h => h.TryGetValue(id, out var x) ? x : double.NaN).Where(double.IsFinite).ToList();
            if (past.Count == 0 || value <= 0) continue;
            var median = BenchScoring.Median(past);
            if (median <= 0) continue;
            var ratio = BenchScoring.LowerIsBetter(part, id) ? median / value : value / median;
            if (ratio < worstRatio) { worstRatio = ratio; worst = id; }
        }
        return worst;
    }

    /// <summary>The repeat confirms when the repeated subtest is still below 85% of that subtest's history median.</summary>
    public static bool RepeatConfirms(string part, string subtest, double repeatValue, IReadOnlyList<double> subtestHistory)
    {
        var past = subtestHistory.Take(BenchScoring.HistoryRuns).Where(double.IsFinite).ToList();
        if (past.Count == 0 || repeatValue <= 0) return false;
        var median = BenchScoring.Median(past);
        var ratio = BenchScoring.LowerIsBetter(part, subtest) ? median / repeatValue : repeatValue / median;
        return ratio < BenchScoring.RegressionRatio;
    }

    /// <summary>Neutral observations (§6): power_limited, clock_drop_after_boost, thermal_slowdown_brief, ram/disk notes.</summary>
    public static List<JsonObject> Observations(VerdictInput input)
    {
        var t = input.Telemetry;
        var list = new List<JsonObject>();

        // power_limited: NVML power cap for ≥ 3 samples (GPU), or the CPU's % Performance Limit below 95 for ≥ 3 samples.
        if (input.Part == BenchParts.Gpu && t.PowerCapSamples >= 3) list.Add(new JsonObject { ["id"] = "power_limited" });
        if (input.Part == BenchParts.Cpu)
        {
            var limits = t.Samples.Select(s => s.CpuPerfLimitPercent).OfType<double>().Where(v => double.IsFinite(v) && v < 95).ToList();
            if (limits.Count >= 3) list.Add(new JsonObject { ["id"] = "power_limited", ["value"] = Math.Round(limits.Min()), ["unit"] = "%" });
        }

        // clock_drop_after_boost (normal): the last third's average clock is under 90% of the first third's peak.
        var clocks = t.Values("clock");
        if (clocks.Count >= 6)
        {
            var third = clocks.Count / 3;
            var peak = clocks.Take(third).Max();
            var late = clocks.Skip(clocks.Count - third).Average();
            if (peak > 0 && late < 0.9 * peak) list.Add(new JsonObject { ["id"] = "clock_drop_after_boost" });
        }

        // thermal_slowdown_brief: NVML thermal slowdown seen, but for less than 5 s in a row.
        if (t.ThermalSlowdownSamples > 0 && t.LongestThermalSlowdown * t.SampleSeconds < ThermalSlowdownSeconds)
            list.Add(new JsonObject { ["id"] = "thermal_slowdown_brief" });

        if (input.Part == BenchParts.Ram && input.Hardware is { RamConfiguredMTs: { } configured, RamRatedMTs: { } rated } && configured > 0 && configured < rated)
            list.Add(new JsonObject { ["id"] = "ram_speed_below_module_rating", ["value"] = configured, ["unit"] = "MT/s", ["rated"] = rated });

        if (input.Part == BenchParts.Disk && input.State == "done" && input.Hardware is { } hw
            && TypicalSeqReadMBs.TryGetValue(hw.DiskMedia, out var typical) && input.Subtests.TryGetValue("seqRead", out var seqRead) && seqRead < typical)
            list.Add(new JsonObject { ["id"] = "disk_below_typical_for_type", ["value"] = Math.Round(seqRead), ["unit"] = "MB/s", ["media"] = hw.DiskMedia });

        return list;
    }
}

/// <summary>
/// Fan guidance (§7): text codes only, never numeric curves. The UI holds the words (design v37 "Cooling tips"):
/// four fixed tips, three app chips (FanControl, G-Helper, Legion Toolkit) with the brand's highlighted, and the pump
/// line on desktops. Shape: {brand:"any|asus|lenovo", tips:[codes], pump:boolean}.
/// </summary>
public static class FanGuidance
{
    public static readonly string[] Tips = ["bios_fan_mode", "clean_filters", "check_fans_spin", "gpu_default_curve"];

    private static readonly Regex Legion = new(@"\b(legion|loq)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>asus = an ASUS laptop; lenovo = a Lenovo Legion or LOQ; otherwise any.</summary>
    public static string Brand(string? manufacturer, string? product, string? family, bool laptop)
    {
        var maker = (manufacturer ?? "").Trim();
        if (laptop && maker.Contains("asus", StringComparison.OrdinalIgnoreCase)) return "asus";
        if (maker.Contains("lenovo", StringComparison.OrdinalIgnoreCase) && Legion.IsMatch((product ?? "") + " " + (family ?? ""))) return "lenovo";
        return "any";
    }

    public static JsonObject Build(string brand, bool laptop) => new()
    {
        ["brand"] = brand, ["tips"] = Json.Array(Tips), ["pump"] = !laptop,
    };
}

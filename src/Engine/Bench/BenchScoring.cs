namespace OptimizationTool.Engine.Bench;

/// <summary>
/// Scores (contracts/m6-benchmark.md §5). A subtest scores 1000 × measured / reference (reference / measured for
/// latency, where lower is better); a part is the geometric mean of its subtests; the overall score is the geometric
/// mean of the measured parts × 10, so the reference PC scores about 10,000. A run missing a part is partial and is
/// never compared.
/// </summary>
public static class BenchScoring
{
    /// <summary>
    /// Reference values for benchVersion 1, in the worker's units (BenchSeams.cs): a mid-range 2024 desktop (6-core/
    /// 12-thread CPU, ~15 TFLOPS GPU with ~270 GB/s memory, dual-channel DDR5-5600, PCIe 4 NVMe). Calibrated by the lead
    /// from worker runs on a high-end 2025 desktop (16-core CPU, ~45 TFLOPS GPU, DDR5, PCIe 4 NVMe), scaled by core count and published
    /// specs. Changing them changes every score, so any change after release needs a new benchVersion.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> References =
        new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            [BenchParts.Cpu] = new Dictionary<string, double> { ["single"] = 2300, ["multi"] = 24000 },          // ops/s
            [BenchParts.Gpu] = new Dictionary<string, double> { ["fma"] = 15000, ["copy"] = 220 },               // GFLOPS, GB/s
            [BenchParts.Ram] = new Dictionary<string, double> { ["read"] = 60, ["write"] = 30, ["copy"] = 38, ["latency"] = 95 }, // GB/s, ns
            [BenchParts.Disk] = new Dictionary<string, double> { ["seqWrite"] = 1800, ["seqRead"] = 3500, ["rand4k"] = 600 }, // MB/s
        };

    /// <summary>Subtests where a lower value is better.</summary>
    public static bool LowerIsBetter(string part, string subtest) => part == BenchParts.Ram && subtest == "latency";

    /// <summary>1000 × measured / reference (inverted for latency). Null for a missing, unknown or non-positive value.</summary>
    public static double? SubtestScore(string part, string subtest, double? value)
    {
        if (value is not { } v || !double.IsFinite(v) || v <= 0) return null;
        if (!References.TryGetValue(part, out var refs) || !refs.TryGetValue(subtest, out var reference)) return null;
        return 1000 * (LowerIsBetter(part, subtest) ? reference / v : v / reference);
    }

    /// <summary>Geometric mean of the part's subtest scores; null unless every subtest of the part has a valid value.</summary>
    public static double? PartScore(string part, IReadOnlyDictionary<string, double> subtests)
    {
        if (!BenchSubtests.ByPart.TryGetValue(part, out var ids)) return null;
        var scores = new List<double>();
        foreach (var id in ids)
        {
            if (!subtests.TryGetValue(id, out var v) || SubtestScore(part, id, v) is not { } s) return null;
            scores.Add(s);
        }
        return GeoMean(scores);
    }

    /// <summary>Overall score: geometric mean of the measured parts × 10, rounded; partial when fewer than all four parts were measured.</summary>
    public static (double? Score, bool Partial) Overall(IReadOnlyDictionary<string, double> partScores)
    {
        var measured = partScores.Values.Where(v => double.IsFinite(v) && v > 0).ToList();
        if (measured.Count == 0) return (null, true);
        return (Math.Round(GeoMean(measured) * 10), measured.Count < BenchParts.RunOrder.Count);
    }

    public static double GeoMean(IReadOnlyCollection<double> values) =>
        Math.Exp(values.Sum(v => Math.Log(v)) / values.Count);

    /// <summary>
    /// configId (§5): a hash of CPU model, GPU model and driver version, RAM size and module count, the tested disk
    /// model and size, the Windows build and the Optimus build. "cfg-" + 16 hex.
    /// </summary>
    public static string ConfigId(BenchHardware hw, string optimusVersion) => "cfg-" + Ids.Hash16(
        hw.CpuModel.Trim(), hw.GpuModel?.Trim() ?? "", hw.GpuDriverVersion?.Trim() ?? "",
        hw.RamBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), hw.RamModules.ToString(System.Globalization.CultureInfo.InvariantCulture),
        hw.DiskModel?.Trim() ?? "", (hw.DiskSizeBytes ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        hw.WindowsBuild.Trim(), optimusVersion);

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>A part score below this share of the history median is a regression (§6 rule 3).</summary>
    public const double RegressionRatio = 0.85;

    /// <summary>How many earlier comparable runs make the history median.</summary>
    public const int HistoryRuns = 3;
}

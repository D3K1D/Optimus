namespace OptimizationTool.Windows.Sensors;

/// <summary>One PDH collect of the Processor Information counters (raw, per instance).</summary>
internal sealed record CpuPdhRaw(IReadOnlyList<PdhItem> Performance, IReadOnlyList<PdhItem> Frequency, IReadOnlyList<PdhItem> Limit);

/// <summary>Estimated clock in GHz, average % Performance Limit, and the highest nominal MHz seen.</summary>
internal sealed record CpuReading(double? ClockGhz, double? LimitPercent, double? MaxNominalMHz);

/// <summary>Pure: Processor Information instances → estimated clock (§3 cpu.clock, cpu.limit).</summary>
internal static class CpuNormalizer
{
    /// <summary>"_Total" and per-group totals such as "0,_Total" are aggregates, not processors.</summary>
    public static bool IsTotal(string instance) => instance.Contains("_Total", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Per processor instance: % Processor Performance (uncapped, may exceed 100 under boost) / 100 × Processor
    /// Frequency (that instance's nominal MHz, so hybrid P/E cores each use their own), averaged across instances.
    /// Instances with an invalid status on either counter are skipped. Performance Limit Flags are never read.
    /// </summary>
    public static CpuReading Normalize(CpuPdhRaw raw)
    {
        var freq = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in raw.Frequency)
            if (!IsTotal(f.Instance) && f.Valid && f.Value > 0) freq[f.Instance] = f.Value;

        double sum = 0;
        var n = 0;
        foreach (var p in raw.Performance)
        {
            if (IsTotal(p.Instance) || !p.Valid || p.Value < 0) continue;
            if (!freq.TryGetValue(p.Instance, out var mhz)) continue;
            sum += p.Value / 100.0 * mhz;
            n++;
        }

        double lsum = 0;
        var ln = 0;
        foreach (var l in raw.Limit)
        {
            if (IsTotal(l.Instance) || !l.Valid || l.Value < 0) continue;
            lsum += l.Value;
            ln++;
        }

        return new CpuReading(
            n > 0 ? Math.Round(sum / n / 1000.0, 3) : null,
            ln > 0 ? Math.Round(lsum / ln, 1) : null,
            freq.Count > 0 ? freq.Values.Max() : null);
    }

    /// <summary>Chart scale: nominal × 1.6 rounded up to 0.5 GHz (boost headroom), 6 GHz when nominal is unknown.</summary>
    public static double ClockMaxGhz(double? nominalMHz) =>
        nominalMHz is > 0 and var m ? Math.Max(1.0, Math.Ceiling(m * 1.6 / 1000.0 * 2) / 2) : 6.0;
}

/// <summary>cpu.clock + cpu.limit from PDH (\Processor Information(*)\…).</summary>
internal sealed class CpuPdhSource : ISensorSource
{
    public const string PerfPath = @"\Processor Information(*)\% Processor Performance";
    public const string FreqPath = @"\Processor Information(*)\Processor Frequency";
    public const string LimitPath = @"\Processor Information(*)\% Performance Limit";

    private readonly PdhQuery _query;

    private CpuPdhSource(PdhQuery query) => _query = query;

    public string Name => "cpu.pdh";
    public IReadOnlyList<string> SlotIds { get; } = ["clock"];

    /// <summary>Opens the query and takes the two-sample start; returns the source and its first reading.</summary>
    public static (CpuPdhSource Source, CpuReading First) Open()
    {
        var q = new PdhQuery([PerfPath, FreqPath, LimitPath]);
        try
        {
            q.Collect();
            Thread.Sleep(250);
            var s = new CpuPdhSource(q);
            return (s, s.ReadRaw());
        }
        catch { q.Dispose(); throw; }
    }

    private CpuReading ReadRaw()
    {
        _query.Collect();
        return CpuNormalizer.Normalize(new CpuPdhRaw(_query.Items(0), _query.Items(1), _query.Items(2)));
    }

    public SourceReading Read()
    {
        var r = ReadRaw();
        return new SourceReading(new Dictionary<string, double?> { ["clock"] = r.ClockGhz }, null, r.LimitPercent);
    }

    public void Dispose() => _query.Dispose();
}

/// <summary>Pure: Energy Meter instance selection and plausibility (§3 cpu.power).</summary>
internal static class EnergyMeterNormalizer
{
    /// <summary>
    /// The CPU package instances: RAPL-style names ("RAPL_Package0_PKG") whose last "_" token is PKG or PACKAGE.
    /// "RAPL_Package0_PP0", "…_CORE", "…_DRAM" and "_Total" are not the package.
    /// </summary>
    public static IReadOnlyList<string> PackageInstances(IEnumerable<string> instances) =>
        instances.Where(IsPackage).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsPackage(string instance)
    {
        if (!instance.StartsWith("RAPL", StringComparison.OrdinalIgnoreCase)) return false;
        var last = instance[(instance.LastIndexOf('_') + 1)..];
        return last.Equals("PKG", StringComparison.OrdinalIgnoreCase) || last.Equals("PACKAGE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Package power in W (mW summed over packages), or null when any package value is invalid.</summary>
    public static double? PackageWatts(IReadOnlyList<PdhItem> power, IReadOnlyList<string> packages)
    {
        if (packages.Count == 0) return null;
        double mw = 0;
        foreach (var name in packages)
        {
            var item = power.FirstOrDefault(i => string.Equals(i.Instance, name, StringComparison.OrdinalIgnoreCase));
            if (item.Instance is null || !item.Valid) return null;
            mw += item.Value;
        }
        return Math.Round(mw / 1000.0, 1);
    }

    /// <summary>Picowatt-hours to joules.</summary>
    public const double JoulesPerPicowattHour = 3.6e-9;

    /// <summary>Default agreement needed between the Power counter and ΔEnergy/ΔTime.</summary>
    public const double ScalingTolerance = 0.25;

    /// <summary>
    /// Mean power in W from the cumulative counters of one instance: Energy (picowatt-hours) and Time (the meter's own
    /// timestamp, ms). Null when the meter did not advance, went backwards, or the interval is under 100 ms.
    /// </summary>
    public static double? EnergyWatts(double energy1PWh, double time1Ms, double energy2PWh, double time2Ms)
    {
        var de = energy2PWh - energy1PWh;
        var dt = time2Ms - time1Ms;
        if (!double.IsFinite(de) || !double.IsFinite(dt) || dt < 100 || de <= 0) return null;
        return de * JoulesPerPicowattHour / (dt / 1000.0);
    }

    /// <summary>
    /// §3 provider/scaling check: the Power counter (W, averaged over the same interval) agrees with ΔEnergy/ΔTime
    /// within <paramref name="tolerance"/> (relative to the energy-derived value).
    /// </summary>
    public static bool ScalingMatches(double? energyW, double? powerW, double tolerance = ScalingTolerance) =>
        energyW is > 0 and var e && powerW is > 0 and var p && Math.Abs(p - e) / e <= tolerance;

    /// <summary>Value of <paramref name="instance"/> in <paramref name="items"/>, or null when missing or invalid.</summary>
    public static double? ValueOf(IReadOnlyList<PdhItem> items, string instance)
    {
        var item = items.FirstOrDefault(i => string.Equals(i.Instance, instance, StringComparison.OrdinalIgnoreCase));
        return item.Instance is not null && item.Valid ? item.Value : null;
    }

    /// <summary>
    /// Every package validates: Power (mW) at the second collect vs ΔEnergy/ΔTime between the two collects.
    /// </summary>
    public static bool Validates(IReadOnlyList<string> packages,
        IReadOnlyList<PdhItem> energy1, IReadOnlyList<PdhItem> time1,
        IReadOnlyList<PdhItem> energy2, IReadOnlyList<PdhItem> time2, IReadOnlyList<PdhItem> power2)
    {
        if (packages.Count == 0) return false;
        foreach (var p in packages)
        {
            if (ValueOf(energy1, p) is not { } e1 || ValueOf(time1, p) is not { } t1 || ValueOf(energy2, p) is not { } e2
                || ValueOf(time2, p) is not { } t2 || ValueOf(power2, p) is not { } mw) return false;
            if (!ScalingMatches(EnergyWatts(e1, t1, e2, t2), mw / 1000.0)) return false;
        }
        return true;
    }

    /// <summary>Plausible package power: every sample strictly between 0 and 1000 W.</summary>
    public static bool Plausible(IEnumerable<double?> watts)
    {
        var any = false;
        foreach (var w in watts)
        {
            if (w is not (> 0 and < 1000)) return false;
            any = true;
        }
        return any;
    }
}

/// <summary>What the Energy Meter probe found (for diagnostics and the Live test).</summary>
internal sealed record EmiCheck(IReadOnlyList<string> Packages, bool Plausible, bool ScalingValidated);

/// <summary>cpu.power from PDH \Energy Meter(*)\Power (mW), package instances only, after a scaling check.</summary>
internal sealed class EnergyMeterSource : ISensorSource
{
    public const string PowerPath = @"\Energy Meter(*)\Power";
    public const string EnergyPath = @"\Energy Meter(*)\Energy";
    public const string TimePath = @"\Energy Meter(*)\Time";
    private const int ValidationIntervalMs = 700;

    private readonly PdhQuery _query;
    private readonly IReadOnlyList<string> _packages;

    private EnergyMeterSource(PdhQuery query, IReadOnlyList<string> packages)
    {
        _query = query;
        _packages = packages;
    }

    public string Name => "cpu.emi";
    public IReadOnlyList<string> SlotIds { get; } = ["power"];

    public static EnergyMeterSource? TryOpen() => TryOpen(out _);

    /// <summary>
    /// Opens the query and picks the package instances. Accepted only when two Power samples are plausible and the
    /// Power counter agrees with ΔEnergy/ΔTime (Energy in picowatt-hours, Time in ms, both cumulative) over a
    /// ~700 ms interval within 25% for every package (§3: provider and scaling validated). Otherwise null.
    /// </summary>
    public static EnergyMeterSource? TryOpen(out EmiCheck check)
    {
        check = new EmiCheck([], false, false);
        PdhQuery q;
        try { q = new PdhQuery([PowerPath, EnergyPath, TimePath]); }
        catch (PdhException) { return null; }
        try
        {
            // Power is a rate: the first collect only primes it (formatting fails with PDH_INVALID_DATA until the second).
            q.Collect();
            Thread.Sleep(250);
            q.Collect();
            var power1 = q.Items(0);
            var energy1 = q.Items(1);
            var time1 = q.Items(2);
            var packages = EnergyMeterNormalizer.PackageInstances(power1.Select(i => i.Instance));
            check = new EmiCheck(packages, false, false);
            if (packages.Count == 0) { q.Dispose(); return null; }

            Thread.Sleep(ValidationIntervalMs);
            q.Collect();
            var power2 = q.Items(0);
            var plausible = EnergyMeterNormalizer.Plausible(
                [EnergyMeterNormalizer.PackageWatts(power1, packages), EnergyMeterNormalizer.PackageWatts(power2, packages)]);
            var scaled = EnergyMeterNormalizer.Validates(packages, energy1, time1, q.Items(1), q.Items(2), power2);
            check = new EmiCheck(packages, plausible, scaled);
            if (!plausible || !scaled) { q.Dispose(); return null; }
            return new EnergyMeterSource(q, packages);
        }
        catch (PdhException) { q.Dispose(); return null; }
    }

    public SourceReading Read()
    {
        _query.Collect();
        var w = EnergyMeterNormalizer.PackageWatts(_query.Items(0), _packages);
        return new SourceReading(new Dictionary<string, double?> { ["power"] = w is > 0 and < 1000 ? w : null });
    }

    public void Dispose() => _query.Dispose();
}

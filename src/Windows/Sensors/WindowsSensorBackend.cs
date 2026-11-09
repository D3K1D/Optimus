using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>
/// Probes this PC's driver-free sources for a part. Every probe is bounded (abandoned on timeout → slot reason
/// timed_out) and never throws for an unreadable source: that is an unavailable slot. Cancelling the token abandons
/// the probe in progress, disposes what was already opened and throws <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class WindowsSensorBackend : ISensorBackend
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public PartPlan Prepare(string part, BenchHardware hardware, CancellationToken ct = default) => part switch
    {
        BenchParts.Cpu => PrepareCpu(ct),
        BenchParts.Gpu => PrepareGpu(hardware, ct),
        BenchParts.Ram => new PartPlan(SlotPlanner.Ram(), []),
        BenchParts.Disk => PrepareDisk(hardware, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "unknown part"),
    };

    private sealed class CpuProbe : IDisposable
    {
        public CpuPdhSource? Source;
        public CpuReading? First;
        public void Dispose() => Source?.Dispose();
    }

    private static PartPlan PrepareCpu(CancellationToken ct)
    {
        var sources = new List<ISensorSource>();

        string? clockReason = null;
        CpuProbe? cpu = null;
        try
        {
            cpu = Bounded.Run(() =>
            {
                var (s, first) = CpuPdhSource.Open();
                return new CpuProbe { Source = s, First = first };
            }, ProbeTimeout, ct, out var timedOut);
            if (timedOut) clockReason = SlotReasons.TimedOut;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { clockReason = SlotReasons.NotSupported; }
        var clockOk = cpu?.Source is not null && cpu.First?.ClockGhz is not null;
        if (clockOk) sources.Add(cpu!.Source!);
        else cpu?.Dispose();

        string? powerReason = null;
        EnergyMeterSource? emi = null;
        try
        {
            emi = Bounded.Run(EnergyMeterSource.TryOpen, ProbeTimeout, ct, out var timedOut);
            if (timedOut) powerReason = SlotReasons.TimedOut;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var s in sources) s.Dispose(); // the clock source opened above is not handed to anyone
            throw;
        }
        catch (Exception) { powerReason = SlotReasons.NotSupported; }
        if (emi is not null) sources.Add(emi);

        var slots = SlotPlanner.Cpu(clockOk, clockOk ? cpu!.First!.MaxNominalMHz : null, clockReason, emi is not null, powerReason);
        return new PartPlan(slots, sources);
    }

    /// <summary>The GPU probe result (owns the opened adapter/device until handed to sources).</summary>
    private sealed class GpuProbe : IDisposable
    {
        public D3dkmtAdapter? Adapter;
        public NvmlDevice? Nvml;
        public GpuCaps Caps = GpuCaps.None;

        public void Dispose()
        {
            Adapter?.Dispose();
            Nvml?.Dispose();
        }
    }

    private static PartPlan PrepareGpu(BenchHardware hardware, CancellationToken ct)
    {
        if (Luids.Parse(hardware.GpuLuid) is not { } luid) return new PartPlan(GpuNormalizer.Plan(GpuCaps.None, SlotReasons.NotSupported), []);

        GpuProbe? probe;
        try
        {
            probe = Bounded.Run(() => ProbeGpu(luid), TimeSpan.FromSeconds(4), ct, out var timedOut);
            if (timedOut) return new PartPlan(GpuNormalizer.Plan(GpuCaps.None, SlotReasons.TimedOut), []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new PartPlan(GpuNormalizer.Plan(GpuCaps.None, SlotReasons.NotSupported), []); }

        var slots = GpuNormalizer.Plan(probe!.Caps);
        var sources = new List<ISensorSource>();
        var d3dSlots = GpuNormalizer.SlotsFor(slots, SlotSources.D3dkmt);
        if (probe.Adapter is not null && d3dSlots.Count > 0) sources.Add(new D3dkmtSource(probe.Adapter, d3dSlots));
        else probe.Adapter?.Dispose();
        if (probe.Nvml is not null)
            sources.Add(new NvmlSource(probe.Nvml, GpuNormalizer.SlotsFor(slots, SlotSources.Nvml), probe.Caps.SlowdownTempC));
        return new PartPlan(slots, sources);
    }

    private static GpuProbe ProbeGpu(long luid)
    {
        var probe = new GpuProbe();
        try
        {
            var dxgi = Dxgi.Adapters().FirstOrDefault(a => a.Luid == luid);
            probe.Adapter = D3dkmtAdapter.Open(luid);
            D3dkmtValues? perf = null;
            uint maxFan = 0;
            (uint Bus, uint Device, uint Function)? address = null;
            if (probe.Adapter is { } a)
            {
                perf = a.Perf() is { } raw ? GpuNormalizer.Convert(raw) : null;
                maxFan = a.MaxFanRpm() ?? 0;
                if (maxFan == 0 && perf is { FanRpm: > 0 }) maxFan = 5000;
                address = a.Address();
            }

            double? maxClock = null, powerLimit = null, slowdown = null;
            bool nClock = false, nTemp = false, nPower = false, nFan = false;
            if (dxgi is { VendorId: GpuNormalizer.VendorNvidia } && address is { } ad && NvmlLibrary.Load() is { } lib)
            {
                probe.Nvml = NvmlDevice.Open(lib, new AdapterPci(ad.Bus, ad.Device, ad.Function, dxgi.VendorId, dxgi.DeviceId));
                if (probe.Nvml is { } n)
                {
                    nClock = n.GraphicsClockMHz() is not null;
                    nTemp = n.TemperatureC() is not null;
                    nPower = n.PowerMilliwatts() is not null;
                    nFan = n.FanPercent() is not null;
                    maxClock = n.MaxGraphicsClockMHz();
                    powerLimit = n.PowerLimitW();
                    slowdown = n.SlowdownTempC();
                }
            }

            probe.Caps = new GpuCaps(
                perf is not null, perf?.TempC is not null, perf?.PowerPercent is not null, perf is null ? 0 : maxFan,
                probe.Nvml is not null, nClock, nTemp, nPower, nFan, maxClock, powerLimit, slowdown);
            return probe;
        }
        catch
        {
            probe.Dispose();
            throw;
        }
    }

    private sealed class DiskProbe
    {
        public double? Temp;
        public string? Reason;
        public int Disk;
    }

    private static PartPlan PrepareDisk(BenchHardware hardware, CancellationToken ct)
    {
        string? reason;
        DiskProbe? p = null;
        try
        {
            p = Bounded.Run(() =>
            {
                if (VolumeDisk.DiskNumber(hardware.DiskVolume) is not { } disk) return new DiskProbe { Reason = SlotReasons.NotSupported };
                var (t, r) = DiskTempSource.Probe(disk);
                return new DiskProbe { Temp = t, Reason = r, Disk = disk };
            }, ProbeTimeout, ct, out var timedOut);
            reason = timedOut ? SlotReasons.TimedOut : p!.Reason;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { reason = SlotReasons.NotSupported; }

        var ok = p?.Temp is not null;
        var slots = SlotPlanner.Disk(hardware.DiskMedia, ok, reason);
        return new PartPlan(slots, ok ? [DiskTempSource.Create(p!.Disk, p.Temp!.Value)] : []);
    }

    public void Dispose() { }
}

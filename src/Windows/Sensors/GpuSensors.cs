using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>D3DKMT_ADAPTER_PERFDATA fields as read (driver units).</summary>
internal sealed record D3dkmtPerfRaw(ulong MemoryFrequencyHz, uint FanRpm, uint PowerDeciPercent, ulong TemperatureDeciC);

/// <summary>D3DKMT perf data in display units. Temperature 0 means "not reported" (null); fan 0 RPM stays 0.</summary>
internal sealed record D3dkmtValues(double? TempC, double FanRpm, double? PowerPercent, double? MemClockMHz);

/// <summary>PCI location of an NVML device (nvmlPciInfo_t) and of the DXGI/D3DKMT adapter.</summary>
internal sealed record NvmlPci(uint Domain, uint Bus, uint Device, uint PciDeviceId, string BusId);
internal sealed record AdapterPci(uint Bus, uint Device, uint Function, uint VendorId, uint DeviceId);

/// <summary>What the GPU probe found for the benchmark adapter (feeds <see cref="GpuPlanner"/>).</summary>
internal sealed record GpuCaps(
    bool D3dkmt, bool D3dTemp, bool D3dPower, uint D3dMaxFanRpm,
    bool Nvml, bool NvmlClock, bool NvmlTemp, bool NvmlPower, bool NvmlFan,
    double? NvmlMaxClockMHz, double? NvmlPowerLimitW, double? SlowdownTempC)
{
    public static readonly GpuCaps None = new(false, false, false, 0, false, false, false, false, false, null, null, null);
}

/// <summary>Pure GPU rules: unit conversion, NVML reason bits, adapter matching, slot planning.</summary>
internal static class GpuNormalizer
{
    public const uint VendorNvidia = 0x10DE, VendorAmd = 0x1002, VendorIntel = 0x8086;

    // nvml.h: nvmlClocksEventReason* (formerly nvmlClocksThrottleReason*).
    public const ulong NvmlClocksEventReasonSwPowerCap = 0x0000000000000004UL;
    public const ulong NvmlClocksEventReasonHwSlowdown = 0x0000000000000008UL;
    public const ulong NvmlClocksEventReasonSwThermalSlowdown = 0x0000000000000020UL;
    public const ulong NvmlClocksEventReasonHwThermalSlowdown = 0x0000000000000040UL;

    public static D3dkmtValues Convert(D3dkmtPerfRaw raw) => new(
        raw.TemperatureDeciC is > 0 and < 2000 ? raw.TemperatureDeciC / 10.0 : null,
        raw.FanRpm,
        raw.PowerDeciPercent is > 0 and < 5000 ? raw.PowerDeciPercent / 10.0 : null,
        raw.MemoryFrequencyHz > 0 ? raw.MemoryFrequencyHz / 1_000_000.0 : null);

    /// <summary>HW_SLOWDOWN alone is not thermal (it includes power-brake events), so it is ignored here.</summary>
    public static GpuThrottle Throttle(ulong reasons, double? slowdownTempC) => new(
        slowdownTempC,
        (reasons & (NvmlClocksEventReasonHwThermalSlowdown | NvmlClocksEventReasonSwThermalSlowdown)) != 0,
        (reasons & NvmlClocksEventReasonSwPowerCap) != 0);

    /// <summary>Function number from an NVML bus id "DDDDDDDD:BB:DD.F" (or legacy "DDDD:BB:DD.F").</summary>
    public static uint? Function(string busId)
    {
        var dot = busId.LastIndexOf('.');
        return dot >= 0 && uint.TryParse(busId.AsSpan(dot + 1), global::System.Globalization.NumberStyles.HexNumber,
            global::System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;
    }

    /// <summary>
    /// The single NVML device at the adapter's PCI bus/device/function whose PCI ids match; null when none or more
    /// than one matches (then NVML is not used).
    /// </summary>
    public static int? Match(AdapterPci adapter, IReadOnlyList<NvmlPci> devices)
    {
        int? hit = null;
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var ids = (d.PciDeviceId & 0xFFFF) == adapter.VendorId && (d.PciDeviceId >> 16) == adapter.DeviceId;
            var fn = Function(d.BusId) ?? 0;
            if (!ids || d.Bus != adapter.Bus || d.Device != adapter.Device || fn != adapter.Function) continue;
            if (hit is not null) return null;
            hit = i;
        }
        return hit;
    }

    private static double Ceil(double v, double step) => Math.Ceiling(v / step) * step;

    /// <summary>Which source fills each GPU slot; null source = unavailable.</summary>
    public static IReadOnlyList<MetricSlot> Plan(GpuCaps c, string? unavailableReason = null)
    {
        if (unavailableReason is not null)
            return
            [
                new("clock", "GHz", SlotSources.None, false, Reason: unavailableReason),
                new("temp", "°C", SlotSources.None, false, Reason: unavailableReason),
                new("power", "W", SlotSources.None, false, Reason: unavailableReason),
                new("fan", "RPM", SlotSources.None, false, Reason: unavailableReason),
            ];

        var clock = c.NvmlClock
            ? new MetricSlot("clock", "GHz", SlotSources.Nvml, true, Max: c.NvmlMaxClockMHz is > 0 and var mx ? Math.Max(1.0, Ceil(mx * 1.1 / 1000.0, 0.5)) : 3.5)
            : new MetricSlot("clock", "GHz", SlotSources.None, false, Reason: SlotReasons.NotSupported);

        var temp = c.NvmlTemp
            ? new MetricSlot("temp", "°C", SlotSources.Nvml, true, Max: 100, Limit: c.SlowdownTempC)
            : c.D3dTemp
                ? new MetricSlot("temp", "°C", SlotSources.D3dkmt, true, Max: 100, Limit: c.SlowdownTempC)
                : new MetricSlot("temp", "°C", SlotSources.None, false, Reason: SlotReasons.NotSupported);

        var power = c.NvmlPower
            ? new MetricSlot("power", "W", SlotSources.Nvml, true, Max: c.NvmlPowerLimitW is > 0 and var lw ? Ceil(lw * 1.1, 50) : 600, Limit: c.NvmlPowerLimitW)
            : c.D3dPower
                ? new MetricSlot("power", "%", SlotSources.D3dkmt, true, Max: 120, Limit: 100)
                : new MetricSlot("power", "W", SlotSources.None, false, Reason: SlotReasons.NotSupported);

        var fan = c.D3dMaxFanRpm > 0
            ? new MetricSlot("fan", "RPM", SlotSources.D3dkmt, true, Max: Ceil(c.D3dMaxFanRpm, 500))
            : c.NvmlFan
                ? new MetricSlot("fan", "%", SlotSources.Nvml, true, Max: 100)
                : new MetricSlot("fan", "RPM", SlotSources.None, false, Reason: SlotReasons.NotSupported);

        return [clock, temp, power, fan];
    }

    /// <summary>The slot ids <paramref name="source"/> fills under <paramref name="slots"/>.</summary>
    public static IReadOnlyList<string> SlotsFor(IReadOnlyList<MetricSlot> slots, string source) =>
        slots.Where(s => s.Available && s.Source == source).Select(s => s.Id).ToList();
}

/// <summary>D3DKMT perf data for one adapter opened by LUID.</summary>
internal sealed unsafe class D3dkmtAdapter : IDisposable
{
    private uint _handle;

    private D3dkmtAdapter(uint handle) => _handle = handle;

    public static D3dkmtAdapter? Open(long luid)
    {
        var open = new Native.D3dkmtOpenAdapterFromLuid { AdapterLuid = luid };
        return Native.D3DKMTOpenAdapterFromLuid(ref open) >= 0 && open.Adapter != 0 ? new D3dkmtAdapter(open.Adapter) : null;
    }

    private bool Query<T>(int type, ref T data) where T : unmanaged
    {
        if (_handle == 0) return false;
        fixed (T* p = &data)
        {
            var q = new Native.D3dkmtQueryAdapterInfo { Adapter = _handle, Type = type, PrivateDriverData = p, PrivateDriverDataSize = (uint)sizeof(T) };
            return Native.D3DKMTQueryAdapterInfo(ref q) >= 0;
        }
    }

    public D3dkmtPerfRaw? Perf()
    {
        var d = new Native.D3dkmtAdapterPerfData();
        return Query(Native.KmtqaitypeAdapterPerfData, ref d) ? new D3dkmtPerfRaw(d.MemoryFrequency, d.FanRpm, d.Power, d.Temperature) : null;
    }

    public uint? MaxFanRpm()
    {
        var c = new Native.D3dkmtAdapterPerfDataCaps();
        return Query(Native.KmtqaitypeAdapterPerfDataCaps, ref c) ? c.MaxFanRpm : null;
    }

    public (uint Bus, uint Device, uint Function)? Address()
    {
        var a = new Native.D3dkmtAdapterAddress();
        return Query(Native.KmtqaitypeAdapterAddress, ref a) ? (a.BusNumber, a.DeviceNumber, a.FunctionNumber) : null;
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0); // one closer, whoever calls
        if (h != 0) Native.D3DKMTCloseAdapter(ref h);
    }
}

/// <summary>GPU slots filled from D3DKMT (temp °C, power % of limit, fan RPM).</summary>
internal sealed class D3dkmtSource(D3dkmtAdapter adapter, IReadOnlyList<string> slots) : ISensorSource
{
    public string Name => "gpu.d3dkmt";
    public IReadOnlyList<string> SlotIds => slots;

    public SourceReading Read()
    {
        var raw = adapter.Perf() ?? throw new InvalidOperationException("d3dkmt perf query failed");
        var v = GpuNormalizer.Convert(raw);
        var values = new Dictionary<string, double?>();
        foreach (var id in slots)
            values[id] = id switch { "temp" => v.TempC, "power" => v.PowerPercent, "fan" => v.FanRpm, _ => null };
        return new SourceReading(values);
    }

    public void Dispose() => adapter.Dispose();
}

/// <summary>GPU slots filled from NVML (clock GHz, temp °C, power W, fan % target) plus throttle reasons.</summary>
internal sealed class NvmlSource(NvmlDevice device, IReadOnlyList<string> slots, double? slowdownC) : ISensorSource
{
    public string Name => "gpu.nvml";
    public IReadOnlyList<string> SlotIds => slots;

    public SourceReading Read()
    {
        var values = new Dictionary<string, double?>();
        foreach (var id in slots)
            values[id] = id switch
            {
                "clock" => device.GraphicsClockMHz() is { } mhz ? mhz / 1000.0 : null,
                "temp" => device.TemperatureC(),
                "power" => device.PowerMilliwatts() is { } mw ? Math.Round(mw / 1000.0, 1) : null,
                "fan" => device.FanPercent(),
                _ => null,
            };
        var reasons = device.ClocksEventReasons();
        return new SourceReading(values, reasons is { } r ? GpuNormalizer.Throttle(r, slowdownC) : new GpuThrottle(slowdownC, false, false));
    }

    public void Dispose() => device.Dispose();
}

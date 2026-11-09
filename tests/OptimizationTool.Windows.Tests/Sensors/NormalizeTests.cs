using OptimizationTool.Engine.Bench;
using OptimizationTool.Windows.Sensors;

namespace OptimizationTool.Windows.Tests.Sensors;

/// <summary>Pure sensor rules with invented values (no machine data).</summary>
public class CpuNormalizeTests
{
    private static PdhItem I(string name, double v, uint status = 0) => new(name, status, v);

    [Theory]
    [InlineData("_Total", true)]
    [InlineData("0,_Total", true)]
    [InlineData("1,_total", true)]
    [InlineData("0,0", false)]
    [InlineData("1,3", false)]
    public void Totals_are_recognised(string name, bool total) => Assert.Equal(total, CpuNormalizer.IsTotal(name));

    [Fact]
    public void Clock_averages_per_instance_and_ignores_totals()
    {
        var raw = new CpuPdhRaw(
            [I("0,0", 100), I("0,1", 50), I("0,_Total", 999), I("_Total", 999)],
            [I("0,0", 3000), I("0,1", 3000), I("0,_Total", 3000), I("_Total", 3000)],
            [I("0,0", 90), I("0,1", 70), I("_Total", 1)]);
        var r = CpuNormalizer.Normalize(raw);
        Assert.Equal(2.25, r.ClockGhz); // (3.0 + 1.5) / 2
        Assert.Equal(80, r.LimitPercent);
        Assert.Equal(3000, r.MaxNominalMHz);
    }

    [Fact]
    public void Hybrid_cores_use_their_own_nominal_frequency()
    {
        // P-cores nominal 3200, E-cores nominal 2400; both at 125% (boost, NOCAP100 lets it exceed 100).
        var raw = new CpuPdhRaw(
            [I("0,0", 125), I("0,1", 125), I("0,2", 125), I("0,3", 125)],
            [I("0,0", 3200), I("0,1", 3200), I("0,2", 2400), I("0,3", 2400)],
            []);
        var r = CpuNormalizer.Normalize(raw);
        Assert.Equal(3.5, r.ClockGhz); // (4.0 + 4.0 + 3.0 + 3.0) / 4
        Assert.Equal(3200, r.MaxNominalMHz);
        Assert.Null(r.LimitPercent);
    }

    [Fact]
    public void Multi_group_instances_are_all_counted()
    {
        var raw = new CpuPdhRaw(
            [I("0,0", 100), I("0,_Total", 100), I("1,3", 150), I("1,_Total", 150), I("_Total", 120)],
            [I("0,0", 2000), I("0,_Total", 2000), I("1,3", 2000), I("1,_Total", 2000), I("_Total", 2000)],
            []);
        Assert.Equal(2.5, CpuNormalizer.Normalize(raw).ClockGhz); // (2.0 + 3.0) / 2
    }

    [Fact]
    public void Invalid_status_or_missing_frequency_is_skipped()
    {
        var raw = new CpuPdhRaw(
            [I("0,0", 100), I("0,1", 100, status: 0xC0000BC6), I("0,2", 100)],
            [I("0,0", 2000), I("0,1", 2000), I("0,2", 0)],
            [I("0,0", double.NaN)]);
        var r = CpuNormalizer.Normalize(raw);
        Assert.Equal(2.0, r.ClockGhz);
        Assert.Null(r.LimitPercent);
    }

    [Fact]
    public void No_instances_gives_null() =>
        Assert.Null(CpuNormalizer.Normalize(new CpuPdhRaw([I("_Total", 100)], [I("_Total", 3000)], [])).ClockGhz);

    [Theory]
    [InlineData(4300.0, 7.0)]  // 6.88 → 7.0
    [InlineData(3000.0, 5.0)]  // 4.8 → 5.0
    [InlineData(null, 6.0)]
    public void Clock_scale_is_fixed_from_nominal(double? nominal, double max) => Assert.Equal(max, CpuNormalizer.ClockMaxGhz(nominal));
}

public class EnergyMeterTests
{
    [Fact]
    public void Package_instance_is_selected()
    {
        var names = new[] { "RAPL_Package0_PKG", "RAPL_Package0_PP0", "RAPL_Package0_Core3_CORE", "RAPL_Package0_DRAM", "_Total" };
        Assert.Equal(["RAPL_Package0_PKG"], EnergyMeterNormalizer.PackageInstances(names));
    }

    [Fact]
    public void Lowercase_and_package_word_are_accepted() =>
        Assert.Equal(2, EnergyMeterNormalizer.PackageInstances(["rapl_package0_pkg", "RAPL_Package1_PACKAGE", "Battery_PKG"]).Count);

    [Fact]
    public void No_package_instance_means_none() =>
        Assert.Empty(EnergyMeterNormalizer.PackageInstances(["_Total", "RAPL_Package0_PP0", "SomeMeter_Package"]));

    [Fact]
    public void Watts_from_milliwatts_summed_over_packages()
    {
        var items = new List<PdhItem> { new("RAPL_Package0_PKG", 0, 45500), new("RAPL_Package1_PKG", 0, 30000), new("RAPL_Package0_PP0", 0, 1) };
        Assert.Equal(75.5, EnergyMeterNormalizer.PackageWatts(items, ["RAPL_Package0_PKG", "RAPL_Package1_PKG"]));
        Assert.Null(EnergyMeterNormalizer.PackageWatts(items, ["RAPL_Package2_PKG"]));
        Assert.Null(EnergyMeterNormalizer.PackageWatts([new("RAPL_Package0_PKG", 0xC0000BBA, 1000)], ["RAPL_Package0_PKG"]));
    }

    [Fact]
    public void Energy_watts_from_picowatt_hours_and_ms()
    {
        // 50 W for 2 s = 100 J = 100 / 3.6e-9 pWh.
        var de = 100 / EnergyMeterNormalizer.JoulesPerPicowattHour;
        Assert.Equal(50, EnergyMeterNormalizer.EnergyWatts(1e15, 1000, 1e15 + de, 3000)!.Value, 6);
        Assert.Null(EnergyMeterNormalizer.EnergyWatts(1e15, 1000, 1e15 + de, 1000)); // meter did not advance in time
        Assert.Null(EnergyMeterNormalizer.EnergyWatts(1e15, 1000, 1e15, 3000));      // no energy
        Assert.Null(EnergyMeterNormalizer.EnergyWatts(2e15, 1000, 1e15, 3000));      // went backwards
    }

    [Theory]
    [InlineData(50.0, 55.0, true)]
    [InlineData(50.0, 40.0, true)]
    [InlineData(50.0, 70.0, false)]
    [InlineData(50.0, 0.05, false)]    // wrong scaling (W reported as mW)
    [InlineData(0.0, 50.0, false)]
    public void Scaling_must_agree_within_25_percent(double energyW, double powerW, bool ok) =>
        Assert.Equal(ok, EnergyMeterNormalizer.ScalingMatches(energyW, powerW));

    private static List<PdhItem> One(string name, double v) => [new(name, 0, v), new("_Total", 0, 0)];

    [Fact]
    public void Package_validates_only_when_power_matches_energy_over_time()
    {
        const string pkg = "RAPL_Package0_PKG";
        var de = 42.0 * 0.7 / EnergyMeterNormalizer.JoulesPerPicowattHour; // 42 W over 700 ms
        var e1 = One(pkg, 5e14);
        var t1 = One(pkg, 10_000);
        var e2 = One(pkg, 5e14 + de);
        var t2 = One(pkg, 10_700);
        Assert.True(EnergyMeterNormalizer.Validates([pkg], e1, t1, e2, t2, One(pkg, 44_000)));
        Assert.False(EnergyMeterNormalizer.Validates([pkg], e1, t1, e2, t2, One(pkg, 44)));        // mis-scaled provider
        Assert.False(EnergyMeterNormalizer.Validates([pkg], e1, t1, e1, t2, One(pkg, 44_000)));    // energy counter frozen
        Assert.False(EnergyMeterNormalizer.Validates([pkg], e1, t1, e2, t2, One("Other_PKG", 44_000))); // power missing
        Assert.False(EnergyMeterNormalizer.Validates([], e1, t1, e2, t2, One(pkg, 44_000)));
    }

    [Fact]
    public void Plausibility_needs_every_sample_between_0_and_1000_W()
    {
        Assert.True(EnergyMeterNormalizer.Plausible([12.5, 80.0]));
        Assert.False(EnergyMeterNormalizer.Plausible([12.5, 0.0]));
        Assert.False(EnergyMeterNormalizer.Plausible([12.5, 1500.0]));
        Assert.False(EnergyMeterNormalizer.Plausible([12.5, null]));
        Assert.False(EnergyMeterNormalizer.Plausible([]));
    }
}

public class GpuNormalizeTests
{
    [Fact]
    public void D3dkmt_units_are_converted()
    {
        var v = GpuNormalizer.Convert(new D3dkmtPerfRaw(MemoryFrequencyHz: 1_250_000_000, FanRpm: 1430, PowerDeciPercent: 875, TemperatureDeciC: 623));
        Assert.Equal(62.3, v.TempC);
        Assert.Equal(87.5, v.PowerPercent);
        Assert.Equal(1430, v.FanRpm);
        Assert.Equal(1250, v.MemClockMHz);
    }

    [Fact]
    public void D3dkmt_zero_temperature_is_not_reported_but_zero_rpm_stays_zero()
    {
        var v = GpuNormalizer.Convert(new D3dkmtPerfRaw(0, 0, 0, 0));
        Assert.Null(v.TempC);
        Assert.Null(v.PowerPercent);
        Assert.Null(v.MemClockMHz);
        Assert.Equal(0, v.FanRpm);
    }

    [Fact]
    public void Nvml_reason_constants_match_the_header()
    {
        Assert.Equal(0x4UL, GpuNormalizer.NvmlClocksEventReasonSwPowerCap);
        Assert.Equal(0x8UL, GpuNormalizer.NvmlClocksEventReasonHwSlowdown);
        Assert.Equal(0x20UL, GpuNormalizer.NvmlClocksEventReasonSwThermalSlowdown);
        Assert.Equal(0x40UL, GpuNormalizer.NvmlClocksEventReasonHwThermalSlowdown);
    }

    [Theory]
    [InlineData(0x0UL, false, false)]
    [InlineData(0x40UL, true, false)]
    [InlineData(0x20UL, true, false)]
    [InlineData(0x4UL, false, true)]
    [InlineData(0x8UL, false, false)]   // HW_SLOWDOWN alone is not thermal
    [InlineData(0x1UL, false, false)]   // idle
    [InlineData(0x64UL, true, true)]    // 0x40 | 0x20 | 0x4
    public void Nvml_reason_bits(ulong reasons, bool thermal, bool powerCap)
    {
        var t = GpuNormalizer.Throttle(reasons, 87);
        Assert.Equal(thermal, t.ThermalSlowdown);
        Assert.Equal(powerCap, t.PowerCap);
        Assert.Equal(87, t.SlowdownTempC);
    }

    private static readonly AdapterPci Adapter = new(Bus: 0x21, Device: 0, Function: 0, VendorId: 0x10DE, DeviceId: 0x2C05);

    [Fact]
    public void Nvml_device_matched_by_bus_and_ids()
    {
        var devices = new List<NvmlPci>
        {
            new(0, 0x05, 0, (0x2204u << 16) | 0x10DE, "00000000:05:00.0"),
            new(0, 0x21, 0, (0x2C05u << 16) | 0x10DE, "00000000:21:00.0"),
        };
        Assert.Equal(1, GpuNormalizer.Match(Adapter, devices));
    }

    [Fact]
    public void Nvml_unmatched_or_ambiguous_is_unused()
    {
        Assert.Null(GpuNormalizer.Match(Adapter, [new(0, 0x21, 0, (0x2C05u << 16) | 0x10DE, "00000000:21:00.1")])); // other function
        Assert.Null(GpuNormalizer.Match(Adapter, [new(0, 0x21, 0, (0x1111u << 16) | 0x10DE, "00000000:21:00.0")])); // other device id
        Assert.Null(GpuNormalizer.Match(Adapter, [new(0, 0x22, 0, (0x2C05u << 16) | 0x10DE, "00000000:22:00.0")])); // other bus
        Assert.Null(GpuNormalizer.Match(Adapter, []));
        var twin = new NvmlPci(0, 0x21, 0, (0x2C05u << 16) | 0x10DE, "00000000:21:00.0");
        Assert.Null(GpuNormalizer.Match(Adapter, [twin, twin]));
    }

    [Theory]
    [InlineData("00000000:21:00.0", 0u)]
    [InlineData("0000:01:00.1", 1u)]
    public void Bus_id_function(string busId, uint fn) => Assert.Equal(fn, GpuNormalizer.Function(busId));

    private static MetricSlot Slot(IReadOnlyList<MetricSlot> s, string id) => s.Single(x => x.Id == id);

    [Fact]
    public void Nvidia_with_nvml()
    {
        var caps = new GpuCaps(D3dkmt: true, D3dTemp: true, D3dPower: true, D3dMaxFanRpm: 0,
            Nvml: true, NvmlClock: true, NvmlTemp: true, NvmlPower: true, NvmlFan: true,
            NvmlMaxClockMHz: 3090, NvmlPowerLimitW: 300, SlowdownTempC: 90);
        var s = GpuNormalizer.Plan(caps);
        Assert.Equal(["clock", "temp", "power", "fan"], s.Select(x => x.Id));
        Assert.All(s, x => Assert.True(x.Available));
        Assert.Equal(("nvml", "GHz", 3.5), (Slot(s, "clock").Source, Slot(s, "clock").Unit, Slot(s, "clock").Max!.Value));
        Assert.Equal(("nvml", 90.0), (Slot(s, "temp").Source, Slot(s, "temp").Limit!.Value));
        Assert.Equal(("nvml", "W", 350.0, 300.0), (Slot(s, "power").Source, Slot(s, "power").Unit, Slot(s, "power").Max!.Value, Slot(s, "power").Limit!.Value));
        Assert.Equal(("nvml", "%"), (Slot(s, "fan").Source, Slot(s, "fan").Unit));
    }

    [Fact]
    public void Nvidia_fan_rpm_from_d3dkmt_when_capable()
    {
        var caps = new GpuCaps(true, true, true, 3300, true, true, true, true, true, 2800, 250, 88);
        var fan = Slot(GpuNormalizer.Plan(caps), "fan");
        Assert.Equal(("d3dkmt", "RPM", 3500.0), (fan.Source, fan.Unit, fan.Max!.Value));
        Assert.Equal(["fan"], GpuNormalizer.SlotsFor(GpuNormalizer.Plan(caps), "d3dkmt"));
        Assert.Equal(["clock", "temp", "power"], GpuNormalizer.SlotsFor(GpuNormalizer.Plan(caps), "nvml"));
    }

    [Fact]
    public void Amd_uses_d3dkmt_power_percent_and_no_clock()
    {
        var caps = GpuCaps.None with { D3dkmt = true, D3dTemp = true, D3dPower = true, D3dMaxFanRpm = 3200 };
        var s = GpuNormalizer.Plan(caps);
        Assert.Equal((false, "not_supported"), (Slot(s, "clock").Available, Slot(s, "clock").Reason));
        Assert.Equal(("d3dkmt", "°C"), (Slot(s, "temp").Source, Slot(s, "temp").Unit));
        Assert.Null(Slot(s, "temp").Limit);
        Assert.Equal(("d3dkmt", "%", 100.0), (Slot(s, "power").Source, Slot(s, "power").Unit, Slot(s, "power").Limit!.Value));
        Assert.Equal(("d3dkmt", "RPM"), (Slot(s, "fan").Source, Slot(s, "fan").Unit));
    }

    [Fact]
    public void Intel_igpu_without_perf_data_is_all_unavailable()
    {
        var s = GpuNormalizer.Plan(GpuCaps.None with { D3dkmt = true });
        Assert.All(s, x => Assert.Equal((false, "not_supported", "none"), (x.Available, x.Reason, x.Source)));
        Assert.Equal(4, s.Count);
    }

    [Fact]
    public void No_gpu_or_timed_out_probe()
    {
        Assert.All(GpuNormalizer.Plan(GpuCaps.None, "not_supported"), x => Assert.Equal("not_supported", x.Reason));
        Assert.All(GpuNormalizer.Plan(GpuCaps.None, "timed_out"), x => Assert.Equal((false, "timed_out"), (x.Available, x.Reason)));
    }

    [Fact]
    public void Luid_round_trip()
    {
        var hex = Luids.Format(0x0000000100002A3FL);
        Assert.Equal("0000000100002A3F", hex);
        Assert.Equal(0x0000000100002A3FL, Luids.Parse(hex));
        Assert.Equal(0x0000000100002A3FL, Luids.Parse("0000000100002a3f"));
        Assert.Null(Luids.Parse("123"));
        Assert.Null(Luids.Parse(null));
    }
}

public class PowerNormalizeTests
{
    [Theory]
    [InlineData(1, 128, "online", "absent", false)]
    [InlineData(0, 8, "offline", "present", true)]
    [InlineData(1, 1, "online", "present", false)]
    [InlineData(255, 255, "unknown", "unknown", false)]
    [InlineData(255, 2, "unknown", "present", true)]
    [InlineData(255, 128, "unknown", "absent", false)]
    [InlineData(7, 0, "unknown", "present", true)]
    public void Status_mapping(byte ac, byte battery, string expectedAc, string expectedBattery, bool blocks)
    {
        var s = PowerNormalizer.FromStatus(ac, battery);
        Assert.Equal((expectedAc, expectedBattery), (s.Ac, s.Battery));
        Assert.Equal(blocks, s.BlocksBenchmark);
    }

    [Theory]
    [InlineData(0u, "online")]
    [InlineData(1u, "offline")]
    [InlineData(2u, "offline")]
    [InlineData(9u, "unknown")]
    public void Acdc_setting(uint value, string ac) => Assert.Equal(ac, PowerNormalizer.AcFromSetting(value));
}

public class SlotPlannerTests
{
    [Fact]
    public void Cpu_slots()
    {
        var s = SlotPlanner.Cpu(true, 4300, null, false, null);
        Assert.Equal(["clock", "temp", "power", "fan"], s.Select(x => x.Id));
        Assert.Equal(("pdh", "GHz", 7.0, true), (s[0].Source, s[0].Unit, s[0].Max!.Value, s[0].Available));
        Assert.Equal("needs_advanced_sensors", s[1].Reason);
        Assert.Equal((false, "not_supported"), (s[2].Available, s[2].Reason));
        Assert.Equal("needs_advanced_sensors", s[3].Reason);

        var withPower = SlotPlanner.Cpu(false, null, "timed_out", true, null);
        Assert.Equal((false, "timed_out"), (withPower[0].Available, withPower[0].Reason));
        Assert.Equal(("emi", "W", true), (withPower[2].Source, withPower[2].Unit, withPower[2].Available));
    }

    [Fact]
    public void Ram_slots_come_from_the_worker()
    {
        var s = SlotPlanner.Ram();
        Assert.Equal(["read", "write", "copy", "latency"], s.Select(x => x.Id));
        Assert.All(s, x => Assert.Equal(("worker", true), (x.Source, x.Available)));
        Assert.Equal("ns", s[3].Unit);
    }

    [Fact]
    public void Disk_slots()
    {
        var s = SlotPlanner.Disk("nvme", false, "needs_admin");
        Assert.Equal(["read", "write", "random4k", "temp"], s.Select(x => x.Id));
        Assert.All(s.Take(3), x => Assert.Equal(("worker", true, "MB/s"), (x.Source, x.Available, x.Unit)));
        Assert.Equal((false, "needs_admin"), (s[3].Available, s[3].Reason));
        Assert.Equal(("wmi", true), (SlotPlanner.Disk("hdd", true, null)[3].Source, SlotPlanner.Disk("hdd", true, null)[3].Available));
    }

    [Theory]
    [InlineData(4L, 11L, "ssd")]
    [InlineData(4L, 17L, "nvme")]
    [InlineData(0L, 17L, "nvme")]
    [InlineData(3L, 11L, "hdd")]
    [InlineData(0L, 7L, "unknown")]
    [InlineData(null, null, "unknown")]
    public void Disk_media(long? media, long? bus, string expected) => Assert.Equal(expected, DiskNormalizer.Media(media, bus));
}

public class HardwareNormalizeTests
{
    [Theory]
    [InlineData(3200L, 26L, 3200)]   // DDR4 MT/s
    [InlineData(1600L, 26L, 1600)]   // DDR4-1600 stays
    [InlineData(1200L, 26L, 2400)]   // DDR4 reported in MHz
    [InlineData(2400L, 34L, 4800)]   // DDR5 reported in MHz
    [InlineData(6000L, 34L, 6000)]
    [InlineData(667L, 24L, 1334)]    // DDR3 MHz
    [InlineData(500L, 26L, 500)]     // too low to be half of a DDR4 rate: left as is
    [InlineData(2133L, 0L, 2133)]    // unknown type: as is
    public void Memory_speed_in_mts(long value, long type, int expected) => Assert.Equal(expected, HardwareNormalizer.MtPerSecond(value, type));

    [Fact]
    public void Memory_speed_missing() => Assert.Null(HardwareNormalizer.MtPerSecond(0, 26));

    [Fact]
    public void Build_with_nothing_read_never_throws()
    {
        var hw = HardwareNormalizer.Build(new HardwareRaw());
        Assert.Equal("unknown", hw.CpuModel);
        Assert.Equal(1, hw.LogicalProcessors);
        Assert.Null(hw.GpuModel);
        Assert.Null(hw.GpuLuid);
        Assert.Equal("unknown", hw.DiskVolume);
        Assert.Equal("unknown", hw.DiskMedia);
        Assert.Equal("unknown", hw.WindowsBuild);
        Assert.Null(hw.RamConfiguredMTs);
    }

    [Fact]
    public void Build_combines_modules_and_prefers_wmi()
    {
        var raw = new HardwareRaw
        {
            CpuNameWmi = "  Example   CPU 9000  ", CpuNameRegistry = "Registry CPU", NominalMHzWmi = 3600, NominalMHzRegistry = 3599,
            LogicalProcessors = 16,
            Gpu = new DxgiAdapterInfo("Example GPU", 0x1002, 0x1234, 0x0000000000001234L, false), GpuDriverVersion = "31.0.1",
            Modules = [new(16L << 30, 4800, 2400, 34), new(16L << 30, 4800, 4800, 34)],
            Volume = "C:", FreeBytes = 100, DiskModel = "Example NVMe", DiskSizeBytes = 1000, DiskMediaType = 4, DiskBusType = 17,
            AvailPhysBytes = 5, WindowsBuild = "10.0.26100",
        };
        var hw = HardwareNormalizer.Build(raw);
        Assert.Equal("Example CPU 9000", hw.CpuModel);
        Assert.Equal(3600, hw.NominalMHz);
        Assert.Equal("0000000000001234", hw.GpuLuid);
        Assert.Equal(32L << 30, hw.RamBytes);
        Assert.Equal(2, hw.RamModules);
        Assert.Equal(4800, hw.RamConfiguredMTs);
        Assert.Equal(4800, hw.RamRatedMTs);
        Assert.Equal("nvme", hw.DiskMedia);
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData("d:", "D:")]
    [InlineData(@"C:\mnt\data\", null)]
    [InlineData(@"\\server\share\", null)]
    [InlineData(null, null)]
    public void Volume_letter(string? root, string? expected) => Assert.Equal(expected, VolumeDisk.Letter(root));

    [Fact]
    public void Disk_model_never_uses_the_user_editable_friendly_name()
    {
        Assert.Equal("Example NVMe 1TB", HardwareNormalizer.DiskModelFrom(new Dictionary<string, object?> { ["Model"] = " Example  NVMe 1TB ", ["FriendlyName"] = "Someone's workstation" }));
        Assert.Null(HardwareNormalizer.DiskModelFrom(new Dictionary<string, object?> { ["Model"] = null, ["FriendlyName"] = "Someone's workstation" }));
        Assert.Null(HardwareNormalizer.DiskModelFrom(new Dictionary<string, object?> { ["Model"] = "   ", ["FriendlyName"] = "Someone's workstation" }));
        Assert.Null(HardwareNormalizer.DiskModelFrom(new Dictionary<string, object?> { ["FriendlyName"] = "Someone's workstation" }));
    }
}

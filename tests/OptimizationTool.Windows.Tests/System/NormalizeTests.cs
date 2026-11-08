using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.SystemDomain;
using OptimizationTool.Windows.SystemDomain.Rules;

namespace OptimizationTool.Windows.Tests.SystemDomain;

public class OsNormalizeTests
{
    private static readonly CollectorOptions O = new();

    [Fact]
    public void Build_22000_or_later_is_windows_11_even_if_product_name_says_10()
    {
        var r = new OsCollector(O).Normalize(new OsRaw("Windows 10 Pro", "Professional", "24H2", "26100", 1234, "Client", "x64", 3L * 86400_000, true));
        Assert.Equal("complete", r.Coverage.Status);
        var os = r.Facts.Single(f => f.Subject == "system.os");
        Assert.Equal("Windows 11", os.Value["family"]!.GetValue<string>());
        Assert.StartsWith("Windows 11 Pro 24H2, build 26100.1234", os.Display);
        var up = r.Facts.Single(f => f.Subject == "system.uptime");
        Assert.Equal(3 * 86400, up.Value["uptimeSeconds"]!.GetValue<long>());
        Assert.Equal(["system.os", "system.uptime"], r.Facts.Select(f => f.Subject));
    }

    [Fact]
    public void Unreadable_registry_is_partial_with_uptime_only()
    {
        var r = new OsCollector(O).Normalize(new OsRaw(null, null, null, null, null, null, "arm64", 1000, false));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Equal("system.uptime", Assert.Single(r.Facts).Subject);
    }
}

public class HardwareNormalizeTests
{
    private static readonly CollectorOptions O = new();
    private static readonly CpuRaw Cpu = new("Example CPU 9000", "GenuineExample", 3000, 1, 12, 4, 8, 16);
    private static readonly FirmwareRaw Fw = new("Example Firmware Inc.", "F.10", "2025-03-04", "Example PCs", "Model 15", null);

    [Fact]
    public void Full_read_is_complete_and_sorted()
    {
        var gpus = new List<GpuRaw> { new("Example Graphics 100", 0x1234, 0x5678, 4UL << 30, 8UL << 30, "31.0.1.2", "2025-01-02", "Example") };
        var r = new HardwareCollector(O).Normalize(new HardwareRaw(Cpu, new MemoryRaw(16UL << 30, 15UL << 30), gpus, null, Fw, []));
        Assert.Equal("complete", r.Coverage.Status);
        Assert.Equal(r.Facts.Select(f => f.Subject).OrderBy(s => s, StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
        Assert.Contains(r.Facts, f => f.Subject.StartsWith("system.gpu.", StringComparison.Ordinal) && f.Subject.Length == "system.gpu.".Length + 8);
        Assert.Equal("16 GiB of memory installed.", r.Facts.Single(f => f.Subject == "system.memory").Display);
        Assert.DoesNotContain(r.Facts, f => f.Value.ToJsonString().Contains("serial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gpu_failure_is_partial()
    {
        var r = new HardwareCollector(O).Normalize(new HardwareRaw(Cpu, new MemoryRaw(8UL << 30, null), null, "COMException", Fw, []));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Contains("gpu", r.Coverage.Reason);
    }

    [Theory]
    [InlineData("6-21-2006", "2006-06-21")]
    [InlineData("03/04/2025", "2025-03-04")]
    [InlineData("garbage", null)]
    public void Registry_dates_normalize(string input, string? expected) => Assert.Equal(expected, HardwareCollector.NormalizeDate(input));
}

public class PowerNormalizeTests
{
    private static readonly CollectorOptions O = new();
    private static readonly Guid Custom = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Custom_copy_of_high_performance_is_classified_by_personality_and_rule_fires()
    {
        var raw = new PowerRaw(
            [new(PowerCollector.Balanced, "Balanced", 2), new(Custom, "My plan", 1)], Custom,
            new PowerStatusRaw(0, 1, 63, 0), true, Guid.Empty, []);
        var r = new PowerCollector(O).Normalize(raw);
        Assert.Equal("complete", r.Coverage.Status);
        var active = r.Facts.Single(f => f.Subject == "power.active_scheme");
        Assert.Equal("high_performance", active.Value["personality"]!.GetValue<string>());
        Assert.Equal("battery", r.Facts.Single(f => f.Subject == "power.source").Value["source"]!.GetValue<string>());
        Assert.Equal("balanced", r.Facts.Single(f => f.Subject == "power.mode").Value["mode"]!.GetValue<string>());

        var rule = new HighPerfOnBatteryRule().Evaluate(Kit.FromFacts("win.system.power", r));
        Assert.Equal("finding", rule.Status);
    }

    [Fact]
    public void Desktop_without_battery_and_no_overlay()
    {
        var raw = new PowerRaw([new(PowerCollector.Balanced, "Balanced", 2)], PowerCollector.Balanced,
            new PowerStatusRaw(1, 128, 255, 0), false, null, []);
        var r = new PowerCollector(O).Normalize(raw);
        Assert.Equal("complete", r.Coverage.Status);
        var src = r.Facts.Single(f => f.Subject == "power.source");
        Assert.False(src.Value["hasBattery"]!.GetValue<bool>());
        Assert.Null(src.Value["batteryPercent"]);
        Assert.Equal("unavailable", r.Facts.Single(f => f.Subject == "power.mode").Quality);
        Assert.Equal("inapplicable", new HighPerfOnBatteryRule().Evaluate(Kit.FromFacts("win.system.power", r)).Status);
    }

    [Fact]
    public void Scheme_enumeration_failure_is_partial()
    {
        var r = new PowerCollector(O).Normalize(new PowerRaw(null, PowerCollector.Balanced, new PowerStatusRaw(1, 1, 90, 0), true, Guid.Empty, ["schemes"]));
        Assert.Equal("partial", r.Coverage.Status);
    }
}

public class VolumesNormalizeTests
{
    private static readonly CollectorOptions O = new();

    [Fact]
    public void Volumes_get_letter_subjects_media_and_partial_for_locked()
    {
        var raw = new VolumesRaw(
        [
            new('D', true, 1000L << 30, 400L << 30, "NTFS", false, true, 11),
            new('C', true, 500L << 30, 11L << 30, "NTFS", true, false, 17),
            new('E', false, 0, 0, null, false, null, null),
        ]);
        var r = new VolumesCollector(O).Normalize(raw);
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Equal(["volume.c", "volume.d"], r.Facts.Select(f => f.Subject));
        Assert.Equal("ssd", r.Facts[0].Value["mediaType"]!.GetValue<string>());
        Assert.Equal("hdd", r.Facts[1].Value["mediaType"]!.GetValue<string>());
        Assert.Equal("The system drive has 11 GiB free out of 500 GiB.", r.Facts[0].Display);
        Assert.Equal("medium", Assert.Single(new SystemVolumeLowRule().Evaluate(Kit.FromFacts("win.system.volumes", r)).Findings).Severity);
    }
}

public class PerfNormalizeTests
{
    private const long Tick = 10_000_000; // 1 s in 100 ns units

    /// <summary>
    /// Synthetic 4-CPU machine, 2 s interval: each interval adds 8 s of total CPU time across processors.
    /// worker.exe burns 3.2 s per interval (40%); helper.exe exits after snapshot 2; a PID is reused at snapshot 3.
    /// </summary>
    private static PerfRaw Raw(bool stoppedEarly = false, string? diskError = null)
    {
        var snaps = new List<PerfSnapshot>();
        for (var i = 0; i < 5; i++)
        {
            var procs = new Dictionary<ProcKey, ProcSample>
            {
                [new(0, 0)] = new("Idle", i * 4 * Tick, 8192),
                [new(100, 5000)] = new("worker.exe", i * 32 * Tick / 10, 300UL << 20),
                [new(200, 6000)] = new("editor.exe", i * 4 * Tick / 10, 900UL << 20),
                [new(999, 1)] = new("OptimizationTool.Host.exe", i * 1 * Tick / 10, 80UL << 20),
            };
            if (i <= 2) procs[new(300, 7000)] = new("helper.exe", i * 8 * Tick / 10, 50UL << 20);
            if (i >= 3) procs[new(300, 9000)] = new("helper.exe", (i - 3) * 2 * Tick / 10, 40UL << 20); // reused PID
            snaps.Add(new PerfSnapshot(i * 2.0, SysIdle: i * 4 * Tick, SysKernel: i * 6 * Tick, SysUser: i * 2 * Tick,
                AvailPhysBytes: (ulong)(i == 0 ? 5 : 1) << 30, TotalPhysBytes: 16UL << 30, CommitTotalBytes: 20UL << 30, CommitLimitBytes: 22UL << 30,
                DiskIdlePercent: i == 0 ? null : 80, DiskBytesPerSec: i == 0 ? null : 1_000_000, DiskSecPerTransfer: i == 0 ? null : 0.002,
                ForegroundPid: 200, Processes: procs));
        }
        return new PerfRaw(snaps, 8, 2, 4, 999, stoppedEarly, diskError, null, 12.5, 20);
    }

    [Fact]
    public void Deltas_keys_and_top_lists()
    {
        var r = PerfNormalizer.Normalize(Raw());
        Assert.Equal("complete", r.Coverage.Status);
        Assert.Equal(r.Facts.Select(f => f.Subject).OrderBy(s => s, StringComparer.Ordinal), r.Facts.Select(f => f.Subject));

        var cpu = r.Facts.Single(f => f.Subject == "perf.cpu");
        Assert.Equal(50, cpu.Value["avgPercent"]!.GetValue<double>()); // (8 total - 4 idle) / 8
        Assert.Equal(4, cpu.Value["sampleCount"]!.GetValue<int>());
        Assert.Equal("PT2S", cpu.Interval);

        var worker = r.Facts.Single(f => f.Value["image"]?.GetValue<string>() == "worker.exe");
        Assert.Equal(40, worker.Value["cpuAvgPercent"]!.GetValue<double>());
        Assert.Equal(1, worker.Value["cpuRank"]!.GetValue<int>());
        Assert.Equal(4, worker.Value["validIntervals"]!.GetValue<int>());
        Assert.DoesNotContain("\\", worker.Display);

        // helper.exe: two instances (PID reuse), each with only its own valid intervals.
        var helpers = r.Facts.Where(f => f.Value["image"]?.GetValue<string>() == "helper.exe").ToList();
        Assert.Equal(2, helpers.Count);
        Assert.Contains(helpers, h => h.Subject.EndsWith(".2", StringComparison.Ordinal));
        Assert.Equal(3, helpers.Sum(h => h.Value["validIntervals"]!.GetValue<int>()));

        Assert.DoesNotContain(r.Facts, f => f.Value["image"]?.GetValue<string>() == "Idle");

        var sample = r.Facts.Single(f => f.Subject == "perf.sample");
        Assert.True(sample.Value["skippable"]!.GetValue<bool>());
        Assert.Equal(4, sample.Value["intervals"]!.GetValue<int>());
        Assert.True(sample.Value["thisToolTopConsumer"]!.GetValue<bool>());
        Assert.False(sample.Value["defenderTopConsumer"]!.GetValue<bool>());
        Assert.True(sample.Value["processIntervalsInvalidated"]!.GetValue<int>() >= 2);

        var disk = r.Facts.Single(f => f.Subject == "perf.disk");
        Assert.Equal(20, disk.Value["busyAvgPercent"]!.GetValue<double>());
        Assert.Equal(2, disk.Value["latencyAvgMs"]!.GetValue<double>());

        var mem = r.Facts.Single(f => f.Subject == "perf.memory");
        Assert.Equal(5, mem.Value["sampleCount"]!.GetValue<int>());
    }

    [Fact]
    public void Rules_on_normalized_sample()
    {
        var ctx = Kit.FromFacts("win.perf.sample", PerfNormalizer.Normalize(Raw()));
        var cpu = new SustainedCpuRule().Evaluate(ctx);
        var f = Assert.Single(cpu.Findings); // worker.exe; editor.exe is foreground and the tool is excluded
        Assert.Contains("worker.exe", f.Card.Text);
        Assert.Contains("4 of 4 samples", f.Card.Text);

        var mem = new MemoryPressureRule().Evaluate(ctx); // 1/16 available in 4 of 5 samples, commit 91%
        Assert.Equal("finding", mem.Status);
        Assert.Contains("editor.exe", Assert.Single(mem.Findings).Card.Text);
    }

    [Fact]
    public void Stopped_early_or_missing_disk_is_partial()
    {
        var r = PerfNormalizer.Normalize(Raw(stoppedEarly: true));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Contains("stopped early", r.Coverage.Reason);
        Assert.Equal("partial", PerfNormalizer.Normalize(Raw(diskError: "pdh 0xc0000bb8")).Coverage.Status);
        Assert.DoesNotContain(PerfNormalizer.Normalize(Raw(diskError: "x")).Facts, f => f.Subject == "perf.disk");
    }

    [Fact]
    public void Zero_snapshots_is_partial_with_sample_record()
    {
        var r = PerfNormalizer.Normalize(new PerfRaw([], 60, 2, 8, 1, true, null, null, 0, 0));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Equal("perf.sample", Assert.Single(r.Facts).Subject);
        Assert.Equal("insufficient", new SustainedCpuRule().Evaluate(Kit.FromFacts("win.perf.sample", r)).Status);
    }
}

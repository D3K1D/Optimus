using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Tests;

/// <summary>M6a pure rules: scores, comparability inputs, verdicts from synthetic telemetry, thermal stop, lease, wire, AI text.</summary>
public class BenchUnitTests
{
    // ---------------------------------------------------------------- score

    [Fact]
    public void Score_ReferenceIs1000PerPart_And10000Overall()
    {
        foreach (var part in BenchParts.RunOrder)
            Assert.Equal(1000, BenchScoring.PartScore(part, BenchScoring.References[part])!.Value, 6);
        var parts = BenchParts.RunOrder.ToDictionary(p => p, _ => 1000.0);
        var (score, partial) = BenchScoring.Overall(parts);
        Assert.Equal(10000, score);
        Assert.False(partial);
    }

    [Fact]
    public void Score_UsesGeometricMeans_LatencyLowerIsBetter_AndPartialWhenAPartIsMissing()
    {
        // CPU: single at 2×, multi at ½ → geometric mean 1000.
        var cpuRef = BenchScoring.References[BenchParts.Cpu];
        Assert.Equal(1000, BenchScoring.PartScore(BenchParts.Cpu, new Dictionary<string, double> { ["single"] = cpuRef["single"] * 2, ["multi"] = cpuRef["multi"] / 2 })!.Value, 6);
        var latencyRef = BenchScoring.References[BenchParts.Ram]["latency"];
        Assert.Equal(2000, BenchScoring.SubtestScore(BenchParts.Ram, "latency", latencyRef / 2)!.Value, 6); // half the latency → twice the score
        Assert.Null(BenchScoring.PartScore(BenchParts.Cpu, new Dictionary<string, double> { ["single"] = cpuRef["single"] })); // missing subtest
        Assert.Null(BenchScoring.SubtestScore(BenchParts.Cpu, "single", 0));

        var (score, partial) = BenchScoring.Overall(new Dictionary<string, double> { ["cpu"] = 4000, ["ram"] = 1000 });
        Assert.Equal(20000, score); // sqrt(4000 × 1000) × 10
        Assert.True(partial);
        Assert.Equal((null, true), BenchScoring.Overall(new Dictionary<string, double>()));
    }

    [Fact]
    public void ConfigId_ChangesWithSetupAndOptimusBuild()
    {
        var hw = FakeBenchHardware.Default;
        var a = BenchScoring.ConfigId(hw, "0.8.0");
        Assert.Matches("^cfg-[0-9a-f]{16}$", a);
        Assert.Equal(a, BenchScoring.ConfigId(hw, "0.8.0"));
        Assert.NotEqual(a, BenchScoring.ConfigId(hw, "0.8.1"));
        Assert.NotEqual(a, BenchScoring.ConfigId(hw with { GpuDriverVersion = "32.0.15.7000" }, "0.8.0"));
        Assert.NotEqual(a, BenchScoring.ConfigId(hw with { RamModules = 4 }, "0.8.0"));
        Assert.Equal(a, BenchScoring.ConfigId(hw with { DiskFreeBytes = 1 }, "0.8.0")); // free space is not the setup
    }

    // ---------------------------------------------------------------- verdicts

    private static SensorSample Gpu(double temp, bool thermal = false, bool powerCap = false, double clock = 2.4) =>
        new(new Dictionary<string, double?> { ["temp"] = temp, ["clock"] = clock }, new GpuThrottle(90, thermal, powerCap), null);

    private static PartTelemetry Telemetry(string part, params SensorSample[] samples)
    {
        var t = new PartTelemetry(part);
        t.Samples.AddRange(samples);
        return t;
    }

    private static VerdictInput Input(string part, PartTelemetry t, double? score = 1000, string state = "done", IReadOnlyList<double>? history = null,
        RepeatOutcome repeat = RepeatOutcome.NotNeeded, BenchHardware? hw = null, Dictionary<string, double>? subtests = null) =>
        new(part, state, state == "done" ? null : "error", score, subtests ?? new Dictionary<string, double>(), t, history ?? [], repeat, hw ?? FakeBenchHardware.Default);

    [Fact]
    public void RunningHot_OnlyOnSpecificThermalEvidence()
    {
        var stopped = Telemetry("gpu", Gpu(88), Gpu(88));
        stopped.ThermalStop = true;
        var v = BenchVerdicts.Decide(Input("gpu", stopped, score: null, state: "cancelled"));
        Assert.Equal("running_hot", v.Verdict);
        Assert.Equal(new[] { "thermal_stop" }, v.Why);

        // NVML thermal slowdown for 5 s → running hot, whatever the score.
        var slow = Telemetry("gpu", Enumerable.Range(0, 5).Select(_ => Gpu(80, thermal: true)).ToArray());
        Assert.Equal("running_hot", BenchVerdicts.Decide(Input("gpu", slow, score: 1500)).Verdict);

        // 4 s is brief: an observation only.
        var brief = Telemetry("gpu", [.. Enumerable.Range(0, 4).Select(_ => Gpu(80, thermal: true)), Gpu(80)]);
        var b = BenchVerdicts.Decide(Input("gpu", brief));
        Assert.Equal("no_issue", b.Verdict);
        Assert.Contains(b.Observations, o => o.Str("id") == "thermal_slowdown_brief");

        // A power cap (HW_SLOWDOWN-like) never makes running_hot; it is power_limited.
        var capped = Telemetry("gpu", Enumerable.Range(0, 6).Select(_ => Gpu(70, powerCap: true)).ToArray());
        var c = BenchVerdicts.Decide(Input("gpu", capped));
        Assert.Equal("no_issue", c.Verdict);
        Assert.Contains(c.Observations, o => o.Str("id") == "power_limited");

        // No sensor: never running hot (CPU has no temperature in M6a).
        Assert.Equal("no_issue", BenchVerdicts.Decide(Input("cpu", Telemetry("cpu"))).Verdict);
    }

    [Fact]
    public void Unmeasured_ForSkippedFailedOrCancelled()
    {
        var v = BenchVerdicts.Decide(new VerdictInput("disk", "failed", "too_slow", null, new Dictionary<string, double>(), Telemetry("disk"), [], RepeatOutcome.NotNeeded, null));
        Assert.Equal("unmeasured", v.Verdict);
        Assert.Equal("too_slow", v.Reason);
    }

    [Fact]
    public void SlowerThanBefore_NeedsConfirmation_ElseUnconfirmedObservation()
    {
        IReadOnlyList<double> history = [1000, 980, 1020];
        Assert.True(BenchVerdicts.IsRegressed(800, history));   // < 85% of 1000
        Assert.False(BenchVerdicts.IsRegressed(860, history));
        Assert.False(BenchVerdicts.IsRegressed(100, []));       // no history, no regression

        var confirmed = BenchVerdicts.Decide(Input("cpu", Telemetry("cpu"), score: 800, history: history, repeat: RepeatOutcome.Confirmed));
        Assert.Equal("slower_than_before", confirmed.Verdict);
        foreach (var outcome in new[] { RepeatOutcome.Unconfirmed, RepeatOutcome.NotRepeatable, RepeatOutcome.NotNeeded })
        {
            var v = BenchVerdicts.Decide(Input("cpu", Telemetry("cpu"), score: 800, history: history, repeat: outcome));
            Assert.Equal("no_issue", v.Verdict);
            Assert.Contains(v.Observations, o => o.Str("id") == "lower_than_before_unconfirmed");
        }
        Assert.DoesNotContain(BenchVerdicts.Decide(Input("cpu", Telemetry("cpu"), score: 950, history: history)).Observations,
            o => o.Str("id") == "lower_than_before_unconfirmed");
    }

    [Fact]
    public void RegressedSubtest_AndRepeatConfirmation_AreDirectionAware()
    {
        var history = new List<IReadOnlyDictionary<string, double>>
        {
            new Dictionary<string, double> { ["read"] = 40, ["write"] = 30, ["copy"] = 30, ["latency"] = 80 },
            new Dictionary<string, double> { ["read"] = 40, ["write"] = 30, ["copy"] = 30, ["latency"] = 80 },
        };
        var now = new Dictionary<string, double> { ["read"] = 38, ["write"] = 29, ["copy"] = 29, ["latency"] = 160 };
        Assert.Equal("latency", BenchVerdicts.RegressedSubtest("ram", now, history));
        Assert.True(BenchVerdicts.RepeatConfirms("ram", "latency", 150, [80, 80]));
        Assert.False(BenchVerdicts.RepeatConfirms("ram", "latency", 85, [80, 80]));
        Assert.True(BenchVerdicts.RepeatConfirms("cpu", "multi", 1000, [2000]));
        Assert.False(BenchVerdicts.RepeatConfirms("cpu", "multi", 1900, [2000]));
    }

    [Fact]
    public void Observations_AreNeutral()
    {
        // CPU performance limit below 95% for ≥ 3 samples → power_limited with the lowest value.
        var cpu = Telemetry("cpu", [.. Enumerable.Range(0, 4).Select(_ => new SensorSample(new Dictionary<string, double?> { ["clock"] = 4.0 }, null, 82))]);
        var o = BenchVerdicts.Observations(Input("cpu", cpu));
        var limited = Assert.Single(o, x => x.Str("id") == "power_limited");
        Assert.Equal(82, (double)limited["value"]!);

        // Clock drop after boost: early peak 5 GHz, late average 4 GHz.
        var clocks = Telemetry("cpu", [.. new[] { 5.0, 5.0, 4.9, 4.5, 4.2, 4.0, 4.0, 4.0, 4.0 }.Select(c => new SensorSample(new Dictionary<string, double?> { ["clock"] = c }, null, 100))]);
        Assert.Contains(BenchVerdicts.Observations(Input("cpu", clocks)), x => x.Str("id") == "clock_drop_after_boost");

        // RAM configured below rating (SMBIOS values, no cause claimed).
        var ram = BenchVerdicts.Observations(Input("ram", Telemetry("ram")));
        Assert.Contains(ram, x => x.Str("id") == "ram_speed_below_module_rating" && (int)x["value"]! == 3200 && (int)x["rated"]! == 3600);

        // Disk below typical for NVMe.
        var disk = BenchVerdicts.Observations(Input("disk", Telemetry("disk"), subtests: new() { ["seqWrite"] = 500, ["seqRead"] = 600, ["rand4k"] = 20 }));
        Assert.Contains(disk, x => x.Str("id") == "disk_below_typical_for_type" && x.Str("media") == "nvme");
        Assert.DoesNotContain(BenchVerdicts.Observations(Input("disk", Telemetry("disk"), subtests: new() { ["seqRead"] = 3000 })),
            x => x.Str("id") == "disk_below_typical_for_type");
    }

    [Fact]
    public void ThermalStopRule_TwoConsecutiveSamplesAtSlowdownMinus3()
    {
        var rule = new ThermalStopRule();
        Assert.False(rule.Observe(87, 90));  // one sample at 87 (= 90 − 3)
        Assert.False(rule.Observe(86, 90));  // reset
        Assert.False(rule.Observe(87, 90));
        Assert.True(rule.Observe(88, 90));   // two in a row
        Assert.True(rule.Observe(50, 90));   // stays tripped (never repeated)

        var none = new ThermalStopRule();
        Assert.False(none.Observe(99, null)); // no slowdown threshold, no rule
        Assert.False(none.Observe(99, null));
        Assert.False(none.Observe(null, 90));
    }

    [Fact]
    public void FanGuidance_BrandAndShape()
    {
        Assert.Equal("asus", FanGuidance.Brand("ASUSTeK COMPUTER INC.", "ROG Zephyrus", null, laptop: true));
        Assert.Equal("any", FanGuidance.Brand("ASUSTeK COMPUTER INC.", "PRIME B650", null, laptop: false)); // ASUS desktop board
        Assert.Equal("lenovo", FanGuidance.Brand("LENOVO", "82JW", "Legion 5 15ACH6H", laptop: true));
        Assert.Equal("lenovo", FanGuidance.Brand("LENOVO", "LOQ 15IRH8", null, laptop: true));
        Assert.Equal("any", FanGuidance.Brand("LENOVO", "ThinkPad T14", "ThinkPad", laptop: true));
        Assert.Equal("any", FanGuidance.Brand(null, null, null, laptop: false));

        var g = FanGuidance.Build("asus", laptop: true);
        Assert.Equal("asus", g.Str("brand"));
        Assert.Equal(new[] { "bios_fan_mode", "clean_filters", "check_fans_spin", "gpu_default_curve" }, Engine.Json.Strings(g["tips"]));
        Assert.False((bool)g["pump"]!);
        Assert.True((bool)FanGuidance.Build("any", laptop: false)["pump"]!);
    }

    // ---------------------------------------------------------------- lease

    [Fact]
    public void Lease_IsSymmetric_WithTheContractsBusyErrors()
    {
        var lease = new WorkLease();
        lease.Acquire(WorkLease.Bench, "bench-1");
        var busy = Assert.Throws<RpcError>(() => lease.Acquire(WorkLease.Scan, "scan-1"));
        Assert.Equal(ErrorCodes.InvalidState, busy.Code);
        Assert.Equal("bench_active", busy.ErrorData!["reason"]!.GetValue<string>());
        Assert.Equal("bench-1", busy.ErrorData!["runId"]!.GetValue<string>());
        lease.Release(WorkLease.Scan, "scan-1"); // a stale release by another holder does nothing
        Assert.NotNull(lease.Current);
        lease.Release(WorkLease.Bench, "bench-1");

        lease.Acquire(WorkLease.Scan, "scan-1");
        var scan = Assert.Throws<RpcError>(() => lease.Acquire(WorkLease.Bench, "bench-2"));
        Assert.Equal(ErrorCodes.ScanActive, scan.Code);
        Assert.Equal("scan-1", scan.ErrorData!["scanId"]!.GetValue<string>());
        lease.Release(WorkLease.Scan, "scan-1");

        lease.Acquire(WorkLease.Action, "plan-1");
        lease.Rename(WorkLease.Action, "plan-1", "op-1");
        var action = Assert.Throws<RpcError>(() => lease.Acquire(WorkLease.Snapshot, "call-1"));
        Assert.Equal("action_active", action.ErrorData!["reason"]!.GetValue<string>());
        Assert.Equal("op-1", action.ErrorData!["operationId"]!.GetValue<string>());
        lease.Release(WorkLease.Action, "op-1");

        Assert.True(WorkLease.AffectsPerformance("power.select_scheme"));
        Assert.True(WorkLease.AffectsPerformance("defender.quick_scan"));
        Assert.False(WorkLease.AffectsPerformance("firewall.enable_public"));
    }

    [Fact]
    public async Task Lease_ConcurrentAcquire_ExactlyOneWins()
    {
        var lease = new WorkLease();
        var wins = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            if (lease.TryAcquire(i % 2 == 0 ? WorkLease.Scan : WorkLease.Bench, "x" + i) is null) Interlocked.Increment(ref wins);
        })));
        Assert.Equal(1, wins);
    }

    // ---------------------------------------------------------------- wire

    [Fact]
    public async Task LineReader_SplitsLines_AndRefusesOversizedLines()
    {
        var text = "{\"ev\":\"hb\"}\r\n{\"ev\":\"hello\"}\nlast";
        var reader = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("{\"ev\":\"hb\"}", await reader.ReadLineAsync(default));
        Assert.Equal("{\"ev\":\"hello\"}", await reader.ReadLineAsync(default));
        Assert.Equal("last", await reader.ReadLineAsync(default));
        Assert.Null(await reader.ReadLineAsync(default));

        var big = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', BoundedLineReader.MaxLineBytes + 1) + "\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => big.ReadLineAsync(default));
        var exact = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', BoundedLineReader.MaxLineBytes) + "\n")));
        Assert.Equal(BoundedLineReader.MaxLineBytes, (await exact.ReadLineAsync(default))!.Length);
    }

    [Fact]
    public void BenchFolder_IsCreated_AndAReparsePointIsRefused()
    {
        var dir = TestHost.NewDataDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = BenchFolder.Prepare(dir);
            Assert.True(Directory.Exists(path));
            Assert.False(BenchFolder.IsReparsePoint(path));

            var target = Path.Combine(dir, "elsewhere");
            Directory.CreateDirectory(target);
            var linked = Path.Combine(dir, "linked");
            Directory.CreateDirectory(linked);
            Directory.Delete(Path.Combine(dir, "bench"));
            try { Directory.CreateSymbolicLink(Path.Combine(dir, "bench"), target); }
            catch (IOException) { return; }           // symlinks need developer mode or admin: skip that half
            catch (UnauthorizedAccessException) { return; }
            Assert.Throws<IOException>(() => BenchFolder.Prepare(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- AI text

    [Theory]
    [InlineData("Try undervolting the GPU.", true)]
    [InlineData("An overclock may help.", true)]
    [InlineData("Set a voltage offset of -50 mV.", true)]
    [InlineData("Use Curve Optimizer per core.", true)]
    [InlineData("Lower Vcore a little.", true)]
    [InlineData("Turn on PBO in the BIOS.", true)]
    [InlineData("Intel XTU can do this.", true)]
    [InlineData("ThrottleStop is another tool.", true)]
    [InlineData("UNDER-VOLTED chips run cooler.", true)]
    [InlineData("Your GPU is at its power limit; clean the filters.", false)]
    [InlineData("The voltage regulator is fine.", false)]
    [InlineData("Use the PBOX app.", false)] // word boundary
    [InlineData("Your clock speed dropped after boost.", false)]
    public void VoltageWords_AreMatchedCaseInsensitivelyOnWordBoundaries(string text, bool expected) =>
        Assert.Equal(expected, AgentService.MentionsVoltage(text));

    [Fact]
    public void DoBenchmarkTag_IsStrippedLastOrBeforeNext()
    {
        var (a, sa) = AgentService.StripBenchmarkTag("Your GPU looks fine.\nDo: benchmark");
        Assert.True(sa);
        Assert.Equal("Your GPU looks fine.", a);

        var (b, sb) = AgentService.StripBenchmarkTag("Run one to compare.\nDo: benchmark\nNext: What is a good score? | Why is it hot?");
        Assert.True(sb);
        Assert.Equal("Run one to compare.\nNext: What is a good score? | Why is it hot?", b);

        var (c, sc) = AgentService.StripBenchmarkTag("Do: benchmark is not something I can do.\nOk.");
        Assert.False(sc);
        Assert.Equal("Do: benchmark is not something I can do.\nOk.", c);
    }

    // ---------------------------------------------------------------- projection

    [Fact]
    public void BenchProjector_SharesNumbersUnitsAndModelNames_Only()
    {
        var body = new JsonObject
        {
            ["id"] = "ev-0123456789abcdef", ["subject"] = "bench.gpu.fma", ["collectorId"] = "bench.gpu", ["category"] = "performance",
            ["observedAt"] = "2026-11-09T10:00:00.000Z", ["quality"] = "measured", ["sensitivity"] = "public",
            ["value"] = new JsonObject
            {
                ["runId"] = "bench-1", ["part"] = "gpu", ["metric"] = "fma", ["value"] = 12000.5, ["unit"] = "GFLOPS", ["source"] = "worker",
                ["device"] = "NVIDIA GeForce RTX 4060", ["benchVersion"] = "1", ["path"] = @"C:\Users\someone\secret.txt",
            },
        };
        var p = EvidenceProjection.ProjectStored(body, new AliasMap(), new ShareOptions(false))!;
        Assert.NotNull(p);
        Assert.Equal("bench.gpu.fma", p.Subject);
        Assert.Equal(12000.5, (double)p.Value["value"]!);
        Assert.Equal("GFLOPS", p.Value.Str("unit"));
        Assert.Equal("NVIDIA GeForce RTX 4060", p.Value.Str("device"));
        Assert.Null(p.Value["path"]);
        Assert.Null(p.Value["runId"]);

        var wrongCollector = (JsonObject)body.DeepClone();
        wrongCollector["collectorId"] = "bench.cpu";
        Assert.Null(EvidenceProjection.ProjectStored(wrongCollector, new AliasMap(), new ShareOptions(false)));
    }
}

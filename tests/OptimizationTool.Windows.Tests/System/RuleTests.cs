using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.SystemDomain.Rules;
using static OptimizationTool.Windows.Tests.SystemDomain.Kit;

namespace OptimizationTool.Windows.Tests.SystemDomain;

public class HighPerfOnBatteryRuleTests
{
    private const string C = "win.system.power";

    private static RuleContext Power(string source, string personality, bool hasBalanced = true, bool? hasBattery = true, string status = "complete") => Ctx(
    [
        Ev("power.source", C, new JsonObject { ["source"] = source, ["hasBattery"] = hasBattery, ["batteryPercent"] = 55 }),
        Ev("power.active_scheme", C, new JsonObject { ["guid"] = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", ["personality"] = personality, ["ultimate"] = false }),
        Ev("power.schemes", C, new JsonObject { ["count"] = 2, ["hasBalanced"] = hasBalanced }),
    ], (C, status));

    [Fact]
    public void Positive_battery_high_performance_with_balanced()
    {
        var r = new HighPerfOnBatteryRule().Evaluate(Power("battery", "high_performance"));
        Assert.Equal("finding", r.Status);
        var f = Assert.Single(r.Findings);
        Assert.Equal("low", f.Severity);
        Assert.Equal("high", f.Confidence);
        Assert.Equal("power.active_scheme", f.Subject);
        Assert.Equal(3, f.EvidenceIds.Count);
        Assert.Equal("ms-settings:powersleep", f.Card.SettingsUri);
        Assert.Equal("preview_available", f.ActionEligibility);
        Assert.Equal("Preview change", f.Card.Options[0]);
        Assert.Equal("power.select_scheme", f.Card.ActionId);
        Assert.Equal("balanced", f.Card.ActionParameters!["personality"]!.GetValue<string>());
        AssertCard(f);
    }

    [Fact]
    public async Task Card_action_resolves_to_balanced_through_the_handler()
    {
        // The card's ActionId/ActionParameters, sent as-is, produce an eligible Balanced plan (fake power API).
        var f = Assert.Single(new HighPerfOnBatteryRule().Evaluate(Power("battery", "high_performance")).Findings);
        var handler = new OptimizationTool.Windows.Actions.Power.SelectSchemeHandler(new Tests.Actions.Power.FakePowerApi());
        var plan = await handler.ResolveAsync(new(f.Card.ActionId!, null, f.Card.ActionParameters, f.Subject), default);
        Assert.True(plan.Eligible);
        Assert.Equal("Balanced", plan.After["activeScheme"]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void Benign_same_plan_on_ac_is_clear() =>
        Assert.Equal("clear", new HighPerfOnBatteryRule().Evaluate(Power("ac", "high_performance")).Status);

    [Fact]
    public void Benign_balanced_on_battery_is_clear() =>
        Assert.Equal("clear", new HighPerfOnBatteryRule().Evaluate(Power("battery", "balanced")).Status);

    [Fact]
    public void Single_plan_machine_is_inapplicable() =>
        Assert.Equal("inapplicable", new HighPerfOnBatteryRule().Evaluate(Power("battery", "high_performance", hasBalanced: false)).Status);

    [Fact]
    public void Desktop_without_battery_is_inapplicable() =>
        Assert.Equal("inapplicable", new HighPerfOnBatteryRule().Evaluate(Power("ac", "high_performance", hasBattery: false)).Status);

    [Fact]
    public void Missing_input_is_insufficient()
    {
        Assert.Equal("insufficient", new HighPerfOnBatteryRule().Evaluate(Ctx([])).Status);
        Assert.Equal("insufficient", new HighPerfOnBatteryRule().Evaluate(Power("battery", "high_performance", status: "denied")).Status);
        Assert.Equal("insufficient", new HighPerfOnBatteryRule().Evaluate(Ctx([], (C, "complete"))).Status);
    }
}

public class StorageRuleTests
{
    private const string C = "win.system.volumes";
    private const double G = 1L << 30;

    private static EvidenceItem Vol(char letter, bool system, double freeGib, double capGib) =>
        Ev($"volume.{letter}", C, new JsonObject
        {
            ["letter"] = letter.ToString().ToUpperInvariant(), ["isSystem"] = system,
            ["freeBytes"] = (long)(freeGib * G), ["capacityBytes"] = (long)(capGib * G),
        });

    [Theory]
    [InlineData(11, 256, "medium")]
    [InlineData(3, 128, "high")]
    [InlineData(80, 1000, "low")]
    public void System_low_positive_with_severity(double free, double cap, string severity)
    {
        var r = new SystemVolumeLowRule().Evaluate(Ctx([Vol('c', true, free, cap)], (C, "complete")));
        var f = Assert.Single(r.Findings);
        Assert.Equal(severity, f.Severity);
        Assert.Equal("volume.c", f.Subject);
        Assert.Equal("ms-settings:storagesense", f.Card.SettingsUri);
        AssertCard(f);
    }

    [Fact]
    public void System_benign_lookalike_low_other_drive_is_clear()
    {
        // The D: drive is nearly full, but the system drive is fine: system_low stays clear.
        var r = new SystemVolumeLowRule().Evaluate(Ctx([Vol('c', true, 120, 500), Vol('d', false, 2, 500)], (C, "complete")));
        Assert.Equal("clear", r.Status);
    }

    [Fact]
    public void System_missing_input_is_insufficient()
    {
        Assert.Equal("insufficient", new SystemVolumeLowRule().Evaluate(Ctx([], (C, "failed"))).Status);
        Assert.Equal("insufficient", new SystemVolumeLowRule().Evaluate(Ctx([Vol('d', false, 2, 500)], (C, "complete"))).Status);
    }

    [Fact]
    public void Volume_low_positive()
    {
        var r = new VolumeLowRule().Evaluate(Ctx([Vol('c', true, 120, 500), Vol('d', false, 20, 1000)], (C, "partial")));
        var f = Assert.Single(r.Findings);
        Assert.Equal("low", f.Severity);
        Assert.Equal("volume.d", f.Subject);
        AssertCard(f);
    }

    [Fact]
    public void Volume_benign_lookalike_small_absolute_but_plenty_percent_is_clear()
    {
        // 12 GiB free sounds low, but it is 40% of a 30 GiB data partition.
        var r = new VolumeLowRule().Evaluate(Ctx([Vol('c', true, 120, 500), Vol('e', false, 12, 30)], (C, "complete")));
        Assert.Equal("clear", r.Status);
    }

    [Fact]
    public void Volume_only_system_is_inapplicable_and_missing_is_insufficient()
    {
        Assert.Equal("inapplicable", new VolumeLowRule().Evaluate(Ctx([Vol('c', true, 1, 500)], (C, "complete"))).Status);
        Assert.Equal("insufficient", new VolumeLowRule().Evaluate(Ctx([])).Status);
    }
}

public class SustainedCpuRuleTests
{
    private const string C = "win.perf.sample";

    private static EvidenceItem Proc(string image, double[] cpu, int fg = 0, bool self = false, bool defender = false, string suffix = "") =>
        Ev($"perf.process.{Ids.Hash16(image)[..8]}{suffix}", C, new JsonObject
        {
            ["image"] = image, ["cpuAvgPercent"] = cpu.Average(), ["cpuPercentSamples"] = Arr(cpu),
            ["validIntervals"] = cpu.Length, ["foregroundIntervals"] = fg, ["isThisTool"] = self, ["isDefender"] = defender,
            ["memoryRank"] = 1,
        });

    private static EvidenceItem Sample(int intervals, int logical = 8) =>
        Ev("perf.sample", C, new JsonObject { ["intervals"] = intervals, ["logicalProcessors"] = logical, ["skippable"] = true });

    [Fact]
    public void Single_core_runaway_on_many_core_machine()
    {
        // 32 threads: one busy core is ~3.1% of the machine, never above 25%.
        double[] oneCore = [3.1, 3.1, 3.0, 3.1, 0.5, 3.1];
        var p = Proc("spinner.exe", oneCore);
        var f = Assert.Single(new SustainedCpuRule().Evaluate(Ctx([Sample(6, 32), p], (C, "complete"))).Findings);
        Assert.Equal("low", f.Severity);
        Assert.Equal(p.Subject, f.Subject);
        Assert.Equal("spinner.exe kept one CPU core busy in the background (5 of 6 samples). Would you like to review it?", f.Card.Text);
        AssertCard(f);
    }

    [Fact]
    public void Single_core_trigger_keeps_exclusions_and_defender_is_info()
    {
        double[] oneCore = [3.1, 3.1, 3.1, 3.1, 3.1, 3.1];
        var ctx = Ctx(
        [
            Sample(6, 32),
            Proc("game.exe", oneCore, fg: 6),
            Proc("OptimizationTool.Host.exe", oneCore, self: true),
            Proc("MsMpEng.exe", oneCore, defender: true),
        ], (C, "complete"));
        var f = Assert.Single(new SustainedCpuRule().Evaluate(ctx).Findings);
        Assert.Equal("info", f.Severity);
        Assert.Contains("MsMpEng.exe", f.Card.Text);
    }

    [Fact]
    public void Single_core_benign_lookalikes_are_clear()
    {
        var ctx = Ctx(
        [
            Sample(6, 32),
            Proc("halfcore.exe", [1.5, 1.5, 1.6, 1.5, 1.5, 1.5]),   // ~0.5 core, steady
            Proc("burst.exe", [3.1, 3.1, 0.2, 0.1, 0.3, 0.2]),      // one core for only 2 of 6 samples
        ], (C, "complete"));
        Assert.Equal("clear", new SustainedCpuRule().Evaluate(ctx).Status);
    }

    [Fact]
    public void Both_triggers_on_small_machine_give_one_finding_with_machine_wide_copy()
    {
        // 4 threads: 0.9 core is 22.5%; 1 core is 25%; 30% is both "> 25%" and "≥ 0.9 core".
        var both = Proc("encoder.exe", [30, 30, 30, 30, 30, 30]);
        var f = Assert.Single(new SustainedCpuRule().Evaluate(Ctx([Sample(6, 4), both], (C, "complete"))).Findings);
        Assert.Contains("above 25% in 6 of 6 samples", f.Card.Text);

        // 23% on 4 threads (0.92 core): only the core trigger fires → still one finding, core copy.
        var core = Proc("encoder.exe", [23, 23, 23, 23, 23, 23]);
        var g = Assert.Single(new SustainedCpuRule().Evaluate(Ctx([Sample(6, 4), core], (C, "complete"))).Findings);
        Assert.Contains("kept one CPU core busy", g.Card.Text);
        Assert.Equal(f.Subject, g.Subject);
    }

    [Fact]
    public void Missing_logical_processor_count_falls_back_to_machine_wide_trigger_only()
    {
        var sample = Ev("perf.sample", C, new JsonObject { ["intervals"] = 6 });
        var p = Proc("spinner.exe", [3.1, 3.1, 3.1, 3.1, 3.1, 3.1]);
        Assert.Equal("clear", new SustainedCpuRule().Evaluate(Ctx([sample, p], (C, "complete"))).Status);
    }

    private static readonly double[] Busy = [40, 45, 38, 50, 10, 42];
    private static readonly double[] Spiky = [90, 2, 3, 1, 2, 4];

    [Fact]
    public void Positive_background_process_names_image_average_and_count()
    {
        var r = new SustainedCpuRule().Evaluate(Ctx([Sample(6), Proc("encoder.exe", Busy)], (C, "complete")));
        var f = Assert.Single(r.Findings);
        Assert.Equal("low", f.Severity);
        Assert.Equal("medium", f.Confidence);
        Assert.Contains("encoder.exe", f.Card.Text);
        Assert.Contains("37.5%", f.Card.Text);
        Assert.Contains("5 of 6 samples", f.Card.Text);
        Assert.Null(f.Card.SettingsUri);
        AssertCard(f);
    }

    [Fact]
    public void Instance_suffix_is_dropped_from_finding_subject()
    {
        var p = Proc("encoder.exe", Busy, suffix: ".2");
        var f = Assert.Single(new SustainedCpuRule().Evaluate(Ctx([Sample(6), p], (C, "partial"))).Findings);
        Assert.Equal(p.Subject[..^2], f.Subject);
    }

    [Fact]
    public void Defender_is_info()
    {
        var f = Assert.Single(new SustainedCpuRule().Evaluate(Ctx([Sample(6), Proc("MsMpEng.exe", Busy, defender: true)], (C, "complete"))).Findings);
        Assert.Equal("info", f.Severity);
        Assert.NotNull(f.Counterevidence);
    }

    [Fact]
    public void Benign_lookalikes_are_clear()
    {
        var ctx = Ctx(
        [
            Sample(6),
            Proc("spiky.exe", Spiky),                     // one big spike, not sustained
            Proc("game.exe", Busy, fg: 6),                // foreground app the user is using
            Proc("OptimizationTool.Host.exe", Busy, self: true), // this tool
        ], (C, "complete"));
        Assert.Equal("clear", new SustainedCpuRule().Evaluate(ctx).Status);
    }

    [Fact]
    public void Missing_input_is_insufficient()
    {
        Assert.Equal("insufficient", new SustainedCpuRule().Evaluate(Ctx([], (C, "skipped"))).Status);
        Assert.Equal("insufficient", new SustainedCpuRule().Evaluate(Ctx([Sample(1), Proc("encoder.exe", [50])], (C, "partial"))).Status);
        Assert.Equal("insufficient", new SustainedCpuRule().Evaluate(Ctx([Sample(6)], (C, "partial"))).Status);
    }
}

public class MemoryPressureRuleTests
{
    private const string C = "win.perf.sample";

    private static EvidenceItem Mem(double[] avail, double commitMax) => Ev("perf.memory", C, new JsonObject
    {
        ["availablePercentSamples"] = Arr(avail), ["commitMaxPercent"] = commitMax,
    });

    [Fact]
    public void Positive_low_available_and_commit_near_limit()
    {
        var big = Ev("perf.process.aaaa0000", C, new JsonObject { ["image"] = "editor.exe", ["memoryRank"] = 1, ["isThisTool"] = false });
        var r = new MemoryPressureRule().Evaluate(Ctx([Mem([6, 7, 5, 8, 12], 94), big], (C, "complete")));
        var f = Assert.Single(r.Findings);
        Assert.Equal("medium", f.Severity);
        Assert.Contains("editor.exe", f.Card.Text);
        Assert.Equal(2, f.EvidenceIds.Count);
        AssertCard(f);
    }

    [Fact]
    public void Benign_low_available_but_commit_has_headroom_is_clear() =>
        Assert.Equal("clear", new MemoryPressureRule().Evaluate(Ctx([Mem([6, 7, 5, 8, 6], 60)], (C, "complete"))).Status);

    [Fact]
    public void Benign_brief_dip_is_clear() =>
        Assert.Equal("clear", new MemoryPressureRule().Evaluate(Ctx([Mem([6, 30, 32, 31, 35], 95)], (C, "complete"))).Status);

    [Fact]
    public void Missing_input_is_insufficient()
    {
        Assert.Equal("insufficient", new MemoryPressureRule().Evaluate(Ctx([], (C, "complete"))).Status);
        Assert.Equal("insufficient", new MemoryPressureRule().Evaluate(Ctx([Mem([5, 5], 95)], (C, "complete"))).Status);
        Assert.Equal("insufficient", new MemoryPressureRule().Evaluate(Ctx([Mem([5, 5, 5], 95)], (C, "timed_out"))).Status);
    }
}

public class UptimeLongRuleTests
{
    private const string C = "win.system.os";

    private static EvidenceItem Up(double days) => Ev("system.uptime", C, new JsonObject { ["uptimeSeconds"] = (long)(days * 86400) });

    [Fact]
    public void Positive_is_info()
    {
        var f = Assert.Single(new UptimeLongRule().Evaluate(Ctx([Up(21.5)], (C, "complete"))).Findings);
        Assert.Equal("info", f.Severity);
        Assert.Contains("21 days", f.Card.Text);
        AssertCard(f);
    }

    [Fact]
    public void Benign_two_weeks_exactly_is_clear() =>
        Assert.Equal("clear", new UptimeLongRule().Evaluate(Ctx([Up(14)], (C, "partial"))).Status);

    [Fact]
    public void Missing_input_is_insufficient()
    {
        Assert.Equal("insufficient", new UptimeLongRule().Evaluate(Ctx([], (C, "complete"))).Status);
        Assert.Equal("insufficient", new UptimeLongRule().Evaluate(Ctx([Up(30)], (C, "failed"))).Status);
    }
}

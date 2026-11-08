using OptimizationTool.Engine;
using OptimizationTool.Windows.Reliability;
using OptimizationTool.Windows.Reliability.Rules;
using static OptimizationTool.Windows.Tests.Reliability.EventsTests;

namespace OptimizationTool.Windows.Tests.Reliability;

public class EventRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 9, 8, 0, 0, TimeSpan.Zero);

    private static RuleContext System(params RawEvent[] events) => TestKit.Ctx("win.events.system", Normalize(EventChannelSpec.System, events));

    // ---- reliability.kernel_power ----

    [Fact]
    public void Kernel_power_without_bugcheck_guides_power_and_cooling()
    {
        var r = new KernelPowerRule().Evaluate(System(KernelPower(T0), KernelPower(T0.AddDays(1)), KernelPower(T0.AddDays(2))));
        var f = Assert.Single(r.Findings);
        Assert.Equal("finding", r.Status);
        Assert.Equal("medium", f.Severity);
        Assert.Contains("3 times in the last 7 days", f.Card.Text);
        Assert.Contains(f.Card.Guide!, s => s.Contains("power cable", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Card.Guide!, s => s.Contains("graphics", StringComparison.OrdinalIgnoreCase));
        TestKit.AssertCard(f);
    }

    [Fact]
    public void Kernel_power_with_nearby_graphics_bugcheck_cites_it()
    {
        var ctx = System(KernelPower(T0), KernelPower(T0.AddDays(1)), Bugcheck(T0.AddDays(1).AddMinutes(3), "0x00000116 (0x1)"));
        var f = Assert.Single(new KernelPowerRule().Evaluate(ctx).Findings);
        Assert.Equal(2, f.EvidenceIds.Count);
        Assert.Contains(f.Card.Guide!, s => s.Contains("graphics driver", StringComparison.Ordinal));
        Assert.Equal("high", f.Confidence);
    }

    [Fact]
    public void Kernel_power_with_other_stop_code_does_not_blame_graphics()
    {
        var f = Assert.Single(new KernelPowerRule().Evaluate(System(KernelPower(T0, 0x9f), KernelPower(T0.AddDays(1), 0x9f))).Findings);
        Assert.DoesNotContain(f.Card.Guide!, s => s.Contains("graphics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(f.Card.Guide!, s => s.Contains("drivers", StringComparison.Ordinal));
    }

    [Fact]
    public void Kernel_power_power_button_holds_and_single_event_are_clear()
    {
        Assert.Equal("clear", new KernelPowerRule().Evaluate(System(KernelPower(T0, 0, "1"), KernelPower(T0.AddDays(1), 0, "1"), KernelPower(T0.AddDays(2), 0, "1"))).Status);
        Assert.Equal("clear", new KernelPowerRule().Evaluate(System(KernelPower(T0))).Status);
        Assert.Equal("clear", new KernelPowerRule().Evaluate(System()).Status);
    }

    [Fact]
    public void Kernel_power_missing_input_is_insufficient_and_many_is_high()
    {
        Assert.Equal("insufficient", new KernelPowerRule().Evaluate(TestKit.Missing("win.events.system")).Status);
        Assert.Equal("insufficient", new KernelPowerRule().Evaluate(TestKit.Missing("win.events.system", "denied")).Status);
        var many = Enumerable.Range(0, 5).Select(i => KernelPower(T0.AddHours(i))).ToArray();
        Assert.Equal("high", new KernelPowerRule().Evaluate(System(many)).Findings.Single().Severity);
    }

    // ---- reliability.app_crashes ----

    private static RuleContext App(params RawEvent[] events) => TestKit.Ctx("win.events.application", Normalize(EventChannelSpec.Application, events));

    [Fact]
    public void App_crashes_three_for_same_app_is_a_low_finding()
    {
        var r = new AppCrashesRule().Evaluate(App(AppError(T0, "contoso.exe"), AppError(T0.AddHours(1), "contoso.exe"), AppError(T0.AddHours(2), "contoso.exe")));
        var f = Assert.Single(r.Findings);
        Assert.Equal("low", f.Severity);
        Assert.Contains("contoso.exe closed unexpectedly 3 times", f.Card.Text);
        TestKit.AssertCard(f);
    }

    [Fact]
    public void App_crashes_spread_across_apps_is_clear_and_ten_is_medium()
    {
        Assert.Equal("clear", new AppCrashesRule().Evaluate(App(AppError(T0, "a.exe"), AppError(T0, "a.exe"), AppError(T0, "b.exe"))).Status);
        Assert.Equal("insufficient", new AppCrashesRule().Evaluate(TestKit.Missing("win.events.application")).Status);
        var ten = Enumerable.Range(0, 10).Select(i => AppError(T0.AddMinutes(i), "a.exe")).ToArray();
        Assert.Equal("medium", new AppCrashesRule().Evaluate(App(ten)).Findings.Single().Severity);
    }

    // ---- reliability.update_failures ----

    private static RuleContext Upd(params RawEvent[] events) => TestKit.Ctx("win.events.update", Normalize(EventChannelSpec.Update, events));

    [Fact]
    public void Update_failures_repeated_without_later_success_is_medium()
    {
        var f = Assert.Single(new UpdateFailuresRule().Evaluate(Upd(UpdateOk(T0, "g0"), UpdateFail(T0.AddDays(1), "0x80070643", "g1"), UpdateFail(T0.AddDays(2), "0x80070643", "g1"))).Findings);
        Assert.Equal("medium", f.Severity);
        Assert.Contains("2026-01-09", f.Card.Text);
        TestKit.AssertCard(f);
    }

    [Fact]
    public void Update_failures_recovered_is_low_single_is_info_none_is_clear()
    {
        Assert.Equal("low", new UpdateFailuresRule().Evaluate(Upd(UpdateFail(T0, "0x1", "g1"), UpdateFail(T0, "0x2", "g2"), UpdateOk(T0.AddDays(1), "g1"))).Findings.Single().Severity);
        Assert.Equal("info", new UpdateFailuresRule().Evaluate(Upd(UpdateFail(T0, "0x1", "g1"), UpdateOk(T0.AddDays(1), "g1"))).Findings.Single().Severity);
        Assert.Equal("clear", new UpdateFailuresRule().Evaluate(Upd(UpdateOk(T0, "g1"))).Status);
        Assert.Equal("insufficient", new UpdateFailuresRule().Evaluate(TestKit.Missing("win.events.update", "unsupported")).Status);
    }

    [Fact]
    public void Update_failures_are_read_from_the_system_log_too()
    {
        // Current Windows logs WindowsUpdateClient 19/20 in System; Operational may be absent or hold other IDs.
        var sys = Normalize(EventChannelSpec.System, UpdateFail(T0, "0x80070643", "g1"), UpdateFail(T0.AddDays(1), "0x80070643", "g1"));
        var ctx = new RuleContext(sys.Facts.Select(f => TestKit.Ev("win.events.system", f)).ToList(),
            new Dictionary<string, string> { ["win.events.system"] = "complete", ["win.events.update"] = "unsupported" }, Profile.Default);
        var f = Assert.Single(new UpdateFailuresRule().Evaluate(ctx).Findings);
        Assert.Equal("medium", f.Severity);
        Assert.Contains("2 times", f.Card.Text);
    }
}

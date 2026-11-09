using OptimizationTool.Engine.Bench;
using OptimizationTool.Windows.Sensors;
using Xunit.Abstractions;

namespace OptimizationTool.Windows.Tests.Sensors;

/// <summary>Runs the real sensors on this PC as the current user. Asserts shape only, never machine values.</summary>
[Trait("Category", "Live")]
public class SensorsLiveTests(ITestOutputHelper output)
{
    private static readonly string[] Sources = ["pdh", "emi", "d3dkmt", "nvml", "wmi", "worker", "none"];
    private static readonly string[] Reasons = ["needs_advanced_sensors", "not_supported", "needs_admin", "timed_out"];

    private static string BenchFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimizationTool", "bench");

    private static async Task<BenchHardware> Describe()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await new BenchHardwareReader().DescribeAsync(BenchFolder, cts.Token);
    }

    [Fact]
    public async Task Describe_returns_shape_within_budget()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hw = await Describe();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"took {watch.Elapsed}");
        output.WriteLine(hw.ToString());
        Assert.False(string.IsNullOrWhiteSpace(hw.CpuModel));
        Assert.True(hw.LogicalProcessors >= 1);
        Assert.True(hw.RamBytes > 0);
        Assert.Matches(@"^([A-Z]:|unknown)$", hw.DiskVolume);
        Assert.Contains(hw.DiskMedia, new[] { "nvme", "ssd", "hdd", "unknown" });
        Assert.Matches(@"^\d+\.\d+\.\d+$", hw.WindowsBuild);
        if (hw.GpuLuid is { } luid) Assert.Matches("^[0-9A-F]{16}$", luid);
        Assert.DoesNotContain(Environment.UserName, hw.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, hw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void AssertSlots(IReadOnlyList<MetricSlot> slots)
    {
        Assert.Equal(4, slots.Count);
        Assert.Equal(4, slots.Select(s => s.Id).Distinct().Count());
        foreach (var s in slots)
        {
            output.WriteLine($"  slot {s}");
            Assert.Contains(s.Source, Sources);
            Assert.False(string.IsNullOrEmpty(s.Unit));
            if (s.Available) Assert.True(s.Max > 0);
            else Assert.Contains(s.Reason, Reasons);
        }
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("disk")]
    [InlineData("ram")]
    public async Task Hub_prepare_and_read(string part)
    {
        var hw = await Describe();
        using var hub = new SensorsFactory().Create();
        var slots = hub.Prepare(part, hw);
        AssertSlots(slots);
        SensorSample? last = null;
        for (var i = 0; i < 3; i++)
        {
            Thread.Sleep(300);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            last = hub.Read();
            Assert.True(watch.ElapsedMilliseconds < 400, $"read took {watch.ElapsedMilliseconds} ms");
        }
        foreach (var (k, v) in last!.Values) output.WriteLine($"  {part}.{k} = {v?.ToString() ?? "null"}");
        output.WriteLine($"  gpu throttle = {last.Gpu}, cpu limit = {last.CpuPerfLimitPercent}");
        Assert.All(last.Values.Keys, k => Assert.Contains(k, slots.Where(s => s.Available).Select(s => s.Id)));
        if (part == "cpu") Assert.Contains("clock", last.Values.Keys);
    }

    [Fact]
    public void Energy_meter_package_scaling_validates_when_present()
    {
        using var source = EnergyMeterSource.TryOpen(out var check);
        output.WriteLine(check.ToString() + " packages=" + string.Join(",", check.Packages));
        // Shape: no package instance → no source. A package instance (RAPL-style) must pass the ΔEnergy/ΔTime check.
        if (check.Packages.Count == 0) Assert.Null(source);
        else
        {
            Assert.True(check.Plausible);
            Assert.True(check.ScalingValidated);
            Assert.NotNull(source);
        }
    }

    [Fact]
    public void Power_state_values()
    {
        using var hub = new SensorsFactory().Create();
        var p = hub.Power();
        output.WriteLine(p.ToString());
        Assert.Contains(p.Ac, new[] { "online", "offline", "unknown" });
        Assert.Contains(p.Battery, new[] { "present", "absent", "unknown" });
        using var watcher = new PowerWatcher();
        output.WriteLine($"subscribed = {watcher.Subscribed}");
    }
}

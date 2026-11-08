using System.Diagnostics;
using System.Text.RegularExpressions;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Inventory;

namespace OptimizationTool.Windows.Tests.Inventory;

[Trait("Category", "Live")]
public class InventoryLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly string[] Groups = ["Specs", "Graphics", "Displays", "Devices", "Audio", "Network", "Drivers"];
    static readonly string[] Statuses = ["complete", "partial", "denied", "unsupported", "failed"];
    static readonly Regex MacLike = new(@"[0-9A-Fa-f]{1,4}(:[0-9A-Fa-f]{1,4}){2,}|\d+:\d+");
    static readonly Regex IpLike = new(@"\d+\.\d+\.\d+\.\d+");

    [Fact]
    public void Module_has_seven_steps_in_order_and_no_collectors_or_rules()
    {
        var m = new InventoryModule();
        var opts = new CollectorOptions();
        Assert.Empty(m.Collectors(opts));
        Assert.Empty(m.Rules());
        Assert.Equal(Groups, m.InventorySteps(opts).Select(s => s.Group));
        Assert.All(m.InventorySteps(opts), s => Assert.InRange(s.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Each_step_runs_on_this_pc_with_safe_labels()
    {
        var caps = new System.Text.Json.Nodes.JsonObject();
        foreach (var step in new InventoryModule().InventorySteps(new CollectorOptions()))
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(step.Timeout);
            var r = await step.RunAsync(cts.Token);
            sw.Stop();
            output.WriteLine($"{step.Group}: {r.Coverage.Status} ({r.Coverage.Observations} obs{(r.Coverage.Reason is null ? "" : ", " + r.Coverage.Reason)}) {sw.ElapsedMilliseconds} ms -> {string.Join(" | ", r.WaveFacts.Select(f => f.Label))}");
            foreach (var f in r.Facts) output.WriteLine($"    {f.Subject} {f.Value.ToJsonString()}");

            Assert.Contains(r.Coverage.Status, Statuses);
            Assert.True(sw.Elapsed < step.Timeout, $"{step.Group} too slow");
            foreach (var f in r.WaveFacts)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Label));
                Assert.DoesNotMatch(MacLike, f.Label);
                Assert.DoesNotMatch(IpLike, f.Label);
                Assert.DoesNotContain('\\', f.Label);
                Assert.DoesNotContain('/', f.Label);
                Assert.True(f.Label.Length <= 40);
            }
            foreach (var f in r.Facts)
            {
                Assert.StartsWith("inventory." + step.Group.ToLowerInvariant() + ".", f.Subject);
                Assert.Equal("measured", f.Quality);
                Assert.DoesNotMatch(MacLike, f.Display);
                Assert.DoesNotMatch(IpLike, f.Display);
            }
            foreach (var (k, v) in r.Capabilities) caps[k] = v?.DeepClone();
        }
        Assert.True(caps.ContainsKey("displayCount"));
        Assert.True(caps.ContainsKey("gpuCount"));
        Assert.True(caps.ContainsKey("hasBattery"));
        Assert.True(caps.ContainsKey("hasWifi"));
        output.WriteLine("capabilities " + caps.ToJsonString());
    }
}

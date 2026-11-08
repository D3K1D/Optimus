using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Defender;
using Xunit.Abstractions;

namespace OptimizationTool.Windows.Tests.Actions.Defender;

/// <summary>Live: ResolveAsync and ReadTargetAsync only. Never ExecuteAsync (that would start a real update or scan).</summary>
[Trait("Category", "Live")]
public class DefenderLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Resolve_and_read_target_on_this_pc()
    {
        var handlers = new DefenderActionModule().Handlers(new ActionContext(null!, false)).ToList();
        foreach (var h in handlers)
        {
            var parameters = h.ActionId == "defender.full_scan" ? new System.Text.Json.Nodes.JsonObject { ["confirmLong"] = true } : null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var r = await h.ResolveAsync(new ActionRequest(h.ActionId, null, parameters, null), cts.Token);
            output.WriteLine($"{h.ActionId,-28} eligible={r.Eligible} code={r.IneligibleCode} before={r.Before.ToJsonString()} rows={string.Join(" | ", r.Rows.Select(x => $"{x.Label}: {x.Before} → {x.After}"))}");
            if (r.Eligible)
            {
                Assert.NotEmpty(r.Rows);
                Assert.NotEmpty(r.Tradeoffs);
                var now = await h.ReadTargetAsync(r.Target, cts.Token);
                Assert.Equal(r.Before.Select(kv => kv.Key), now.Select(kv => kv.Key));
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(r.IneligibleCode));
            }
        }
        var tool = new WindowsDefenderSystem().ResolveTool();
        output.WriteLine($"MpCmdRun resolved={tool.Path is not null} problem={tool.Problem}");
    }
}

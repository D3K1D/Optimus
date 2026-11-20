using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Shell;

namespace OptimizationTool.Tests;

/// <summary>
/// Design v59: explorer.restart is report-only, like repair.sfc and network.flush_dns. The real handler runs over fake
/// shell and process seams (nothing is ended or started): restarted, started and not_running end the operation and plan
/// "completed" (a receipt with completedAt, never verifiedAt; never indeterminate), failed ends "failed" with result
/// "failed"; the data_loss flag stays, the progress line is "Restarting Windows Explorer", and the card is never closed.
/// </summary>
public class ExplorerRestartEngineTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";

    private sealed class Processes : IProcessControl
    {
        public readonly Dictionary<uint, ProcessInfo> Running = [];
        public bool Stuck; // the shell did not end in time
        public uint SelfPid => 500;
        public uint CurrentSession => 1;
        public string? CurrentUserSid => Me;
        public ProcessInfo? Query(uint pid) => Running.TryGetValue(pid, out var p) ? p : null;
        public TerminateResult Terminate(uint pid, long createTime, string imagePath, TimeSpan wait)
        {
            if (Stuck) return new("timeout");
            return Running.Remove(pid) ? new("ended") : new("gone");
        }
    }

    /// <summary>Windows brings the shell back by itself (restarted), only when Optimus starts it (started), or never (not_running).</summary>
    private sealed class Shell(Processes processes, string mode) : IShellControl
    {
        private uint _pid = 100;
        public bool RemoteSession => false;
        public uint? ShellPid()
        {
            if (!processes.Running.ContainsKey(_pid) && mode == "restarted") Spawn();
            return processes.Running.ContainsKey(_pid) ? _pid : null;
        }
        public bool StartExplorer()
        {
            if (mode == "not_running") return true; // it said it started, but no shell came back
            Spawn();
            return true;
        }
        private void Spawn()
        {
            _pid++;
            processes.Running[_pid] = new ProcessInfo(_pid, 2000 + _pid, @"Q:\Windows\explorer.exe", 1, Me, false);
        }
    }

    [Theory]
    [InlineData("restarted")]
    [InlineData("started")]
    [InlineData("not_running")]
    [InlineData("failed")]
    public async Task Each_result_is_reported_never_verified(string result)
    {
        var processes = new Processes();
        processes.Running[100] = new ProcessInfo(100, 1000, @"Q:\Windows\explorer.exe", 1, Me, false);
        processes.Stuck = result == "failed";
        var handler = new ExplorerRestartHandler(new Shell(processes, result), processes, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        Assert.True(((OptimizationTool.Engine.Actions.IActionHandler)handler).ReportsOnly);

        IAuditModule module = new FakeModule("shell", [new FakeCollector("fake.shell", "performance", _ => Task.FromResult(FakeCollector.One("shell.stuck",
            new JsonObject { ["stuck"] = true }, "The taskbar is not responding.")))],
            [new FakeRule("shell.stuck", "performance", ["fake.shell"], ctx =>
            {
                var e = ctx.Get("shell.stuck")!;
                return RuleResult.Found(new FindingDraft(e.Subject, "The taskbar is stuck", "low", "high", [e.Id],
                    new CardDraft("Restart Windows Explorer?", ["Preview change", "Ignore"], ActionId: ExplorerRestartHandler.Id)));
            })]);
        await using var host = TestHost.StartActions([new FakeActionModule(handler)], [module]);
        var c = await host.ConnectAsync(minor: 3);
        var (scan, _) = await c.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        var finding = (await c.Call("report.get", new JsonObject { ["reportId"] = reportId }))["findings"]!.AsArray().Single(f => f.Str("ruleId") == "shell.stuck")!;

        var row = (await c.Call("action.catalog", new JsonObject()))["actions"]!.AsArray().Single(a => a.Str("actionId") == ExplorerRestartHandler.Id)!;
        Assert.Equal(["data_loss"], row["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        var plan = await c.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = ExplorerRestartHandler.Id, ["findingId"] = finding.Str("id"),
        });
        Assert.Equal(("provider", "awaiting_user"), (plan.Str("kind"), plan.Str("status")));
        Assert.Equal(["data_loss"], plan["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(["shell:explorer"], plan["resources"]!.AsArray().Select(f => f!.GetValue<string>()));
        var op = await Act.Approve(c, plan);
        var final = await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == plan.Str("planId")
            && e["payload"].Str("state") is "verified" or "completed" or "failed" or "indeterminate");
        SchemaTests.AssertValid("event", final);
        Assert.Equal(result, final["payload"].Str("result"));
        if (result != "failed")
            Assert.Contains(c.Events, e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == plan.Str("planId")
                && e["payload"]!["progress"]?.GetValue<string>() == "Restarting Windows Explorer");

        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        SchemaTests.AssertValid("operation", done);
        var want = result == "failed" ? "failed" : "completed";
        Assert.Equal((want, result), (done.Str("state"), done.Str("result")));
        Assert.Equal(want, (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.False(done["undo"]!["available"]!.GetValue<bool>());
        if (want == "completed")
        {
            Assert.Null(done["error"]);
            var receipt = done["receipt"]!.AsObject();
            Assert.False(receipt.ContainsKey("verifiedAt"));
            Assert.NotEqual("", receipt.Str("completedAt"));
        }
        else
        {
            Assert.Null(done["receipt"]);
            Assert.Equal("not_applied", done["error"].Str("code")); // the fixture's failCode
        }
        // The card is not closed by what Windows reported: no verified decision on the finding.
        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        Assert.DoesNotContain(followup["decisions"]!.AsArray(), d => d.Str("findingId") == finding.Str("id") && d.Str("status") == "verified");
    }
}

using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Repair;

namespace OptimizationTool.Windows.Tests.Actions.Admin;

/// <summary>repair.sfc and repair.dism_restorehealth (m9 §3.2), with a fake tool: nothing is ever started.</summary>
public sealed class RepairTests
{
    private sealed class Lines : IProgress<string>
    {
        public readonly List<string> Seen = [];
        public void Report(string value) { lock (Seen) Seen.Add(value); }
    }

    private static (RepairBrokerHandler B, BrokerStepRequest Step, ActionResolution R) Broker(RepairKind k, FakeRepairTool tool, TimeSpan? deadline = null)
    {
        var b = new RepairBrokerHandler(k, tool) { RunDeadline = deadline ?? k.Deadline };
        var r = RepairPlan.Resolve(k, tool, null, null);
        Assert.True(r.Eligible, r.IneligibleMessage);
        return (b, Admin.Step(k.ActionId, r, null), r);
    }

    [Fact]
    public async Task Host_handlers_are_administrator_provider_single_actions_with_long_and_restart()
    {
        var tool = new FakeRepairTool();
        var sfc = new RepairHandler(RepairKind.Sfc, tool);
        var dism = new RepairHandler(RepairKind.Dism, tool);
        Assert.Equal(("admin", "provider", false, true), (sfc.Privilege, sfc.Kind, sfc.SupportsUndo, sfc.TakesLease));
        Assert.Equal([ActionFlags.Restart, ActionFlags.Long], ActionFlags.Union(sfc.Flags));
        Assert.Equal([ActionFlags.Restart, ActionFlags.Network, ActionFlags.Long], ActionFlags.Union(dism.Flags));
        Assert.Equal(TimeSpan.FromMinutes(60), ((IActionHandler)sfc).LongDeadline);
        Assert.Equal(TimeSpan.FromMinutes(90), ((IActionHandler)dism).LongDeadline);
        Assert.Null(sfc.UndoRequest(RepairPlan.Target(RepairKind.Sfc), []));
        var r = await sfc.ResolveAsync(new ActionRequest("repair.sfc", null, null, null), CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Equal(["repair:sfc"], r.Resources);
        Assert.Equal("failed", (await sfc.ExecuteAsync(new ActionExecution("p", "h", r.Target, r.Before, r.After, null), null, CancellationToken.None)).State); // never in the host
        Assert.Empty(tool.Runs);
        // Parameters are refused; the tool must be verified.
        Assert.Equal("invalid_arguments", (await sfc.ResolveAsync(new ActionRequest("repair.sfc", null, new() { ["args"] = "/revert" }, null), CancellationToken.None)).IneligibleCode);
        tool.Problem = "untrusted";
        Assert.Equal("tool_untrusted", (await dism.ResolveAsync(new ActionRequest("repair.dism_restorehealth", null, null, null), CancellationToken.None)).IneligibleCode);
        tool.Problem = "not_found";
        Assert.Equal("tool_missing", (await dism.ResolveAsync(new ActionRequest("repair.dism_restorehealth", null, null, null), CancellationToken.None)).IneligibleCode);
    }

    [Fact]
    public void Broker_runs_the_fixed_argv_only_with_progress_and_never_verifies()
    {
        var tool = new FakeRepairTool { Lines = ["Beginning system scan.", "Verification 5% complete.", "Verification 5% complete.", "Verification 63% complete.", "Windows Resource Protection did not find any integrity violations."] };
        var (b, step, r) = Broker(RepairKind.Sfc, tool);
        var progress = new Lines();
        var o = b.Run(b.Resolve(step), step, new BrokerRun("plan-1", progress, CancellationToken.None));
        Assert.Equal("completed", o.State);
        Assert.Equal("no_violations", o.Observed.Str("result"));
        var run = Assert.Single(tool.Runs);
        Assert.Equal(@"Q:\Windows\System32\sfc.exe", run.Path);
        Assert.Equal(["/scannow"], run.Args);
        Assert.Equal(["Checking Windows files", "Checking Windows files · 5%", "Checking Windows files · 63%"], progress.Seen);

        var dismTool = new FakeRepairTool { Lines = ["[==========================100.0%==========================] The restore operation completed successfully."] };
        var (d, dstep, _) = Broker(RepairKind.Dism, dismTool);
        Assert.Equal("repaired", d.Run(d.Resolve(dstep), dstep, new BrokerRun("plan-2", new Lines(), CancellationToken.None)).Observed.Str("result"));
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth"], Assert.Single(dismTool.Runs).Args);
        Assert.Null(b.Protect(r));
        Assert.False(b.IsUndo(step));
        Assert.Equal(TimeSpan.FromMinutes(90), ((IBrokerHandler)d).Deadline);
    }

    [Theory]
    [InlineData("sfc", 0, "Windows Resource Protection found corrupt files and successfully repaired them.", "completed", "repaired")]
    [InlineData("sfc", 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.", "completed", "could_not_repair")]
    [InlineData("sfc", 1, "Windows Resource Protection could not perform the requested operation.", "failed", "failed")]
    [InlineData("sfc", 0, "Die Überprüfung ist abgeschlossen.", "completed", "finished")]
    [InlineData("sfc", 2, "", "failed", "failed")]
    [InlineData("dism", 0, "No component store corruption detected.", "completed", "healthy")]
    [InlineData("dism", 0, "The restore operation completed successfully.", "completed", "repaired")]
    [InlineData("dism", 0, "Vorgang erfolgreich.", "completed", "finished")]
    [InlineData("dism", 87, "Error: 87", "failed", "failed")]
    [InlineData("dism", unchecked((int)0x800F081F), "The source files could not be found.", "failed", "failed")]
    public void Exit_codes_and_result_lines_map_to_a_plain_result(string tool, int exit, string line, string state, string result)
    {
        var k = tool == "sfc" ? RepairKind.Sfc : RepairKind.Dism;
        var o = RepairPlan.Outcome(k, exit, [line]);
        Assert.Equal(state, o.State);
        Assert.NotEqual("verified", o.State);
        Assert.Equal(result, o.Observed.Str("result"));
        Assert.Equal(exit, o.Observed["exitCode"]!.GetValue<int>());
        Assert.DoesNotContain("\\", o.ReceiptText);
    }

    [Fact]
    public async Task Stop_kills_the_tool_and_the_deadline_ends_it()
    {
        var tool = new FakeRepairTool { Gate = new TaskCompletionSource() };
        var (b, step, _) = Broker(RepairKind.Sfc, tool);
        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => b.Run(b.Resolve(step), step, new BrokerRun("plan-1", new Lines(), stop.Token)));
        while (tool.Runs.Count == 0) await Task.Delay(5);
        await stop.CancelAsync();
        var o = await running;
        Assert.True(tool.Killed);
        Assert.Equal(("failed", "stopped"), (o.State, o.Code));

        var slow = new FakeRepairTool { Gate = new TaskCompletionSource() };
        var (late, lstep, _) = Broker(RepairKind.Dism, slow, TimeSpan.FromMilliseconds(50));
        var timedOut = late.Run(late.Resolve(lstep), lstep, new BrokerRun("plan-1", new Lines(), CancellationToken.None));
        Assert.True(slow.Killed);
        Assert.Equal(("failed", "timeout"), (timedOut.State, timedOut.Code));
        Assert.Contains("90 minutes", timedOut.ReceiptText);
    }

    [Fact]
    public void Broker_refuses_an_untrusted_tool_at_run_time()
    {
        var tool = new FakeRepairTool();
        var (b, step, r) = Broker(RepairKind.Sfc, tool);
        tool.Problem = "untrusted"; // replaced between the window and the run
        var o = b.Run(r, step, new BrokerRun("plan-1", new Lines(), CancellationToken.None));
        Assert.Equal(("failed", "tool_untrusted"), (o.State, o.Code));
        Assert.Empty(tool.Runs);
        Assert.Equal("tool_untrusted", b.Resolve(step).IneligibleCode);
    }

    [Fact]
    public void Percent_parsing_handles_sfc_and_dism_lines()
    {
        Assert.Equal(45, RepairPlan.PercentOf("Verification 45% complete."));
        Assert.Equal(12, RepairPlan.PercentOf("[====       12.3%          ]"));
        Assert.Equal(7, RepairPlan.PercentOf("V\0e\0r 7\0%"));
        Assert.Null(RepairPlan.PercentOf("Beginning system scan."));
        Assert.Null(RepairPlan.PercentOf("1000%"));
    }
}

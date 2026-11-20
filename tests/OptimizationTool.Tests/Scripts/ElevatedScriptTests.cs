using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions.Scripts;

namespace OptimizationTool.Tests.Scripts;

/// <summary>
/// Elevated scripts on the host (m9 §5.4, D6): both flags required (--enable-admin-actions and --enable-elevated-scripts),
/// the broker's runner used only for an elevated plan, the user-scope runner never for one, the same lifecycle, receipt and
/// transcript, and a broker that dies mid-run leaves indeterminate. The broker runner is a fake.
/// </summary>
public class ElevatedScriptTests
{
    private static async Task<(ScriptPlanTests.Rig Rig, FakeScriptRunner Broker)> Start(bool admin = true, bool elevatedFlag = true, bool restorePoint = false)
    {
        var pc = new FakePc();
        var user = new FakeScriptRunner();
        var broker = new FakeScriptRunner { Lines = [(false, "as admin")] };
        var parser = new FakeScriptParser();
        var policy = new FakeScriptPolicy();
        var rp = restorePoint ? new FakeRestorePoint() : null;
        var scripts = new ScriptOptions
        {
            Runner = user, ElevatedRunner = broker, EnableElevatedScripts = elevatedFlag, Parser = parser, Policy = policy, RestorePoint = rp,
            ProgressInterval = TimeSpan.FromMilliseconds(40), Identity = () => new ScrubIdentity(["Sam"], ["DESKTOP-SAM7"], []),
        };
        var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false),
            agent: AgentSetup.Options(null, new FakeProvider("anthropic_api")), scripts: scripts, enableAdmin: admin);
        return (new ScriptPlanTests.Rig(host, await host.ConnectAsync(minor: 3), user, parser, policy, rp, pc), broker);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Without_both_flags_an_elevated_script_is_admin_unavailable(bool admin, bool flag)
    {
        var (r, broker) = await Start(admin, flag);
        await using var _ = r;
        var res = await ScriptPlanTests.Propose(r, await ScriptPlanTests.NewTask(r), ScriptPlanTests.Args(elevated: true));
        Assert.Equal("ineligible", res.Str("status"));
        Assert.Equal("admin_unavailable", res["guard"]!["refused"]![0].Str("rule"));
        Assert.Equal(0, broker.Calls);
        // A user-scope script is unaffected.
        Assert.Equal("awaiting_user", (await ScriptPlanTests.Propose(r, await ScriptPlanTests.NewTask(r)))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_both_flags_it_runs_through_the_brokers_runner_only_and_ends_like_any_script()
    {
        var (r, broker) = await Start();
        await using var _ = r;
        var plan = await ScriptPlanTests.Proposed(r, ScriptPlanTests.Args(elevated: true), ScriptPlanTests.Grant(admin: true));
        Assert.Equal("admin", plan.Str("privilege"));
        Assert.True(plan["script"]!["elevated"]!.GetValue<bool>());
        var op = await ScriptPlanTests.RunToEnd(r, plan.Str("planId"));
        Assert.Equal("completed", op.Str("state"));
        Assert.Equal(1, broker.Calls);
        Assert.Equal(0, r.Runner.Calls); // never the user-scope runner
        var req = Assert.Single(broker.Requests);
        Assert.Equal((plan.Str("planId"), plan["script"].Str("sha256")), (req.PlanId, req.Sha256));
        Assert.Equal(0, op["receipt"]!["exitCode"]!.GetValue<int>());
        var tr = await r.C.Call("operation.transcript", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Contains("[out] as admin", tr.Str("text"));
        // And a user-scope plan in the same host uses the user runner.
        var userPlan = await ScriptPlanTests.Proposed(r);
        await ScriptPlanTests.RunToEnd(r, userPlan.Str("planId"));
        Assert.Equal((1, 1), (broker.Calls, r.Runner.Calls));
    }

    [Fact]
    public async Task Restore_point_first_says_a_second_prompt_follows_and_a_broker_death_is_indeterminate()
    {
        var (r, broker) = await Start(restorePoint: true);
        await using var _ = r;
        var plan = await ScriptPlanTests.Proposed(r, ScriptPlanTests.Args(elevated: true), ScriptPlanTests.Grant(admin: true));
        broker.Gate = new TaskCompletionSource();
        var op = await ScriptPlanTests.Approve(r, plan.Str("planId"), ScriptPlanTests.Options(restorePoint: true));
        await r.C.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Asking Windows for permission again, to run the script");
        while (broker.Started == 0) await Task.Delay(10);
        // The broker died mid-run: the runner reports the job gone without a stop from us.
        broker.Gate.SetCanceled();
        await ScriptPlanTests.Ended(r.C, plan.Str("planId"));
        var done = await r.C.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("created", done["restorePoint"].Str("result"));
        Assert.Equal(1, r.Rp!.Calls); // its own broker session, before the script's
        Assert.Equal(("indeterminate", "interrupted"), (done.Str("state"), done["error"].Str("code")));
    }

    [Fact]
    public async Task Cancelled_in_the_brokers_window_or_refused_by_its_guard_did_not_run()
    {
        var (r, broker) = await Start();
        await using var _ = r;
        broker.NotStartedCode = "broker_cancelled";
        var op = await ScriptPlanTests.RunToEnd(r, (await ScriptPlanTests.Proposed(r, ScriptPlanTests.Args(elevated: true), ScriptPlanTests.Grant(admin: true))).Str("planId"));
        Assert.Equal(("failed", "not_started"), (op.Str("state"), op["error"].Str("code")));
        Assert.Contains("cancelled in Windows", op["error"].Str("message"));
        broker.NotStartedCode = "guard_dynamic_code";
        var op2 = await ScriptPlanTests.RunToEnd(r, (await ScriptPlanTests.Proposed(r, ScriptPlanTests.Args(elevated: true), ScriptPlanTests.Grant(admin: true))).Str("planId"));
        Assert.Contains("own check refused", op2["error"].Str("message"));
    }
}

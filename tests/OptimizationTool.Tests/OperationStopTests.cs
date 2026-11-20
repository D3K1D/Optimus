using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Tests;

/// <summary>
/// operation.stop on a single long provider operation (a Defender scan shaped fake): the handler's run is cancelled, the
/// operation ends failed with error code cancelled and the plan cancelled (no receipt, nothing verified), its locks are
/// free once the run ended; a second stop and a stop after the end are no-ops; a setting-kind operation ignores stop.
/// </summary>
public class OperationStopTests
{
    private static Task<JsonObject> Ended(Client c, string planId) =>
        c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == planId
            && e["payload"].Str("state") is "verified" or "completed" or "failed" or "indeterminate");

    [Fact]
    public async Task Stop_ends_a_running_scan_cancelled_and_frees_its_target()
    {
        var pc = new FakePc();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c, "defender.quick_scan", scheme: null);
        var op = await Act.Approve(c, plan);
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Scanning · quick");

        var stop = new JsonObject { ["operationId"] = op.Str("operationId") };
        await c.Call("operation.stop", stop);
        await c.Call("operation.stop", stop);                        // twice: still one cancel
        var ended = await Ended(c, plan.Str("planId"));
        Assert.Equal("failed", ended["payload"].Str("state"));
        var done = await c.Call("operation.get", stop);
        SchemaTests.AssertValid("operation", done);
        Assert.Equal(("failed", "cancelled"), (done.Str("state"), done["error"].Str("code")));
        Assert.Null(done["receipt"]);
        Assert.False(done["undo"]!["available"]!.GetValue<bool>());
        var after = await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") });
        Assert.Equal("cancelled", after.Str("status"));
        Assert.Equal("2026-11-01T00:00:00Z", pc.LastScan);              // nothing recorded as done

        // After the end: a no-op that returns the same operation.
        var again = await c.Call("operation.stop", stop);
        Assert.Equal(("failed", "cancelled"), (again.Str("state"), again["error"].Str("code")));

        // Its target is free: the next scan runs and finishes.
        pc.ProviderGate.SetResult();
        var second = await Act.Preview(c, "defender.quick_scan", scheme: null);
        await Act.Approve(c, second);
        Assert.Equal("verified", (await Ended(c, second.Str("planId")))["payload"].Str("state"));
        Assert.Equal(2, pc.ProviderRuns);
    }

    /// <summary>
    /// m9 review R5: a Stop that arrives before the run registers its stop signal (queued: still prepared; or between the
    /// attempted state and the registration) is kept and applied: the operation ends exactly as a normal stop does, and the
    /// tool never starts.
    /// </summary>
    [Theory]
    [InlineData("queued", "prepared")]
    [InlineData("registering", "attempted")]
    public async Task An_early_stop_is_kept_and_ends_the_scan_cancelled_before_the_tool_starts(string stage, string stateWhenStopped)
    {
        var pc = new FakePc();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var actions = host.Server.Engine.Actions;
        var stopped = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.RunSeam = (at, operationId) =>
        {
            if (at == stage) stopped.TrySetResult((JsonObject)actions.OperationStop(new JsonObject { ["operationId"] = operationId }));
            return Task.CompletedTask;
        };
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c, "defender.quick_scan", scheme: null);
        var op = await Act.Approve(c, plan);

        // operation.stop answers the operation as it is at that moment (not yet ended), as a stop of a running scan does.
        var answer = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((op.Str("operationId"), stateWhenStopped), (answer.Str("operationId"), answer.Str("state")));
        SchemaTests.AssertValid("operation", answer);

        var ended = await Ended(c, plan.Str("planId"));
        Assert.Equal("failed", ended["payload"].Str("state"));
        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        SchemaTests.AssertValid("operation", done);
        Assert.Equal(("failed", "cancelled", "Stopped."), (done.Str("state"), done["error"].Str("code"), done["error"].Str("message")));
        Assert.Null(done["receipt"]);
        Assert.Null(done["result"]);                                    // a scan's stop names no result, as a normal stop
        Assert.False(done["undo"]!["available"]!.GetValue<bool>());
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Equal(0, pc.ProviderRuns);                               // the tool never started
        Assert.Equal(0, actions.PendingStopCount);                      // nothing kept after the end

        // Its target is free: the next scan runs and finishes, and the earlier stop does not reach it.
        actions.RunSeam = null;
        pc.ProviderGate.SetResult();
        var second = await Act.Preview(c, "defender.quick_scan", scheme: null);
        await Act.Approve(c, second);
        Assert.Equal("verified", (await Ended(c, second.Str("planId")))["payload"].Str("state"));
        Assert.Equal(1, pc.ProviderRuns);
    }

    [Fact]
    public async Task A_stop_after_the_scan_finished_stays_a_no_op_and_keeps_nothing()
    {
        var pc = new FakePc();
        pc.ProviderGate.SetResult();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c, "defender.quick_scan", scheme: null);
        var op = await Act.Approve(c, plan);
        Assert.Equal("verified", (await Ended(c, plan.Str("planId")))["payload"].Str("state"));
        var answer = await c.Call("operation.stop", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal(("verified", "no_threats"), (answer.Str("state"), answer.Str("result")));
        Assert.Equal(0, host.Server.Engine.Actions.PendingStopCount);
        Assert.Equal("verified", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
    }

    /// <summary>
    /// m9 review S5: the preparation transaction fails at its commit (a deferred foreign key the test adds to every new
    /// operation): no operation, no run, and no pending-stop entry is left behind.
    /// </summary>
    [Fact]
    public async Task A_failed_preparation_commit_leaves_no_pending_stop()
    {
        var pc = new FakePc();
        pc.ProviderGate.SetResult();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var store = host.Server.Engine.Store;
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c, "defender.quick_scan", scheme: null);
        store.Read(db => db.Exec("PRAGMA foreign_keys = ON"));
        store.Write(db =>
        {
            db.Exec("CREATE TABLE s5_parent (id TEXT PRIMARY KEY)");
            db.Exec("CREATE TABLE s5_guard (id TEXT REFERENCES s5_parent(id) DEFERRABLE INITIALLY DEFERRED)");
            db.Exec("CREATE TRIGGER s5_fail AFTER INSERT ON operations BEGIN INSERT INTO s5_guard VALUES (NEW.operation_id); END");
        });

        await Assert.ThrowsAnyAsync<Exception>(() => Act.Approve(c, plan));
        Assert.Null(store.Read(db => db.Scalar("SELECT operation_id FROM operations WHERE plan_id = $p", ("$p", plan.Str("planId")))));
        Assert.Equal(0, pc.ProviderRuns);
        Assert.Equal(0, host.Server.Engine.Actions.PendingStopCount);
    }

    [Fact]
    public async Task Stop_on_a_setting_change_is_a_no_op()
    {
        var pc = new FakePc { WriteDelay = TimeSpan.FromMilliseconds(400) };
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c, "power.select_scheme", scheme: "balanced");
        var op = await Act.Approve(c, plan);
        await c.Call("operation.stop", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("verified", (await Ended(c, plan.Str("planId")))["payload"].Str("state"));
        Assert.Equal(1, pc.Writes);
    }
}

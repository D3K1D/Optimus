using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Tests.Scripts;
using OptimizationTool.Windows.Actions.Broker;

namespace OptimizationTool.Tests;

/// <summary>
/// m9 §13 R2 "Locks follow the writer": resource locks and the script lease stay held until the broker process (or a
/// straggling handler) really ends, not until the pipe answers or breaks, and a failed finish transaction does not release
/// them early. The real host-side BrokerElevation runs over a local unelevated pipe; the "broker" is a scripted client in
/// this process and its process is a fake that exits when the test says so. Nothing is elevated, nothing touches this PC.
/// </summary>
public class LocksFollowTheWriterTests
{
    private static readonly string Self = Environment.ProcessPath!; // the "broker" image the fake client really runs as

    private sealed class FakeProcess : IBrokerProcess
    {
        public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited => Exit.Task.IsCompleted;
        public Task WaitForExitAsync() => Exit.Task;
        public void Dispose() { }
    }

    /// <summary>
    /// A scripted broker per launch: connects, runs <see cref="Script"/>, and its process exits only once the script ended
    /// AND <see cref="Linger"/> completed. <see cref="Handle"/> false: Windows gave no process handle.
    /// </summary>
    private sealed class ScriptedBroker : IBrokerLauncher
    {
        public Func<NamedPipeClientStream, string, Task> Script = (_, _) => Task.CompletedTask;
        public TaskCompletionSource Linger = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Handle = true;
        public int Launches;

        public BrokerLaunch Launch(string brokerPath, string arguments)
        {
            Interlocked.Increment(ref Launches);
            var parts = arguments.Split(' ');
            var (pipeName, nonce) = (parts[1], parts[3]);
            var process = Handle ? new FakeProcess() : null;
            var (script, linger) = (Script, Linger);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                    await pipe.ConnectAsync(5000);
                    await script(pipe, nonce);
                }
                catch (Exception) { /* the host closed the pipe */ }
                finally
                {
                    await linger.Task;
                    process?.Exit.TrySetResult();
                }
            });
            return new BrokerLaunch(true, false, (uint)Environment.ProcessId, Process: process);
        }
    }

    private static async Task AnswerVerified(NamedPipeClientStream p, string n)
    {
        var req = BrokerRequest.Parse(await BrokerProtocol.ReadAsync(p, CancellationToken.None))!;
        var answer = new BrokerResponse(n, [.. req.Steps.Select(s => new BrokerStepOutcome(s.N, "verified", new JsonObject { ["value"] = "on" }, "Done."))], "rcpt-00000000000000c1");
        await BrokerProtocol.WriteAsync(p, answer.ToJson(), CancellationToken.None);
    }

    private static Func<NamedPipeClientStream, string, Task> Failing(string kind, TaskCompletionSource linger) => async (p, n) =>
    {
        await BrokerProtocol.ReadAsync(p, CancellationToken.None);
        switch (kind)
        {
            case "pipe_loss": p.Dispose(); break;                       // the pipe breaks, the broker keeps writing
            case "timeout": await linger.Task; break;                   // no answer in time, the pipe stays open
            default: await BrokerProtocol.WriteAsync(p, new JsonObject { ["nonce"] = n, ["what"] = "not a response" }, CancellationToken.None); break;
        }
    };

    private static string JournalRoot() => Path.Combine(Path.GetTempPath(), "ot-locks-tests", Guid.NewGuid().ToString("N"));

    private static async Task<(TestHost Host, Client C)> StartAdmin(ScriptedBroker broker)
    {
        var pc = new FakeSettings().Set("c", "off");
        pc.Privilege["c"] = "admin";
        var elevation = new BrokerElevation(Self, broker, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400), JournalRoot());
        var host = TestHost.StartActions([new FakeActionModule(new FakeSettingHandler(pc))], elevation: elevation, enableAdmin: true);
        return (host, await host.ConnectAsync(minor: 3));
    }

    private static Task<JsonObject> PreviewC(Client c) => c.Call("action.preview", new JsonObject
    {
        ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "setting.set", ["parameters"] = new JsonObject { ["settingId"] = "c", ["value"] = "on" },
    });

    private static async Task<string?> Refusal(Client c, JsonObject plan)
    {
        try
        {
            await c.Call("action.approve", Act.ApproveParams(plan, await Act.Nonce(c, plan.Str("planId"))));
            return null;
        }
        catch (OptimizationTool.Transport.JsonRpcException e) { return (e.ErrorData as JsonObject)?["reason"]?.GetValue<string>() ?? e.Message; }
    }

    private static async Task<JsonObject> ApproveWhenFree(Client c, JsonObject plan)
    {
        for (var i = 0; ; i++)
        {
            try { return await Act.Approve(c, plan); }
            catch (OptimizationTool.Transport.JsonRpcException) when (i < 100) { await Task.Delay(50); }
        }
    }

    [Theory]
    [InlineData("pipe_loss")]
    [InlineData("timeout")]
    [InlineData("bad_frame")]
    public async Task An_admin_write_whose_broker_outlives_the_transport_keeps_its_locks_until_the_process_exits(string kind)
    {
        var broker = new ScriptedBroker();
        var linger = broker.Linger;
        broker.Script = Failing(kind, linger);
        var (host, c) = await StartAdmin(broker);
        await using var _ = host;

        var op = await Act.Approve(c, await PreviewC(c));
        Assert.Equal("indeterminate", op.Str("state"));

        // The broker may still be writing: the same resource is refused, and no second broker starts.
        var second = await PreviewC(c);
        Assert.Equal("action_active", await Refusal(c, second));
        await Task.Delay(300);
        Assert.Equal("action_active", await Refusal(c, second));
        Assert.Equal(1, broker.Launches);

        // The broker process exits: the locks are released and the next plan runs.
        broker.Script = AnswerVerified;
        broker.Linger = new TaskCompletionSource();
        broker.Linger.SetResult();
        linger.SetResult();
        Assert.Equal("verified", (await ApproveWhenFree(c, second)).Str("state"));
        Assert.Equal(2, broker.Launches);
    }

    [Fact]
    public async Task Without_a_process_handle_the_locks_stay_held()
    {
        var broker = new ScriptedBroker { Handle = false };
        broker.Script = Failing("pipe_loss", broker.Linger);
        broker.Linger.SetResult(); // the "process" ends at once, but the host can't know it
        var (host, c) = await StartAdmin(broker);
        await using var _ = host;

        Assert.Equal("indeterminate", (await Act.Approve(c, await PreviewC(c))).Str("state"));
        var second = await PreviewC(c);
        await Task.Delay(500);
        Assert.Equal("action_active", await Refusal(c, second)); // held until the host restarts (restart reconcile classifies it)
        Assert.Equal(1, broker.Launches);
    }

    [Fact]
    public async Task An_elevated_script_whose_pipe_breaks_keeps_the_lease_and_the_exclusion_until_the_broker_exits()
    {
        var broker = new ScriptedBroker();
        var linger = broker.Linger;
        broker.Script = async (p, n) =>
        {
            await BrokerProtocol.ReadAsync(p, CancellationToken.None);
            await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Started(n, 1), CancellationToken.None);
            p.Dispose(); // the pipe dies mid-run; the elevated script may still be running
        };
        var pc = new FakePc();
        var scripts = new ScriptOptions
        {
            Runner = new FakeScriptRunner(), ElevatedRunner = new BrokerScriptRunner(new BrokerElevation(Self, broker, TimeSpan.FromSeconds(10), journalRoot: JournalRoot())),
            EnableElevatedScripts = true, Parser = new FakeScriptParser(), Policy = new FakeScriptPolicy(),
            ProgressInterval = TimeSpan.FromMilliseconds(40), Identity = () => new ScrubIdentity(["Sam"], ["DESKTOP-SAM7"], []),
        };
        var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false),
            agent: AgentSetup.Options(null, new FakeProvider("anthropic_api")), scripts: scripts, enableAdmin: true);
        await using var r = new ScriptPlanTests.Rig(host, await host.ConnectAsync(minor: 3), (FakeScriptRunner)scripts.Runner, new FakeScriptParser(), new FakeScriptPolicy(), null, pc);

        var plan = await ScriptPlanTests.Proposed(r, ScriptPlanTests.Args(elevated: true), ScriptPlanTests.Grant(admin: true));
        var op = await ScriptPlanTests.Approve(r, plan.Str("planId"));
        await ScriptPlanTests.Ended(r.C, plan.Str("planId"));
        Assert.Equal("indeterminate", (await r.C.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") })).Str("state"));

        await Task.Delay(300);
        Assert.Equal(op.Str("operationId"), host.Server.Engine.Lease.Current?.Id); // still leased
        Assert.Equal("action_active", await Refusal(r.C, await Act.Preview(r.C)));  // and every other action is excluded

        linger.SetResult();
        for (var i = 0; host.Server.Engine.Lease.Current is not null; i++)
        {
            Assert.True(i < 200, "the lease was never released");
            await Task.Delay(25);
        }
        Assert.Equal("verified", (await ApproveWhenFree(r.C, await Act.Preview(r.C))).Str("state"));
    }

    /// <summary>
    /// A broker call whose first session answers exactly while our deadline wins (after the cancellation, before the catch
    /// looks at the call): its answer carries an unfinished Ended. Later sessions answer verified at once.
    /// </summary>
    private sealed class RacingElevation : IElevation
    {
        public readonly TaskCompletionSource<ElevatedResult> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct) =>
            Interlocked.Increment(ref Calls) == 1 ? First.Task
                : Task.FromResult(new ElevatedResult([.. request.Steps.Select(s => (s.N, new ActionOutcome("verified", (JsonObject)s.After.DeepClone(), "Done.")))],
                    "rcpt-00000000000000c2", true));

        /// <summary>The first call returns now (its broker process still running).</summary>
        public void Answer() => First.TrySetResult(new ElevatedResult([(1, new ActionOutcome("verified", [], "Done."))], "rcpt-00000000000000c1", true)
        {
            Ended = Exit.Task,
        });

        public ElevatedJournal? Journal(string planId) => new(null, []);
    }

    /// <summary>
    /// m9 review S1: the deadline wins, then the broker call completes before the catch inspects it; its Ended still holds
    /// the locks until the broker process exits. Single administrator operation and an administrator fix step alike.
    /// </summary>
    [Theory]
    [InlineData("single")]
    [InlineData("fix")]
    public async Task A_broker_call_completing_during_the_deadline_keeps_the_locks_until_its_process_exits(string path)
    {
        var pc = new FakeSettings().Set("c", "off");
        pc.Privilege["c"] = "admin";
        var elevation = new RacingElevation();
        var host = TestHost.StartActions([new FakeActionModule(new FakeSettingHandler(pc))], elevation: elevation, enableAdmin: true);
        await using var _ = host;
        var actions = host.Server.Engine.Actions;
        actions.ElevationWaitSeam = TimeSpan.FromMilliseconds(100);
        actions.ElevationCancelledSeam = planId =>
        {
            elevation.Answer();
            return Task.CompletedTask;
        };
        var c = await host.ConnectAsync(minor: 3);

        if (path == "single") Assert.Equal("indeterminate", (await Act.Approve(c, await PreviewC(c))).Str("state"));
        else
        {
            var fix = await actions.CreateFixAsync("Fix", "Why.", "Claude",
                [new FixStepRequest("setting.set", new JsonObject { ["settingId"] = "c", ["value"] = "on" }, null, null)], null, RequestedBy.User);
            var op = await Act.Approve(c, fix);
            for (var i = 0; (await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") })).Str("state") is not "indeterminate"; i++)
            {
                Assert.True(i < 400, "the fix never ended");
                await Task.Delay(25);
            }
        }
        Assert.True(elevation.First.Task.IsCompleted); // the call did complete, after the deadline won

        // The broker process has not exited: the same resource is refused, and no second session starts.
        var second = await PreviewC(c);
        Assert.Equal("action_active", await Refusal(c, second));
        await Task.Delay(300);
        Assert.Equal("action_active", await Refusal(c, second));
        Assert.Equal(1, elevation.Calls);

        // It exits: the locks are released and the next plan runs.
        actions.ElevationCancelledSeam = null;
        elevation.Exit.SetResult();
        Assert.Equal("verified", (await ApproveWhenFree(c, second)).Str("state"));
        Assert.Equal(2, elevation.Calls);
    }

    [Fact]
    public async Task A_failed_finish_with_an_unfinished_handler_keeps_the_locks_until_the_handler_ends()
    {
        var stubborn = new FakeStubbornHandler();
        var other = new FakeStubbornHandler("test.stubborn_b");
        await using var host = TestHost.StartActions([new FakeActionModule(stubborn, other)], settingTimeout: TimeSpan.FromSeconds(1));
        var c = await host.ConnectAsync(minor: 3);

        var first = await Act.Preview(c, "test.stubborn", scheme: null);
        var call = c.Call("action.approve", Act.ApproveParams(first, await Act.Nonce(c, first.Str("planId"))));
        for (var i = 0; stubborn.Runs == 0; i++)
        {
            Assert.True(i < 400, "the handler never started");
            await Task.Delay(5);
        }
        // The finish transaction will fail: the operation row is unreadable when the deadline passes.
        host.Server.Engine.Store.Write(db => db.Exec("UPDATE operations SET body = '{' WHERE plan_id = $p", ("$p", first.Str("planId"))));
        await Assert.ThrowsAsync<OptimizationTool.Transport.JsonRpcException>(() => call);

        // The write may still land: the same resource stays locked.
        var second = await Act.Preview(c, "test.stubborn_b", scheme: null);
        Assert.Equal("action_active", await Refusal(c, second));
        await Task.Delay(200);
        Assert.Equal("action_active", await Refusal(c, second));
        Assert.Equal(0, other.Runs);

        stubborn.Gate.SetResult();
        await stubborn.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        other.Gate.SetResult();
        await ApproveWhenFree(c, second);
        Assert.Equal(1, other.Runs);
    }
}

using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// Host side of the boundary over a real (unelevated, local) named pipe, with a fake launcher standing in for UAC
/// and a scripted fake broker. Nothing is elevated and the firewall is never touched.
/// </summary>
public sealed class BrokerElevationTests
{
    private static readonly string Self = Environment.ProcessPath!; // the "broker" image the fake client really runs as

    private static ElevatedStep Firewall(int n = 1) =>
        new(n, FirewallPlan.ActionId, FirewallPlan.HandlerVersion, FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null);

    private static ElevatedRequest Request(params ElevatedStep[] steps) => new("plan-1", "hash-1", steps.Length == 0 ? [Firewall()] : steps);

    /// <summary>The launched broker process as the host sees it: exits when <see cref="Exit"/> completes.</summary>
    private sealed class FakeProcess : IBrokerProcess
    {
        public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Disposed;
        public bool HasExited => Exit.Task.IsCompleted;
        public Task WaitForExitAsync() => Exit.Task;
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Connects to the host's pipe like the broker would and runs the script. Its fake process exits when the script ends
    /// and, when <see cref="Linger"/> is set, only once that completes too (a broker still writing after the pipe is gone).
    /// <paramref name="handle"/> false: Windows reported a start but gave no process handle.
    /// </summary>
    private sealed class FakeLauncher(Func<NamedPipeClientStream, string, Task>? script, bool cancel = false, bool handle = true) : IBrokerLauncher
    {
        public string? Arguments { get; private set; }
        public Task? Running { get; private set; }
        public int Launches { get; private set; }
        public FakeProcess? Process { get; private set; }
        public TaskCompletionSource? Linger { get; init; }

        public BrokerLaunch Launch(string brokerPath, string arguments)
        {
            Launches++;
            Arguments = arguments;
            if (cancel) return new BrokerLaunch(false, true, 0);
            var parts = arguments.Split(' ');
            var parsed = BrokerArgs.Parse(parts) ?? throw new InvalidOperationException("bad args");
            var process = handle ? new FakeProcess() : null;
            Process = process;
            if (script is not null)
                Running = Task.Run(async () =>
                {
                    try
                    {
                        using var pipe = new NamedPipeClientStream(".", parsed.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                        await pipe.ConnectAsync(5000);
                        await script(pipe, parsed.Nonce);
                    }
                    finally
                    {
                        if (Linger is not null) await Linger.Task;
                        process?.Exit.TrySetResult();
                    }
                });
            else process?.Exit.TrySetResult();
            return new BrokerLaunch(true, false, (uint)Environment.ProcessId, Process: process);
        }
    }

    // ---------------------------------------------------------------- the session ends with the broker process (m9 §13 R2)

    [Theory]
    [InlineData("pipe_loss")]
    [InlineData("timeout")]
    [InlineData("bad_frame")]
    public async Task After_a_transport_failure_the_session_ends_only_when_the_broker_process_exits(string kind)
    {
        var linger = new TaskCompletionSource();
        var launcher = new FakeLauncher(async (p, n) =>
        {
            await BrokerProtocol.ReadAsync(p, CancellationToken.None);
            switch (kind)
            {
                case "pipe_loss": p.Dispose(); break;
                case "timeout": await Task.Delay(1500); break;
                default: await BrokerProtocol.WriteAsync(p, new JsonObject { ["nonce"] = n, ["what"] = "not a response" }, CancellationToken.None); break;
            }
        }) { Linger = linger };
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400));
        var result = await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        Assert.Equal("indeterminate", result.Steps[0].Outcome.State);
        Assert.True(result.Dispatched);
        var ended = result.Ended!;
        await Task.Delay(300);
        Assert.False(ended.IsCompleted); // the pipe is gone, the broker may still be writing
        Assert.False(launcher.Process!.Disposed);
        linger.SetResult();
        await ended.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(launcher.Process.Disposed); // the handle is closed only after the process ended
    }

    [Fact]
    public async Task A_clean_answer_also_ends_with_the_process_and_a_refusal_before_sending_ends_at_once()
    {
        var linger = new TaskCompletionSource();
        var launcher = new FakeLauncher((p, n) => Answer(p, n, "verified")) { Linger = linger };
        var result = await new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10))
            .RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        Assert.Equal("verified", result.Steps[0].Outcome.State);
        await Task.Delay(100);
        Assert.False(result.Ended!.IsCompleted);
        linger.SetResult();
        await result.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        // Nothing was sent (no connection): nothing to wait for, and the handle is closed.
        var silent = new FakeLauncher(null);
        var none = await new BrokerElevation(Self, silent, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1))
            .RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        Assert.False(none.Dispatched);
        Assert.True(none.Ended is null || none.Ended.IsCompleted);
        Assert.True(silent.Process!.Disposed);
    }

    [Fact]
    public async Task Without_a_process_handle_the_session_never_counts_as_ended()
    {
        var launcher = new FakeLauncher(async (p, _) => { await BrokerProtocol.ReadAsync(p, CancellationToken.None); p.Dispose(); }, handle: false);
        var result = await new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2))
            .RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        await launcher.Running!;
        Assert.Equal("indeterminate", result.Steps[0].Outcome.State);
        await Task.Delay(300);
        Assert.False(result.Ended!.IsCompleted); // termination can't be established: held until the host restarts
    }

    [Fact]
    public async Task Elevated_script_pipe_loss_ends_only_when_the_broker_process_exits()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-broker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var linger = new TaskCompletionSource();
            var launcher = new FakeLauncher(async (p, n) =>
            {
                await BrokerProtocol.ReadAsync(p, CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Started(n, 1), CancellationToken.None);
                p.Dispose();
            }) { Linger = linger };
            var r = await new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10)).RunScriptAsync(ScriptReq(root), new Observer(), CancellationToken.None);
            Assert.True(r.Killed);
            await Task.Delay(300);
            Assert.False(r.Ended!.IsCompleted);
            linger.SetResult();
            await r.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>A scripted broker: reads the v2 request, checks every hash, answers each step with the given state.</summary>
    private static async Task<BrokerRequest> Answer(NamedPipeClientStream pipe, string nonce, string state, bool echoNonce = true)
    {
        var req = BrokerRequest.Parse(await BrokerProtocol.ReadAsync(pipe, CancellationToken.None))!;
        Assert.Equal(nonce, req.Nonce);
        foreach (var s in req.Steps) Assert.Equal(BrokerContent.Hash(s.ActionId, s.HandlerVersion, s.Target, s.Before, s.After, s.Parameters), s.ContentHash);
        var response = new BrokerResponse(echoNonce ? nonce : new string('f', 64),
            [.. req.Steps.Select(s => new BrokerStepOutcome(s.N, state, FirewallPlan.State(true, true), "done"))], "rcpt-0000000000000001");
        await BrokerProtocol.WriteAsync(pipe, response.ToJson(), CancellationToken.None);
        return req;
    }

    [Fact]
    public async Task Verified_answer_maps_per_step_with_the_brokers_receipt()
    {
        var launcher = new FakeLauncher((p, n) => Answer(p, n, "verified"));
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var result = await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        Assert.Equal("verified", Assert.Single(result.Steps).Outcome.State);
        Assert.Equal("rcpt-0000000000000001", result.ReceiptId);
        Assert.True(result.Dispatched);
        Assert.Matches("^--pipe OptimizationTool\\.Broker\\.[0-9a-f]{32} --nonce [0-9a-f]{64}$", launcher.Arguments);
        await launcher.Running!;
    }

    [Fact]
    public async Task Progress_frames_of_a_long_step_reach_the_caller_and_bad_ones_are_dropped()
    {
        var launcher = new FakeLauncher(async (p, n) =>
        {
            var req = BrokerRequest.Parse(await BrokerProtocol.ReadAsync(p, CancellationToken.None))!;
            await BrokerProtocol.WriteAsync(p, BrokerProgress.ToJson(n, 1, "Checking Windows files · 10%"), CancellationToken.None);
            await BrokerProtocol.WriteAsync(p, BrokerProgress.ToJson(new string('f', 64), 1, "other session"), CancellationToken.None); // another nonce
            await BrokerProtocol.WriteAsync(p, BrokerProgress.ToJson(n, 5, "a step not sent"), CancellationToken.None);
            await BrokerProtocol.WriteAsync(p, BrokerProgress.ToJson(n, 1, "Checking Windows files · 80%"), CancellationToken.None);
            var answer = new BrokerResponse(n, [.. req.Steps.Select(s => new BrokerStepOutcome(s.N, "completed", new JsonObject { ["result"] = "no_violations" }, "done"))], "rcpt-0000000000000002");
            await BrokerProtocol.WriteAsync(p, answer.ToJson(), CancellationToken.None);
        });
        var seen = new List<(int, string)>();
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var step = new ElevatedStep(1, "repair.sfc", "1.0.0", new JsonObject { ["type"] = "repair", ["id"] = "sfc" }, [], new JsonObject { ["operation"] = "sfc" }, null,
            Long: true, Deadline: TimeSpan.FromMinutes(60));
        var result = await e.RunAsync(new ElevatedRequest("plan-1", "hash-1", [step]) { Progress = new SyncProgress(seen) }, CancellationToken.None, CancellationToken.None);
        await launcher.Running!;
        Assert.Equal("completed", Assert.Single(result.Steps).Outcome.State);
        Assert.Equal([(1, "Checking Windows files · 10%"), (1, "Checking Windows files · 80%")], seen);
        // The host's wait covers the tool's own deadline (60 min) plus the broker's window and margins.
        Assert.True(BrokerElevation.ResponseWait([step]) >= TimeSpan.FromMinutes(64));
        Assert.True(BrokerElevation.ResponseWait([step with { Deadline = TimeSpan.FromMinutes(90) }]) >= TimeSpan.FromMinutes(94));
    }

    // ---------------------------------------------------------------- elevated scripts, host side (m9 §5.4)

    private sealed class Observer : OptimizationTool.Engine.Actions.Scripts.IScriptRunObserver
    {
        public string? Folder;
        public readonly List<(bool, string)> Lines = [];
        public void Started(DateTimeOffset at, string runFolder) => Folder = runFolder;
        public void Line(bool stderr, string text) { lock (Lines) Lines.Add((stderr, text)); }
    }

    private static OptimizationTool.Engine.Actions.Scripts.ScriptRunRequest ScriptReq(string root)
    {
        var ser = OptimizationTool.Engine.Actions.Scripts.ScriptText.Serialise("Write-Output \"hi\"");
        return new(op, ser.Bytes, ser.Sha256, TimeSpan.FromMinutes(1), true, root) { PlanId = "plan-1" };
    }
    private const string op = "op-0000000000000009";

    [Fact]
    public async Task Elevated_script_frames_reach_the_observer_and_the_end_maps_to_the_run_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-broker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            BrokerScriptRequest? seen = null;
            var launcher = new FakeLauncher(async (p, n) =>
            {
                seen = BrokerScriptRequest.Parse(await BrokerProtocol.ReadAsync(p, CancellationToken.None));
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Started(n, 1), CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Transcript(n, 2, [(false, "hi"), (true, "warn")]), CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.End(n, 3, "exited", 7, null, 2, false), CancellationToken.None);
            });
            var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            var obs = new Observer();
            var result = await new BrokerScriptRunner(e).RunAsync(ScriptReq(root), obs, CancellationToken.None);
            await launcher.Running!;
            Assert.Equal((true, 7), (result.Exited, result.ExitCode!.Value));
            Assert.Equal([(false, "hi"), (true, "warn")], obs.Lines);
            Assert.StartsWith(root, obs.Folder); // the transcript is kept under this host's runs root
            Assert.Equal(("plan-1", op, true), (seen!.PlanId, seen.OperationId, seen.BypassPolicy));
            Assert.Equal(ScriptReq(root).Sha256, seen.Sha256);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Elevated_script_out_of_sequence_or_a_dead_broker_is_killed_and_refusals_are_not_started()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-broker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            JsonNode? stopFrame = null;
            var gap = new FakeLauncher(async (p, n) =>
            {
                await BrokerProtocol.ReadAsync(p, CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Started(n, 1), CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Transcript(n, 3, [(false, "skipped a frame")]), CancellationToken.None);
                stopFrame = await BrokerProtocol.ReadAsync(p, CancellationToken.None);
            });
            var r1 = await new BrokerElevation(Self, gap, TimeSpan.FromSeconds(10)).RunScriptAsync(ScriptReq(root), new Observer(), CancellationToken.None);
            await gap.Running!;
            Assert.True(r1.Killed);
            Assert.True(BrokerStop.Is(stopFrame, BrokerArgs.Parse(gap.Arguments!.Split(' '))!.Nonce));

            var dies = new FakeLauncher(async (p, n) =>
            {
                await BrokerProtocol.ReadAsync(p, CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Started(n, 1), CancellationToken.None);
                p.Dispose(); // the broker died mid-run
            });
            var r2 = await new BrokerElevation(Self, dies, TimeSpan.FromSeconds(10)).RunScriptAsync(ScriptReq(root), new Observer(), CancellationToken.None);
            Assert.True(r2.Killed); // the engine records it indeterminate

            var refused = new FakeLauncher(async (p, n) =>
            {
                await BrokerProtocol.ReadAsync(p, CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.End(n, 1, "not_started", null, "guard_boot_or_disk", 0, false), CancellationToken.None);
            });
            Assert.Equal("guard_boot_or_disk", (await new BrokerElevation(Self, refused, TimeSpan.FromSeconds(10)).RunScriptAsync(ScriptReq(root), new Observer(), CancellationToken.None)).NotStartedCode);

            var uac = await new BrokerElevation(Self, new FakeLauncher(null, cancel: true)).RunScriptAsync(ScriptReq(root), new Observer(), CancellationToken.None);
            Assert.Equal("uac_cancelled", uac.NotStartedCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Elevated_script_stop_sends_the_stop_frame()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-broker-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var stop = new CancellationTokenSource();
            var launcher = new FakeLauncher(async (p, n) =>
            {
                await BrokerProtocol.ReadAsync(p, CancellationToken.None);
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.Started(n, 1), CancellationToken.None);
                await stop.CancelAsync();
                var f = await BrokerProtocol.ReadAsync(p, CancellationToken.None);
                Assert.True(BrokerStop.Is(f, n));
                await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.End(n, 2, "killed", null, "stopped", 0, false), CancellationToken.None);
            });
            var r = await new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10)).RunScriptAsync(ScriptReq(root), new Observer(), stop.Token);
            await launcher.Running!;
            Assert.True(r.Killed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SyncProgress(List<(int, string)> seen) : IProgress<(int N, string Text)>
    {
        public void Report((int N, string Text) value) { lock (seen) seen.Add(value); }
    }

    [Fact]
    public async Task Several_admin_steps_go_in_one_session_in_the_plans_order()
    {
        BrokerRequest? seen = null;
        var launcher = new FakeLauncher(async (p, n) => seen = await Answer(p, n, "verified"));
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var steps = new[]
        {
            new ElevatedStep(2, "setting.set", "1.0.0", new JsonObject { ["type"] = "setting", ["id"] = "x" }, [], [], new JsonObject { ["settingId"] = "x", ["value"] = "on" }),
            Firewall(4),
        };
        var result = await e.RunAsync(Request(steps), CancellationToken.None, CancellationToken.None);
        await launcher.Running!;
        Assert.Equal(1, launcher.Launches); // one UAC prompt
        Assert.Equal([2, 4], seen!.Steps.Select(s => s.N));
        Assert.Equal([2, 4], result.Steps.Select(s => s.N));
    }

    [Fact]
    public async Task Uac_cancelled_is_declined_and_not_dispatched()
    {
        var e = new BrokerElevation(Self, new FakeLauncher(null, cancel: true));
        var result = await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        var outcome = Assert.Single(result.Steps).Outcome;
        Assert.Equal("declined", outcome.State);
        Assert.Equal("uac_cancelled", outcome.Code);
        Assert.False(result.Dispatched);
    }

    [Fact]
    public async Task Broker_cancelled_in_its_window_is_declined()
    {
        var launcher = new FakeLauncher(async (p, n) =>
        {
            var req = BrokerRequest.Parse(await BrokerProtocol.ReadAsync(p, CancellationToken.None))!;
            var answer = new BrokerResponse(n, [.. req.Steps.Select(s => new BrokerStepOutcome(s.N, "declined", [], "Cancelled.", "broker_cancelled"))], null);
            await BrokerProtocol.WriteAsync(p, answer.ToJson(), CancellationToken.None);
        });
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var outcome = Assert.Single((await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None)).Steps).Outcome;
        Assert.Equal("declined", outcome.State);
        Assert.Equal("broker_cancelled", outcome.Code);
    }

    [Fact]
    public async Task No_broker_connection_is_failed_because_nothing_was_sent()
    {
        var e = new BrokerElevation(Self, new FakeLauncher(null), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1));
        var result = await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        Assert.Equal("failed", result.Steps[0].Outcome.State);
        Assert.Equal("broker_timeout", result.Steps[0].Outcome.Code);
        Assert.False(result.Dispatched);
    }

    [Fact]
    public async Task Broker_that_never_answers_is_indeterminate_and_dispatched()
    {
        var launcher = new FakeLauncher(async (p, _) => { await BrokerProtocol.ReadAsync(p, CancellationToken.None); await Task.Delay(1500); });
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));
        var result = await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None);
        Assert.Equal("indeterminate", result.Steps[0].Outcome.State);
        Assert.True(result.Dispatched);
    }

    [Fact]
    public async Task Answer_with_wrong_nonce_is_indeterminate()
    {
        var launcher = new FakeLauncher((p, n) => Answer(p, n, "verified", echoNonce: false));
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var outcome = (await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None)).Steps[0].Outcome;
        Assert.Equal("indeterminate", outcome.State);
        Assert.Equal("bad_response", outcome.Code);
    }

    [Fact]
    public async Task Stop_is_forwarded_as_a_stop_frame()
    {
        using var stop = new CancellationTokenSource();
        var gotStop = new TaskCompletionSource<bool>();
        var launcher = new FakeLauncher(async (p, n) =>
        {
            await BrokerProtocol.ReadAsync(p, CancellationToken.None);
            stop.Cancel();
            gotStop.TrySetResult(BrokerStop.Is(await BrokerProtocol.ReadAsync(p, CancellationToken.None), n));
            var answer = new BrokerResponse(n, [new BrokerStepOutcome(1, "verified", [], "ok"), new BrokerStepOutcome(2, "skipped", [], "Not started.")], "rcpt-0000000000000002");
            await BrokerProtocol.WriteAsync(p, answer.ToJson(), CancellationToken.None);
        });
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var result = await e.RunAsync(Request(Firewall(1), Firewall(2)), stop.Token, CancellationToken.None);
        Assert.True(await gotStop.Task);
        Assert.Equal(["verified", "skipped"], result.Steps.Select(s => s.Outcome.State));
    }

    [Fact]
    public async Task Client_that_is_not_the_broker_image_is_refused()
    {
        var launcher = new FakeLauncher(async (p, _) => { try { await BrokerProtocol.ReadAsync(p, CancellationToken.None); } catch (InvalidDataException) { } });
        var otherImage = Path.Combine(Path.GetTempPath(), "not-the-broker.exe");
        File.WriteAllBytes(otherImage, []);
        try
        {
            var e = new BrokerElevation(otherImage, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
            var outcome = (await e.RunAsync(Request(), CancellationToken.None, CancellationToken.None)).Steps[0].Outcome;
            Assert.Equal("failed", outcome.State);
            Assert.Equal("broker_mismatch", outcome.Code);
        }
        finally { File.Delete(otherImage); }
    }

    [Fact]
    public async Task Only_registered_admin_actions_are_elevated()
    {
        var launcher = new FakeLauncher(null);
        var e = new BrokerElevation(Self, launcher);
        var step = Firewall() with { ActionId = "power.select_scheme" };
        var result = await e.RunAsync(Request(step), CancellationToken.None, CancellationToken.None);
        Assert.Equal("action_not_allowed", result.Steps[0].Outcome.Code);
        Assert.Null(launcher.Arguments);
        var tooMany = await e.RunAsync(Request([.. Enumerable.Range(1, 7).Select(Firewall)]), CancellationToken.None, CancellationToken.None);
        Assert.Equal("bad_request", tooMany.Steps[0].Outcome.Code);
        Assert.Null(launcher.Arguments);
    }

    [Fact]
    public void Pipe_acl_denies_network_and_allows_only_user_and_admins()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var rules = BrokerElevation.Security(user).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) && r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny);
        Assert.Equal(2, rules.Count(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow));
    }
}

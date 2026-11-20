using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// Host side of administrator checks (m9 §17.2) and the one-helper exclusion for every connection type (§17.6, A6), over
/// a real unelevated local pipe with a fake launcher and a scripted fake helper. Nothing is elevated, nothing is read.
/// </summary>
public sealed class ReadElevationTests
{
    private static readonly string Self = Environment.ProcessPath!;

    private sealed class FakeProcess : IBrokerProcess
    {
        public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited => Exit.Task.IsCompleted;
        public Task WaitForExitAsync() => Exit.Task;
        public void Dispose() { }
    }

    /// <summary>Runs <paramref name="script"/> as the helper for every launch; each process exits after its script and, when set, the launch's linger.</summary>
    private sealed class FakeLauncher(Func<NamedPipeClientStream, string, Task> script, bool handle = true, bool cancel = false) : IBrokerLauncher
    {
        private int _launches;
        public int Launches => Volatile.Read(ref _launches);
        public Func<int, TaskCompletionSource?> Linger { get; init; } = _ => null;
        /// <summary>The time the user spends on the UAC prompt (before Windows reports the launch).</summary>
        public TimeSpan Delay { get; init; }
        /// <summary>False: the helper starts but never connects (it lingers per <see cref="Linger"/>).</summary>
        public bool Connects { get; init; } = true;
        public readonly List<Task> Running = [];

        public BrokerLaunch Launch(string brokerPath, string arguments)
        {
            var n = Interlocked.Increment(ref _launches);
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            if (cancel) return new BrokerLaunch(false, true, 0);
            if (!Connects)
            {
                var silent = handle ? new FakeProcess() : null;
                if (Linger(n) is { } l) _ = l.Task.ContinueWith(_ => silent?.Exit.TrySetResult(), TaskScheduler.Default);
                else silent?.Exit.TrySetResult();
                return new BrokerLaunch(true, false, (uint)Environment.ProcessId, Process: silent);
            }
            var parsed = BrokerArgs.Parse(arguments.Split(' '))!;
            var process = handle ? new FakeProcess() : null;
            var linger = Linger(n);
            lock (Running)
                Running.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var pipe = new NamedPipeClientStream(".", parsed.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                        await pipe.ConnectAsync(5000);
                        await script(pipe, parsed.Nonce);
                    }
                    catch (Exception) { /* a helper that dies */ }
                    finally
                    {
                        if (linger is not null) await linger.Task;
                        process?.Exit.TrySetResult();
                    }
                }));
            return new BrokerLaunch(true, false, (uint)Environment.ProcessId, Process: process);
        }
    }

    private static ElevatedReadRequest Request(params ElevatedReadCheck[] checks) =>
        new("req-0001", checks.Length == 0 ? [new(1, AdminChecks.DefenderHistory, "1", 30), new(2, AdminChecks.StorageHealth, "1", null)] : checks);

    private static BrokerElevation Elevation(FakeLauncher launcher, bool enabled = true, TimeSpan? bound = null, TimeSpan? wait = null, TimeSpan? connect = null) =>
        new(Self, launcher, connect ?? TimeSpan.FromSeconds(10), wait) { AdminChecksEnabled = enabled, LifetimeBoundOverride = bound };

    /// <summary>A well-behaved helper: reads the request, answers started, one ok result per check (items from <paramref name="items"/>), end.</summary>
    private static Func<NamedPipeClientStream, string, Task> Helper(Func<int, JsonObject[]>? items = null, Func<BrokerReadRequest, string, NamedPipeClientStream, Task>? custom = null) =>
        async (p, nonce) =>
        {
            var req = BrokerReadRequest.Parse(await BrokerProtocol.ReadAsync(p, CancellationToken.None))!;
            Assert.Equal(nonce, req.Nonce);
            if (custom is not null) { await custom(req, nonce, p); return; }
            var seq = 0;
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Started(nonce, req.RequestId, ++seq), CancellationToken.None);
            foreach (var c in req.Checks)
            {
                var list = items?.Invoke(c.N) ?? [c.Id == AdminChecks.DefenderHistory ? ReadSessionTests.Threat() : ReadSessionTests.Disk()];
                await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Result(nonce, req.RequestId, ++seq, c.N, "ok", null, new BrokerReadCoverage(true, list.Length, list.Length), list), CancellationToken.None);
            }
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.End(nonce, req.RequestId, ++seq, "complete", null), CancellationToken.None);
        };

    [Fact]
    public async Task A_read_session_maps_every_checks_result_and_reports_started()
    {
        var launcher = new FakeLauncher(Helper());
        var started = 0;
        var r = await Elevation(launcher).RunReadAsync(Request() with { Started = () => started++ }, CancellationToken.None);
        Assert.Equal(("complete", true), (r.State, r.Dispatched));
        Assert.Equal(1, started);
        Assert.Equal([1, 2], r.Checks.Select(c => c.N));
        Assert.All(r.Checks, c => Assert.Equal("ok", c.State));
        Assert.Equal("Trojan:Win32/Test", r.Checks[0].Items[0]["threatName"]!.GetValue<string>());
        await r.Ended!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task With_the_flag_off_or_a_bad_request_nothing_is_launched()
    {
        var launcher = new FakeLauncher(Helper());
        var off = await Elevation(launcher, enabled: false).RunReadAsync(Request(), CancellationToken.None);
        Assert.Equal(("not_started", "admin_unavailable"), (off.State, off.Code));
        Assert.All(off.Checks, c => Assert.Equal(("failed", "admin_unavailable"), (c.State, c.Code)));
        foreach (var bad in new[]
        {
            Request(new ElevatedReadCheck(1, "security.tpm", "1", null)),
            Request(new ElevatedReadCheck(1, AdminChecks.DefenderHistory, "2", null)),
            Request(new ElevatedReadCheck(1, AdminChecks.DefenderHistory, "1", 91)),
            Request(new ElevatedReadCheck(2, AdminChecks.DefenderHistory, "1", null)),
            Request(new ElevatedReadCheck(1, AdminChecks.StorageHealth, "1", null), new ElevatedReadCheck(2, AdminChecks.StorageHealth, "1", null)),
            new ElevatedReadRequest("bad id!", [new(1, AdminChecks.StorageHealth, "1", null)]),
            new ElevatedReadRequest("req", []),
        })
        {
            var r = await Elevation(launcher).RunReadAsync(bad, CancellationToken.None);
            Assert.Equal(("not_started", "bad_request"), (r.State, r.Code));
        }
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Uac_cancelled_is_declined_and_nothing_was_sent()
    {
        var r = await Elevation(new FakeLauncher(Helper(), cancel: true)).RunReadAsync(Request(), CancellationToken.None);
        Assert.Equal(("declined", "uac_cancelled", false), (r.State, r.Code, r.Dispatched));
        Assert.All(r.Checks, c => Assert.Equal("cancelled", c.State));
    }

    public static TheoryData<string> BadSessions() => ["extra_field", "seq_gap", "wrong_order", "ok_before_started", "end_too_early", "other_request", "write_outcome",
        "end_null_request_id", "private_model", "path_threat_name", "unc_threat_name", "host_port_threat_name", "spaced_threat_name"];

    [Theory]
    [MemberData(nameof(BadSessions))]
    public async Task A_malformed_session_keeps_nothing_and_sends_stop(string kind)
    {
        var stopSeen = new TaskCompletionSource();
        var launcher = new FakeLauncher(Helper(custom: async (req, nonce, p) =>
        {
            var id = req.RequestId;
            var leaky = ReadSessionTests.Threat();
            leaky["path"] = @"C:\Users\x\evil.exe";
            JsonObject Ok(int seq, int n, params JsonObject[] items) => BrokerReadFrames.Result(nonce, id, seq, n, "ok", null, new BrokerReadCoverage(true, items.Length, items.Length), items);
            var frames = kind switch
            {
                "extra_field" => new[] { BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, leaky) },
                "seq_gap" => [BrokerReadFrames.Started(nonce, id, 1), Ok(3, 1, ReadSessionTests.Threat())],
                "wrong_order" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 2, ReadSessionTests.Disk())],
                "ok_before_started" => [Ok(1, 1, ReadSessionTests.Threat())],
                "end_too_early" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat()), BrokerReadFrames.End(nonce, id, 3, "complete", null)],
                "other_request" => [BrokerReadFrames.Started(nonce, "req-9999", 1)],
                // Review item 7: the terminal frame must carry the exact request id after results.
                "end_null_request_id" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat()), Ok(3, 2, ReadSessionTests.Disk()),
                    BrokerReadFrames.End(nonce, null, 4, "complete", null)],
                // Review item 1 at the host: a private value in a listed field fails intake.
                "private_model" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat()), Ok(3, 2, Model("someone@example.com"))],
                // Re-check A at the host: a threat name outside Defender's syntax fails intake (the helper would have nulled it).
                "path_threat_name" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat("C:/x/y"))],
                "unc_threat_name" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat("//fileserver/share/report.csv"))],
                "host_port_threat_name" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat("host:8080/path"))],
                "spaced_threat_name" => [BrokerReadFrames.Started(nonce, id, 1), Ok(2, 1, ReadSessionTests.Threat("Trojan:Win32/Some Name"))],
                _ => [new BrokerResponse(nonce, [], null).ToJson()],
            };
            foreach (var f in frames) await BrokerProtocol.WriteAsync(p, f, CancellationToken.None);
            if (BrokerStop.Is(await BrokerProtocol.ReadAsync(p, CancellationToken.None), nonce)) stopSeen.TrySetResult();
        }));
        var r = await Elevation(launcher).RunReadAsync(Request(), CancellationToken.None);
        Assert.Equal(("malformed", true), (r.State, r.Dispatched));
        Assert.All(r.Checks, c => Assert.Equal(("failed", "malformed", 0), (c.State, c.Code, c.Items.Count)));
        await stopSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static JsonObject Model(string model)
    {
        var d = ReadSessionTests.Disk();
        d["model"] = model;
        return d;
    }

    public static TheoryData<string> BadBytes() => ["oversized", "not_json", "too_deep"];

    /// <summary>Review item 5: malformed framing after a valid result keeps nothing (malformed, not interrupted).</summary>
    [Theory]
    [MemberData(nameof(BadBytes))]
    public async Task Malformed_framing_after_a_valid_result_keeps_nothing(string kind)
    {
        var launcher = new FakeLauncher(Helper(custom: async (req, nonce, p) =>
        {
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Started(nonce, req.RequestId, 1), CancellationToken.None);
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Result(nonce, req.RequestId, 2, 1, "ok", null, new BrokerReadCoverage(true, 1, 1), [ReadSessionTests.Threat()]), CancellationToken.None);
            byte[] payload = kind switch
            {
                "not_json" => "{not json"u8.ToArray(),
                "too_deep" => System.Text.Encoding.UTF8.GetBytes(new string('[', 40) + new string(']', 40)),
                _ => [],
            };
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, kind == "oversized" ? (uint)(BrokerProtocol.MaxFrameBytes + 1) : (uint)payload.Length);
            await p.WriteAsync(header);
            if (payload.Length > 0) await p.WriteAsync(payload);
            await p.FlushAsync();
            try { await BrokerProtocol.ReadAsync(p, CancellationToken.None); } catch (Exception) { } // the stop
        }));
        var r = await Elevation(launcher).RunReadAsync(Request(), CancellationToken.None);
        Assert.Equal("malformed", r.State);
        Assert.All(r.Checks, c => Assert.Equal(("failed", "malformed", 0), (c.State, c.Code, c.Items.Count)));
    }

    [Fact]
    public async Task A_lost_connection_keeps_finished_checks_and_marks_the_rest_interrupted()
    {
        var launcher = new FakeLauncher(Helper(custom: async (req, nonce, p) =>
        {
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Started(nonce, req.RequestId, 1), CancellationToken.None);
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Result(nonce, req.RequestId, 2, 1, "ok", null, new BrokerReadCoverage(true, 1, 1), [ReadSessionTests.Threat()]), CancellationToken.None);
            p.Dispose();
        }));
        var r = await Elevation(launcher).RunReadAsync(Request(), CancellationToken.None);
        Assert.Equal("interrupted", r.State);
        Assert.Equal(("ok", 1), (r.Checks[0].State, r.Checks[0].Items.Count));
        Assert.Equal(("failed", "interrupted"), (r.Checks[1].State, r.Checks[1].Code));
    }

    [Fact]
    public async Task The_helpers_refusal_and_a_different_administrator_come_back_as_refused()
    {
        var launcher = new FakeLauncher(Helper(custom: async (req, nonce, p) =>
            await BrokerReadSession.RefuseAsync(p, nonce, req.ToJson(), "different_admin")));
        var r = await Elevation(launcher).RunReadAsync(Request(), CancellationToken.None);
        Assert.Equal(("refused", "different_admin"), (r.State, r.Code));
        Assert.All(r.Checks, c => Assert.Equal(("failed", "different_admin"), (c.State, c.Code)));
    }

    [Fact]
    public async Task Stop_is_forwarded_to_the_helper()
    {
        var launcher = new FakeLauncher(Helper(custom: async (req, nonce, p) =>
        {
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Started(nonce, req.RequestId, 1), CancellationToken.None);
            Assert.True(BrokerStop.Is(await BrokerProtocol.ReadAsync(p, CancellationToken.None), nonce));
            var seq = 1;
            foreach (var c in req.Checks)
                await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Result(nonce, req.RequestId, ++seq, c.N, "cancelled", "stopped", new BrokerReadCoverage(false, 0, 0), []), CancellationToken.None);
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.End(nonce, req.RequestId, ++seq, "stopped", null), CancellationToken.None);
        }));
        using var stop = new CancellationTokenSource();
        var r = await Elevation(launcher).RunReadAsync(Request() with { Started = () => stop.Cancel() }, stop.Token);
        Assert.Equal("stopped", r.State);
        Assert.All(r.Checks, c => Assert.Equal("cancelled", c.State));
    }

    [Fact]
    public async Task A_stop_after_the_exclusion_is_taken_and_before_the_launch_launches_nothing()
    {
        var launcher = new FakeLauncher(Helper());
        var e = Elevation(launcher);
        using var stop = new CancellationTokenSource();
        // The revocation lands after _one was acquired (the admission runs there), just before the launcher would run.
        var r = await e.RunReadAsync(Request() with { Admit = () => { stop.Cancel(); return true; } }, stop.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("stopped", false), (r.State, r.Dispatched));
        Assert.All(r.Checks, c => Assert.Equal(("cancelled", "stopped"), (c.State, c.Code)));
        Assert.Null(r.Ended);
        Assert.Equal(0, launcher.Launches); // no UAC, nothing sent
        var next = await e.RunReadAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); // nothing held: the exclusion is free
        Assert.Equal(("complete", 1), (next.State, launcher.Launches));
    }

    // ---------------------------------------------------------------- the one-helper exclusion (A6, §17.6)

    private static ElevatedRequest Write() => new("plan-1", "hash-1",
        [new(1, FirewallPlan.ActionId, FirewallPlan.HandlerVersion, FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null)]);

    private sealed class NullObserver : IScriptRunObserver
    {
        public void Started(DateTimeOffset at, string runFolder) { }
        public void Line(bool stderr, string text) { }
    }

    /// <summary>A helper for whichever connection type comes: answers it at once (then its process may linger).</summary>
    private static async Task AnyHelper(NamedPipeClientStream p, string nonce)
    {
        var first = await BrokerProtocol.ReadAsync(p, CancellationToken.None);
        if (BrokerReadRequest.IsRead(first)) { await BrokerReadSession.RefuseAsync(p, nonce, first, "check_not_allowed"); return; }
        if (BrokerScriptRequest.IsScript(first)) { await BrokerProtocol.WriteAsync(p, BrokerScriptFrames.End(nonce, 1, "not_started", null, "broker_cancelled", 0, false), CancellationToken.None); return; }
        var req = BrokerRequest.Parse(first)!;
        await BrokerProtocol.WriteAsync(p, new BrokerResponse(nonce, [.. req.Steps.Select(s => new BrokerStepOutcome(s.N, "declined", [], "no", "broker_cancelled"))], null).ToJson(), CancellationToken.None);
    }

    private static Task Run(BrokerElevation e, string kind, string root) => kind switch
    {
        "request" => e.RunAsync(Write(), CancellationToken.None, CancellationToken.None),
        "script" => e.RunScriptAsync(new ScriptRunRequest("op-1", [1], new string('a', 64), TimeSpan.FromMinutes(1), false, root), new NullObserver(), CancellationToken.None),
        _ => e.RunReadAsync(Request(), CancellationToken.None),
    };

    [Theory]
    [InlineData("request")]
    [InlineData("script")]
    [InlineData("read")]
    public async Task The_next_helper_waits_until_the_previous_helper_process_has_exited(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-excl-" + Guid.NewGuid().ToString("N"));
        var linger = new TaskCompletionSource();
        var launcher = new FakeLauncher(AnyHelper) { Linger = n => n == 1 ? linger : null };
        var e = Elevation(launcher);
        await Run(e, kind, root).WaitAsync(TimeSpan.FromSeconds(10)); // answered; its process is still running
        var second = Run(e, "read", root);
        await Task.Delay(400);
        Assert.Equal(1, launcher.Launches); // held: the first helper has not exited
        linger.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, launcher.Launches);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("script")]
    public async Task A_read_queued_behind_a_write_or_script_is_admitted_only_after_the_exclusion_and_can_be_refused_there(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-excl-" + Guid.NewGuid().ToString("N"));
        var linger = new TaskCompletionSource();
        var launcher = new FakeLauncher(AnyHelper) { Linger = n => n == 1 ? linger : null };
        var e = Elevation(launcher);
        await Run(e, kind, root).WaitAsync(TimeSpan.FromSeconds(10)); // answered; its helper process still runs: the exclusion is held
        bool? heldWhenAsked = null;
        var read = e.RunReadAsync(Request() with { Admit = () => { heldWhenAsked = !linger.Task.IsCompleted; return false; } }, CancellationToken.None);
        await Task.Delay(300);
        Assert.Null(heldWhenAsked); // not asked while the write's helper runs
        linger.SetResult();
        var r = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(heldWhenAsked); // asked once the exclusion was free, right before the launch
        Assert.Equal(("not_started", "not_admitted", false), (r.State, r.Code, r.Dispatched));
        Assert.Equal(1, launcher.Launches); // only the write's helper
    }

    [Theory]
    [InlineData("request", true)]
    [InlineData("script", true)]
    [InlineData("read", true)]
    [InlineData("read", false)]
    public async Task A_helper_that_never_exits_or_has_no_handle_holds_the_exclusion_only_until_its_lifetime_bound(string kind, bool handle)
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-excl-" + Guid.NewGuid().ToString("N"));
        var never = new TaskCompletionSource();
        var launcher = new FakeLauncher(AnyHelper, handle) { Linger = n => n == 1 ? never : null };
        var e = Elevation(launcher, bound: TimeSpan.FromSeconds(1.5));
        await Run(e, kind, root).WaitAsync(TimeSpan.FromSeconds(10));
        var second = Run(e, "read", root);
        await Task.Delay(300);
        Assert.Equal(1, launcher.Launches);
        await second.WaitAsync(TimeSpan.FromSeconds(10)); // released by the bound, not by an exit
        Assert.Equal(2, launcher.Launches);
        never.SetResult();
    }

    /// <summary>Review item 3: the bound's clock starts at the launch, not before the UAC prompt.</summary>
    [Fact]
    public async Task A_long_UAC_wait_does_not_shorten_the_bound()
    {
        var never = new TaskCompletionSource();
        var launcher = new FakeLauncher(Helper(custom: async (req, nonce, p) =>
        {
            await BrokerProtocol.WriteAsync(p, BrokerReadFrames.Started(nonce, req.RequestId, 1), CancellationToken.None);
            p.Dispose(); // the exchange is interrupted; the helper process goes on (an approved step still finishing)
        })) { Linger = n => n == 1 ? never : null, Delay = TimeSpan.FromSeconds(1.2) };
        var e = Elevation(launcher, bound: TimeSpan.FromSeconds(1));
        var first = await e.RunReadAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("interrupted", first.State);
        var second = e.RunReadAsync(Request(), CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(1, launcher.Launches); // counted from acquisition (before the 1.2 s prompt) the 1 s bound would be over already
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, launcher.Launches);
        never.SetResult();
    }

    /// <summary>Review item 4: a helper that started but never connected (or was rejected) still holds the exclusion until it exits or the bound.</summary>
    [Theory]
    [InlineData("read", true)]
    [InlineData("request", true)]
    [InlineData("script", true)]
    [InlineData("read", false)]
    public async Task A_helper_that_starts_but_never_connects_holds_the_exclusion_until_its_bound(string kind, bool handle)
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-excl-" + Guid.NewGuid().ToString("N"));
        var never = new TaskCompletionSource();
        var silent = new FakeLauncher(AnyHelper, handle) { Linger = n => n == 1 ? never : null, Connects = false };
        var e = new BrokerElevation(Self, silent, TimeSpan.FromMilliseconds(300)) { AdminChecksEnabled = true, LifetimeBoundOverride = TimeSpan.FromSeconds(1.5) };
        await Run(e, kind, root).WaitAsync(TimeSpan.FromSeconds(10)); // broker_timeout: nothing was sent
        var second = Run(e, "read", root);
        await Task.Delay(300);
        Assert.Equal(1, silent.Launches);
        await second.WaitAsync(TimeSpan.FromSeconds(10)); // released by the bound
        Assert.Equal(2, silent.Launches);
        never.SetResult();
    }

    /// <summary>Re-check B: a native launch that succeeded is Started even when its process waiter can't be built.</summary>
    [Fact]
    public void A_launch_whose_process_waiter_fails_still_counts_as_started_without_a_process()
    {
        var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var r = ShellExecuteLauncher.Launched(handle, _ => throw new InvalidOperationException("RegisterWaitForSingleObject failed"), _ => 4242);
        Assert.Equal((true, false, 4242u), (r.Started, r.Cancelled, r.ProcessId));
        Assert.Null(r.Process);
        Assert.True(handle.IsClosed);
        var noId = ShellExecuteLauncher.Launched(new Microsoft.Win32.SafeHandles.SafeProcessHandle(IntPtr.Zero, ownsHandle: false), _ => new FakeProcess(), _ => throw new System.ComponentModel.Win32Exception(5));
        Assert.Equal((true, 0u), (noId.Started, noId.ProcessId));
        Assert.Null(noId.Process);
    }

    /// <summary>A launcher standing in for ShellExecute after a successful native launch whose waiter fails: the helper never connects.</summary>
    private sealed class WaiterFailsLauncher : IBrokerLauncher
    {
        public int Launches;
        public BrokerLaunch Launch(string brokerPath, string arguments)
        {
            Interlocked.Increment(ref Launches);
            return ShellExecuteLauncher.Launched(new Microsoft.Win32.SafeHandles.SafeProcessHandle(IntPtr.Zero, ownsHandle: false),
                _ => throw new InvalidOperationException("wait"), _ => 0);
        }
    }

    [Fact]
    public async Task A_launch_whose_waiter_failed_holds_the_exclusion_until_the_bound_not_at_once()
    {
        var launcher = new WaiterFailsLauncher();
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromMilliseconds(300)) { AdminChecksEnabled = true, LifetimeBoundOverride = TimeSpan.FromSeconds(1.5) };
        var first = await e.RunReadAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("not_started", "broker_timeout"), (first.State, first.Code));
        var second = e.RunReadAsync(Request(), CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref launcher.Launches)); // held: Windows launched a helper it can't watch
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Volatile.Read(ref launcher.Launches));
    }

    [Fact]
    public async Task A_helper_that_never_connected_releases_the_exclusion_when_it_exits()
    {
        var exit = new TaskCompletionSource();
        var silent = new FakeLauncher(AnyHelper) { Linger = n => n == 1 ? exit : null, Connects = false };
        var e = new BrokerElevation(Self, silent, TimeSpan.FromMilliseconds(300)) { AdminChecksEnabled = true, LifetimeBoundOverride = TimeSpan.FromMinutes(5) };
        var first = await e.RunReadAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("not_started", "broker_timeout"), (first.State, first.Code));
        Assert.Null(first.Ended); // no request was dispatched: no plan locks follow it
        var second = e.RunReadAsync(Request(), CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(1, silent.Launches);
        exit.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, silent.Launches);
    }

    [Fact]
    public async Task Nothing_launched_releases_the_exclusion_at_once()
    {
        var launcher = new FakeLauncher(AnyHelper, cancel: true);
        var e = Elevation(launcher);
        await e.RunReadAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await e.RunReadAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, launcher.Launches);
    }

    [Fact]
    public void The_lifetime_bounds_are_the_helpers_budget_plus_its_extension_plus_margins()
    {
        Assert.Equal(TimeSpan.FromMinutes(19), BrokerElevation.ReadLifetime(1));
        Assert.Equal(TimeSpan.FromMinutes(22), BrokerElevation.ReadLifetime(4));
        Assert.Equal(TimeSpan.FromMinutes(29), BrokerElevation.ScriptLifetime(TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(49), BrokerElevation.ScriptLifetime(TimeSpan.FromMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(19.75), BrokerElevation.RequestLifetime(Write().Steps));
        var repair = Write().Steps[0] with { Deadline = TimeSpan.FromMinutes(90) };
        Assert.Equal(TimeSpan.FromMinutes(15 + 2 + 91 + 2), BrokerElevation.RequestLifetime([repair]));
        // Never shorter than what the host itself waits for the helper's answer.
        Assert.True(BrokerElevation.ReadLifetime(4) > BrokerElevation.ReadWait(4) + TimeSpan.FromMinutes(3));
        Assert.True(BrokerElevation.RequestLifetime([repair]) > BrokerElevation.ResponseWait([repair]) + TimeSpan.FromMinutes(3));
    }
}

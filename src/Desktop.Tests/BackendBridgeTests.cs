using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Backend;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public class BackendBridgeTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static BackendConnection Connect(StubPipeServer stub, bool allowAnyHost = true, string? hostPath = null) =>
        new(new BackendOptions
        {
            PipeName = stub.PipeName,
            HostPath = hostPath ?? Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Host.exe"),
            LaunchHost = false,
            AllowAnyHost = allowAnyHost,
            ConnectBudget = TimeSpan.FromSeconds(1),
        }, DesktopLog.Disabled);

    private static async Task WaitForState(BackendConnection b, BackendState state)
    {
        var sw = Stopwatch.StartNew();
        while (b.Current.State != state)
        {
            if (sw.Elapsed > Wait) throw new TimeoutException($"State stayed {b.Current.State}, wanted {state}.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Round_trip_forwards_and_maps_renderer_id()
    {
        await using var stub = new StubPipeServer();
        await using var backend = Connect(stub);
        backend.Start();
        await WaitForState(backend, BackendState.Ready);
        Assert.Equal(1, stub.Hellos);

        var dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, DesktopLog.Disabled);
        var reply = await dispatcher.HandleAsync("""{"type":"rpc","id":17,"method":"scan.get","params":{"scanId":"scan-1"}}""", Doc, true, default);
        var msg = JsonNode.Parse(reply!)!;
        Assert.Equal("rpc.result", msg["type"]!.GetValue<string>());
        Assert.Equal(17, msg["id"]!.GetValue<int>());
        Assert.Equal("scan.get", msg["result"]!["echo"]!.GetValue<string>());
        Assert.Equal("scan-1", msg["result"]!["params"]!["scanId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hello_from_renderer_never_reaches_the_backend()
    {
        await using var stub = new StubPipeServer();
        await using var backend = Connect(stub);
        backend.Start();
        await WaitForState(backend, BackendState.Ready);
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, DesktopLog.Disabled);
        var reply = JsonNode.Parse((await dispatcher.HandleAsync("""{"type":"rpc","id":3,"method":"session.hello","params":{}}""", Doc, true, default))!)!;
        Assert.Equal(-32012, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, stub.Hellos);
        lock (stub.Methods) Assert.Equal(["session.hello"], stub.Methods);
    }

    [Fact]
    public async Task Events_are_forwarded()
    {
        await using var stub = new StubPipeServer();
        await using var backend = Connect(stub);
        var events = new BlockingCollection<JsonNode?>();
        backend.EventReceived += e => events.Add(e);
        backend.Start();
        await WaitForState(backend, BackendState.Ready);

        await stub.NotifyAsync(new JsonObject { ["stream"] = "scan:scan-1", ["seq"] = 7, ["type"] = "scan.progress", ["payload"] = new JsonObject { ["done"] = 2 } });
        Assert.True(events.TryTake(out var evt, Wait));
        var msg = JsonNode.Parse(BridgeMessages.Event(evt))!;
        Assert.Equal("event", msg["type"]!.GetValue<string>());
        Assert.Equal(7, msg["event"]!["seq"]!.GetValue<int>());
    }

    [Fact]
    public async Task Drop_fails_pending_call_retryable_then_reconnects_and_rehellos()
    {
        await using var stub = new StubPipeServer();
        stub.Hold.Add("scan.start");
        await using var backend = Connect(stub);
        var states = new ConcurrentQueue<BackendState>();
        backend.StateChanged += (s, _) => states.Enqueue(s);
        backend.Start();
        await WaitForState(backend, BackendState.Ready);

        var dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, DesktopLog.Disabled);
        var pending = dispatcher.HandleAsync("""{"type":"rpc","id":21,"method":"scan.start","params":{"idempotencyKey":"k1"}}""", Doc, true, default);
        await stub.HeldCallArrived.Task.WaitAsync(Wait);
        stub.Drop();

        var reply = JsonNode.Parse((await pending.WaitAsync(Wait))!)!;
        Assert.Equal("rpc.error", reply["type"]!.GetValue<string>());
        Assert.Equal(21, reply["id"]!.GetValue<int>());
        Assert.Equal(-32000, reply["error"]!["code"]!.GetValue<int>());
        Assert.True(reply["error"]!["data"]!["retryable"]!.GetValue<bool>());

        // The pending call can fail before the connection notices the drop, so wait for the
        // reconnect itself rather than for a Ready that may still be the old connection's.
        var sw = Stopwatch.StartNew();
        while (!(states.Contains(BackendState.Reconnecting) && backend.Current.State == BackendState.Ready && stub.Hellos == 2))
        {
            if (sw.Elapsed > Wait) throw new TimeoutException($"No reconnect: state {backend.Current.State}, hellos {stub.Hellos}.");
            await Task.Delay(20);
        }

        // Retry with the same key goes through on the new connection.
        stub.Hold.Clear();
        var retry = JsonNode.Parse((await dispatcher.HandleAsync("""{"type":"rpc","id":22,"method":"scan.start","params":{"idempotencyKey":"k1"}}""", Doc, true, default))!)!;
        Assert.Equal("rpc.result", retry["type"]!.GetValue<string>());
        Assert.Equal("k1", retry["result"]!["params"]!["idempotencyKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Calls_while_not_connected_are_retryable_errors()
    {
        await using var stub = new StubPipeServer(start: false);
        await using var backend = Connect(stub);
        backend.Start();
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, DesktopLog.Disabled);
        var reply = JsonNode.Parse((await dispatcher.HandleAsync("""{"type":"rpc","id":1,"method":"report.list","params":{}}""", Doc, true, default))!)!;
        Assert.Equal(-32000, reply["error"]!["code"]!.GetValue<int>());
        Assert.True(reply["error"]!["data"]!["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Incompatible_server_sets_incompatible_state()
    {
        await using var stub = new StubPipeServer { Incompatible = true };
        await using var backend = Connect(stub);
        backend.Start();
        await WaitForState(backend, BackendState.Incompatible);
        Assert.Equal("Update Optimus", backend.Current.Detail!["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task Host_that_exits_as_other_session_is_reported_at_once_not_as_endless_connecting()
    {
        // A stand-in host that ends at once with the host's "other session" exit code (one host per user and data folder).
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        await using var backend = new BackendConnection(new BackendOptions
        {
            PipeName = "OptimizationTool.test." + Guid.NewGuid().ToString("N"), HostPath = cmd, LaunchHost = true, AllowAnyHost = true,
            HostArguments = ["/c", "exit " + OptimizationTool.Transport.HostExitCodes.OtherSession],
            ConnectBudget = TimeSpan.FromMinutes(5), // far away: the detail must not wait for the "unavailable" budget
        }, DesktopLog.Disabled);
        backend.Start();
        var sw = Stopwatch.StartNew();
        while (!(backend.Current.Detail is JsonValue v && v.GetValue<string>() == "other_session"))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Never reported other_session.");
            await Task.Delay(50);
        }
        Assert.Equal(BackendState.Connecting, backend.Current.State);
    }

    /// <summary>A stand-in host (cmd.exe) that appends a line to <paramref name="launches"/> on every start, then exits with <paramref name="exitCode"/>.</summary>
    private static BackendConnection FailingHost(string launches, int exitCode, IReadOnlyList<TimeSpan>? backoff = null) =>
        new(new BackendOptions
        {
            PipeName = "OptimizationTool.test." + Guid.NewGuid().ToString("N"),
            HostPath = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            LaunchHost = true, AllowAnyHost = true,
            HostArguments = ["/d", "/c", $"echo x>>{launches}& exit {exitCode}"], // the temp path has no spaces
            ConnectBudget = TimeSpan.FromMinutes(5),
            LaunchBackoff = backoff,
        }, DesktopLog.Disabled);

    private static int Launches(string file)
    {
        try { return File.Exists(file) ? File.ReadAllLines(file).Count(l => l.Trim() == "x") : 0; }
        catch (IOException) { return -1; } // cmd is writing it right now
    }

    private static async Task WaitForDetail(BackendConnection b, string detail, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (!(b.Current.Detail is JsonValue v && v.GetValue<string>() == detail))
        {
            if (sw.Elapsed > within) throw new TimeoutException($"Never reported {detail} (detail {b.Current.Detail?.ToJsonString() ?? "null"}).");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Host_that_exits_as_needs_update_is_reported_and_not_relaunched_until_the_page_retries()
    {
        var launches = Path.Combine(Path.GetTempPath(), "ot-launch-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await using var backend = FailingHost(launches, OptimizationTool.Transport.HostExitCodes.NeedsUpdate);
            backend.Start();
            await WaitForDetail(backend, "needs_update", TimeSpan.FromSeconds(60));
            Assert.Equal(BackendState.Connecting, backend.Current.State);

            await Task.Delay(3000); // the old shell relaunched every 1-5 s here, forever
            Assert.Equal(1, Launches(launches));
            Assert.Equal("needs_update", backend.Current.Detail!.GetValue<string>());

            backend.RetryNow(); // "Try again": one launch at once, which ends the same way
            var sw = Stopwatch.StartNew();
            while (Launches(launches) < 2)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Retry did not relaunch the host.");
                await Task.Delay(20);
            }
            await WaitForDetail(backend, "needs_update", TimeSpan.FromSeconds(30));
            await Task.Delay(1500);
            Assert.Equal(2, Launches(launches));
        }
        finally { File.Delete(launches); }
    }

    [Fact]
    public async Task Host_that_keeps_failing_backs_off_then_reports_cant_start_and_keeps_a_slow_retry()
    {
        var launches = Path.Combine(Path.GetTempPath(), "ot-launch-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var step = TimeSpan.FromMilliseconds(150);
            await using var backend = FailingHost(launches, 1, [step, step, step, TimeSpan.FromSeconds(2)]);
            backend.Start();
            await WaitForDetail(backend, "cant_start", TimeSpan.FromSeconds(60));
            var atCantStart = Launches(launches);
            Assert.True(atCantStart >= BackendConnection.CantStartAfter, $"cant_start after {atCantStart} launches");
            Assert.Equal(BackendState.Connecting, backend.Current.State);

            // The slow retry continues (2 s backoff here, 30 s in the app), with no flood of launches.
            await Task.Delay(7000); // the poll slows to 5 s once a startup detail is shown
            var later = Launches(launches);
            Assert.InRange(later, atCantStart + 1, atCantStart + 3);
            Assert.Equal("cant_start", backend.Current.Detail!.GetValue<string>());
        }
        finally { File.Delete(launches); }
    }

    [Fact]
    public void Default_launch_backoff_is_2_5_10_then_30_seconds() =>
        Assert.Equal([2, 5, 10, 30], BackendConnection.DefaultLaunchBackoff.Select(t => (int)t.TotalSeconds));

    [Fact]
    public async Task Retry_backend_is_shell_handled_and_wakes_the_connection()
    {
        var retries = 0;
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), new ThrowingBackend(), DesktopLog.Disabled, retryBackend: () => retries++);
        var ok = JsonNode.Parse((await dispatcher.HandleAsync("""{"type":"rpc","id":1,"method":"shell.retryBackend","params":{}}""", Doc, true, default))!)!;
        Assert.Equal("""{"retrying":true}""", ok["result"]!.ToJsonString());
        var noParams = JsonNode.Parse((await dispatcher.HandleAsync("""{"type":"rpc","id":2,"method":"shell.retryBackend"}""", Doc, true, default))!)!;
        Assert.True(noParams["result"]!["retrying"]!.GetValue<bool>());
        Assert.Equal(2, retries);

        var bad = JsonNode.Parse((await dispatcher.HandleAsync("""{"type":"rpc","id":3,"method":"shell.retryBackend","params":{"x":1}}""", Doc, true, default))!)!;
        Assert.Equal(-32602, bad["error"]!["code"]!.GetValue<int>());
        Assert.Equal(2, retries);
        Assert.Contains(BackendConnection.RetryMethod, (IReadOnlySet<string>)BridgeRouter.ShellMethods);
        Assert.DoesNotContain(BackendConnection.RetryMethod, (IReadOnlySet<string>)BridgeRouter.DesktopMethods);
    }

    private sealed class ThrowingBackend : IBackend
    {
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            throw new InvalidOperationException("shell.retryBackend must not be forwarded");
    }

    [Fact]
    public async Task Unexpected_server_image_is_refused()
    {
        await using var stub = new StubPipeServer();
        await using var backend = Connect(stub, allowAnyHost: false, hostPath: @"C:\nowhere\OptimizationTool.Host.exe");
        backend.Start();
        var sw = Stopwatch.StartNew();
        while (!(backend.Current.Detail is JsonValue v && v.GetValue<string>() == "untrusted_host"))
        {
            if (sw.Elapsed > Wait) throw new TimeoutException("Never reported untrusted_host.");
            await Task.Delay(50);
        }
        Assert.NotEqual(BackendState.Ready, backend.Current.State);
        Assert.Equal(0, stub.Hellos);
    }

    [Fact]
    public async Task Expected_server_image_is_accepted_without_override()
    {
        // The stub runs inside the test host process, so its image is this process's image.
        var self = Environment.ProcessPath!;
        await using var stub = new StubPipeServer();
        await using var backend = Connect(stub, allowAnyHost: false, hostPath: self);
        backend.Start();
        await WaitForState(backend, BackendState.Ready);
    }

    [Fact]
    public void Options_parse_known_args_and_reject_unknown()
    {
        var o = DesktopOptions.Parse(["--ui-dir", @"C:\x\ui", "--fixture-speed", "fast", "--dev-allow-any-host", "--pipe-name", "p.1"]);
        Assert.Equal(@"C:\x\ui", o.UiDir);
        Assert.True(o.DevAllowAnyHost);
        Assert.Equal("p.1", o.PipeName);
        Assert.Equal(["--fixture-speed", "fast"], o.HostArguments());
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--nope"]));
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--fixture-speed"]));
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--fixture-speed", "a b"]));
    }

    [Fact]
    public void Sample_seconds_and_source_pass_through_to_host()
    {
        var o = DesktopOptions.Parse(["--fixture-speed", "fast", "--sample-seconds", "60", "--source", "windows"]);
        Assert.Equal(["--fixture-speed", "fast", "--sample-seconds", "60", "--source", "windows"], o.HostArguments());
        Assert.Equal(["--sample-seconds", "1"], DesktopOptions.Parse(["--sample-seconds", "1"]).HostArguments());
        Assert.Equal(["--sample-seconds", "600"], DesktopOptions.Parse(["--sample-seconds", "600"]).HostArguments());
        Assert.Equal(["--source", "fixture"], DesktopOptions.Parse(["--source", "fixture"]).HostArguments());
        Assert.Empty(DesktopOptions.Parse([]).HostArguments());
    }

    [Theory]
    [InlineData("--sample-seconds", "0")]
    [InlineData("--sample-seconds", "601")]
    [InlineData("--sample-seconds", "-5")]
    [InlineData("--sample-seconds", "+5")]
    [InlineData("--sample-seconds", "1.5")]
    [InlineData("--sample-seconds", " 60")]
    [InlineData("--sample-seconds", "0060")]
    [InlineData("--sample-seconds", "١٢")]
    [InlineData("--sample-seconds", "abc")]
    [InlineData("--source", "Windows")]
    [InlineData("--source", "live")]
    [InlineData("--source", "windows ")]
    [InlineData("--source", "")]
    public void Rejects_invalid_sample_seconds_and_source(string name, string value) =>
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse([name, value]));
}

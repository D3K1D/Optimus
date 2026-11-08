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
        Assert.Equal("Update Optimization Tool", backend.Current.Detail!["action"]!.GetValue<string>());
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

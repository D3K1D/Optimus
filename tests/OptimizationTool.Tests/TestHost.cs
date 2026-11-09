using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Host;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>An in-process host on a unique pipe name with a temp data directory.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public string DataDir { get; }
    public string PipeName { get; }
    public double Speed { get; }
    public HostServer Server { get; private set; }
    private readonly bool _ownsDir;
    private readonly HostOptions _template;
    private readonly List<JsonRpcClient> _clients = [];

    private TestHost(HostOptions options, bool ownsDir)
    {
        DataDir = options.DataDir;
        PipeName = options.PipeName;
        Speed = options.FixtureSpeed;
        _ownsDir = ownsDir;
        _template = options;
        Server = HostServer.Start(options);
    }

    public static string NewDataDir() => Path.Combine(Path.GetTempPath(), "ot-tests", Guid.NewGuid().ToString("N"));

    /// <summary>A fixture-source host (M1 behaviour).</summary>
    public static TestHost Start(double speed = 0, string? dataDir = null, TimeSpan? idle = null) =>
        new(new HostOptions
        {
            DataDir = dataDir ?? NewDataDir(), PipeName = NewPipeName(), FixtureSpeed = speed, IdleExit = idle, DevAllowAnyClient = true,
        }, dataDir is null);

    /// <summary>A module-source host (M2) over the given modules.</summary>
    public static TestHost StartModules(IReadOnlyList<IAuditModule> modules, string? dataDir = null, TimeProvider? time = null,
        CollectorOptions? collectorOptions = null, TimeSpan? scanDeadline = null) =>
        new(new HostOptions
        {
            DataDir = dataDir ?? NewDataDir(), PipeName = NewPipeName(), Modules = modules, IdleExit = null,
            Time = time ?? TimeProvider.System, CollectorOptions = collectorOptions ?? new CollectorOptions(),
            ScanDeadline = scanDeadline ?? TimeSpan.FromSeconds(120), DevAllowAnyClient = true,
        }, dataDir is null);

    /// <summary>An M3 host: modules (or the fixture when null), agent options, any agent image allowed unless told otherwise.</summary>
    public static TestHost StartAgent(OptimizationTool.Engine.Agent.AgentOptions agent, IReadOnlyList<IAuditModule>? modules = null,
        TimeProvider? time = null, bool devAllowAnyAgent = true, Action<string>? log = null, string? dataDir = null, bool devAllowAnyClient = true) =>
        new(new HostOptions
        {
            DataDir = dataDir ?? NewDataDir(), PipeName = NewPipeName(), Modules = modules, IdleExit = null,
            Time = time ?? TimeProvider.System, Agent = agent, DevAllowAnyAgent = devAllowAnyAgent, DevAllowAnyClient = devAllowAnyClient, Log = log,
        }, dataDir is null);

    /// <summary>An M4 host: fake action handlers (no real writes), optional audit modules, elevation and firewall switch.</summary>
    public static TestHost StartActions(IReadOnlyList<OptimizationTool.Engine.Actions.IActionModule> actions, IReadOnlyList<IAuditModule>? modules = null,
        OptimizationTool.Engine.Actions.IElevation? elevation = null, bool enableFirewall = false, TimeProvider? time = null,
        OptimizationTool.Engine.Agent.AgentOptions? agent = null) =>
        new(new HostOptions
        {
            DataDir = NewDataDir(), PipeName = NewPipeName(), Modules = modules ?? [], IdleExit = null, Time = time ?? TimeProvider.System,
            Agent = agent ?? new OptimizationTool.Engine.Agent.AgentOptions(), DevAllowAnyAgent = true, DevAllowAnyClient = true,
            ActionModules = actions, Elevation = elevation, EnableFirewallHandler = enableFirewall,
        }, true);

    /// <summary>An M6a host: benchmark fakes (no workload ever runs), optional agent, audit modules (fixture when null) and actions.</summary>
    public static TestHost StartBench(OptimizationTool.Engine.Bench.BenchOptions bench, OptimizationTool.Engine.Agent.AgentOptions? agent = null,
        IReadOnlyList<IAuditModule>? modules = null, double speed = 0, IReadOnlyList<OptimizationTool.Engine.Actions.IActionModule>? actions = null,
        string? dataDir = null) =>
        new(new HostOptions
        {
            DataDir = dataDir ?? NewDataDir(), PipeName = NewPipeName(), Modules = modules, IdleExit = null, FixtureSpeed = speed,
            Agent = agent ?? new OptimizationTool.Engine.Agent.AgentOptions(), DevAllowAnyAgent = true, DevAllowAnyClient = true, ActionModules = actions ?? [], Bench = bench,
        }, dataDir is null);

    private static string NewPipeName() => "OptimizationTool.test." + Guid.NewGuid().ToString("N");

    /// <summary>Stops this host and starts a new one on the same data directory, pipe name and source.</summary>
    public async Task RestartAsync()
    {
        await CloseClientsAsync();
        await Server.DisposeAsync();
        Server = HostServer.Start(new HostOptions
        {
            DataDir = DataDir, PipeName = PipeName, FixtureSpeed = Speed, IdleExit = null, Modules = _template.Modules,
            Time = _template.Time, CollectorOptions = _template.CollectorOptions, ScanDeadline = _template.ScanDeadline,
            Agent = _template.Agent, DevAllowAnyAgent = _template.DevAllowAnyAgent, DevAllowAnyClient = _template.DevAllowAnyClient, Log = _template.Log,
            ActionModules = _template.ActionModules, Elevation = _template.Elevation, EnableFirewallHandler = _template.EnableFirewallHandler,
            Bench = _template.Bench,
        });
    }

    public async Task StopAsync()
    {
        await CloseClientsAsync();
        await Server.DisposeAsync();
    }

    public async Task<Client> ConnectAsync(bool hello = true, string kind = "test", int minor = 0)
    {
        var rpc = await JsonRpcClient.ConnectAsync(PipeName, TimeSpan.FromSeconds(5));
        _clients.Add(rpc);
        var client = new Client(rpc);
        if (hello) client.Hello = (JsonObject)(await rpc.CallAsync("session.hello", Rpc.HelloParams(kind: kind, minor: minor)))!;
        return client;
    }

    private async Task CloseClientsAsync()
    {
        foreach (var c in _clients)
            try { await c.DisposeAsync(); } catch (ObjectDisposedException) { } // the test already closed it
        _clients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseClientsAsync();
        await Server.DisposeAsync();
        if (_ownsDir)
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A connected client that records every pushed event.</summary>
public sealed class Client
{
    public JsonRpcClient Rpc { get; }
    public JsonObject? Hello { get; set; }
    public ConcurrentQueue<JsonObject> Events { get; } = new();
    public TaskCompletionSource<Exception?> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Client(JsonRpcClient rpc)
    {
        Rpc = rpc;
        rpc.Notification += (method, p) =>
        {
            if (method == "events.notify" && p is JsonObject o) Events.Enqueue((JsonObject)o.DeepClone());
        };
        rpc.Disconnected += ex => Disconnected.TrySetResult(ex);
    }

    public async Task<JsonObject> Call(string method, JsonObject? p = null) =>
        (JsonObject)(await Rpc.CallAsync(method, p ?? []))!;

    public async Task<JsonRpcException> Fails(string method, JsonObject? p = null) =>
        await Assert.ThrowsAsync<JsonRpcException>(() => Rpc.CallAsync(method, p ?? []));

    public async Task<JsonObject> WaitForEvent(Func<JsonObject, bool> match, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var found = Events.FirstOrDefault(match);
            if (found is not null) return found;
            await Task.Delay(10);
        }
        throw new TimeoutException("Event did not arrive.");
    }

    public Task<JsonObject> WaitForScanFinished(string scanId, int timeoutMs = 10000) =>
        WaitForEvent(e => (string?)e["stream"] == "scan:" + scanId && (string?)e["type"] == "scan.finished", timeoutMs);

    /// <summary>Starts a scan and waits for its scan.finished event; returns (scan, finished payload).</summary>
    public async Task<(JsonObject Scan, JsonObject Finished)> ScanAsync(string intent, string text = "Begin scan", string? key = null)
    {
        var scan = await Call("scan.start", OptimizationTool.Tests.Rpc.ScanParams(intent, text, key ?? Guid.NewGuid().ToString()));
        var finished = await WaitForScanFinished((string)scan["scanId"]!);
        return (await Call("scan.get", new JsonObject { ["scanId"] = (string)scan["scanId"]! }), finished["payload"]!.AsObject());
    }
}

public static class Rpc
{
    public static JsonObject HelloParams(int major = 1, string kind = "test", int minor = 0) => new()
    {
        ["protocol"] = new JsonObject { ["major"] = major, ["minor"] = minor },
        ["client"] = new JsonObject { ["kind"] = kind, ["version"] = "0.1.0" },
        ["features"] = new JsonArray(),
    };

    public static JsonObject ScanParams(string intent, string text, string key) => new()
    {
        ["idempotencyKey"] = key, ["intent"] = intent, ["text"] = text,
    };

    public static JsonObject Respond(string reportId, long revision, string scope, string? cardId = null, string? option = null, string? text = null, string? key = null)
    {
        var p = new JsonObject
        {
            ["idempotencyKey"] = key ?? Guid.NewGuid().ToString(), ["reportId"] = reportId, ["expectedRevision"] = revision, ["scope"] = scope,
        };
        if (cardId is not null) p["cardId"] = cardId;
        if (option is not null) p["option"] = option;
        if (text is not null) p["text"] = text;
        return p;
    }

    /// <summary>Onboarding the M1 path uses: Local only, Gaming + performance, inventory skipped, complete.</summary>
    public static async Task<JsonObject> OnboardLocal(Client c, string[]? uses = null, string[]? goals = null, string consent = "skipped")
    {
        var current = await c.Call("onboarding.get");
        return await c.Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)current["revision"]!,
            ["patch"] = new JsonObject
            {
                ["provider"] = new JsonObject { ["value"] = "local", ["label"] = "Local only" },
                ["uses"] = new JsonArray((uses ?? ["Gaming"]).Select(u => (JsonNode?)u).ToArray()),
                ["goals"] = new JsonArray((goals ?? ["Improve PC performance"]).Select(u => (JsonNode?)u).ToArray()),
                ["inventoryConsent"] = consent,
                ["step"] = "complete",
            },
        });
    }

    public static string Str(this JsonNode? node, string key) => (string)node![key]!;
}

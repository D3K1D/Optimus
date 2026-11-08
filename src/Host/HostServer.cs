using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Transport;

namespace OptimizationTool.Host;

public sealed class HostOptions
{
    public required string DataDir { get; init; }
    public required string PipeName { get; init; }
    public double FixtureSpeed { get; init; } = 1;

    /// <summary>Live Windows audit modules; null runs the M1 fixture (--source fixture).</summary>
    public IReadOnlyList<IAuditModule>? Modules { get; init; }

    public CollectorOptions CollectorOptions { get; init; } = new();
    public TimeSpan ScanDeadline { get; init; } = TimeSpan.FromSeconds(120);
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Exit after this long with no clients and no active work. Null never exits on idle.</summary>
    public TimeSpan? IdleExit { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>M3: provider adapters, key store and projection for the agent gateway.</summary>
    public OptimizationTool.Engine.Agent.AgentOptions Agent { get; init; } = new();

    /// <summary>Development only: accept an agent client from any image path (normally only OptimizationTool.Mcp.exe beside the host).</summary>
    public bool DevAllowAnyAgent { get; init; }

    /// <summary>M4: compiled action handlers (WindowsModules.Actions) and how machine-scope ones elevate.</summary>
    public IReadOnlyList<IActionModule> ActionModules { get; init; } = [];
    public IElevation? Elevation { get; init; }

    /// <summary>firewall.enable_public stays off unless set (--enable-firewall-handler), m4 §2.</summary>
    public bool EnableFirewallHandler { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The per-user named-pipe JSON-RPC server (protocol §1–§3). Owns the pipe name (first instance), verifies each
/// client's user and session, gates on session.hello, applies the client kind's allowlist, and pushes committed
/// events to every handshaken connection.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    public const int ProtocolMajor = 1, ProtocolMinor = 4;
    public const string AgentExecutable = "OptimizationTool.Mcp.exe";
    /// <summary>scan.fixture for the M1 fixture source, audit.windows for live collectors (protocol §9).</summary>
    public string[] Features => _engine.IsLive ? ["audit.windows"] : ["scan.fixture"];

    /// <summary>Methods each client kind may call after the handshake.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowlists = new Dictionary<string, IReadOnlySet<string>>
    {
        ["desktop"] = AuditEngine.DesktopMethods.ToHashSet(),
        ["test"] = AuditEngine.DesktopMethods.ToHashSet(),
        ["agent"] = AuditEngine.AgentMethods.ToHashSet(),
    };

    private readonly HostOptions _options;
    private readonly AuditEngine _engine;
    private readonly Action<string> _log;
    private readonly PipeSecurity _security;
    private readonly (SecurityIdentifier User, uint Session) _self;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private NamedPipeServerStream? _listening;
    private Task _acceptLoop = Task.CompletedTask;
    private Task _idleLoop = Task.CompletedTask;
    private int _disposed;

    /// <summary>Completes when the host stops (idle exit or <see cref="DisposeAsync"/>).</summary>
    public Task Completion => _completion.Task;

    public AuditEngine Engine => _engine;

    public int ConnectionCount => _connections.Count;

    private HostServer(HostOptions options, AuditEngine engine)
    {
        _options = options;
        _engine = engine;
        _log = options.Log ?? (_ => { });
        _self = ClientIdentity.Current();
        _security = CreateSecurity(_self.User);
    }

    /// <summary>Opens the engine and claims the pipe name. Throws if another host already owns it.</summary>
    public static HostServer Start(HostOptions options)
    {
        var engine = AuditEngine.Open(new EngineOptions
        {
            DataDir = options.DataDir, Modules = options.Modules, CollectorOptions = options.CollectorOptions,
            FixtureSpeed = options.FixtureSpeed, ScanDeadline = options.ScanDeadline, Time = options.Time, Log = options.Log,
            Agent = options.Agent,
            Actions = new ActionOptions
            {
                Handlers = options.ActionModules.SelectMany(m => m.Handlers(new ActionContext(options.Elevation ?? new NoElevation(), options.EnableFirewallHandler))).ToList(),
                EnableFirewallHandler = options.EnableFirewallHandler,
            },
        });
        var server = new HostServer(options, engine);
        try
        {
            server._listening = server.CreatePipe(first: true);
        }
        catch
        {
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        engine.Store.EventCommitted += server.Broadcast;
        server._acceptLoop = Task.Run(server.AcceptLoopAsync);
        if (options.IdleExit is { } idle) server._idleLoop = Task.Run(() => server.IdleLoopAsync(idle));
        server._log($"listening on {options.PipeName}");
        return server;
    }

    /// <summary>
    /// Current user full control, SYSTEM full control, NETWORK denied; nothing inherited. The owner is this process's
    /// default owner (<see cref="WindowsIdentity.Owner"/>): that is what a CurrentUserOnly client compares against —
    /// the user normally, but BUILTIN\Administrators when the app runs elevated ("Run as administrator"), where
    /// owning the pipe as the user made every client refuse it.
    /// </summary>
    internal static PipeSecurity CreateSecurity(SecurityIdentifier user, SecurityIdentifier? owner = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.SetOwner(owner ?? DefaultOwner() ?? user);
        return security;
    }

    private static SecurityIdentifier? DefaultOwner()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.Owner;
    }

    private NamedPipeServerStream CreatePipe(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        // .NET refuses PipeOptions.CurrentUserOnly together with an explicit PipeSecurity (ArgumentException), and
        // on Windows that option only installs a user-only ACL. The explicit ACL below is that restriction plus
        // SYSTEM and the NETWORK deny, with the user as owner so CurrentUserOnly clients accept the server.
        return NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, _security);
    }

    private async Task AcceptLoopAsync()
    {
        var pipe = _listening!;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                _log($"accept failed: {ex.Message}");
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = CreatePipe(first: false);
                _listening = pipe;
                continue;
            }

            var accepted = pipe;
            try
            {
                pipe = CreatePipe(first: false);
                _listening = pipe;
            }
            catch (Exception ex)
            {
                _log($"could not create the next pipe instance: {ex.Message}");
                await accepted.DisposeAsync().ConfigureAwait(false);
                break;
            }
            _ = Task.Run(() => ServeAsync(accepted));
        }
        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        ClientInfo client;
        try
        {
            client = ClientIdentity.Inspect(pipe);
        }
        catch (Exception ex)
        {
            _log($"client rejected: identity unavailable ({ex.Message})");
            await pipe.DisposeAsync().ConfigureAwait(false);
            return;
        }
        if (!ClientIdentity.IsAllowed(client.User, client.SessionId, _self.User, _self.Session))
        {
            _log($"client rejected: pid {client.ProcessId} user {client.User} session {client.SessionId}");
            await pipe.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var connection = new Connection(pipe, this, "c-" + Ids.RandomHex(6)) { Client = client };
        _log($"client {connection.Id} connected: pid {client.ProcessId} {client.ImagePath}");
        _connections[connection] = 0;
        try
        {
            await connection.RunAsync(_stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            if (connection.ClientKind == "agent")
                try { _engine.Agent.ConnectionClosed(Context(connection)); }
                catch (Exception ex) { _log($"client {connection.Id}: close handling failed ({ex.GetType().Name})"); }
            _log($"client {connection.Id} disconnected");
        }
    }

    private void Broadcast(EventRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "events.notify", ["params"] = record.ToJson(),
        });
        foreach (var connection in _connections.Keys)
            // Agent connections never receive events: they could see other tasks' activity or pairing codes.
            if (connection.ClientKind is "desktop" or "test") connection.Send(bytes);
    }

    private async Task IdleLoopAsync(TimeSpan idle)
    {
        DateTime? idleSince = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(10, idle.TotalMilliseconds / 4))), _stop.Token).ConfigureAwait(false);
                if (!_connections.IsEmpty || _engine.HasActiveWork) { idleSince = null; continue; }
                idleSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - idleSince >= idle)
                {
                    _log("idle: no clients and no active work; exiting");
                    _ = DisposeAsync().AsTask();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Handles one request object and returns the response, or null for a client notification.</summary>
    internal async Task<JsonObject?> HandleAsync(Connection connection, JsonNode? message)
    {
        if (message is JsonArray) return Error(null, ErrorCodes.InvalidRequest, "Batches are not supported.");
        if (message is not JsonObject request) return Error(null, ErrorCodes.InvalidRequest, "A request must be a JSON object.");

        var idNode = request["id"];
        if (idNode is null) return null; // a client notification: nothing to answer
        if (idNode is not JsonValue idValue || !idValue.TryGetValue<double>(out _))
            return Error(null, ErrorCodes.InvalidRequest, "id must be a number.");
        var id = idNode.DeepClone();

        if (request["jsonrpc"]?.ToString() is { } version && version != "2.0")
            return Error(id, ErrorCodes.InvalidRequest, "jsonrpc must be \"2.0\".");
        if (request["method"] is not JsonValue mv || !mv.TryGetValue<string>(out var method))
            return Error(id, ErrorCodes.InvalidRequest, "method must be a string.");

        try
        {
            if (method == "session.hello") return Result(id, Hello(connection, request["params"]));
            if (connection.ClientKind is null)
                return Error(id, ErrorCodes.HandshakeRequired, "Send session.hello first.");
            if (!AuditEngine.DesktopMethods.Contains(method) && !AuditEngine.AgentMethods.Contains(method))
                return Error(id, ErrorCodes.MethodNotFound, $"Unknown method {method}.");
            if (!Allowlists[connection.ClientKind].Contains(method))
                return Error(id, ErrorCodes.MethodNotAllowed, $"{method} is not allowed for {connection.ClientKind} clients.",
                    new JsonObject { ["method"] = method, ["clientKind"] = connection.ClientKind });

            return Result(id, await _engine.InvokeAsync(method, request["params"], Context(connection)).ConfigureAwait(false));
        }
        catch (RpcError e)
        {
            return Error(id, e.Code, e.Message, e.ErrorData?.DeepClone());
        }
        catch (Exception ex)
        {
            // provider.setKey: never log anything that could carry the key (only the exception type).
            _log(method == "provider.setKey" ? $"{method} failed: {ex.GetType().Name}" : $"{method} failed: {ex}");
            return Error(id, ErrorCodes.InternalError, "Internal error.");
        }
    }

    private JsonObject Hello(Connection connection, JsonNode? parameters)
    {
        if (parameters is not JsonObject p || p["protocol"] is not JsonObject protocol || protocol["major"] is not JsonValue majorValue
            || !majorValue.TryGetValue<int>(out var major))
            throw RpcError.Params("protocol.major", "protocol.major is required.");
        if (major != ProtocolMajor)
            throw new RpcError(ErrorCodes.IncompatibleProtocol, $"Protocol {major}.x is not supported.", new JsonObject
            {
                ["supported"] = new JsonObject { ["major"] = ProtocolMajor, ["minor"] = ProtocolMinor },
                ["action"] = "Update Optimization Tool",
            });
        var kind = p["client"]?["kind"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null;
        if (kind is null || !Allowlists.ContainsKey(kind))
            throw RpcError.Params("client.kind", "client.kind must be desktop, test or agent.");
        if (kind == "agent" && !_options.DevAllowAnyAgent && !IsTrustedAgentImage(connection.Client?.ImagePath))
        {
            _log($"client {connection.Id} rejected as agent: unexpected image");
            _engine.Agent.BridgeRejected(); // the app shows "Setup issue" (m3-agent §9); this connection gets no events
            throw RpcError.State("untrusted_agent", "Agent clients must be OptimizationTool.Mcp.exe installed with Optimization Tool.");
        }

        connection.ClientKind = kind;
        return new JsonObject
        {
            ["protocol"] = new JsonObject { ["major"] = ProtocolMajor, ["minor"] = ProtocolMinor },
            ["server"] = new JsonObject { ["version"] = AuditEngine.Version },
            ["features"] = Json(Features),
            ["connectionId"] = connection.Id,
            ["eventCursor"] = _engine.UserCursor(),
        };
    }

    private static OptimizationTool.Engine.Agent.CallContext Context(Connection c) =>
        new(c.Id, c.ClientKind ?? "", (int)(c.Client?.ProcessId ?? 0), c.Client?.User?.Value, (int)(c.Client?.SessionId ?? 0));

    /// <summary>An agent client must be the MCP bridge installed beside this host (m3-agent §1).</summary>
    internal static bool IsTrustedAgentImage(string? imagePath) =>
        imagePath is not null && string.Equals(Path.GetFullPath(imagePath), Path.Combine(AppContext.BaseDirectory, AgentExecutable), StringComparison.OrdinalIgnoreCase);

    private static JsonArray Json(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    private static JsonObject Result(JsonNode? id, JsonNode? result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    internal static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error };
    }

    internal void Log(string message) => _log(message);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            await Completion.ConfigureAwait(false);
            return;
        }
        _engine.Store.EventCommitted -= Broadcast;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _acceptLoop.ConfigureAwait(false); } catch (Exception ex) { _log($"accept loop: {ex.Message}"); }
        foreach (var connection in _connections.Keys) await connection.DisposeAsync().ConfigureAwait(false);
        try { await _idleLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _engine.DisposeAsync().ConfigureAwait(false);
        _completion.TrySetResult();
    }
}

/// <summary>Used when no broker is wired: machine-scope actions are declined (nothing is written).</summary>
internal sealed class NoElevation : IElevation
{
    public Task<ActionOutcome> RunAsync(string actionId, ActionExecution execution, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("declined", [], "Administrator approval is not available.", "no_broker", "No elevation route."));
}

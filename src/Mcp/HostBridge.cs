using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Transport;

namespace OptimizationTool.Mcp;

internal sealed record BridgeOptions
{
    /// <summary>Pipe to the host. Default: this user's session pipe (PipeNames.ForCurrentSession).</summary>
    public string PipeName { get; init; } = PipeNames.ForCurrentSession();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5); // slow or busy PCs (and CI) missed 2 s
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>One tool.call; longer than a 60 s sample.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>How long run_diagnostic keeps polling a long operation before handing the operationId to the model.</summary>
    public TimeSpan PollBudget { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxArgumentBytes { get; init; } = 64 * 1024;
    public string Version { get; init; } = "0.1.0";
    /// <summary>m5 §3: the run token a headless run put in our environment (OT_RUN_TOKEN, 32 hex), or null. Sent in agent.pair.</summary>
    public string? RunToken { get; init; } = ReadRunToken(Environment.GetEnvironmentVariable("OT_RUN_TOKEN"));

    public static string? ReadRunToken(string? value) =>
        value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? value : null;
}

/// <summary>Who launched us, from the MCP initialize clientInfo.</summary>
internal sealed record ClientIdentity(string Kind, string? Version)
{
    public static ClientIdentity From(string? name, string? version)
    {
        var n = (name ?? "").ToLowerInvariant();
        var kind = n.Contains("claude") ? "claude_code" : n.Contains("codex") ? "codex" : "unknown";
        return new ClientIdentity(kind, string.IsNullOrWhiteSpace(version) ? null : Truncate(version, 64));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>A tool reply: the text the model sees, whether it is an error, and the JSON object if any.</summary>
internal sealed record BridgeReply(string Text, bool IsError, JsonObject? Structured);

/// <summary>
/// The pipe side of the bridge: one `agent` connection to the host, the pairing state machine (§6), and
/// forwarding of tool calls as `tool.call`. Thread-safe; tool calls run concurrently once paired.
/// </summary>
internal sealed class HostBridge(BridgeOptions options) : IAsyncDisposable
{
    public static string WaitingMessage(string code) =>
        $"Waiting for approval in Optimus (code {code}). Ask the user to approve it in the app, then call this tool again.";

    public const string NotRunningMessage =
        "Optimus isn't running. Open Optimus to connect, then call this tool again.";

    private static readonly HashSet<string> PairingLostReasons =
        ["not_paired", "unpaired", "pairing_required", "pairing_unknown", "pairing_expired", "unknown_pairing"];

    /// <summary>Readable text for the bridge's own messages. Host results keep default escaping so markup stays escaped.</summary>
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Connection? _connection;
    private string? _pairingId;
    private string? _code;
    private bool _approved;
    private bool _verifyPairing;

    private sealed class Connection(JsonRpcClient rpc)
    {
        public JsonRpcClient Rpc { get; } = rpc;
        public volatile bool Dropped;
    }

    private sealed class HostUnavailableException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    public async Task<BridgeReply> CallAsync(string tool, JsonObject arguments, ClientIdentity client, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var reply = await CallCoreAsync(tool, arguments, client, ct).ConfigureAwait(false);
        var outcome = reply.IsError ? reply.Structured?["error"]?["code"]?.ToString() ?? "error" : "ok";
        Log.Info($"tool={tool} outcome={outcome} ms={started.ElapsedMilliseconds}");
        return reply;
    }

    private async Task<BridgeReply> CallCoreAsync(string tool, JsonObject arguments, ClientIdentity client, CancellationToken ct)
    {
        if (!ToolCatalog.IsKnown(tool)) return Error("invalid_arguments", $"Unknown tool '{Clip(tool)}'.");
        if (JsonSerializer.SerializeToUtf8Bytes(arguments).Length > options.MaxArgumentBytes)
            return Error("too_large", $"Arguments exceed {options.MaxArgumentBytes / 1024} KiB.");

        Connection connection;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var gate = await EnsurePairedAsync(connection, client, ct).ConfigureAwait(false);
            if (gate is not null) return gate;
        }
        catch (HostUnavailableException e) { return Error(e.Code, e.Message); }
        catch (IOException)
        {
            MarkDropped(connection: null);
            return Error("unavailable", "The connection to Optimus was lost. Call this tool again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error("timeout", "Optimus did not answer in time. Call this tool again.");
        }
        finally { _gate.Release(); }

        var reply = await ForwardAsync(connection, client, tool, arguments, options.CallTimeout, ct).ConfigureAwait(false);
        if (tool == "run_diagnostic") reply = await PollOperationAsync(connection, client, reply, ct).ConfigureAwait(false);
        return reply;
    }

    // ---- connection -------------------------------------------------------------------------------------------

    private async Task<Connection> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is { Dropped: false } live) return live;
        if (_connection is { } dead)
        {
            _connection = null;
            await dead.Rpc.DisposeAsync().ConfigureAwait(false);
        }
        // A new connection: a pairing survives only if the host still has it, so re-check it before use.
        if (_pairingId is not null) _verifyPairing = true;

        JsonRpcClient rpc;
        try
        {
            rpc = await JsonRpcClient.ConnectAsync(options.PipeName, options.ConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new HostUnavailableException("unavailable", NotRunningMessage); }
        catch (IOException) { throw new HostUnavailableException("unavailable", NotRunningMessage); }
        catch (UnauthorizedAccessException)
        {
            throw new HostUnavailableException("unavailable", "Optimus's connection belongs to another user. Open Optimus as this user.");
        }

        var connection = new Connection(rpc);
        rpc.Disconnected += _ => connection.Dropped = true;
        try
        {
            using var timeout = Timeout(ct, options.ControlTimeout);
            await rpc.CallAsync("session.hello", new JsonObject
            {
                ["protocol"] = new JsonObject { ["major"] = 1, ["minor"] = 2 },
                ["client"] = new JsonObject { ["kind"] = "agent", ["version"] = options.Version },
                ["features"] = new JsonArray(),
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonRpcException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            await rpc.DisposeAsync().ConfigureAwait(false);
            Log.Warn($"hello failed: {(e is JsonRpcException j ? j.Code.ToString() : e.GetType().Name)}");
            throw e is JsonRpcException { Code: -32010 }
                ? new HostUnavailableException("unavailable", "This Optimus version doesn't support Claude Code or Codex yet. Update Optimus.")
                : new HostUnavailableException("unavailable", "Optimus refused the connection. Use the bridge installed with the app, then call this tool again.");
        }

        Log.Info("connected");
        _connection = connection;
        return connection;
    }

    private void MarkDropped(Connection? connection)
    {
        var target = connection ?? _connection;
        if (target is not null) target.Dropped = true;
    }

    // ---- pairing (§6) -----------------------------------------------------------------------------------------

    /// <summary>Null when paired and approved; otherwise the reply to return (waiting, denied, …). Holds the gate.</summary>
    private async Task<BridgeReply?> EnsurePairedAsync(Connection connection, ClientIdentity client, CancellationToken ct)
    {
        if (_pairingId is not null && (!_approved || _verifyPairing))
        {
            _verifyPairing = false;
            var status = await PairStatusAsync(connection, ct).ConfigureAwait(false);
            switch (status)
            {
                case "approved" or "active" or "bound":
                    _approved = true;
                    break;
                case "pending":
                    _approved = false;
                    return Waiting();
                case "denied":
                    ResetPairing();
                    Log.Info("pairing denied");
                    return Error("not_allowed", "The pairing was denied in Optimus. Call this tool again to request a new code.");
                default: // expired, revoked, unknown to this host (e.g. host restarted)
                    ResetPairing();
                    Log.Info($"pairing {status}; re-pairing");
                    break;
            }
        }

        if (_pairingId is null)
        {
            await PairAsync(connection, client, ct).ConfigureAwait(false);
            return _approved ? null : Waiting();
        }
        return null;
    }

    private async Task PairAsync(Connection connection, ClientIdentity client, CancellationToken ct)
    {
        var parameters = new JsonObject { ["client"] = client.Kind, ["companionPid"] = Environment.ProcessId };
        if (client.Version is not null) parameters["clientVersion"] = client.Version;
        if (options.RunToken is not null) parameters["runToken"] = options.RunToken; // m5 §3: started by the app's own headless run
        using var timeout = Timeout(ct, options.ControlTimeout);
        JsonNode? result;
        try
        {
            result = await connection.Rpc.CallAsync("agent.pair", parameters, timeout.Token).ConfigureAwait(false);
        }
        catch (JsonRpcException e)
        {
            throw new HostUnavailableException(HostCodeName(e), $"Optimus could not start pairing: {Clip(e.Message)}");
        }
        var pairingId = result?["pairingId"]?.GetValue<string>();
        var approved = result?["status"]?.GetValue<string>() == "approved";
        var code = result?["code"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pairingId) || (!approved && string.IsNullOrEmpty(code)))
            throw new HostUnavailableException("unavailable", "Optimus returned an invalid pairing.");
        _pairingId = pairingId;
        _code = approved ? null : Clip(code!, 16);
        _approved = approved; // a run token is approved at once: no code, no waiting
        _verifyPairing = false;
        Log.Info(approved ? $"run paired client={client.Kind}" : $"pairing requested client={client.Kind}");
    }

    private async Task<string> PairStatusAsync(Connection connection, CancellationToken ct)
    {
        using var timeout = Timeout(ct, options.ControlTimeout);
        try
        {
            var result = await connection.Rpc.CallAsync("agent.pairStatus", new JsonObject { ["pairingId"] = _pairingId }, timeout.Token).ConfigureAwait(false);
            return result?["status"]?.GetValue<string>() ?? "unknown";
        }
        catch (JsonRpcException) { return "unknown"; } // not_found / invalid_state: the host no longer has it
    }

    private void ResetPairing()
    {
        _pairingId = null;
        _code = null;
        _approved = false;
        _verifyPairing = false;
    }

    private BridgeReply Waiting()
    {
        var text = WaitingMessage(_code!);
        return new BridgeReply(text, IsError: false, new JsonObject { ["status"] = "pairing_required", ["code"] = _code, ["message"] = text });
    }

    // ---- forwarding -------------------------------------------------------------------------------------------

    private async Task<BridgeReply> ForwardAsync(Connection connection, ClientIdentity client, string tool, JsonObject arguments, TimeSpan limit, CancellationToken ct)
    {
        JsonNode? result;
        try
        {
            using var timeout = Timeout(ct, limit);
            result = await connection.Rpc.CallAsync("tool.call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error("timeout", $"Optimus did not answer within {(int)limit.TotalSeconds} s.");
        }
        catch (IOException)
        {
            connection.Dropped = true;
            return Error("unavailable", "The connection to Optimus was lost. Call this tool again.");
        }
        catch (JsonRpcException e)
        {
            if (IsPairingLost(e.ErrorData?["reason"]?.ToString()) || IsPairingLost(e.ErrorData?["code"]?.ToString()) ||
                (e.Code == -32001 && e.ErrorData?["kind"]?.ToString() == "pairing"))
                return await RepairAsync(connection, client, ct).ConfigureAwait(false);
            return new BridgeReply(HostErrorJson(e).ToJsonString(TextJson), IsError: true, HostErrorJson(e));
        }

        if (result is not JsonObject obj)
            return Error("unavailable", "Optimus returned an empty result.");

        if (obj["error"] is JsonObject error)
        {
            var code = error["code"]?.ToString();
            if (IsPairingLost(code)) return await RepairAsync(connection, client, ct).ConfigureAwait(false);
            if (code is "revoked" or "expired")
            {
                // The user stopped the task or it went idle. Don't pop a new pairing card unasked:
                // the next call requests a new code.
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try { ResetPairing(); } finally { _gate.Release(); }
                Log.Info($"task {code}; next call re-pairs");
            }
            return new BridgeReply(obj.ToJsonString(), IsError: true, obj);
        }
        return new BridgeReply(obj.ToJsonString(), IsError: false, obj);
    }

    private async Task<BridgeReply> RepairAsync(Connection connection, ClientIdentity client, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ResetPairing();
            Log.Info("host lost the pairing; re-pairing");
            await PairAsync(connection, client, ct).ConfigureAwait(false);
            return _approved ? Error("unavailable", "The connection to Optimus was renewed. Call this tool again.") : Waiting();
        }
        catch (HostUnavailableException e) { return Error(e.Code, e.Message); }
        catch (IOException)
        {
            connection.Dropped = true;
            return Error("unavailable", "The connection to Optimus was lost. Call this tool again.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>run_diagnostic returned a running operation: poll get_operation for a while so short waits need no model turn.</summary>
    private async Task<BridgeReply> PollOperationAsync(Connection connection, ClientIdentity client, BridgeReply reply, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (!reply.IsError && reply.Structured is { } running &&
               running["status"]?.ToString() == "running" && running["operationId"]?.ToString() is { Length: > 0 } operationId)
        {
            var eta = running["etaSeconds"] is JsonValue v && v.TryGetValue<double>(out var s) ? TimeSpan.FromSeconds(s) : options.MaxPollInterval;
            var wait = TimeSpan.FromTicks(Math.Clamp(eta.Ticks, TimeSpan.FromMilliseconds(100).Ticks, options.MaxPollInterval.Ticks));
            if (deadline.Elapsed + wait > options.PollBudget) break;
            await Task.Delay(wait, ct).ConfigureAwait(false);
            var next = await ForwardAsync(connection, client, "get_operation", new JsonObject { ["operationId"] = operationId }, options.ControlTimeout, ct).ConfigureAwait(false);
            if (next.IsError) return next;
            reply = next;
        }
        return reply;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static bool IsPairingLost(string? reason) => reason is not null && PairingLostReasons.Contains(reason);

    /// <summary>JSON-RPC error from the host → the §3 error shape, keeping the host's code and message.</summary>
    private static JsonObject HostErrorJson(JsonRpcException e) => new()
    {
        ["error"] = new JsonObject
        {
            ["code"] = HostCodeName(e),
            ["message"] = Clip(e.Message, 500),
            ["hostCode"] = e.Code,
        },
    };

    private static string HostCodeName(JsonRpcException e)
    {
        if (e.ErrorData?["code"] is JsonValue c && c.TryGetValue<string>(out var named) && named.Length is > 0 and <= 40) return named;
        return e.Code switch
        {
            -32602 => "invalid_arguments",
            -32601 or -32012 => "not_allowed",
            -32001 => "not_found",
            -32013 => e.ErrorData?["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) && reason.Length <= 40 ? reason : "invalid_state",
            _ => "unavailable",
        };
    }

    private static BridgeReply Error(string code, string message)
    {
        var obj = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
        return new BridgeReply(obj.ToJsonString(TextJson), IsError: true, obj);
    }

    private static CancellationTokenSource Timeout(CancellationToken ct, TimeSpan limit)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        return cts;
    }

    private static string Clip(string s, int max = 80) => s.Length <= max ? s : s[..max];

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } c) await c.Rpc.DisposeAsync().ConfigureAwait(false);
        _connection = null;
        _gate.Dispose();
    }
}

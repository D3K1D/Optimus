using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizationTool.Transport;

/// <summary>
/// Minimal JSON-RPC 2.0 client over a framed stream. Used by the desktop shell and test clients.
/// Server-to-client notifications (no id) are raised through <see cref="Notification"/>.
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private long _nextId;
    private int _disposed;

    /// <summary>Raised on the read loop for each notification: (method, params).</summary>
    public event Action<string, JsonNode?>? Notification;

    /// <summary>Raised once when the connection ends, with the cause if it was not a clean close.</summary>
    public event Action<Exception?>? Disconnected;

    public JsonRpcClient(Stream stream)
    {
        _stream = stream;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Connects to the current user's backend pipe.</summary>
    public static async Task<JsonRpcClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            System.Security.Principal.TokenImpersonationLevel.Identification); // the server may identify us, never impersonate
        try
        {
            await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
            return new JsonRpcClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a request and returns its result, or throws <see cref="JsonRpcException"/> on an error response.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
    {
        var response = await CallRawAsync(method, parameters, ct).ConfigureAwait(false);
        if (response["error"] is JsonObject error)
            throw new JsonRpcException(
                error["code"]?.GetValue<int>() ?? 0,
                error["message"]?.GetValue<string>() ?? "",
                error["data"]?.DeepClone());
        return response["result"]?.DeepClone();
    }

    /// <summary>Sends a request and returns the whole response object (result or error), for forwarding.</summary>
    public async Task<JsonObject> CallRawAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters.DeepClone();
        try
        {
            await SendAsync(request, ct).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await Frame.WriteAsync(_stream, bytes, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? cause = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var payload = await Frame.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (payload is null) break;
                if (JsonNode.Parse(payload) is not JsonObject message) continue;

                if (message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id))
                {
                    if (_pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(message);
                }
                else if (message["method"]?.GetValue<string>() is { } method)
                {
                    Notification?.Invoke(method, message["params"]);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { cause = ex; }
        finally
        {
            var failure = new IOException("Connection to the backend closed.", cause);
            foreach (var tcs in _pending.Values) tcs.TrySetException(failure);
            Disconnected?.Invoke(cause);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        try { await _readLoop.ConfigureAwait(false); } catch { /* already surfaced through Disconnected */ }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

public sealed class JsonRpcException(int code, string message, JsonNode? data) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;
}

using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OptimizationTool.Engine;
using OptimizationTool.Transport;

namespace OptimizationTool.Host;

/// <summary>
/// One client connection. Requests are read and answered in order; responses and pushed events share one
/// writer so frames never interleave. Any frame error (oversize, truncated) drops the connection.
/// </summary>
internal sealed class Connection(NamedPipeServerStream pipe, HostServer server, string id) : IAsyncDisposable
{
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public string Id { get; } = id;

    /// <summary>Set by a successful session.hello; null until then.</summary>
    public volatile string? ClientKind;

    /// <summary>The verified client process (PID, user, session, image).</summary>
    public ClientInfo? Client { get; init; }

    public void Send(byte[] frame) => _outbox.Writer.TryWrite(frame);

    public async Task RunAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _cts.Token);
        var writer = Task.Run(() => WriteLoopAsync(linked.Token));
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var payload = await Frame.ReadAsync(pipe, linked.Token).ConfigureAwait(false);
                if (payload is null) break;

                JsonNode? message;
                try { message = JsonNode.Parse(payload); }
                catch (JsonException)
                {
                    Send(Serialize(HostServer.Error(null, ErrorCodes.ParseError, "Invalid JSON.")));
                    continue;
                }

                var response = await server.HandleAsync(this, message).ConfigureAwait(false);
                if (response is not null) Send(Fit(response));
            }
        }
        catch (FrameException ex)
        {
            server.Log($"client {Id}: {ex.Message} Dropping the connection.");
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _outbox.Writer.TryComplete();
            try { await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* closing anyway */ }
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (frame.Length > Frame.MaxPayloadBytes)
                {
                    server.Log($"client {Id}: response of {frame.Length} bytes exceeds the frame cap; dropped");
                    continue;
                }
                await Frame.WriteAsync(pipe, frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    private static byte[] Serialize(JsonObject message) => JsonSerializer.SerializeToUtf8Bytes(message);

    /// <summary>A response over the frame cap becomes a small error with the same id, so the request still settles.</summary>
    internal static byte[] Fit(JsonObject response, int maxBytes = Frame.MaxPayloadBytes)
    {
        var bytes = Serialize(response);
        if (bytes.Length <= maxBytes) return bytes;
        return Serialize(HostServer.Error(response["id"]?.DeepClone(), ErrorCodes.InternalError, "The response was too large to send.",
            new JsonObject { ["reason"] = "response_too_large", ["bytes"] = bytes.Length }));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _outbox.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        await pipe.DisposeAsync().ConfigureAwait(false);
    }
}

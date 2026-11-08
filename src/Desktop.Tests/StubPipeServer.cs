using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Tests;

/// <summary>
/// Minimal fake host: answers session.hello, echoes other calls as {"echo":method,"params":…}, can push events,
/// hold calls, refuse the handshake as incompatible, and drop the connection.
/// </summary>
public sealed class StubPipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private NamedPipeServerStream? _current;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string PipeName { get; } = "OptimizationTool.test." + Guid.NewGuid().ToString("N");
    public bool Incompatible { get; set; }
    /// <summary>Methods whose requests are never answered (to simulate an in-flight call during a drop).</summary>
    public HashSet<string> Hold { get; } = [];
    public int Connections;
    public int Hellos;
    public List<string> Methods { get; } = [];
    public TaskCompletionSource HeldCallArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StubPipeServer(bool start = true) => _loop = start ? Task.Run(() => RunAsync(_cts.Token)) : Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(ct);
                Interlocked.Increment(ref Connections);
                _current = server;
                while (!ct.IsCancellationRequested)
                {
                    var frame = await Frame.ReadAsync(server, ct);
                    if (frame is null) break;
                    var req = JsonNode.Parse(frame)!.AsObject();
                    var method = req["method"]!.GetValue<string>();
                    lock (Methods) Methods.Add(method);
                    JsonObject resp;
                    if (method == "session.hello")
                    {
                        Interlocked.Increment(ref Hellos);
                        resp = Incompatible
                            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32010, ["message"] = "incompatible_protocol", ["data"] = new JsonObject { ["supported"] = new JsonObject { ["major"] = 2, ["minor"] = 0 }, ["action"] = "Update Optimization Tool" } } }
                            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req["id"]!.DeepClone(), ["result"] = new JsonObject { ["protocol"] = new JsonObject { ["major"] = 1, ["minor"] = 0 }, ["server"] = new JsonObject { ["version"] = "stub" }, ["features"] = new JsonArray(), ["connectionId"] = "c-stub" } };
                    }
                    else if (Hold.Contains(method))
                    {
                        HeldCallArrived.TrySetResult();
                        continue;
                    }
                    else
                    {
                        resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = req["id"]!.DeepClone(), ["result"] = new JsonObject { ["echo"] = method, ["params"] = req["params"]?.DeepClone() } };
                    }
                    await WriteAsync(server, resp, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                _current = null;
                await server.DisposeAsync();
            }
        }
    }

    private async Task WriteAsync(Stream s, JsonObject msg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await Frame.WriteAsync(s, JsonSerializer.SerializeToUtf8Bytes(msg), ct); }
        finally { _writeLock.Release(); }
    }

    public Task NotifyAsync(JsonObject eventParams) =>
        _current is { } s
            ? WriteAsync(s, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "events.notify", ["params"] = eventParams }, _cts.Token)
            : throw new InvalidOperationException("No client.");

    /// <summary>Drops the current connection; the loop then accepts a new one.</summary>
    public void Drop() => _current?.Dispose();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _current?.Dispose();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}

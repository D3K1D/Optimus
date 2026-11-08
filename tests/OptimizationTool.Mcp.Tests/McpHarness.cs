using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OptimizationTool.Mcp.Tests;

/// <summary>The bridge's MCP server and an SDK client, connected in-process over in-memory streams.</summary>
internal sealed class McpHarness : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _cts = new();
    private HostBridge _bridge = null!;
    private McpServer _server = null!;
    private Task _serverTask = Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;

    static McpHarness() => Log.FilePath = null;

    public static async Task<McpHarness> StartAsync(string pipeName, string clientName = "claude-code", string clientVersion = "2.1.310",
        Func<BridgeOptions, BridgeOptions>? configure = null)
    {
        var h = new McpHarness();
        var options = new BridgeOptions
        {
            PipeName = pipeName,
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            ControlTimeout = TimeSpan.FromSeconds(5),
            CallTimeout = TimeSpan.FromSeconds(5),
        };
        if (configure is not null) options = configure(options);
        h._bridge = new HostBridge(options);
        var serverOptions = BridgeServer.CreateOptions(h._bridge, "test");
        var serverTransport = new StreamServerTransport(h._clientToServer.Reader.AsStream(), h._serverToClient.Writer.AsStream(), ToolCatalog.ServerName);
        h._server = McpServer.Create(serverTransport, serverOptions);
        h._serverTask = h._server.RunAsync(h._cts.Token);

        var clientTransport = new StreamClientTransport(h._clientToServer.Writer.AsStream(), h._serverToClient.Reader.AsStream());
        h.Client = await McpClient.CreateAsync(clientTransport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = clientName, Version = clientVersion },
        });
        return h;
    }

    public Task<CallToolResult> CallAsync(string tool, object? arguments = null)
    {
        var dict = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToNode(arguments)!.AsObject().ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.DeepClone());
        return Client.CallToolAsync(tool, dict).AsTask();
    }

    public static string Text(CallToolResult r) => Assert.IsType<TextContentBlock>(Assert.Single(r.Content)).Text;
    public static JsonObject Json(CallToolResult r) => JsonNode.Parse(Text(r))!.AsObject();

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _cts.Cancel();
        try { await _serverTask; } catch { }
        await _server.DisposeAsync();
        await _bridge.DisposeAsync();
        _cts.Dispose();
    }
}

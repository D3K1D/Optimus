using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OptimizationTool.Mcp;

/// <summary>MCP server options for the bridge: the fixed tool list and a call handler that forwards to the host.</summary>
internal static class BridgeServer
{
    public static McpServerOptions CreateOptions(HostBridge bridge, string version) => new()
    {
        ServerInfo = new Implementation { Name = ToolCatalog.ServerName, Title = "Optimus", Version = version },
        ServerInstructions = ToolCatalog.Instructions,
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. ToolCatalog.Tools] }),
            CallToolHandler = async (request, ct) =>
            {
                var name = request.Params?.Name ?? "";
                var arguments = new JsonObject();
                if (request.Params?.Arguments is { } args)
                    foreach (var (key, value) in args)
                        arguments[key] = JsonNode.Parse(value.GetRawText());

                var info = request.Server.ClientInfo;
                var reply = await bridge.CallAsync(name, arguments, ClientIdentity.From(info?.Name, info?.Version), ct).ConfigureAwait(false);
                return ToResult(reply);
            },
        },
    };

    public static CallToolResult ToResult(BridgeReply reply) => new()
    {
        Content = [new TextContentBlock { Text = reply.Text }],
        StructuredContent = reply.Structured is null ? null : JsonSerializer.SerializeToElement(reply.Structured),
        IsError = reply.IsError,
    };
}

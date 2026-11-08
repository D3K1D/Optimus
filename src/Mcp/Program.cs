// Stdio MCP bridge for Claude Code and Codex (contracts/m3-agent.md §6).
// stdout carries only MCP protocol messages; diagnostics go to %LOCALAPPDATA%\OptimizationTool\logs\mcp.log.
using ModelContextProtocol.Server;
using OptimizationTool.Mcp;

// Anything that writes through Console.Out by mistake lands on stderr, never in the protocol stream.
// (The stdio transport writes to the raw standard output stream, not Console.Out.)
Console.SetOut(Console.Error);

const string Version = "0.1.0";
var options = new BridgeOptions { Version = Version };
for (var i = 0; i < args.Length; i++)
{
    // Development/test override: point the bridge at another pipe owned by this user.
    if (args[i] == "--pipe" && i + 1 < args.Length) options = options with { PipeName = args[++i] };
    else if (args[i] == "--no-log") Log.FilePath = null;
}

Log.Info($"start version={Version}");
try
{
    await using var bridge = new HostBridge(options);
    var serverOptions = BridgeServer.CreateOptions(bridge, Version);
    await using var transport = new StdioServerTransport(serverOptions);
    await using var server = McpServer.Create(transport, serverOptions);
    await server.RunAsync();
    Log.Info("stop");
    return 0;
}
catch (Exception e)
{
    Log.Warn($"fatal {e.GetType().Name}");
    await Console.Error.WriteLineAsync($"optimization-tool MCP bridge stopped: {e.GetType().Name}");
    return 1;
}

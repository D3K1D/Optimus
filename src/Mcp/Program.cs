// Stdio MCP bridge for Claude Code (contracts/m3-agent.md §6).
// stdout carries only MCP protocol messages; diagnostics go to %LOCALAPPDATA%\OptimizationTool\logs\mcp.log (and dev.log
// when the developer log is on: docs/error-logging.md). The log never echoes anywhere.
using ModelContextProtocol.Server;
using OptimizationTool.Mcp;
using OptimizationTool.Transport.Logging;

// Anything that writes through Console.Out by mistake lands on stderr, never in the protocol stream.
// (The stdio transport writes to the raw standard output stream, not Console.Out.)
Console.SetOut(Console.Error);

const string Version = "0.1.0";
var options = new BridgeOptions { Version = Version };
var noLog = false;
for (var i = 0; i < args.Length; i++)
{
    // Development/test override: point the bridge at another pipe owned by this user.
    if (args[i] == "--pipe" && i + 1 < args.Length) options = options with { PipeName = args[++i] };
    else if (args[i] == "--no-log") noLog = true;
}

// --no-log keeps AppLog.Current a no-op (tests).
var dataDir = DevLogSwitch.DefaultDataDir;
var log = noLog ? AppLog.Disabled : AppLog.Open("mcp", dataDir, DevLogSwitch.IsOn(args, dataDir), echo: null);
if (!noLog) CrashHandlers.Register(log, dataDir, ErrorSites.McpUnhandled);

log.Info($"lifecycle: start version={Version} dev-log={DevLogSwitch.Explain(args, dataDir)}");
try
{
    await using var bridge = new HostBridge(options);
    var serverOptions = BridgeServer.CreateOptions(bridge, Version);
    await using var transport = new StdioServerTransport(serverOptions);
    await using var server = McpServer.Create(transport, serverOptions);
    await server.RunAsync();
    log.Info("lifecycle: stop");
    return 0;
}
catch (Exception e)
{
    log.Error("lifecycle: fatal", ErrorSites.McpUnhandled, e);
    await Console.Error.WriteLineAsync($"optimization-tool MCP bridge stopped: {e.GetType().Name}");
    return 1;
}

using System.Globalization;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Bench;
using OptimizationTool.Host.Bench;
using OptimizationTool.Host;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Providers;
using OptimizationTool.Transport;
using OptimizationTool.Transport.Logging;
using OptimizationTool.Windows;

// OptimizationTool.Host [--data-dir <path>] [--pipe-name <name>] [--source windows|fixture] [--fixture-speed <factor>]
//                       [--sample-seconds <n>] [--idle-exit-seconds <n>] [--dev-allow-any-client] [--enable-firewall-handler] [--enable-admin-actions] [--enable-elevated-scripts] [--dev-log]
const string Usage = "Usage: OptimizationTool.Host [--data-dir <path>] [--pipe-name <name>] [--source windows|fixture] "
    + "[--fixture-speed <factor>] [--sample-seconds <n>] [--idle-exit-seconds <n>] [--dev-allow-any-client] [--enable-firewall-handler] [--enable-admin-actions] [--enable-elevated-scripts] [--dev-log]";
var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimizationTool");
var pipeName = PipeNames.ForCurrentSession();
var source = "windows";
var speed = 1.0;
var sampleSeconds = 60.0;
var idleSeconds = 120.0;
var devAllowAnyClient = false;
var enableFirewallHandler = false;
var enableAdminActions = false; // m9 D6: administrator actions added in M9 stay off until the VM pass
var enableElevatedScripts = false; // m9 §5.4, D6: elevated scripts need this AND --enable-admin-actions

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
    switch (args[i])
    {
        case "--data-dir": dataDir = Path.GetFullPath(Next()); break;
        case "--pipe-name": pipeName = Next(); break;
        case "--source": source = Next(); break;
        case "--fixture-speed": speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--sample-seconds": sampleSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--idle-exit-seconds": idleSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--dev-allow-any-client" or "--dev-allow-any-agent": devAllowAnyClient = true; break; // old name kept as an alias
        case "--enable-firewall-handler": enableFirewallHandler = true; break;
        case "--enable-admin-actions": enableAdminActions = true; break;
        case "--enable-elevated-scripts": enableElevatedScripts = true; break;
        case DevLogSwitch.Argument: break; // read by DevLogSwitch below
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}.");
            Console.Error.WriteLine(Usage);
            return 64;
    }
}
if (source is not ("windows" or "fixture") || sampleSeconds <= 0)
{
    Console.Error.WriteLine(Usage);
    return 64;
}

// Diagnostics go to stderr and to <data dir>\logs\host.log, and to dev.log when the developer log is on (scrubbed of the
// user's folder, names, SIDs and emails: docs/error-logging.md). The shell starts the host with no console, so the files
// are the only record of why a host failed or exited.
var hostLog = AppLog.Open("host", dataDir, DevLogSwitch.IsOn(args, dataDir), echo: Console.Error);
void Log(string message) => hostLog.Info(message);
CrashHandlers.Register(hostLog, dataDir, ErrorSites.HostUnhandled);
Log($"lifecycle: start {typeof(AppLog).Assembly.GetName().Version?.ToString(3)} pid={Environment.ProcessId} source={source} dev-log={DevLogSwitch.Explain(args, dataDir)}");

// One host per pipe name. The mutex is held on this (main) thread for the life of the process.
using var mutex = new Mutex(initiallyOwned: true, @"Local\OptimizationTool.Host." + pipeName, out var owned);
if (!owned)
{
    Log($"another host already serves {pipeName}; exiting");
    return HostExitCodes.AlreadyServing;
}

// One host per user and data folder across Windows sessions: the pipe is per session, the database is per user, and
// two hosts on one database would each mark the other's live work as interrupted. Held on this (main) thread for
// the life of the process; a host that dies abandons it, and the next start takes it over.
using var userLock = SingleHostLock.TryAcquire(SingleHostLock.NameForCurrentUser(dataDir));
if (userLock is null)
{
    Log("other_session: Optimus is open in another session; exiting");
    mutex.ReleaseMutex();
    return HostExitCodes.OtherSession;
}

// In-app providers (src/Providers): Anthropic and OpenAI adapters with keys, and the ChatGPT plan through a browser
// sign-in; secrets in current-user DPAPI under the data directory (secrets folder). The plan adapter shares the
// sign-in so it can refresh its token and retry once on a 401.
var credentials = new DpapiCredentialStore(dataDir);
var chatGpt = new ChatGptSignIn(dataDir);
using var providers = ProviderRegistry.CreateDefault(store: credentials, chatGpt: chatGpt);

// M6a: live sensors, hardware and the worker beside the host; the fixture source uses in-process fakes (no workload
// ever runs), with timings scaled by --fixture-speed.
BenchOptions bench = source == "windows"
    ? new BenchOptions
    {
        Sensors = new OptimizationTool.Windows.Sensors.SensorsFactory(),
        Hardware = new OptimizationTool.Windows.Sensors.BenchHardwareReader(),
        Launcher = new WindowsBenchLauncher(),
    }
    : FixtureBench.Options(speed);

// One broker route for every administrator change: single plans, fix plans and the scripts' restore point (m9 §3.5).
var elevation = new BrokerElevation { AdminChecksEnabled = enableAdminActions }; // m9 §17.2: administrator checks behind the same flag

HostServer server;
try
{
    server = StartServer();
}
catch (DataTooNewException ex)
{
    // A newer Optimus migrated this data folder. One line, no stack trace; the shell shows "needs update" and stops relaunching.
    Log($"needs_update: database schema v{ex.Found} is newer than this build supports (v{ex.Supported}); update Optimus");
    userLock.Dispose();
    mutex.ReleaseMutex();
    return HostExitCodes.NeedsUpdate;
}


HostServer StartServer() => HostServer.Start(new HostOptions
{
    DataDir = dataDir, PipeName = pipeName, FixtureSpeed = speed,
    Modules = source == "windows" ? WindowsModules.All : null,
    CollectorOptions = new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(sampleSeconds) },
    IdleExit = idleSeconds > 0 ? TimeSpan.FromSeconds(idleSeconds) : null, Log = Log,
    DevAllowAnyAgent = devAllowAnyClient, DevAllowAnyClient = devAllowAnyClient,
    // M4: compiled handlers; machine-scope ones elevate through the one-shot broker (UAC). Firewall stays off by default.
    ActionModules = source == "windows" ? WindowsModules.Actions : [],
    Elevation = elevation,
    EnableFirewallHandler = enableFirewallHandler,
    EnableAdminActions = enableAdminActions,
    // m9 §5: approved scripts (the user turns them on; each one is read and approved in the native dialog). Live source only.
    Scripts = source == "windows"
        ? new OptimizationTool.Engine.Actions.Scripts.ScriptOptions
        {
            Runner = new OptimizationTool.Windows.Actions.Scripts.WindowsScriptRunner(),
            Parser = new OptimizationTool.Windows.Actions.Scripts.PowerShellParser(Path.Combine(dataDir, "script-runs")),
            Policy = new OptimizationTool.Windows.Actions.Scripts.ScriptPolicyReader(),
            // m9 §5.2: the restore-point pre-step goes through the broker only with --enable-admin-actions; otherwise flag_off.
            RestorePoint = enableAdminActions
                ? new OptimizationTool.Windows.Actions.RestorePoint.BrokerRestorePointStep(elevation, new OptimizationTool.Windows.Actions.RestorePoint.WindowsRestorePointSystem())
                : null,
            // m9 §5.4: an elevated script is one broker session, only with both flags; otherwise admin_unavailable.
            ElevatedRunner = enableAdminActions && enableElevatedScripts ? new BrokerScriptRunner(elevation) : null,
            EnableElevatedScripts = enableElevatedScripts,
        }
        : new(),
    // Design v59: settingsPath beside Settings links only when Windows shows its pages in English.
    DisplayLanguage = new OptimizationTool.Windows.WindowsDisplayLanguage(),
    Bench = bench,
    Agent = new AgentOptions
    {
        Providers = [.. providers.All],
        SignIns = [chatGpt],
        Credentials = credentials,
        McpExecutablePath = Path.Combine(AppContext.BaseDirectory, HostServer.AgentExecutable),
        CliRunner = new CliRunner(), // m5: headless Claude Code runs in the background
        FileIdentity = source == "windows" ? WindowsModules.FileIdentity : null, // m9 §6.1: research subjects' signature, read locally
        ElevatedReads = elevation, // m9 §17.2: administrator checks through the same one-shot helper (behind --enable-admin-actions)
    },
});
Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = server.DisposeAsync().AsTask(); };
Console.Out.WriteLine($"ready pipe={pipeName} source={source} data={dataDir}");
Console.Out.Flush();
Log("lifecycle: ready");

server.Completion.GetAwaiter().GetResult();
userLock.Dispose();
mutex.ReleaseMutex();
return 0;

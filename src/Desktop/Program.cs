using OptimizationTool.Desktop.Backend;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop;

internal static class Program
{
    public const string HostExeName = "OptimizationTool.Host.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        // docs/error-logging.md: desktop.log (and dev.log when switched on) under %LOCALAPPDATA%\OptimizationTool\logs.
        var dataDir = DevLogSwitch.DefaultDataDir;
        var devLog = DevLogSwitch.IsOn(args, dataDir);
        var log = AppLog.Open("desktop", dataDir, devLog);
        CrashHandlers.Register(log, dataDir, ErrorSites.DesktopUnhandled);

        DesktopOptions options;
        try
        {
            options = DesktopOptions.Parse(args) with { DevLog = devLog };
        }
        catch (ArgumentException ex)
        {
            log.Error($"startup: {ex.Message}");
            MessageBox.Show(ex.Message, "Optimus", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        log.Info($"lifecycle: startup version {version}, ui-dir {(options.UiDir is null ? "<default>" : "override")}, " +
                 $"pipe {(options.PipeName is null ? "<session>" : "override")}, allow-any-host {options.DevAllowAnyHost}, dev-log {DevLogSwitch.Explain(args, dataDir)}");

        var backendOptions = new BackendOptions
        {
            PipeName = options.PipeName ?? PipeNames.ForCurrentSession(),
            HostPath = Path.Combine(AppContext.BaseDirectory, HostExeName),
            // With a test pipe override the server is external; launching the host would serve a different pipe.
            LaunchHost = options.PipeName is null,
            HostArguments = options.HostArguments(),
            AllowAnyHost = options.DevAllowAnyHost,
            ClientVersion = version,
            DataDir = dataDir,
        };

        ApplicationConfiguration.Initialize();
        // UI-thread exceptions: logged, then the same WinForms dialog as before (Continue keeps going, Quit exits).
        Application.ThreadException += (_, e) => OnThreadException(log, dataDir, e.Exception);
        var backend = new BackendConnection(backendOptions, log);
        try
        {
            using var form = new MainForm(options, log, backend);
            Application.Run(form);
        }
        finally
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            log.Info("lifecycle: shutdown");
        }
        return 0;
    }

    private static void OnThreadException(AppLog log, string dataDir, Exception ex)
    {
        log.Error("crash: exception on the UI thread", ErrorSites.WinFormsThread, ex);
        DialogResult result;
        using (var dialog = new ThreadExceptionDialog(ex)) result = dialog.ShowDialog();
        if (result != DialogResult.Abort) return;
        log.Warn("crash: quit after a UI thread exception", ErrorSites.WinFormsThread);
        CrashMarker.Write(dataDir, "desktop", ex, ErrorSites.WinFormsThread);
        Application.Exit();
    }
}

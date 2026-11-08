using OptimizationTool.Desktop.Backend;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop;

internal static class Program
{
    public const string HostExeName = "OptimizationTool.Host.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var log = new DesktopLog(DesktopLog.DefaultPath);
        DesktopOptions options;
        try
        {
            options = DesktopOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            log.Error($"startup: {ex.Message}");
            MessageBox.Show(ex.Message, "Optimus", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        log.Info($"startup: version {version}, ui-dir {(options.UiDir is null ? "<default>" : "override")}, " +
                 $"pipe {(options.PipeName is null ? "<session>" : "override")}, allow-any-host {options.DevAllowAnyHost}");

        var backendOptions = new BackendOptions
        {
            PipeName = options.PipeName ?? PipeNames.ForCurrentSession(),
            HostPath = Path.Combine(AppContext.BaseDirectory, HostExeName),
            // With a test pipe override the server is external; launching the host would serve a different pipe.
            LaunchHost = options.PipeName is null,
            HostArguments = options.HostArguments(),
            AllowAnyHost = options.DevAllowAnyHost,
            ClientVersion = version,
        };

        ApplicationConfiguration.Initialize();
        var backend = new BackendConnection(backendOptions, log);
        try
        {
            using var form = new MainForm(options, log, backend);
            Application.Run(form);
        }
        finally
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            log.Info("shutdown");
        }
        return 0;
    }
}

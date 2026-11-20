namespace OptimizationTool.Transport.Logging;

/// <summary>
/// Logs what would otherwise vanish: <c>AppDomain.UnhandledException</c> (Error with the full stack, then a crash marker) and
/// <c>TaskScheduler.UnobservedTaskException</c> (Warn, <see cref="ErrorSites.UnobservedTask"/>). Neither changes what the
/// runtime does next.
/// </summary>
public static class CrashHandlers
{
    /// <param name="log">The process's log.</param>
    /// <param name="dataDir">Where the crash marker goes; null writes none.</param>
    /// <param name="unhandledSite">This process's unhandled-exception site (E-UNH-001 host, 002 desktop, 003 mcp).</param>
    public static void Register(AppLog log, string? dataDir, string unhandledSite)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                log.Error("crash: unhandled exception", unhandledSite, ex);
                if (dataDir is not null) CrashMarker.Write(dataDir, log.Process, ex, unhandledSite);
            }
            else
            {
                log.Error("crash: unhandled non-exception object", unhandledSite);
                if (dataDir is not null) CrashMarker.Write(dataDir, log.Process, "Unknown", unhandledSite);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.Warn("crash: unobserved task exception", ErrorSites.UnobservedTask, e.Exception);
    }
}

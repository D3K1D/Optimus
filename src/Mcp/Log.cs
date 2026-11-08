namespace OptimizationTool.Mcp;

/// <summary>
/// Minimal file log at %LOCALAPPDATA%\OptimizationTool\logs\mcp.log. Never stdout (that is the MCP channel).
/// Only events, tool names, outcome codes and durations: never tool arguments, results, pairing codes or secrets.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1 << 20;

    /// <summary>Null disables logging (tests).</summary>
    public static string? FilePath { get; set; } = DefaultPath();

    private static string? DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "OptimizationTool", "logs", "mcp.log");
    }

    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);

    private static void Write(string level, string message)
    {
        var path = FilePath;
        if (path is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {level} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the bridge.
        }
    }
}

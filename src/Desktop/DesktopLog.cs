using System.Globalization;
using System.Text;

namespace OptimizationTool.Desktop;

/// <summary>
/// Append-only text log at %LOCALAPPDATA%\OptimizationTool\logs\desktop.log. Records lifecycle, method names and ids only:
/// never params, results, report contents or secrets.
/// </summary>
public sealed class DesktopLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly string? _path;
    private readonly Lock _lock = new();

    public static DesktopLog Disabled { get; } = new(null);

    public DesktopLog(string? path)
    {
        _path = path;
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimizationTool", "logs", "desktop.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        if (_path is null) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{Environment.ProcessId}] {level} {message}{Environment.NewLine}");
        lock (_lock)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

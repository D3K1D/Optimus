using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OptimizationTool.Transport.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// The one logger per process (docs/error-logging.md §1). Info and up go to <c>&lt;data&gt;\logs\&lt;process&gt;.log</c>; when the
/// developer log is on, every level also goes to <c>&lt;data&gt;\logs\dev.log</c>, shared by all processes. Every line is scrubbed
/// and carries its caller. Each write opens, appends and closes the file, so readers never block a writer. Never throws.
/// </summary>
public sealed class AppLog
{
    public const long ProcessRotateBytes = 2 * 1024 * 1024;
    public const long ProcessCheckEveryBytes = 256 * 1024;
    public const long DevRotateBytes = 16 * 1024 * 1024;
    public const int DevKeep = 3;
    public const string DevMutexName = @"Local\OptimizationTool.DevLog";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static AppLog _current = new("none", null, false, null, LogScrubber.None);

    private readonly string? _path;
    private readonly string? _devPath;
    private readonly TextWriter? _echo;
    private readonly LogScrubber _scrubber;
    private readonly Lock _lock = new();
    private readonly Mutex? _devMutex;
    private long _sinceCheck;

    /// <summary>The process-wide log: a no-op until <see cref="Open"/> with makeCurrent.</summary>
    public static AppLog Current => Volatile.Read(ref _current);

    /// <summary>Writes nothing anywhere.</summary>
    public static AppLog Disabled { get; } = new("none", null, false, null, LogScrubber.None);

    public string Process { get; }

    /// <summary>True when dev.log is being written.</summary>
    public bool DevEnabled => _devPath is not null;

    private AppLog(string process, string? dataDir, bool devLog, TextWriter? echo, LogScrubber scrubber)
    {
        Process = process;
        _echo = echo;
        _scrubber = scrubber;
        if (dataDir is null) return;
        try
        {
            _path = PathFor(dataDir, process);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            RotateIfOver(_path, ProcessRotateBytes, 1);
            if (devLog)
            {
                _devPath = DevPathFor(dataDir);
                try { _devMutex = new Mutex(false, DevMutexName); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _path = null;
            _devPath = null;
        }
    }

    /// <param name="dataDir">The data folder; null writes no files (echo only).</param>
    /// <param name="devLog">Also write dev.log (see <see cref="DevLogSwitch"/>).</param>
    /// <param name="echo">Also write each normal-file line here (the host's stderr). Never stdout in the MCP bridge.</param>
    /// <param name="makeCurrent">Make this <see cref="Current"/>.</param>
    /// <param name="scrubber">Default: <see cref="LogScrubber.ForCurrentUser"/>.</param>
    public static AppLog Open(string process, string? dataDir, bool devLog, TextWriter? echo = null, bool makeCurrent = true,
        LogScrubber? scrubber = null)
    {
        var log = new AppLog(process, dataDir, devLog, echo, scrubber ?? LogScrubber.ForCurrentUser());
        if (makeCurrent) Volatile.Write(ref _current, log);
        return log;
    }

    public static string PathFor(string dataDir, string process) => Path.Combine(dataDir, "logs", process + ".log");
    public static string DevPathFor(string dataDir) => Path.Combine(dataDir, "logs", "dev.log");

    public void Debug(string message, string? site = null, Exception? ex = null, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0, [CallerMemberName] string member = "") => Write(LogLevel.Debug, message, site, ex, file, line, member);

    public void Info(string message, string? site = null, Exception? ex = null, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0, [CallerMemberName] string member = "") => Write(LogLevel.Info, message, site, ex, file, line, member);

    public void Warn(string message, string? site = null, Exception? ex = null, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0, [CallerMemberName] string member = "") => Write(LogLevel.Warn, message, site, ex, file, line, member);

    public void Error(string message, string? site = null, Exception? ex = null, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0, [CallerMemberName] string member = "") => Write(LogLevel.Error, message, site, ex, file, line, member);

    public void Write(LogLevel level, string message, string? site, Exception? ex, string file, int line, string member)
    {
        var toNormal = level >= LogLevel.Info && (_path is not null || _echo is not null);
        if (!toNormal && _devPath is null) return;
        try
        {
            var now = DateTime.UtcNow;
            var head = string.Create(CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-ddTHH:mm:ss.fffZ} {Process} {Environment.ProcessId}:{Environment.CurrentManagedThreadId} {LevelName(level)} "
                + $"{OneLine(_scrubber.Scrub(message))}{(string.IsNullOrEmpty(site) ? "" : " [" + site + "]")}");
            var where = string.Create(CultureInfo.InvariantCulture, $" @{CodeLocation.From(file)}:{line}");
            var exText = ex is null ? "" : ExceptionText(ex);

            if (toNormal)
            {
                var text = head + (level >= LogLevel.Warn ? where : "") + Environment.NewLine + exText;
                lock (_lock)
                {
                    if (_echo is not null)
                    {
                        try { _echo.Write(text); _echo.Flush(); }
                        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                    }
                    if (_path is not null)
                    {
                        // The per-process file is checked at open and every 256 KiB written; dev.log on every write.
                        _sinceCheck += text.Length;
                        var check = _sinceCheck >= ProcessCheckEveryBytes;
                        if (check) _sinceCheck = 0;
                        Append(_path, text, check ? ProcessRotateBytes : -1, 1);
                    }
                }
            }
            if (_devPath is not null)
            {
                var text = head + where + (string.IsNullOrEmpty(member) ? "" : " " + member) + Environment.NewLine + exText;
                WriteDev(text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                      or ObjectDisposedException or InvalidOperationException or FormatException) { }
    }

    /// <summary><c>ex.ToString()</c>, scrubbed, <c>/_/</c> removed, every line indented two spaces.</summary>
    public string ExceptionText(Exception ex)
    {
        string raw;
        try { raw = ex.ToString(); }
        catch (Exception e) when (e is not OutOfMemoryException) { raw = ex.GetType().FullName ?? "Exception"; }
        var scrubbed = _scrubber.Scrub(raw).Replace("/_/", "", StringComparison.Ordinal);
        var sb = new StringBuilder(scrubbed.Length + 64);
        foreach (var l in scrubbed.Split('\n'))
            sb.Append("  ").Append(l.TrimEnd('\r')).Append(Environment.NewLine);
        return sb.ToString();
    }

    private void WriteDev(string text)
    {
        var owned = false;
        try
        {
            if (_devMutex is not null)
            {
                try { owned = _devMutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { owned = true; } // a process died holding it: it is ours now
            }
            Append(_devPath!, text, DevRotateBytes, DevKeep);
        }
        finally
        {
            if (owned)
            {
                try { _devMutex!.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
        }
    }

    // Open, append, close. Rotates first when the file is past the cap: path → .1 → … → .keep (the oldest is dropped).
    private static void Append(string path, string text, long rotateBytes, int keep)
    {
        try
        {
            var bytes = Utf8.GetBytes(text);
            if (rotateBytes >= 0) RotateIfOver(path, rotateBytes, keep);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void RotateIfOver(string path, long rotateBytes, int keep)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= rotateBytes) return;
            for (var i = keep - 1; i >= 1; i--)
            {
                var from = path + "." + i.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(from)) File.Move(from, path + "." + (i + 1).ToString(CultureInfo.InvariantCulture), overwrite: true);
            }
            File.Move(path, path + ".1", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        _ => "ERROR",
    };

    private static string OneLine(string s) =>
        s.Contains('\n') || s.Contains('\r') ? s.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ') : s;
}

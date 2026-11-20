using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizationTool.Transport.Logging;

/// <summary>One crash marker: which process crashed, when (UTC), the exception type, the error site and the app version.</summary>
public sealed record CrashInfo(string Process, DateTimeOffset At, string Type, string? Site, string Version);

/// <summary>
/// <c>&lt;data&gt;\logs\crash-&lt;process&gt;.json</c>, written when a process dies of an unhandled exception (or the shell sees the
/// host end with a crash exit). Holds no message and no stack: those are in the log. Read and cleared by the shell. Never throws.
/// </summary>
public static class CrashMarker
{
    private const string Prefix = "crash-";

    public static string PathFor(string dataDir, string process) => Path.Combine(dataDir, "logs", Prefix + process + ".json");

    public static void Write(string dataDir, string process, Exception ex, string? site) =>
        Write(dataDir, process, ex?.GetType().Name ?? "Exception", site);

    public static void Write(string dataDir, string process, string type, string? site)
    {
        try
        {
            var path = PathFor(dataDir, process);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = new JsonObject
            {
                ["process"] = process,
                ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["type"] = type,
                ["site"] = site,
                ["version"] = AppVersion,
            };
            File.WriteAllText(path, json.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    /// <summary>Every readable marker, oldest first. Unreadable ones are skipped.</summary>
    public static IReadOnlyList<CrashInfo> ReadAll(string dataDir)
    {
        var list = new List<CrashInfo>();
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            if (!Directory.Exists(dir)) return list;
            foreach (var file in Directory.EnumerateFiles(dir, Prefix + "*.json"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject o) continue;
                    var process = o["process"]?.GetValue<string>();
                    var at = o["at"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(process) || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)) continue;
                    list.Add(new CrashInfo(process, when, o["type"]?.GetValue<string>() ?? "", o["site"]?.GetValue<string>(),
                        o["version"]?.GetValue<string>() ?? ""));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    /// <summary>Deletes every marker.</summary>
    public static void ClearAll(string dataDir)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, Prefix + "*.json"))
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    internal static string AppVersion =>
        typeof(CrashMarker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

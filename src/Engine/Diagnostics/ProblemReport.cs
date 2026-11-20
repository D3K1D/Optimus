using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Diagnostics;

/// <summary>A built problem report: the issue title and the full text the user reads, copies or sends.</summary>
public sealed record ProblemReportResult(string Title, string Text);

/// <summary>
/// "Report a problem" (docs/error-logging.md §2): a compact report built from the per-process logs
/// (<c>host.log</c>, <c>desktop.log</c>, <c>mcp.log</c> and their <c>.1</c>) and the crash markers in <c>&lt;data&gt;\logs</c>.
/// It holds the app and Windows versions, the operation kind, the last exception (type, site id, Optimus frames) and the
/// recent WARN/ERROR lines. Every output line is filtered token by token with <see cref="IdentityFilter"/> and
/// <see cref="NeverStore.RejectsName"/> (a rejected token becomes <c>&lt;removed&gt;</c>); known-safe tokens (code
/// locations, site ids, protocol method names, versions, .NET type names) are kept. The log already avoids personal data:
/// this is the second line of defence. Reading never blocks a writer.
/// </summary>
public static partial class ProblemReport
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxLine = 240;
    public const int MaxFrames = 20;
    public const int MaxRecent = 30;
    public const int TailBytes = 256 * 1024;
    public const string Removed = "<removed>";
    public const string RecentHeader = "Recent warnings and errors";
    public const string LastErrorHeader = "Last error";
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    /// <summary>The operation kinds a page may name; anything else is <c>other</c>.</summary>
    public static readonly FrozenSet<string> Operations =
        new[] { "scan", "fix", "action", "script", "bench", "ai_task", "checkup", "research", "crash", "benchmark", "ai" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The per-process logs read, in this order (each with its rotated <c>.1</c> first).</summary>
    public static readonly IReadOnlyList<string> LogProcesses = ["host", "desktop", "mcp"];

    private static readonly FrozenSet<string> Processes = new[] { "host", "desktop", "mcp", "broker", "bench" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ProtocolMethods =
        AuditEngine.DesktopMethods.Concat(AuditEngine.AgentMethods).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The page's operation, or <c>other</c> when missing or not in <see cref="Operations"/>.</summary>
    public static string Operation(string? operation) => operation is not null && Operations.Contains(operation) ? operation : "other";

    /// <summary>
    /// Builds the report from <paramref name="dataDir"/>\logs. Never throws for missing or unreadable logs: the app and
    /// Windows lines are always there.
    /// </summary>
    /// <param name="identities">This PC's user, PC and domain names; defaults to <see cref="IdentityFilter.Local()"/>.</param>
    /// <param name="now">The clock for the 24-hour window; defaults to now.</param>
    public static ProblemReportResult Build(string? dataDir, string appVersion, string windowsVersion, string architecture, string? operation,
        IReadOnlyList<LocalIdentity>? identities = null, DateTimeOffset? now = null)
    {
        var ids = IdentityFilter.Prepare(identities ?? IdentityFilter.Local());
        var clock = now ?? DateTimeOffset.UtcNow;
        var op = Operation(operation);
        var logs = string.IsNullOrEmpty(dataDir) ? null : Path.Combine(dataDir, "logs");
        var entries = logs is null ? [] : ReadEntries(logs);
        var crashes = string.IsNullOrEmpty(dataDir) ? [] : CrashMarker.ReadAll(dataDir);

        var last = LastException(entries);
        var errorSite = last?.Site ?? entries.LastOrDefault(e => e.Level == "ERROR" && e.Site is not null)?.Site;
        var crash = crashes.Count > 0 ? crashes[^1] : null;
        if (errorSite is null && crash?.Site is { } cs && SiteId().IsMatch(cs)) errorSite = cs;
        var errorType = last?.Type ?? (crash is not null && DotNetName().IsMatch(crash.Type) ? Short(crash.Type) : null);
        var location = last?.Frames.FirstOrDefault()?.Location ?? last?.HeaderLocation;

        var head = new List<string>
        {
            $"Optimus {appVersion} · Windows {windowsVersion} · {architecture}",
            $"Operation: {op}",
        };
        var errorParts = new[] { errorSite, errorType, location }.Where(p => p is not null).ToList();
        if (errorParts.Count > 0) head.Add("Error: " + string.Join(" · ", errorParts));
        if (crash is not null && Processes.Contains(crash.Process))
        {
            var parts = new List<string> { $"Crash: {crash.Process} at {crash.At.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}" };
            if (DotNetName().IsMatch(crash.Type)) parts.Add(Short(crash.Type));
            if (crash.Site is { } site && SiteId().IsMatch(site)) parts.Add(site);
            head.Add(string.Join(" · ", parts));
        }

        var exception = new List<string>();
        var frames = new List<string>();
        if (last is not null)
        {
            var message = last.Message is { } m && MessagePasses(m, ids) ? ": " + Clip(Sanitize.Fold(m)) : "";
            exception.Add("  " + last.Type + message);
            foreach (var f in last.Frames) frames.Add("    at " + f.Method + (f.Location is null ? "" : " in " + f.Location));
        }

        var since = clock - RecentWindow;
        var recent = entries.Where(e => e.Level is "WARN" or "ERROR" && e.At >= since)
            .OrderBy(e => e.At).ThenBy(e => e.Order)
            .TakeLast(MaxRecent)
            .Select(e => $"  {e.At.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {e.Process} {e.Level} {Clip(e.Message)}")
            .ToList();

        var title = Clip($"Problem: {op} {errorSite ?? errorType}".TrimEnd(), 80);
        title = Clip(FilterLine(title, ids), 80);

        // Filter every line, then fit the 16 KiB cap: oldest log lines first, then frames from the bottom, then a hard cut.
        head = head.Select(l => Clip(FilterLine(l, ids))).ToList();
        exception = exception.Select(l => Clip(FilterLine(l, ids))).ToList();
        frames = frames.Select(l => Clip(FilterLine(l, ids))).ToList();
        recent = recent.Select(l => Clip(FilterLine(l, ids))).ToList();

        string Compose()
        {
            var sb = new StringBuilder();
            foreach (var l in head) sb.Append(l).Append('\n');
            if (exception.Count > 0)
            {
                sb.Append('\n').Append(LastErrorHeader).Append('\n');
                foreach (var l in exception) sb.Append(l).Append('\n');
                foreach (var l in frames) sb.Append(l).Append('\n');
            }
            sb.Append('\n').Append(RecentHeader).Append('\n');
            if (recent.Count == 0) sb.Append("  none\n");
            foreach (var l in recent) sb.Append(l).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        var text = Compose();
        while (Encoding.UTF8.GetByteCount(text) > MaxBytes && recent.Count > 0) { recent.RemoveAt(0); text = Compose(); }
        while (Encoding.UTF8.GetByteCount(text) > MaxBytes && frames.Count > 0) { frames.RemoveAt(frames.Count - 1); text = Compose(); }
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) text = CutToBytes(text, MaxBytes);
        return new ProblemReportResult(title, text);
    }

    // ------------------------------------------------------------------ filtering

    /// <summary>
    /// One line, token by token: quoted spans go first (the log never quotes safe text), then each space-separated token
    /// is kept when known-safe, else replaced by <see cref="Removed"/> when the identity or never-store check rejects it
    /// (or it holds a path separator). Two neighbouring tokens that name an identity only together are both removed.
    /// Leading indentation is kept; runs of removed tokens collapse into one.
    /// </summary>
    public static string FilterLine(string line, IReadOnlyList<LocalIdentity> identities)
    {
        var indent = line.Length - line.TrimStart(' ').Length;
        var body = Sanitize.Fold(line);
        try { body = Quoted().Replace(body, " " + Removed + " "); }
        catch (RegexMatchTimeoutException) { return new string(' ', indent) + Removed; }
        var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var safe = new bool[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == Removed || IsSafe(tokens[i])) { safe[i] = true; continue; }
            if (Rejects(tokens[i], identities)) { tokens[i] = Removed; safe[i] = true; }
        }
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (safe[i] || safe[i + 1]) continue;
            if (IdentityFilter.Contains(tokens[i] + " " + tokens[i + 1], identities)) { tokens[i] = Removed; tokens[i + 1] = Removed; safe[i] = safe[i + 1] = true; }
        }
        var sb = new StringBuilder(new string(' ', indent));
        var first = true;
        string? prev = null;
        foreach (var t in tokens)
        {
            if (t == Removed && prev == Removed) continue;
            if (!first) sb.Append(' ');
            sb.Append(t);
            first = false;
            prev = t;
        }
        return sb.ToString();
    }

    private static bool Rejects(string token, IReadOnlyList<LocalIdentity> identities) =>
        token.Contains('\\') || token.Contains('/') || IdentityFilter.Contains(token, identities) || NeverStore.RejectsName(token);

    /// <summary>A known-safe token: a code location, a site id, a protocol method, a plain version or a .NET type/method name.</summary>
    internal static bool IsSafe(string token)
    {
        var core = token.Trim('(', ')', ',', ';', '.', ':');
        if (core.Length == 0) return false;
        try
        {
            if (CodeLocationToken().IsMatch(core) && !core.Contains("..", StringComparison.Ordinal)) return true;
            if (SiteToken().IsMatch(core) || ClockTime().IsMatch(core)) return true;
            if (ProtocolMethods.Contains(core) || ShellMethod().IsMatch(core)) return true;
            if (Version().IsMatch(core) && !NeverStore.Ipv4Like(core) && !NeverStore.DottedMacLike(core)) return true;
            if (DotNetName().IsMatch(core)) return true;
        }
        catch (RegexMatchTimeoutException) { return false; }
        return false;
    }

    /// <summary>The exception message is shown only when no token in it is rejected and nothing in it is quoted.</summary>
    private static bool MessagePasses(string message, IReadOnlyList<LocalIdentity> identities)
    {
        var folded = Sanitize.Fold(message);
        if (folded.Length == 0) return false;
        try { if (Quoted().IsMatch(folded)) return false; }
        catch (RegexMatchTimeoutException) { return false; }
        var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
            if (!IsSafe(t) && Rejects(t, identities)) return false;
        return !IdentityFilter.Contains(folded, identities);
    }

    // ------------------------------------------------------------------ parsing

    private sealed record Frame(string Method, string? Location);

    private sealed class Entry
    {
        public required DateTimeOffset At { get; init; }
        public required string Process { get; init; }
        public required string Level { get; init; }
        public required string Message { get; init; }
        public required long Order { get; init; }
        public string? Site { get; init; }
        public List<string> Exception { get; } = [];
    }

    private sealed record LastError(string Type, string? Message, string? Site, string? HeaderLocation, IReadOnlyList<Frame> Frames);

    private static List<Entry> ReadEntries(string logs)
    {
        var entries = new List<Entry>();
        long order = 0;
        foreach (var process in LogProcesses)
        {
            foreach (var name in new[] { process + ".log.1", process + ".log" })
            {
                var text = ReadTail(Path.Combine(logs, name));
                if (text.Length == 0) continue;
                Entry? current = null;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r').TrimStart('﻿');
                    if (line.StartsWith("  ", StringComparison.Ordinal))
                    {
                        if (current is not null && current.Exception.Count < 400) current.Exception.Add(line.Trim());
                        continue;
                    }
                    current = null;
                    Match m;
                    try { m = LogLine().Match(line); }
                    catch (RegexMatchTimeoutException) { continue; }
                    if (!m.Success || !DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) continue;
                    var proc = m.Groups["proc"].Success && Processes.Contains(m.Groups["proc"].Value) ? m.Groups["proc"].Value : process;
                    var message = m.Groups["msg"].Value;
                    if (message.Length > 4 * MaxLine) message = message[..(4 * MaxLine)];
                    var site = SiteInLine().Match(message) is { Success: true } sm ? sm.Groups[1].Value : null;
                    current = new Entry { At = at, Process = proc, Level = m.Groups["lvl"].Value, Message = message, Order = order++, Site = site };
                    entries.Add(current);
                }
            }
        }
        return entries;
    }

    /// <summary>The last <see cref="TailBytes"/> of a log, opened so that writers (append, rotate) are never blocked. "" when unreadable.</summary>
    internal static string ReadTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var start = Math.Max(0, length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (start > 0)
            {
                var nl = text.IndexOf('\n');
                text = nl < 0 ? "" : text[(nl + 1)..];
            }
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    private static LastError? LastException(List<Entry> entries)
    {
        var entry = entries.Where(e => e.Exception.Count > 0).OrderBy(e => e.At).ThenBy(e => e.Order).LastOrDefault();
        if (entry is null) return null;

        var first = entry.Exception[0];
        if (first.StartsWith("--->", StringComparison.Ordinal)) first = first[4..].Trim();
        var colon = first.IndexOf(": ", StringComparison.Ordinal);
        var typeName = colon < 0 ? first.TrimEnd(':') : first[..colon];
        string type;
        try { type = DotNetName().IsMatch(typeName) ? Short(typeName) : "Exception"; }
        catch (RegexMatchTimeoutException) { type = "Exception"; }
        var message = colon < 0 ? null : first[(colon + 2)..];

        var frames = new List<Frame>();
        foreach (var line in entry.Exception)
        {
            if (frames.Count >= MaxFrames) break;
            if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
            Match m;
            try { m = FrameLine().Match(line); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            var method = m.Groups["m"].Value.Trim();
            if (!method.StartsWith("OptimizationTool.", StringComparison.Ordinal)) continue;
            var location = m.Groups["file"].Success ? Location(m.Groups["file"].Value, m.Groups["ln"].Value) : null;
            frames.Add(new Frame(method, location));
        }
        var headerLocation = HeaderLocation().Match(entry.Message) is { Success: true } hm ? hm.Groups[1].Value : null;
        return new LastError(type, message, entry.Site, headerLocation, frames);
    }

    /// <summary>A stack-trace file as <c>src/...:line</c>: cut at the last <c>src</c> folder, forward slashes. Null when there is none.</summary>
    private static string? Location(string file, string line)
    {
        var f = file.Replace('\\', '/');
        if (f.StartsWith("/_/", StringComparison.Ordinal)) f = f[3..];
        if (!f.StartsWith("src/", StringComparison.Ordinal))
        {
            var at = f.LastIndexOf("/src/", StringComparison.Ordinal);
            if (at < 0) return null;
            f = f[(at + 1)..];
        }
        var loc = f + ":" + line;
        return CodeLocationToken().IsMatch(loc) && !loc.Contains("..", StringComparison.Ordinal) ? loc : null;
    }

    private static string Short(string typeName)
    {
        var dot = typeName.LastIndexOf('.');
        return dot < 0 ? typeName : typeName[(dot + 1)..];
    }

    private static string Clip(string s, int max = MaxLine) => s.Length <= max ? s : CutChars(s, max);

    private static string CutChars(string s, int max)
    {
        var n = Math.Min(max, s.Length);
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    private static string CutToBytes(string s, int maxBytes)
    {
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Encoding.UTF8.GetByteCount(s.AsSpan(0, mid)) <= maxBytes) lo = mid; else hi = mid - 1;
        }
        if (lo > 0 && char.IsHighSurrogate(s[lo - 1])) lo--;
        return s[..lo];
    }

    // ------------------------------------------------------------------ patterns

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z) (?:(?<proc>[a-z]+) )?(?:\[?\d+(?::\d+)?\]? )?(?<lvl>DEBUG|INFO|WARN|ERROR) (?<msg>.*)$",
        RegexOptions.CultureInvariant, 200)]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^at (?<m>[^(]+?)(?:\(.*?\))?(?: in (?<file>.+):line (?<ln>\d+))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex FrameLine();

    [GeneratedRegex(@"\[(E-[A-Z]{3}-\d{3})\]", RegexOptions.CultureInvariant, 200)]
    private static partial Regex SiteInLine();

    [GeneratedRegex(@"^E-[A-Z]{3}-\d{3}$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex SiteId();

    [GeneratedRegex(@"(?:^|\s)@(src/[A-Za-z0-9_./-]+\.cs:\d+)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex HeaderLocation();

    [GeneratedRegex(@"^@?src/[A-Za-z0-9_./-]+\.cs:(?:\d+|line)$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex CodeLocationToken();

    [GeneratedRegex(@"^\[?E-[A-Z]{3}-\d{3}\]?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex SiteToken();

    [GeneratedRegex(@"^(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"^shell\.[a-z][A-Za-z]{2,30}$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ShellMethod();

    [GeneratedRegex(@"^v?\d{1,9}(?:\.\d{1,9}){1,3}$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Version();

    /// <summary>A .NET type or method name in our or the framework's namespaces (generic arity, nested and compiler names allowed).</summary>
    [GeneratedRegex(@"^(?:OptimizationTool|System|Microsoft)(?:\.[A-Za-z_<>`0-9+|]+)+$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex DotNetName();

    /// <summary>A quoted span: double quotes, typographic quotes, backticks, or single quotes not inside a word.</summary>
    [GeneratedRegex(@"""[^""]*""|“[^”]*”|‘[^’]*’|`[^`]*`|(?<!\w)'[^'\r\n]*'(?!\w)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Quoted();
}

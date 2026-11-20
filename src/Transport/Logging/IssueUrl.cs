namespace OptimizationTool.Transport.Logging;

/// <summary>
/// The prefilled GitHub new-issue URL for "Report a problem" (docs/error-logging.md §2):
/// <c>https://github.com/D3K1D/Optimus/issues/new?template=bug_report.yml&amp;title=…&amp;report=…</c>, every value
/// percent-encoded. When the whole URL would pass <c>maxLength</c>, the report is trimmed: the oldest lines of the
/// "Recent warnings and errors" section go first, then stack frames from the bottom, then a hard cut; a trimmed report ends
/// with <see cref="TrimNote"/>. Pure: no I/O.
/// </summary>
public static class IssueUrl
{
    public const string Base = "https://github.com/D3K1D/Optimus/issues/new?template=bug_report.yml";
    public const string Host = "github.com";
    public const string PathPrefix = "/D3K1D/Optimus/issues/new";
    public const string TrimNote = "(trimmed: use Copy report for the full text)";
    public const int DefaultMaxLength = 8000;
    private const string RecentHeader = "Recent warnings and errors";

    public static (Uri Url, bool Trimmed) Build(string title, string report, int maxLength = DefaultMaxLength)
    {
        title ??= "";
        report ??= "";
        var full = Compose(title, report);
        if (full.Length <= maxLength) return (new Uri(full), false);

        // The title must leave room for the note; it is cut only when even the note alone would not fit.
        var minimal = Compose(title, TrimNote);
        if (minimal.Length > maxLength)
        {
            if (Compose("", TrimNote).Length > maxLength)
                throw new ArgumentOutOfRangeException(nameof(maxLength), "Too short for an issue URL.");
            title = LongestPrefix(title, t => Compose(t, TrimNote).Length <= maxLength);
        }

        var lines = report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        string Text() => string.Join('\n', lines).TrimEnd('\n') + "\n" + TrimNote;
        bool Fits() => Compose(title, Text()).Length <= maxLength;

        // 1. The oldest log lines (the top of the recent section).
        var header = lines.FindIndex(l => l == RecentHeader);
        if (header >= 0)
            while (!Fits() && header + 1 < lines.Count && lines[header + 1].StartsWith("  ", StringComparison.Ordinal))
                lines.RemoveAt(header + 1);

        // 2. Stack frames, from the bottom.
        while (!Fits())
        {
            var frame = lines.FindLastIndex(l => l.StartsWith("    at ", StringComparison.Ordinal));
            if (frame < 0) break;
            lines.RemoveAt(frame);
        }

        // 3. A hard cut of what is left.
        if (!Fits())
        {
            var body = string.Join('\n', lines);
            var kept = LongestPrefix(body, b => Compose(title, b.TrimEnd('\n') + "\n" + TrimNote).Length <= maxLength);
            lines = [kept];
        }
        return (new Uri(Compose(title, Text())), true);
    }

    private static string Compose(string title, string report) =>
        Base + "&title=" + Uri.EscapeDataString(title) + "&report=" + Uri.EscapeDataString(report);

    /// <summary>The longest prefix of <paramref name="s"/> (never splitting a surrogate pair) that <paramref name="fits"/>.</summary>
    private static string LongestPrefix(string s, Func<string, bool> fits)
    {
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (fits(Cut(s, mid))) lo = mid; else hi = mid - 1;
        }
        return Cut(s, lo);

        static string Cut(string s, int n) => n > 0 && n < s.Length && char.IsHighSurrogate(s[n - 1]) ? s[..(n - 1)] : s[..n];
    }
}

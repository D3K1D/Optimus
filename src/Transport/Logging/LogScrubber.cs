using System.Text;
using System.Text.RegularExpressions;

namespace OptimizationTool.Transport.Logging;

/// <summary>
/// Hides who and where from every log line: the profile folder becomes <c>%USERPROFILE%</c>, the user name <c>&lt;user&gt;</c>,
/// the PC name <c>&lt;pc&gt;</c>, the domain name <c>&lt;domain&gt;</c>, any SID <c>&lt;sid&gt;</c> and any email <c>&lt;email&gt;</c>.
/// Names match whole words only (case-insensitive), so a short user name such as "an" does not rewrite "scan". Never throws.
/// </summary>
public sealed partial class LogScrubber
{
    private const int TimeoutMs = 200;
    private readonly (string Find, string Replace)[] _words;

    /// <summary>A scrubber that still hides SIDs and emails but knows no names (tests, the disabled log).</summary>
    public static LogScrubber None { get; } = new(null, null, null, null);

    public LogScrubber(string? profileDir, string? userName, string? machineName, string? domainName)
    {
        var list = new List<(string, string)>();
        // Longest first: the profile folder contains the user name.
        if (!string.IsNullOrWhiteSpace(profileDir) && profileDir.Length > 3) list.Add((profileDir.TrimEnd('\\', '/'), "%USERPROFILE%"));
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 2) list.Add((userName, "<user>"));
        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 2) list.Add((machineName, "<pc>"));
        // A local account's "domain" is the PC name, already covered above.
        if (!string.IsNullOrWhiteSpace(domainName) && domainName.Length >= 2
            && !string.Equals(domainName, machineName, StringComparison.OrdinalIgnoreCase))
            list.Add((domainName, "<domain>"));
        _words = [.. list];
    }

    /// <summary>This user's profile folder, user name, PC name and domain.</summary>
    public static LogScrubber ForCurrentUser()
    {
        static string? Safe(Func<string?> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException) { return null; }
        }
        return new LogScrubber(
            Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Safe(() => Environment.UserName),
            Safe(() => Environment.MachineName),
            Safe(() => Environment.UserDomainName));
    }

    /// <summary>The text with names, SIDs and emails replaced.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            // Profile folder first (it holds the user name), then patterns, then names.
            if (_words.Length > 0 && _words[0].Replace == "%USERPROFILE%") text = ReplaceWord(text, _words[0].Find, _words[0].Replace);
            text = Sid().Replace(text, "<sid>");
            text = Email().Replace(text, "<email>");
            foreach (var (find, replace) in _words)
                if (replace != "%USERPROFILE%") text = ReplaceWord(text, find, replace);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return "<unscrubbable>";
        }
    }

    // Replaces whole-word occurrences only, so a short user name such as "an" does not rewrite "scan".
    private static string ReplaceWord(string text, string find, string replace)
    {
        StringBuilder? sb = null;
        var start = 0;
        for (var i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.OrdinalIgnoreCase))
        {
            var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var end = i + find.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (!before || !after) continue;
            sb ??= new StringBuilder(text.Length);
            sb.Append(text, start, i - start).Append(replace);
            start = end;
        }
        return sb is null ? text : sb.Append(text, start, text.Length - start).ToString();
    }

    [GeneratedRegex(@"\bS-1-\d+(?:-\d+)+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Sid();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Email();
}

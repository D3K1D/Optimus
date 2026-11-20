using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// URL hygiene for research sources (m9 §6.3), applied when a source is stored and again when it is opened: https only, a
/// public host name (no IP literal, no credentials, no port, IDN in punycode), at most 300 characters, fragment removed.
/// Returns the cleaned URL, or null when it is refused. The shell (src/Desktop SourceOpener) repeats the same rules.
/// </summary>
public static class UrlHygiene
{
    public const int MaxLength = 300;

    /// <summary>Single-label and reserved names that never point at a public page.</summary>
    private static readonly string[] PrivateSuffixes =
        [".local", ".localhost", ".internal", ".lan", ".home", ".corp", ".intranet", ".private", ".home.arpa", ".invalid", ".test", ".example", ".onion", ".arpa"];

    /// <summary>The cleaned URL or null; a refusal logs its reason code at Debug (never the URL).</summary>
    public static string? Clean(string? raw)
    {
        var clean = Clean(raw, out var reason);
        if (clean is null && AppLog.Current.DevEnabled) AppLog.Current.Debug($"research: url refused reason={reason}", ErrorSites.UrlRefused);
        return clean;
    }

    /// <param name="reason">Why the URL was refused (a reason code), null when it passed.</param>
    public static string? Clean(string? raw, out string? reason)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 2048) { reason = "empty_or_too_long"; return null; }
        foreach (var ch in raw)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\' || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) { reason = "bad_character"; return null; }
        if (!raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { reason = "not_https"; return null; }

        // The authority as written: no credentials and no port of any kind (":443" included).
        var rest = raw["https://".Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        if (authority.Length == 0 || authority.Contains('@') || authority.Contains(':') || authority.Contains('[') || authority.Contains('%')) { reason = "credentials_or_port"; return null; }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { reason = "unparsable"; return null; }
        if (u.UserInfo.Length > 0 || !u.IsDefaultPort || u.HostNameType != UriHostNameType.Dns) { reason = "not_a_dns_host"; return null; }

        string host;
        try { host = u.IdnHost.ToLowerInvariant(); }
        catch (UriFormatException) { reason = "bad_idn"; return null; }
        if (!PublicHost(host)) { reason = "private_host"; return null; }

        var cleaned = "https://" + host + u.PathAndQuery;
        reason = cleaned.Length <= MaxLength ? null : "too_long";
        return reason is null ? cleaned : null;
    }

    /// <summary>The punycode host of a cleaned URL (for display as the source's domain).</summary>
    public static string Domain(string cleanedUrl) => new Uri(cleanedUrl).IdnHost.ToLowerInvariant();

    private static bool PublicHost(string host)
    {
        if (host.Length is 0 or > 253 || host.EndsWith('.') || host.StartsWith('.')) return false;
        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')) return false;
            if (!label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        }
        var tld = labels[^1];
        if (tld.Length < 2 || tld.All(char.IsAsciiDigit)) return false; // a numeric last label is an address, not a name
        if (!tld.StartsWith("xn--", StringComparison.Ordinal) && !tld.All(char.IsAsciiLetter)) return false;
        var dotted = "." + host;
        return !PrivateSuffixes.Any(s => dotted.EndsWith(s, StringComparison.Ordinal)) && host != "localhost";
    }
}

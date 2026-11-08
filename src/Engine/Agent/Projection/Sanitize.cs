using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// String hygiene for the projection (contracts/m3-agent.md §3.5). Every string from the PC is hostile data: it may
/// carry instructions, markup, credentials, paths or addresses. <see cref="Name"/> makes an opt-in name safe to show
/// a model; <see cref="LooksSensitive"/> is the last-line check run on every string the projection would share.
/// </summary>
public static class Sanitize
{
    /// <summary>Longest shared name (contract §3.5).</summary>
    public const int MaxName = 64;

    /// <summary>Input is cut to this many chars before any regex runs (bounds work on 10 KB+ strings).</summary>
    public const int MaxScan = 4096;

    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Removes control, format (zero-width, bidi overrides/isolates), separator, surrogate, private-use and
    /// unassigned characters, applies NFKC (folds full-width look-alikes such as '＼' to their ASCII form), turns every
    /// whitespace run into one space and trims. Null/empty in → "".
    /// </summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length > MaxScan) s = s[..MaxScan];
        var stripped = Strip(s);
        try { stripped = stripped.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return ""; }
        return Collapse(Strip(stripped));
    }

    private static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (char.GetUnicodeCategory(ch))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    // A removed tab/newline separates words; a zero-width char just disappears.
                    if (ch is '\t' or '\n' or '\r' or '\v' or '\f' or '\u2028' or '\u2029' or '\u0085') sb.Append(' ');
                    continue;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Characters that never survive into a shared name: path separators and markup/citation brackets.</summary>
    private static bool Neutralized(char c) => c is '\\' or '/' or '<' or '>' or '[' or ']' or '{' or '}' or '`' or '"' or '|' or '*' or '?';

    /// <summary>
    /// A name the user chose to share (share.names): folded, rejected when it looks sensitive (path, address,
    /// credential, identifier…), path separators and markup characters replaced by spaces, capped at
    /// <see cref="MaxName"/>. Returns null when nothing safe is left; the caller then uses an alias.
    /// </summary>
    public static string? Name(string? raw) => Phrase(raw, MaxName);

    /// <summary>
    /// As <see cref="Name"/> with another length cap: any free text from the PC or a model that may leave only when it is
    /// safe (finding titles with share.names on, AI card notes). Null when it looks sensitive or nothing is left.
    /// </summary>
    public static string? Phrase(string? raw, int maxLength)
    {
        var s = Fold(raw);
        if (s.Length == 0 || LooksSensitive(s)) return null;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Neutralized(ch) ? ' ' : ch);
        s = Collapse(sb.ToString());
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd();
        // A cut can leave a dangling high surrogate only if one survived Fold, which it cannot; re-check anyway.
        if (s.Length == 0 || LooksSensitive(s)) return null;
        return s;
    }

    // ------------------------------------------------------------------ detector

    private static readonly Regex[] Patterns =
    [
        new(@"(?<![a-z0-9])[a-z]:[\\/]", Opts, RegexBudget),                                    // drive path C:\ or C:/
        new(@"\\\\[^\\\s]+", Opts, RegexBudget),                                                // UNC \\server, \\?\
        new(@"%[a-z_][a-z0-9_()]*%", Opts, RegexBudget),                                         // %USERPROFILE%
        new(@"(?:^|[\\/\s])~[\\/]", Opts, RegexBudget),                                         // ~/ home
        new(@"(?:^|[\\/])(?:users|home|documents and settings)[\\/]", Opts, RegexBudget),       // \Users\name
        new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", Opts, RegexBudget), // IPv4 (index 5)
        new(@"(?<![0-9a-z:])(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}(?![0-9a-z:])", Opts, RegexBudget),              // IPv6 full
        new(@"(?<![0-9a-z:])(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?(?![0-9a-z:])(?<=[0-9a-f]::|::[0-9a-f]{1,4}|[0-9a-f])", Opts, RegexBudget), // IPv6 compressed
        new(@"(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])", Opts, RegexBudget),  // MAC
        new(@"(?<![0-9a-f])[0-9a-f]{12}(?![0-9a-f])(?=.*\b(?:mac|bssid)\b)", Opts, RegexBudget), // bare MAC next to "mac"
        new(@"\bS-1-\d+(?:-\d+){1,14}\b", Opts, RegexBudget),                                    // SID
        new(@"[a-z0-9._%+-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+", Opts, RegexBudget),                    // email
        new(@"\b[a-z][a-z0-9+.-]{1,15}://", Opts, RegexBudget),                                   // any URL (credentials or not)
        new(@"\bwww\.[a-z0-9-]+\.[a-z]{2,}", Opts, RegexBudget),                                  // bare web address
        new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", Opts, RegexBudget), // GUID (identifier)
        new(@"\b(?:pass(?:word|wd|phrase)?|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|auth(?:orization)?|credentials?|session(?:id)?|cookie|sig|signature|bearer)\s*[=:]", Opts, RegexBudget),
        new(@"\bbearer\s+[a-z0-9._~+/-]{6,}", Opts, RegexBudget),                                 // bearer token
        new(@"\bbasic\s+[a-z0-9+/]{8,}={0,2}", Opts, RegexBudget),                                // basic auth
        new(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, RegexBudget), // AWS key ID
        new(@"\beyJ[a-z0-9_-]{6,}\.[a-z0-9_-]{6,}", Opts, RegexBudget),                           // JWT
        new(@"\b(?:gh[pousr]_[a-z0-9]{20,}|github_pat_[a-z0-9_]{20,}|xox[abprs]-[a-z0-9-]{10,}|sk-[a-z0-9_-]{16,}|AIza[0-9a-z_-]{30,})", Opts, RegexBudget),
        new(@"-----BEGIN [A-Z ]*(?:PRIVATE KEY|CERTIFICATE)", Opts, RegexBudget),
        new(@"\b(?:SSID|serial(?:\s*(?:number|no))?|S/N)\s*[=:#]", Opts, RegexBudget),           // labelled identifiers
    ];

    private const int Ipv4Pattern = 5;

    /// <summary>
    /// True when the string looks like it carries a path, an address (IPv4/IPv6/MAC), a SID, an email, a URL, a
    /// GUID, a credential (key=, token=, password=, bearer, AWS/GitHub/Slack/JWT-style secrets) or a long hex/base64
    /// token. Over-reporting is intended: a hit drops the field. A regex timeout counts as a hit.
    /// </summary>
    public static bool LooksSensitive(string? s) => Detect(Fold(s), allowDottedQuad: false);

    /// <summary>As <see cref="LooksSensitive"/>, but a plain dotted version such as 1.2.3.4 is not an IPv4 address.</summary>
    public static bool LooksSensitiveVersion(string? s) => Detect(Fold(s), allowDottedQuad: true);

    private static bool Detect(string s, bool allowDottedQuad)
    {
        if (s.Length == 0) return false;
        try
        {
            for (var i = 0; i < Patterns.Length; i++)
            {
                if (allowDottedQuad && i == Ipv4Pattern) continue;
                if (Patterns[i].IsMatch(s)) return true;
            }
        }
        catch (RegexMatchTimeoutException) { return true; }
        return HasLongToken(s);
    }

    /// <summary>Hex runs ≥ 24 and base64/base64url runs ≥ 32 that mix letters and digits (keys, hashes, tokens).</summary>
    private static bool HasLongToken(string s)
    {
        int start = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '+' or '/' or '_' or '-' or '=')) continue;
            if (i - start >= 24 && TokenLooksSecret(s.AsSpan(start, i - start))) return true;
            start = i + 1;
        }
        return false;
    }

    private static bool TokenLooksSecret(ReadOnlySpan<char> t)
    {
        var token = t.ToString();
        // Judge dash/underscore parts separately so "Microsoft-Windows-Win32k-…" style names are not tokens.
        foreach (var part in token.Split('-', '_', '/', '+', '='))
            if (part.Length >= 24 && part.All(char.IsAsciiHexDigit) && part.Any(char.IsAsciiDigit)) return true;
        foreach (var part in token.Split('-', '_'))
            if (part.TrimEnd('=').Length >= 20 && Mixed(part.TrimEnd('='))) return true;
        var core = token.TrimEnd('=');
        return core.Length >= 32 && !core.Contains('-') && Mixed(core);
    }

    /// <summary>Digits plus both letter cases: random-looking, not a word or a version.</summary>
    private static bool Mixed(string s)
    {
        bool digit = false, upper = false, lower = false;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c)) digit = true;
            else if (char.IsAsciiLetterUpper(c)) upper = true;
            else if (char.IsAsciiLetterLower(c)) lower = true;
        }
        return digit && upper && lower;
    }
}

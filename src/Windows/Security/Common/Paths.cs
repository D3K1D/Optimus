namespace OptimizationTool.Windows.Security.Common;

/// <summary>
/// Known folder roots, used to (a) expand a fixed whitelist of environment tokens in launch strings — never
/// arbitrary variables — and (b) normalize image paths for evidence (contract §1.6): a path under a known root
/// becomes "%Token%\rest"; anything else keeps only "…\filename" so user or share names never reach evidence.
/// </summary>
internal sealed class PathRoots
{
    private readonly (string Token, string Path)[] _roots;

    public PathRoots(IEnumerable<(string Token, string Path)> roots)
    {
        _roots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => (r.Token, Path: r.Path.TrimEnd('\\')))
            .OrderByDescending(r => r.Path.Length)
            .ToArray();
    }

    public static PathRoots FromEnvironment()
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        return new PathRoots(
        [
            ("SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("ProgramFiles", Env("ProgramW6432") is { Length: > 0 } pf ? pf : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("APPDATA", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("USERPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        ]);
    }

    /// <summary>Tokens accepted in launch strings, mapped to the roots above (plus aliases).</summary>
    private string? Lookup(string token)
    {
        var t = token.ToUpperInvariant() switch
        {
            "WINDIR" or "SYSTEMROOT" => "SystemRoot",
            "PROGRAMFILES" or "PROGRAMW6432" => "ProgramFiles",
            "PROGRAMFILES(X86)" => "ProgramFiles(x86)",
            "PROGRAMDATA" or "ALLUSERSPROFILE" => "ProgramData",
            "LOCALAPPDATA" => "LOCALAPPDATA",
            "APPDATA" => "APPDATA",
            "USERPROFILE" => "USERPROFILE",
            "SYSTEMDRIVE" => "SystemDrive",
            "COMMONPROGRAMFILES" or "COMMONPROGRAMW6432" => "CommonProgramFiles",
            "COMMONPROGRAMFILES(X86)" => "CommonProgramFiles(x86)",
            _ => null,
        };
        if (t == "SystemDrive")
            return _roots.FirstOrDefault(r => r.Token == "SystemRoot").Path is { Length: >= 2 } w ? w[..2] : null;
        if (t == "CommonProgramFiles")
            return _roots.FirstOrDefault(r => r.Token == "ProgramFiles").Path is { Length: > 0 } pf ? pf + @"\Common Files" : null;
        if (t == "CommonProgramFiles(x86)")
            return _roots.FirstOrDefault(r => r.Token == "ProgramFiles(x86)").Path is { Length: > 0 } px ? px + @"\Common Files" : null;
        return t is null ? null : _roots.FirstOrDefault(r => r.Token == t).Path;
    }

    /// <summary>Expands only whitelisted %TOKEN%s; unknown tokens are left as text (the path then won't resolve).</summary>
    public string ExpandKnown(string s)
    {
        if (s.IndexOf('%') < 0) return s;
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var start = s.IndexOf('%', i);
            if (start < 0) { result.Append(s, i, s.Length - i); break; }
            var end = s.IndexOf('%', start + 1);
            if (end < 0) { result.Append(s, i, s.Length - i); break; }
            result.Append(s, i, start - i);
            var token = s.Substring(start + 1, end - start - 1);
            if (Lookup(token) is { } value) { result.Append(value); i = end + 1; }
            else { result.Append('%').Append(token); i = end; }
        }
        return result.ToString();
    }

    /// <summary>"C:\Program Files\App\a.exe" → "%ProgramFiles%\App\a.exe"; unknown roots → "…\a.exe".</summary>
    public string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = StripPrefixes(path.Trim().Trim('"'));
        foreach (var (token, root) in _roots)
        {
            if (p.Length > root.Length && p[root.Length] == '\\' && p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return "%" + token + "%" + p[root.Length..];
        }
        var name = FileName(p);
        return name.Length == 0 ? null : "\u2026\\" + name;
    }

    public static string StripPrefixes(string p)
    {
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        return p;
    }

    public static string FileName(string p)
    {
        var i = p.LastIndexOfAny(['\\', '/']);
        return Text.Clean(i >= 0 ? p[(i + 1)..] : p, 120);
    }

    /// <summary>True when a normalized path sits under the Windows folder.</summary>
    public static bool IsUnderWindows(string? normalized) =>
        normalized is not null && normalized.StartsWith("%SystemRoot%\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Splits a launch string into the image it would start and whether arguments follow — as text only. Nothing is
/// executed, shell-parsed or expanded beyond the <see cref="PathRoots"/> whitelist. Arguments are never kept.
/// </summary>
internal static class LaunchString
{
    private static readonly string[] Executable = [".exe", ".com", ".bat", ".cmd", ".dll", ".scr", ".ps1", ".vbs", ".js", ".lnk"];

    public sealed record Parsed(string? Image, bool HasArguments, string? Payload);

    public static Parsed Parse(string? raw, PathRoots roots, Func<string, bool> fileExists, string? systemDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, false, null);
        var s = roots.ExpandKnown(raw.Trim());
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) && systemDir is not null)
            s = Path.GetDirectoryName(systemDir) + s[@"\SystemRoot".Length..];
        s = PathRoots.StripPrefixes(s);

        string image;
        string rest;
        if (s.StartsWith('"'))
        {
            var close = s.IndexOf('"', 1);
            image = close > 0 ? s[1..close] : s[1..];
            rest = close > 0 ? s[(close + 1)..] : "";
        }
        else
        {
            (image, rest) = SplitUnquoted(s, fileExists);
        }

        image = Resolve(image.Trim(), fileExists, systemDir);
        var hasArgs = rest.Trim().Length > 0;
        string? payload = null;
        if (hasArgs && PathRoots.FileName(image).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            // rundll32 "C:\x\y.dll",Entry → payload is the DLL (an image, not an argument we keep).
            var arg = rest.Trim();
            string dll;
            if (arg.StartsWith('"')) { var c = arg.IndexOf('"', 1); dll = c > 0 ? arg[1..c] : arg[1..]; }
            else { var c = arg.IndexOfAny([',', ' ']); dll = c > 0 ? arg[..c] : arg; }
            payload = Resolve(dll.Trim(), fileExists, systemDir);
        }
        return new(image.Length == 0 ? null : image, hasArgs, payload);
    }

    private static (string Image, string Tail) SplitUnquoted(string s, Func<string, bool> fileExists)
    {
        if (fileExists(s)) return (s, "");
        // Try growing prefixes at each space: "C:\Program Files\App\app.exe -x" (bounded to 16 spaces).
        var spaces = 0;
        for (var i = s.IndexOf(' '); i > 0 && spaces < 16; i = s.IndexOf(' ', i + 1), spaces++)
        {
            var candidate = s[..i];
            if (fileExists(candidate) || (!HasExtension(candidate) && fileExists(candidate + ".exe")))
                return (candidate, s[i..]);
        }
        // Fall back to the first token that looks executable, else the first token.
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        for (var t = 0; t < tokens.Length && t < 16; t++)
        {
            acc = t == 0 ? tokens[0] : acc + " " + tokens[t];
            if (HasExtension(acc)) return (acc, string.Join(' ', tokens.Skip(t + 1)));
        }
        return (tokens.Length > 0 ? tokens[0] : s, tokens.Length > 1 ? string.Join(' ', tokens.Skip(1)) : "");
    }

    private static bool HasExtension(string p) => Executable.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string Resolve(string image, Func<string, bool> fileExists, string? systemDir)
    {
        if (image.Length == 0) return image;
        if (image.IndexOf('\\') < 0 && image.IndexOf('/') < 0 && systemDir is not null)
        {
            var name = HasExtension(image) ? image : image + ".exe";
            var inSystem = Path.Combine(systemDir, name);
            if (fileExists(inSystem)) return inSystem;
            return name;
        }
        if (systemDir is not null && image.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetDirectoryName(systemDir) ?? "", image);
        if (!HasExtension(image) && !fileExists(image) && fileExists(image + ".exe")) return image + ".exe";
        return image;
    }
}

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

    /// <summary>
    /// For actions (never evidence): the image and the raw argument text that follows it, so an action's fingerprint can
    /// identify what a host process (rundll32, a script host, …) really runs with <see cref="HostedPayload"/>.
    /// </summary>
    public static (string? Image, string Tail) Split(string? raw, PathRoots roots, Func<string, bool> fileExists, string? systemDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "");
        var (image, rest) = SplitCore(raw, roots, fileExists, systemDir);
        return (image.Length == 0 ? null : image, rest);
    }

    private static readonly HashSet<string> DllHosts = new(StringComparer.OrdinalIgnoreCase) { "rundll32.exe", "regsvr32.exe" };
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase) { "wscript.exe", "cscript.exe", "mshta.exe" };
    private static readonly HashSet<string> PowerShells = new(StringComparer.OrdinalIgnoreCase) { "powershell.exe", "pwsh.exe" };
    /// <summary>Hosts whose arguments are themselves code or name what runs in a way Optimus doesn't parse: arguments → unidentified.</summary>
    private static readonly HashSet<string> OpaqueHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "conhost.exe", "forfiles.exe", "msbuild.exe", "installutil.exe", "regasm.exe", "regsvcs.exe", "msiexec.exe", "hh.exe",
        "bash.exe", "wsl.exe", "msxsl.exe", "odbcconf.exe", "pcalua.exe", "scriptrunner.exe", "explorer.exe", "control.exe", "certutil.exe", "bitsadmin.exe",
    };
    private static readonly HashSet<string> PsFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "noprofile", "nop", "nologo", "noninteractive", "noni", "noexit", "sta", "mta",
    };
    private static readonly HashSet<string> PsValued = new(StringComparer.OrdinalIgnoreCase)
    {
        "executionpolicy", "ep", "ex", "exec", "windowstyle", "w", "win", "version", "v", "inputformat", "if", "outputformat", "of", "o", "workingdirectory", "wd",
    };

    /// <summary>
    /// What a host process runs (m9 §3.2 fingerprints): the payload file of rundll32/regsvr32 (the DLL), a script host
    /// (the script), PowerShell (-File's script), as a fully qualified path. Identified is false when the program is a
    /// host whose arguments can't be tied to one file (an inline command, a URL, regsvr32 /i, cmd /c …): an action must
    /// then not offer to re-enable it. Any other program: no payload, identified. Text only; nothing is run.
    /// </summary>
    public static (string? Payload, bool Identified) HostedPayload(string? image, string? arguments, string? workingDirectory, Func<string, bool> fileExists,
        string? systemDir, PathRoots? roots = null)
    {
        if (string.IsNullOrWhiteSpace(image)) return (null, true);
        var name = PathRoots.FileName(image.Trim().Trim('"'));
        var args = Tokens(roots is null || arguments is null ? arguments ?? "" : roots.ExpandKnown(arguments));
        if (args.Count == 0) return (null, true);
        if (OpaqueHosts.Contains(name)) return (null, false);
        string? file = null;
        if (DllHosts.Contains(name))
        {
            if (name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
            {
                file = args[0].Value.Split(',')[0]; // a DLL path, then its entry point after the comma
            }
            else
            {
                if (args.Any(a => !a.Quoted && (a.Value.StartsWith("/i", StringComparison.OrdinalIgnoreCase) || a.Value.StartsWith("-i", StringComparison.OrdinalIgnoreCase))))
                    return (null, false); // DllInstall gets a free string (a URL with scrobj): not one file
                file = args.FirstOrDefault(a => a.Quoted || !(a.Value.StartsWith('/') || a.Value.StartsWith('-'))).Value;
            }
        }
        else if (ScriptHosts.Contains(name))
        {
            file = args.FirstOrDefault(a => a.Quoted || !a.Value.StartsWith("//", StringComparison.Ordinal)).Value;
            if (file is not null && name.Equals("mshta.exe", StringComparison.OrdinalIgnoreCase) && file.Contains(':') && !(file.Length > 2 && file[1] == ':'))
                return (null, false); // a URL or an inline javascript:/vbscript: program
        }
        else if (PowerShells.Contains(name))
        {
            for (var i = 0; i < args.Count && file is null; i++)
            {
                var a = args[i];
                if (a.Quoted || !(a.Value.StartsWith('-') || a.Value.StartsWith('/'))) return (null, false); // a command, not a file
                var sw = a.Value.TrimStart('-', '/');
                if (sw.Equals("file", StringComparison.OrdinalIgnoreCase) || sw.Equals("f", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Count) return (null, false);
                    file = args[i + 1].Value;
                }
                else if (PsValued.Contains(sw)) i++;
                else if (!PsFlags.Contains(sw)) return (null, false); // -Command, -EncodedCommand, anything Optimus doesn't know
            }
            if (file is null) return (null, false);
        }
        else return (null, true);

        if (string.IsNullOrWhiteSpace(file)) return (null, false);
        var path = PathRoots.StripPrefixes(file.Trim());
        if (path.IndexOf('\\') < 0 && path.IndexOf('/') < 0)
        {
            // A bare name: DLL hosts load from System32 first; otherwise (and then) the program's working folder.
            if (DllHosts.Contains(name) && systemDir is not null)
            {
                var dll = HasExtension(path) || path.Contains('.') ? path : path + ".dll";
                if (fileExists(Path.Combine(systemDir, dll))) return (Path.Combine(systemDir, dll), true);
            }
        }
        if (!Path.IsPathFullyQualified(path) && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var wd = roots?.ExpandKnown(workingDirectory.Trim().Trim('"')) ?? workingDirectory.Trim().Trim('"');
            if (Path.IsPathFullyQualified(wd)) path = Path.Combine(wd, path);
        }
        return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? (path, true) : (null, false);
    }

    /// <summary>Command-line tokens: whitespace-separated, double quotes group (and are removed).</summary>
    private static List<(string Value, bool Quoted)> Tokens(string s)
    {
        var list = new List<(string, bool)>();
        var i = 0;
        while (i < s.Length && list.Count < 64)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            var sb = new System.Text.StringBuilder();
            var quoted = false;
            var inQuote = false;
            while (i < s.Length && (inQuote || !char.IsWhiteSpace(s[i])))
            {
                if (s[i] == '"') { inQuote = !inQuote; quoted = true; }
                else sb.Append(s[i]);
                i++;
            }
            list.Add((sb.ToString(), quoted));
        }
        return list;
    }

    public static Parsed Parse(string? raw, PathRoots roots, Func<string, bool> fileExists, string? systemDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, false, null);
        var (image, rest) = SplitCore(raw, roots, fileExists, systemDir);
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

    private static (string Image, string Tail) SplitCore(string raw, PathRoots roots, Func<string, bool> fileExists, string? systemDir)
    {
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

        return (Resolve(image.Trim(), fileExists, systemDir), rest);
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

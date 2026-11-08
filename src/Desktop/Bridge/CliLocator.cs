using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>One place a CLI may live. <see cref="Source"/> is path | installer | npm | winget | app.</summary>
public sealed record CliCandidate(string Path, string Source);

/// <summary>
/// Everything <see cref="CliLocator"/> reads from the machine, injectable so tests are hermetic. Null folders disable
/// the known locations under them.
/// </summary>
public sealed record CliEnvironment
{
    /// <summary>PATH built fresh from the registry (machine then user, expanded).</summary>
    public Func<string?> FreshPath { get; init; } = () => null;
    /// <summary>This process's PATH (as inherited at startup).</summary>
    public Func<string?> ProcessPath { get; init; } = () => null;
    public Func<string, bool> FileExists { get; init; } = _ => false;
    /// <summary>Immediate subdirectories (full paths) of a directory; empty when missing or unreadable.</summary>
    public Func<string, IEnumerable<string>> Subdirectories { get; init; } = _ => [];
    /// <summary>Leaf signer O= of a validly Authenticode-signed file, else null.</summary>
    public Func<string, string?> SignedPublisher { get; init; } = _ => null;
    /// <summary>Installed location of a Store package family for the current user, else null.</summary>
    public Func<string, string?> PackageLocation { get; init; } = _ => null;
    public string? UserProfile { get; init; }
    public string? AppData { get; init; }
    public string? LocalAppData { get; init; }
    public string? ProgramFiles { get; init; }

    public static CliEnvironment System { get; } = new()
    {
        FreshPath = SystemPath.FromRegistry,
        ProcessPath = () => Environment.GetEnvironmentVariable("PATH"),
        FileExists = File.Exists,
        Subdirectories = SafeSubdirectories,
        SignedPublisher = Authenticode.ValidPublisher,
        PackageLocation = StorePackages.InstalledLocation,
        UserProfile = Folder(Environment.SpecialFolder.UserProfile),
        AppData = Folder(Environment.SpecialFolder.ApplicationData),
        LocalAppData = Folder(Environment.SpecialFolder.LocalApplicationData),
        ProgramFiles = Folder(Environment.SpecialFolder.ProgramFiles),
    };

    private static string? Folder(Environment.SpecialFolder f) =>
        Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } p && Path.IsPathFullyQualified(p) ? p : null;

    private static IEnumerable<string> SafeSubdirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }
}

/// <summary>
/// Finds the Claude Code / Codex CLI (contracts/onboarding.md §2): first on PATH (fresh registry PATH, then the process
/// PATH; absolute directories only, .exe before .cmd), then at the known install locations per client. Outside PATH a
/// .exe must be validly signed with the client's publisher as leaf O=, and a .cmd is accepted only in %APPDATA%\npm\.
/// For Claude, the desktop app's own claude.exe (WindowsApps, AnthropicClaude) is never a candidate. The caller then
/// runs the identity check (<c>--version</c>) on each candidate in order. No shell is involved.
/// </summary>
public sealed class CliLocator
{
    public const string ClaudeAppFamily = "Claude_pzs8sxrjxfjjc";
    public const string CodexAppFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    public const string ClaudePublisher = "Anthropic, PBC";
    public const string CodexPublisher = "OpenAI OpCo, LLC";
    private const int MaxAppVersions = 4;

    private readonly CliEnvironment _env;

    public CliLocator(CliEnvironment env) => _env = env;

    /// <summary>PATH-only locator (no known locations); kept for simple callers and tests.</summary>
    public CliLocator(Func<string?> pathVariable, Func<string, bool> fileExists)
        : this(new CliEnvironment { FreshPath = pathVariable, FileExists = fileExists }) { }

    public static CliLocator System { get; } = new(CliEnvironment.System);

    /// <summary>First PATH hit for <paramref name="baseName"/> (.exe before .cmd per directory), or null.</summary>
    public string? Find(string baseName) => PathHits(baseName).FirstOrDefault();

    /// <summary>Candidates for a client id (claude_code | codex) in resolution order, deduplicated by full path.</summary>
    public IEnumerable<CliCandidate> Candidates(string clientId)
    {
        var claude = clientId == "claude_code";
        var exe = claude ? "claude" : "codex";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in PathHits(exe))
            if (!(claude && IsClaudeDesktopApp(hit)) && seen.Add(hit))
                yield return new CliCandidate(hit, "path");

        var publisher = claude ? ClaudePublisher : CodexPublisher;
        foreach (var (raw, source) in claude ? ClaudeLocations() : CodexLocations())
        {
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!seen.Add(path) || !_env.FileExists(path)) continue;
            if (claude && IsClaudeDesktopApp(path)) continue;
            if (!AcceptableOutsidePath(path, publisher)) continue;
            yield return new CliCandidate(path, source);
        }
    }

    /// <summary>True when the Claude desktop app is installed (Store package or the older AnthropicClaude install).</summary>
    public bool ClaudeAppInstalled() =>
        _env.PackageLocation(ClaudeAppFamily) is not null
        || (_env.LocalAppData is { } l && _env.FileExists(Path.Combine(l, "AnthropicClaude", "claude.exe")));

    // ---- PATH ----

    private IEnumerable<string> PathHits(string baseName)
    {
        foreach (var dir in SystemPath.Directories(_env.FreshPath(), _env.ProcessPath()))
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(dir, baseName + ext)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (_env.FileExists(candidate)) yield return candidate;
            }
    }

    // ---- known locations ----

    internal bool AcceptableOutsidePath(string path, string publisher)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return string.Equals(_env.SignedPublisher(path), publisher, StringComparison.Ordinal);
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return _env.AppData is { } a && string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(Path.Combine(a, "npm")), StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>The desktop app's window exe is also claude.exe and must never be run as the CLI.</summary>
    private bool IsClaudeDesktopApp(string path) =>
        path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
        || (_env.LocalAppData is { } l && path.StartsWith(Path.Combine(l, "AnthropicClaude") + @"\", StringComparison.OrdinalIgnoreCase));

    private IEnumerable<(string, string)> ClaudeLocations()
    {
        if (_env.UserProfile is { } home) yield return (Path.Combine(home, ".local", "bin", "claude.exe"), "installer");
        if (_env.AppData is { } roaming) yield return (Path.Combine(roaming, "npm", "claude.cmd"), "npm");
        foreach (var w in WinGet("claude.exe", "Anthropic.ClaudeCode_")) yield return (w, "winget");
        if (_env.LocalAppData is { } local)
        {
            // Bundled by the Claude desktop app once its Code tab is opened: claude-code\<version>\<hash>\claude.exe.
            var root = Path.Combine(local, "Claude-Data", "claude_pzs8sxrjxfjjc", "claude-code");
            var versions = _env.Subdirectories(root)
                .Select(d => (dir: d, v: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(x => x.v is not null)
                .OrderByDescending(x => x.v)
                .Take(MaxAppVersions);
            foreach (var (dir, _) in versions)
                foreach (var hash in _env.Subdirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                    yield return (Path.Combine(hash, "claude.exe"), "app");
        }
    }

    private IEnumerable<(string, string)> CodexLocations()
    {
        if (_env.AppData is { } roaming) yield return (Path.Combine(roaming, "npm", "codex.cmd"), "npm");
        if (_env.LocalAppData is { } local) yield return (Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"), "installer");
        foreach (var w in WinGet("codex.exe", null)) yield return (w, "winget");
        if (_env.PackageLocation(CodexAppFamily) is { } app && Path.IsPathFullyQualified(app))
            yield return (Path.Combine(app, "app", "resources", "codex.exe"), "app");
        if (_env.UserProfile is { } home) yield return (Path.Combine(home, ".codex", ".sandbox-bin", "codex.exe"), "app");
    }

    private IEnumerable<string> WinGet(string exe, string? packagePrefix)
    {
        if (_env.LocalAppData is { } local) yield return Path.Combine(local, "Microsoft", "WinGet", "Links", exe);
        if (_env.ProgramFiles is { } pf) yield return Path.Combine(pf, "WinGet", "Links", exe);
        if (packagePrefix is not null && _env.LocalAppData is { } l)
            foreach (var d in _env.Subdirectories(Path.Combine(l, "Microsoft", "WinGet", "Packages"))
                         .Where(d => Path.GetFileName(d).StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(d, exe);
    }
}

/// <summary>Store package lookup for the current user (no admin needed). Any failure means "not found".</summary>
internal static class StorePackages
{
    public static string? InstalledLocation(string familyName)
    {
        try
        {
            var pm = new global::Windows.Management.Deployment.PackageManager();
            string? best = null;
            ulong bestVersion = 0;
            foreach (var p in pm.FindPackagesForUser("", familyName))
            {
                var v = p.Id.Version;
                var packed = ((ulong)v.Major << 48) | ((ulong)v.Minor << 32) | ((ulong)v.Build << 16) | v.Revision;
                string? path;
                try { path = p.InstalledLocation?.Path; }
                catch (Exception) { continue; }
                if (path is null || (best is not null && packed <= bestVersion)) continue;
                best = path;
                bestVersion = packed;
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

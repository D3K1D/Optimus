using Microsoft.Win32;

namespace OptimizationTool.Transport;

/// <summary>
/// One child process invocation, shared by the desktop shell (CLI status, registration) and the host (headless
/// Claude Code / Codex runs, contracts/m5-chat.md §3). Either <see cref="ArgumentList"/> (passed to
/// ProcessStartInfo.ArgumentList), or, only for a .cmd shim, a <see cref="CmdArguments"/> line for cmd.exe built from
/// validated tokens by <see cref="ForCli"/>. User text never belongs in an argument: callers pass it on stdin.
/// <see cref="Argv"/> is always the logical CLI argv (the .cmd path for a resolved npm shim, too).
/// </summary>
public sealed record ProcessCommand(string FileName, IReadOnlyList<string> ArgumentList, string? CmdArguments)
{
    /// <summary>Characters cmd.exe treats specially even inside quotes (or that would end a quoted token).</summary>
    private static readonly char[] CmdUnsafe = ['"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n'];

    /// <summary>The logical argv the user sees: CLI path then arguments. Used by tests and the confirm dialog.</summary>
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>
    /// .exe → run directly with an argument list. A known npm shim (<c>claude.cmd</c> / <c>codex.cmd</c>, see
    /// <see cref="NpmShim"/>) → what it runs, directly with an argument list (no cmd.exe, so any profile path works).
    /// Any other .cmd → <c>%SystemRoot%\System32\cmd.exe /d /s /c ""shim" "arg" …"</c>:
    /// every token is validated to contain no quote or cmd metacharacter, so quoting each token and wrapping the
    /// whole line (the documented /s form) cannot change how it is split. Returns null if any token is unsafe or
    /// empty (an empty token cannot be expressed safely on the cmd.exe line; use the <c>--flag=</c> form instead).
    /// </summary>
    public static ProcessCommand? ForCli(string cliPath, IReadOnlyList<string> args, string systemRoot, NpmShimEnvironment? npm = null)
    {
        if (!Path.IsPathFullyQualified(cliPath)) return null;
        var argv = new List<string> { cliPath };
        argv.AddRange(args);
        if (argv.Any(a => a.Length == 0 || a.Any(char.IsControl))) return null;

        if (cliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new ProcessCommand(cliPath, args.ToArray(), null) { Argv = argv };

        if (!cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return null;
        if (NpmShim.Resolve(cliPath, npm ?? NpmShimEnvironment.System).Target is { } target)
            return new ProcessCommand(target.FileName, [.. target.Prefix, .. args], null) { Argv = argv };
        if (argv.Any(a => a.IndexOfAny(CmdUnsafe) >= 0)) return null;
        var cmd = Path.Combine(systemRoot, "System32", "cmd.exe");
        var line = "/d /s /c \"" + string.Join(' ', argv.Select(a => "\"" + a + "\"")) + "\"";
        return new ProcessCommand(cmd, [], line) { Argv = argv };
    }
}

/// <summary>
/// Why a child process could not be started, as a fixed code (never a path): <c>policy</c> (AppLocker, Software
/// Restriction or App Control blocked it), <c>access_denied</c>, or <c>launch_failed</c> (anything else).
/// </summary>
public static class LaunchFailure
{
    public const string Policy = "policy", AccessDenied = "access_denied", Failed = "launch_failed";

    public static string Classify(Exception ex) => ex is System.ComponentModel.Win32Exception w ? Classify(w.NativeErrorCode) : Failed;

    public static string Classify(int nativeError) => nativeError switch
    {
        1260 => Policy,           // ERROR_ACCESS_DISABLED_BY_POLICY (AppLocker / SRP)
        4551 => Policy,           // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION (App Control for Business / WDAC)
        5 => AccessDenied,        // ERROR_ACCESS_DENIED
        _ => Failed,
    };
}

/// <summary>PATH as Windows would give a newly started process, read fresh from the registry.</summary>
public static class SystemPath
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>HKLM Path then HKCU Path (REG_EXPAND_SZ expanded), or null when neither can be read.</summary>
    public static string? FromRegistry()
    {
        var machine = Read(Registry.LocalMachine, MachineKey);
        var user = Read(Registry.CurrentUser, "Environment");
        if (machine is null && user is null) return null;
        return string.Join(';', new[] { machine, user }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Search order: fresh entries, then process entries; absolute, unquoted, de-duplicated directories only.</summary>
    public static IReadOnlyList<string> Directories(string? fresh, string? process)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ((fresh ?? "") + ";" + (process ?? "")).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = raw.Trim('"');
            // Relative entries (e.g. ".") would resolve against the current directory: skip them.
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            if (seen.Add(dir.TrimEnd('\\'))) result.Add(dir);
        }
        return result;
    }

    /// <summary>PATH for child processes: the fresh registry PATH followed by any process PATH entries it lacks.</summary>
    public static string? ForChildren(string? fresh, string? process)
    {
        if (string.IsNullOrEmpty(fresh)) return process;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var raw in (fresh + ";" + (process ?? "")).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(raw.Trim('"').TrimEnd('\\'))) parts.Add(raw);
        return string.Join(';', parts);
    }

    /// <summary>The PATH a child process gets: fresh registry PATH followed by this process's entries it lacks.</summary>
    public static string? ForChildren() => ForChildren(FromRegistry(), Environment.GetEnvironmentVariable("PATH"));

    private static string? Read(RegistryKey hive, string subKey)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: false);
            return key?.GetValue("Path") as string; // REG_EXPAND_SZ is expanded by default.
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

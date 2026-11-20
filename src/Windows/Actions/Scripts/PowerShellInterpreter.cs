using Microsoft.Win32;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Scripts;

/// <summary>
/// Windows PowerShell 5.1 for approved scripts (contracts/m9-ai-powers.md §5.4): &lt;system directory&gt;\WindowsPowerShell\
/// v1.0\powershell.exe, the directory from the Windows API (<see cref="Environment.SystemDirectory"/> calls
/// GetSystemDirectoryW; never an environment variable or PATH), not a reparse point anywhere on its path, and signed by
/// Microsoft (embedded or catalog) as MpCmdRun is.
/// </summary>
public static class PowerShellInterpreter
{
    /// <summary>The system directory from GetSystemDirectoryW.</summary>
    public static string SystemDirectory => System.Environment.SystemDirectory;

    public static string FolderOf(string systemDirectory) => Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");

    /// <summary>The verified interpreter path, or (null, problem): not_found | untrusted.</summary>
    public static (string? Path, string? Problem) Locate()
    {
        var path = Path.Combine(FolderOf(SystemDirectory), "powershell.exe");
        try
        {
            if (!File.Exists(path)) return (null, "not_found");
            if (HasReparsePoint(path)) return (null, "untrusted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "not_found"); }
        using var checker = new SignatureChecker();
        return checker.Check(path).IsMicrosoft ? (path, null) : (null, "untrusted");
    }

    /// <summary>True when the file or any folder above it is a reparse point (a junction or link could redirect it).</summary>
    public static bool HasReparsePoint(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            var info = new FileInfo(p);
            if (info.Exists || Directory.Exists(p))
                if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (Path.GetPathRoot(p) == p) break;
        }
        return false;
    }

    /// <summary>
    /// The child's environment, built from scratch (§5.4; the parser uses the same): system and profile locations from
    /// Windows APIs, PATH = System32, Wbem and the PowerShell folder, PSModulePath = the two system module folders, TEMP/TMP =
    /// the run folder. No inherited variable reaches the child (no profiler, startup hook or module-path injection).
    /// </summary>
    public static Dictionary<string, string> Environment(string runFolder, string systemDirectory, Func<System.Environment.SpecialFolder, string> folder,
        string userName, string userDomain, string machineName, int processors, bool is64)
    {
        var windows = folder(System.Environment.SpecialFolder.Windows);
        var programFiles = folder(System.Environment.SpecialFolder.ProgramFiles);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows, ["windir"] = windows, ["SystemDrive"] = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "C:",
            ["ComSpec"] = Path.Combine(systemDirectory, "cmd.exe"),
            ["PATH"] = string.Join(';', systemDirectory, Path.Combine(systemDirectory, "Wbem"), FolderOf(systemDirectory)),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL",
            ["PSModulePath"] = string.Join(';', Path.Combine(programFiles, "WindowsPowerShell", "Modules"), Path.Combine(FolderOf(systemDirectory), "Modules")),
            ["TEMP"] = runFolder, ["TMP"] = runFolder,
            ["USERPROFILE"] = folder(System.Environment.SpecialFolder.UserProfile),
            ["APPDATA"] = folder(System.Environment.SpecialFolder.ApplicationData),
            ["LOCALAPPDATA"] = folder(System.Environment.SpecialFolder.LocalApplicationData),
            ["ProgramData"] = folder(System.Environment.SpecialFolder.CommonApplicationData),
            ["ALLUSERSPROFILE"] = folder(System.Environment.SpecialFolder.CommonApplicationData),
            ["PUBLIC"] = Path.GetDirectoryName(folder(System.Environment.SpecialFolder.CommonDesktopDirectory)) ?? "",
            ["ProgramFiles"] = programFiles, ["ProgramW6432"] = programFiles,
            ["ProgramFiles(x86)"] = folder(System.Environment.SpecialFolder.ProgramFilesX86),
            ["CommonProgramFiles"] = folder(System.Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = folder(System.Environment.SpecialFolder.CommonProgramFilesX86),
            ["USERNAME"] = userName, ["USERDOMAIN"] = userDomain, ["COMPUTERNAME"] = machineName,
            ["NUMBER_OF_PROCESSORS"] = processors.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PROCESSOR_ARCHITECTURE"] = is64 ? "AMD64" : "x86", ["OS"] = "Windows_NT",
        };
        var profile = env["USERPROFILE"];
        if (profile.Length > 2 && profile[1] == ':') { env["HOMEDRIVE"] = profile[..2]; env["HOMEPATH"] = profile[2..]; }
        foreach (var key in env.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToList()) env.Remove(key);
        return env;
    }

    /// <summary>The environment for this user and PC.</summary>
    public static Dictionary<string, string> Environment(string runFolder) =>
        Environment(runFolder, SystemDirectory, System.Environment.GetFolderPath, System.Environment.UserName, System.Environment.UserDomainName,
            System.Environment.MachineName, System.Environment.ProcessorCount, System.Environment.Is64BitOperatingSystem);

    /// <summary>
    /// Structured arguments (§5.4): -NoProfile -NonInteractive -InputFormat None [-ExecutionPolicy Bypass] -File &lt;path&gt;. Bypass
    /// only when the local policy would refuse the script (the card said so); never when a Group Policy manages it.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string scriptPath, bool bypassPolicy) =>
    [
        "-NoProfile", "-NonInteractive", "-InputFormat", "None", .. bypassPolicy ? new[] { "-ExecutionPolicy", "Bypass" } : [], "-File", scriptPath,
    ];

    /// <summary>One command line for CreateProcessW from the interpreter path and the arguments (MSVC argv quoting).</summary>
    public static string CommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('"').Append(exe).Append('"'); // the interpreter path, always quoted (it never holds a quote)
        foreach (var a in args) { sb.Append(' '); Quote(sb, a); }
        return sb.ToString();
    }

    private static void Quote(System.Text.StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '\n', '\v']) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}

/// <summary>
/// The PowerShell execution policy as it applies to Optimus's run (§5.4). A Group Policy (HKLM or HKCU
/// SOFTWARE\Policies\Microsoft\Windows\PowerShell, EnableScripts or ExecutionPolicy) → managed: every script is ineligible and
/// the policy is never weakened. Otherwise the local policy (CurrentUser over LocalMachine; none set means Restricted on
/// Windows clients) decides whether the run adds -ExecutionPolicy Bypass for its one process.
/// </summary>
public sealed class ScriptPolicyReader(Func<RegistryHive, string, string, object?>? read = null) : IScriptPolicy
{
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\PowerShell";
    public const string ShellKey = @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell";
    private readonly Func<RegistryHive, string, string, object?> _read = read ?? ReadRegistry;

    public ScriptPolicyState Read()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            if (_read(hive, PolicyKey, "EnableScripts") is not null || _read(hive, PolicyKey, "ExecutionPolicy") is not null)
                return new ScriptPolicyState(Managed: true, NeedsBypass: false);
        var local = _read(RegistryHive.CurrentUser, ShellKey, "ExecutionPolicy") as string;
        if (string.IsNullOrWhiteSpace(local) || local.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
            local = _read(RegistryHive.LocalMachine, ShellKey, "ExecutionPolicy") as string;
        var effective = string.IsNullOrWhiteSpace(local) || local.Equals("Undefined", StringComparison.OrdinalIgnoreCase) ? "Restricted" : local.Trim();
        var allows = effective.Equals("RemoteSigned", StringComparison.OrdinalIgnoreCase) || effective.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase)
                     || effective.Equals("Bypass", StringComparison.OrdinalIgnoreCase);
        return new ScriptPolicyState(Managed: false, NeedsBypass: !allows);
    }

    private static object? ReadRegistry(RegistryHive hive, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(key, writable: false);
            return k?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return "unreadable"; }
    }
}

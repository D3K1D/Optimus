namespace OptimizationTool.Transport.Logging;

/// <summary>
/// The developer log (<c>dev.log</c>) is on when any of: the <c>--dev-log</c> argument, <c>OPTIMUS_DEV_LOG=1</c>, or the file
/// <c>&lt;data&gt;\dev-log.on</c> exists.
/// </summary>
/// <remarks>
/// A shell started by a packaged (MSIX) app, such as the Claude desktop app, has its AppData writes redirected into that
/// package's own copy (<c>%LOCALAPPDATA%\Packages\&lt;package&gt;\LocalCache\Local\OptimizationTool</c>). A marker made there is
/// invisible to an installed Optimus, which logged only "dev-log=False" (0.9.1). The switch stays strict; the start line
/// (<see cref="Explain(IReadOnlyList{string}?, string?)"/>) says why the log is off and names a package holding such a marker.
/// </remarks>
public static class DevLogSwitch
{
    public const string Argument = "--dev-log";
    public const string EnvironmentVariable = "OPTIMUS_DEV_LOG";
    public const string MarkerFileName = "dev-log.on";

    /// <summary>The default data folder: %LOCALAPPDATA%\OptimizationTool.</summary>
    public static string DefaultDataDir => Path.Combine(LocalAppData, "OptimizationTool");

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string MarkerPath(string dataDir) => Path.Combine(dataDir, MarkerFileName);

    public static bool IsOn(IReadOnlyList<string>? args, string? dataDir) =>
        IsOn(args, dataDir, Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Same, with the environment value given (tests).</summary>
    public static bool IsOn(IReadOnlyList<string>? args, string? dataDir, string? environmentValue) =>
        Source(args, dataDir, environmentValue) is not null;

    /// <summary>
    /// For the start line: <c>on (flag|env|marker)</c>, or <c>off (no --dev-log, OPTIMUS_DEV_LOG unset, no dev-log.on in the data
    /// folder)</c>, plus <c>; a dev-log.on is in the redirected AppData of package &lt;name&gt;, which Optimus doesn't read</c>.
    /// </summary>
    public static string Explain(IReadOnlyList<string>? args, string? dataDir) =>
        Explain(args, dataDir, Environment.GetEnvironmentVariable(EnvironmentVariable), LocalAppData);

    /// <summary>Same, with the environment value and the local AppData folder given (tests).</summary>
    public static string Explain(IReadOnlyList<string>? args, string? dataDir, string? environmentValue, string? localAppData)
    {
        if (Source(args, dataDir, environmentValue) is { } source) return $"on ({source})";
        var text = $"off (no {Argument}, {EnvironmentVariable} {(string.IsNullOrEmpty(environmentValue) ? "unset" : "not 1")}, "
                   + $"{(string.IsNullOrEmpty(dataDir) ? "no data folder" : $"no {MarkerFileName} in the data folder")})";
        return RedirectedMarkerPackage(dataDir, localAppData) is { } package
            ? $"{text}; a {MarkerFileName} is in the redirected AppData of package {package}, which Optimus doesn't read"
            : text;
    }

    /// <summary>"flag", "env", "marker", or null when off.</summary>
    private static string? Source(IReadOnlyList<string>? args, string? dataDir, string? environmentValue)
    {
        if (args is not null && args.Contains(Argument, StringComparer.OrdinalIgnoreCase)) return "flag";
        if (environmentValue?.Trim() == "1") return "env";
        if (string.IsNullOrEmpty(dataDir)) return null;
        try { return File.Exists(MarkerPath(dataDir)) ? "marker" : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// The first package folder under <c>&lt;localAppData&gt;\Packages</c> whose redirected copy of this data folder holds a
    /// marker (<c>Packages\&lt;package&gt;\LocalCache\Local\&lt;data folder name&gt;\dev-log.on</c>), or null. Only for the default
    /// data folder (directly under local AppData). Never throws.
    /// </summary>
    public static string? RedirectedMarkerPackage(string? dataDir, string? localAppData)
    {
        if (string.IsNullOrEmpty(dataDir) || string.IsNullOrEmpty(localAppData)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) return null;
            var packages = Path.Combine(root, "Packages");
            if (!Directory.Exists(packages)) return null;
            var name = Path.GetFileName(full);
            foreach (var package in Directory.EnumerateDirectories(packages))
                if (File.Exists(Path.Combine(package, "LocalCache", "Local", name, MarkerFileName))) return Path.GetFileName(package);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException) { return null; }
    }
}

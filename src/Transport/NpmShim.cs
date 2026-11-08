using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizationTool.Transport;

/// <summary>What <see cref="NpmShim"/> reads from the machine, injectable so tests are hermetic.</summary>
public sealed record NpmShimEnvironment
{
    private const int MaxPackageJsonBytes = 256 * 1024;

    public Func<string, bool> FileExists { get; init; } = File.Exists;
    /// <summary>A small text file's contents, or null when missing, unreadable or too large.</summary>
    public Func<string, string?> ReadText { get; init; } = SafeRead;
    /// <summary>The PATH a child process gets (node.exe is looked up on it when the shim folder has none).</summary>
    public Func<string?> ChildPath { get; init; } = SystemPath.ForChildren;

    public static NpmShimEnvironment System { get; } = new();

    /// <summary>Never resolves (every .cmd stays a cmd.exe line).</summary>
    public static NpmShimEnvironment None { get; } = new() { FileExists = _ => false, ReadText = _ => null, ChildPath = () => null };

    private static string? SafeRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxPackageJsonBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// The npm global shims <c>claude.cmd</c> / <c>codex.cmd</c> resolved to what they run, so the CLI can be started
/// directly with an argument list instead of through cmd.exe (whose line cannot carry a profile path with
/// <c>&amp; ! % ^</c>). Only the two known packages, read from <c>&lt;shim dir&gt;\node_modules\&lt;package&gt;\package.json</c>
/// <c>bin</c>: a <c>.exe</c> entry runs as is; a <c>.js</c>/<c>.cjs</c>/<c>.mjs</c> entry runs as <c>node.exe &lt;entry&gt;</c>, with
/// node.exe from the shim folder first, then the child PATH (as the shim itself does). The entry must stay inside the
/// package folder and exist.
/// </summary>
public static class NpmShim
{
    /// <summary>A direct start: <see cref="FileName"/> then <see cref="Prefix"/> before the CLI's own arguments.</summary>
    public sealed record Target(string FileName, IReadOnlyList<string> Prefix);

    /// <summary><see cref="Target"/> when resolved; <see cref="NodeMissing"/> when the package needs node.exe and none was found.</summary>
    public sealed record Resolution(Target? Target, bool NodeMissing);

    private static readonly Resolution NotResolved = new(null, false);

    private static readonly Dictionary<string, (string[] Package, string Bin)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = (["@anthropic-ai", "claude-code"], "claude"),
        ["codex"] = (["@openai", "codex"], "codex"),
    };

    public static Resolution Resolve(string cmdPath, NpmShimEnvironment env)
    {
        try
        {
            if (!Path.IsPathFullyQualified(cmdPath) || !cmdPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return NotResolved;
            if (!Known.TryGetValue(Path.GetFileNameWithoutExtension(cmdPath), out var known)) return NotResolved;
            var shimDir = Path.GetDirectoryName(Path.GetFullPath(cmdPath));
            if (shimDir is null) return NotResolved;
            var packageDir = Path.GetFullPath(Path.Combine([shimDir, "node_modules", .. known.Package]));
            if (BinEntry(env.ReadText(Path.Combine(packageDir, "package.json")), known.Bin) is not { } bin) return NotResolved;
            if (bin.Length == 0 || Path.IsPathRooted(bin) || bin.Any(char.IsControl) || bin.Contains(':')) return NotResolved;
            var entry = Path.GetFullPath(Path.Combine(packageDir, bin));
            if (!entry.StartsWith(packageDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !env.FileExists(entry)) return NotResolved;

            if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new(new Target(entry, []), false);
            if (!(entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
                  || entry.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))) return NotResolved;
            return FindNode(shimDir, env) is { } node ? new(new Target(node, [entry]), false) : new(null, true);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return NotResolved; }
    }

    /// <summary>package.json <c>bin</c>: a string (the package's only command) or the object entry for <paramref name="name"/>.</summary>
    internal static string? BinEntry(string? packageJson, string name)
    {
        if (packageJson is null) return null;
        try
        {
            var bin = JsonNode.Parse(packageJson)?["bin"];
            if (bin is JsonValue v && v.TryGetValue<string>(out var single)) return single;
            return bin is JsonObject o && o[name] is JsonValue e && e.TryGetValue<string>(out var s) ? s : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    private static string? FindNode(string shimDir, NpmShimEnvironment env)
    {
        var local = Path.Combine(shimDir, "node.exe");
        if (env.FileExists(local)) return local;
        foreach (var dir in SystemPath.Directories(env.ChildPath(), null))
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir, "node.exe")); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (env.FileExists(candidate)) return candidate;
        }
        return null;
    }
}

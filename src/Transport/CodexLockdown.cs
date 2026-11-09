using System.Text.RegularExpressions;

namespace OptimizationTool.Transport;

/// <summary>
/// M6.0 (contracts/m6-benchmark.md §0): the Codex features an in-app run turns off, and the shell's probe of what this
/// Codex has (<c>codex features list</c>). The host keeps its own copy of <see cref="Features"/>
/// (OptimizationTool.Engine.Agent.HeadlessCommand.CodexLockdownFeatures; a test keeps them equal) and never passes a name
/// outside it.
/// </summary>
public static partial class CodexLockdown
{
    /// <summary>Every feature §0 disables, in its order.</summary>
    public static readonly IReadOnlyList<string> Features =
    [
        "shell_tool", "unified_exec", "apps", "browser_use", "browser_use_external", "computer_use", "in_app_browser",
        "image_generation", "plugins", "remote_plugin", "tool_suggest", "skill_mcp_dependency_install", "multi_agent",
    ];

    /// <summary>Features that must be listed (or reported removed) for in-app use.</summary>
    public static readonly IReadOnlyList<string> Required = ["apps"];

    /// <summary>The oldest Codex the lock-down was tested on.</summary>
    public static readonly Version CodexMinVersion = new(0, 167, 0);

    /// <summary>
    /// The numeric part of a Codex version ("codex-cli 0.167.0-alpha.3.2" or "0.167.0-alpha.3.2" → 0.167.0); null when
    /// there is none.
    /// </summary>
    public static Version? ParseVersion(string? text)
    {
        if (text is null || VersionPattern().Match(text) is not { Success: true } m) return null;
        return int.TryParse(m.Groups[1].ValueSpan, out var a) && int.TryParse(m.Groups[2].ValueSpan, out var b) && int.TryParse(m.Groups[3].ValueSpan, out var c)
            ? new Version(a, b, c) : null;
    }

    /// <summary>True when the version can be read and is at least <see cref="CodexMinVersion"/>.</summary>
    public static bool VersionSupported(string? text) => ParseVersion(text) is { } v && v >= CodexMinVersion;

    /// <summary>
    /// The §0 features to disable, from <c>codex features list</c> output (lines <c>&lt;name&gt; &lt;stage words…&gt;
    /// &lt;true|false&gt;</c>): each one that is listed and not <c>removed</c>, in §0 order. Null (fail closed) when a
    /// <see cref="Required"/> feature is neither listed nor removed, which also covers empty or garbage output. Lines of
    /// another shape are ignored.
    /// </summary>
    public static IReadOnlyList<string>? ParseFeatures(string? output)
    {
        if (output is null) return null;
        var stages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var m = FeatureLine().Match(raw.TrimEnd('\r'));
            if (m.Success) stages.TryAdd(m.Groups["name"].Value, m.Groups["stage"].Value.Trim());
        }
        if (Required.Any(r => !stages.ContainsKey(r))) return null;
        return [.. Features.Where(f => stages.TryGetValue(f, out var stage) && stage != "removed")];
    }

    [GeneratedRegex(@"(\d{1,6})\.(\d{1,6})\.(\d{1,6})")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\s*(?<name>[a-z0-9_.]{1,80})\s+(?<stage>[a-z][a-z ]{0,40}?)\s+(?:true|false)\s*$")]
    private static partial Regex FeatureLine();
}

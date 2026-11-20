namespace OptimizationTool.Tests;

/// <summary>
/// M9 "Drop Codex": product source under src/ no longer names Codex, except the explicit legacy spots that refuse a
/// Codex bridge and end what an older version left (each listed with how many lines may name it). Test projects under
/// src/ (*.Tests) are not product source.
/// </summary>
public sealed class RetiredNameTests
{
    /// <summary>Path relative to src/ (forward slashes) → the number of lines that may name the retired client.</summary>
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Engine/Agent/AgentService.Retired.cs"] = 4, // the legacy constants and the start-up step
        ["Mcp/HostBridge.cs"] = 2,                    // the bridge still names a Codex client, so the host can refuse it with a message
    };

    private static readonly string[] TextExtensions = [".cs", ".csproj", ".props", ".targets", ".json", ".xml", ".resx", ".manifest", ".config", ".html", ".js", ".css", ".md", ".txt", ".ps1", ".cmd"];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OptimizationTool.slnx"))) return dir.FullName;
        throw new InvalidOperationException("OptimizationTool.slnx not found above the test assembly.");
    }

    [Fact]
    public void ProductSource_NamesTheRetiredClientOnlyInTheLegacySpots()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            var parts = relative.Split('/');
            if (parts.Any(p => p is "bin" or "obj" || p.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))) continue;
            if (!TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            var lines = File.ReadAllLines(file).Select((text, i) => (text, i)).Where(x => x.text.Contains("codex", StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{relative}:{x.i + 1}: {x.text.Trim()}").ToList();
            if (lines.Count > 0) found[relative] = lines;
        }

        var unexpected = found.Where(f => !Allowed.TryGetValue(f.Key, out var max) || f.Value.Count > max).SelectMany(f => f.Value).ToList();
        Assert.True(unexpected.Count == 0, "Codex is named outside the legacy allowlist:\n" + string.Join("\n", unexpected));
        Assert.True(found.ContainsKey("Engine/Agent/AgentService.Retired.cs"), "the legacy spots moved: update the allowlist");
    }
}

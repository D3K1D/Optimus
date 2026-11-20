namespace OptimizationTool.Transport.Logging;

/// <summary>Turns a compiler path into a repo-relative one such as <c>src/Engine/Agent/AgentService.cs</c>.</summary>
public static class CodeLocation
{
    /// <summary>
    /// <c>/_/src/X.cs</c> (PathMap) → <c>src/X.cs</c>; an un-mapped path is cut at the last <c>\src\</c> or <c>/src/</c>,
    /// else only the file name is kept. Separators become '/'.
    /// </summary>
    public static string From(string? compilerPath)
    {
        if (string.IsNullOrEmpty(compilerPath)) return "?";
        var p = compilerPath.Replace('\\', '/');
        if (p.StartsWith("/_/", StringComparison.Ordinal)) return p[3..];
        var cut = p.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0) return p[(cut + 1)..];
        var slash = p.LastIndexOf('/');
        return slash >= 0 ? p[(slash + 1)..] : p;
    }
}

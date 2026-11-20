using System.Text.Json.Nodes;
using OptimizationTool.Transport;

namespace OptimizationTool.Engine;

/// <summary>Windows' display language, as the host reads it (production: WindowsDisplayLanguage; tests: a fake).</summary>
public interface IDisplayLanguage
{
    /// <summary>True when Windows shows its own pages in English (any English variant).</summary>
    bool IsEnglish { get; }
}

/// <summary>
/// Design v59: the settingsPath the host sends beside a settingsUri / manualUri — the page's English path from the one
/// table (<see cref="SettingsPages"/>), only when Windows' display language is English. No language seam, a language that
/// can't be read, another language, or a page without a path: no field at all (never null).
/// </summary>
internal sealed class SettingsPaths(IDisplayLanguage? language)
{
    public static readonly SettingsPaths None = new(null);

    public string? For(string? uri)
    {
        if (language is null || SettingsPages.EnglishPath(uri) is not { } path) return null;
        try { return language.IsEnglish ? path : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Adds settingsPath to <paramref name="o"/> when <paramref name="uri"/> has one; returns <paramref name="o"/>.</summary>
    public JsonObject Add(JsonObject o, string? uri)
    {
        if (For(uri) is { } path) o["settingsPath"] = path;
        return o;
    }
}

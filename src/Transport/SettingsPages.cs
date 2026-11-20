using System.Collections.Frozen;

namespace OptimizationTool.Transport;

/// <summary>
/// The reviewed Windows pages (contracts/m2-collectors.md §5) and each one's path as Windows 11 names it in English
/// (design ReportsMore, v59). One table for the shell and the host: the host sends the path as <c>settingsPath</c> beside a
/// <c>settingsUri</c> / <c>manualUri</c> only when Windows' display language is English; a URI not listed here gets none.
/// The UI's fixture keeps a copy (SETTINGS_PATHS in ui/js/adapter-fixture.js), held equal by a test.
/// </summary>
public static class SettingsPages
{
    /// <summary>Each page in catalog order: its URI (exact, case-sensitive) and its English path.</summary>
    public static IReadOnlyList<(string Uri, string EnglishPath)> All { get; } =
    [
        ("windowsdefender://threat", "Windows Security › Virus & threat protection"),
        ("windowsdefender://network", "Windows Security › Firewall & network protection"),
        ("ms-settings:windowsdefender", "Windows Security"),
        ("ms-settings:startupapps", "Settings › Apps › Startup"),
        ("ms-settings:powersleep", "Settings › System › Power & battery"),
        ("ms-settings:storagesense", "Settings › System › Storage › Storage Sense"),
        ("ms-settings:appsfeatures", "Settings › Apps › Installed apps"),
        ("ms-settings:privacy-feedback", "Settings › Privacy & security › Diagnostics & feedback"),
        ("ms-settings:display-advanced", "Settings › System › Display › Advanced display"),
        ("ms-settings:display-advancedgraphics", "Settings › System › Display › Graphics"),
        ("ms-settings:personalization-start", "Settings › Personalization › Start"),
        ("ms-settings:windowsupdate-history", "Settings › Windows Update › Update history"),
        ("ms-settings:troubleshoot", "Settings › System › Troubleshoot › Other troubleshooters"),
    ];

    private static readonly FrozenDictionary<string, string> Paths = All.ToFrozenDictionary(p => p.Uri, p => p.EnglishPath, StringComparer.Ordinal);

    /// <summary>The page's English path, or null for a URI with no designed path (e.g. bare ms-settings:).</summary>
    public static string? EnglishPath(string? uri) => uri is not null && Paths.TryGetValue(uri, out var path) ? path : null;
}

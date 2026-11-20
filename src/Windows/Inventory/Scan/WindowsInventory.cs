using System.Globalization;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Inventory;

/// <summary>
/// win.inventory.windows (m9 §6.10): Windows itself: edition, display version, build and update revision (UBR, the
/// installed update level), from HKLM\…\Windows NT\CurrentVersion. Only the named values are read; the product ID,
/// keys and registered owner are never read or kept.
/// </summary>
internal sealed class WindowsInventoryCollector(Func<CancellationToken, Bag>? read = null, IReadOnlyList<LocalIdentity>? identities = null)
    : InventoryCollector<Bag>(read ?? WindowsInventoryReader.Read, identities)
{
    public override string Area => "windows";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public override string GapTitle => "Windows details";

    internal override CollectorResult Normalize(Bag raw)
    {
        var ids = Identities;
        var items = new ItemList(10);
        var status = new Dictionary<string, string> { ["version"] = raw.Count == 0 ? "failed" : "ok" };
        int? build = int.TryParse(raw.Str("CurrentBuild") ?? raw.Str("CurrentBuildNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : null;
        var ubr = raw.Long("UBR") is { } u and >= 0 and < 1_000_000 ? u : (long?)null;
        // ProductName still says "Windows 10" on Windows 11; the build number decides.
        var family = build is >= 22000 ? "Windows 11" : build is >= 10240 ? "Windows 10" : InventoryKit.Clean(raw.Str("ProductName"), ids, 60);
        var edition = InventoryKit.Clean(OsCollector.EditionName(raw.Str("EditionID")), ids, 40);
        var displayVersion = InventoryKit.Version(raw.Str("DisplayVersion"), ids);
        var buildText = build is { } bn ? bn.ToString(CultureInfo.InvariantCulture) + (ubr is { } r ? "." + r.ToString(CultureInfo.InvariantCulture) : "") : null;
        if (family is not null)
        {
            var name = edition is null || family.Contains(edition, StringComparison.OrdinalIgnoreCase) ? family : family + " " + edition;
            var version = string.Join(' ', new[] { displayVersion, buildText is null ? null : "build " + buildText }.Where(s => s is not null));
            items.Add(InventoryKit.Item(("kind", "os"), ("name", name), ("publisher", "Microsoft"), ("version", version),
                ("displayVersion", displayVersion), ("build", build), ("ubr", ubr), ("edition", edition),
                ("installationType", InventoryKit.Clean(raw.Str("InstallationType"), ids, 20))));
        }
        var fact = InventoryKit.Fact(Area, "Windows registry", items, status,
            items.Items.FirstOrDefault() is { } os ? $"{os["name"]} {os["version"]}." : "Windows version unknown.");
        return InventoryKit.Result(fact, status, new Dictionary<string, string> { ["version"] = "Windows version details" }, items);
    }
}

internal static class WindowsInventoryReader
{
    private static readonly string[] Values = ["ProductName", "EditionID", "DisplayVersion", "CurrentBuild", "CurrentBuildNumber", "UBR", "InstallationType"];

    public static Bag Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
        var bag = new Bag();
        if (key is null) return bag;
        foreach (var name in Values) if (key.GetValue(name) is { } v) bag[name] = v;
        return bag;
    }
}

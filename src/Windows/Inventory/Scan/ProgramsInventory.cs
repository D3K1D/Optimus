using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Reliability;

namespace OptimizationTool.Windows.Inventory;

/// <summary>
/// win.inventory.programs (m9 §6.10): installed programs from the uninstall keys (HKLM 64- and 32-bit views, HKCU) and
/// Store/MSIX packages, each with name, publisher and version. Same reader as win.apps.installed (its seam): system
/// components and updates (SystemComponent, ParentKeyName, KB entries, update release types) are skipped; framework,
/// resource and Windows-signed packages are not read. Uninstall strings and install folders are never read.
/// </summary>
internal sealed class ProgramsInventoryCollector(Func<CancellationToken, RawApps>? read = null, IReadOnlyList<LocalIdentity>? identities = null)
    : InventoryCollector<RawApps>(read ?? AppsReader.Read, identities)
{
    public const int Cap = 500;
    public override string Area => "programs";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public override string GapTitle => "Program list";

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["hklm64"] = "Programs installed for everyone", ["hklm32"] = "32-bit programs installed for everyone",
        ["hkcu"] = "Programs installed for you", ["packages"] = "Store apps",
    };

    internal override CollectorResult Normalize(RawApps raw)
    {
        var ids = Identities;
        var items = new ItemList(Cap);
        int skippedSystem = 0, skippedUpdates = 0;
        foreach (var e in raw.Entries.OrderBy(e => e.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Scope, StringComparer.Ordinal))
        {
            var name = InventoryKit.Clean(e.DisplayName, ids);
            if (name is null) continue;
            if (e.SystemComponent) { skippedSystem++; continue; }
            if (AppsNormalizer.IsUpdate(e, name)) { skippedUpdates++; continue; }
            items.Add(InventoryKit.Item(("name", name), ("publisher", InventoryKit.Clean(e.Publisher, ids, 80)), ("version", InventoryKit.Version(e.Version, ids)),
                ("source", "installed"), ("scope", e.Scope)));
        }
        foreach (var p in raw.Packages.OrderBy(p => p.DisplayName ?? p.FamilyName, StringComparer.OrdinalIgnoreCase))
        {
            var display = p.DisplayName is { } d && !d.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? d : p.FamilyName.Split('_')[0];
            var name = InventoryKit.Clean(display, ids);
            if (name is null) continue;
            var publisher = p.Publisher is { } pub && !pub.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? InventoryKit.Clean(pub, ids, 80) : null;
            items.Add(InventoryKit.Item(("name", name), ("publisher", publisher), ("version", InventoryKit.Version(p.Version, ids)), ("source", "store")));
        }
        var status = raw.SourceStatus.ToDictionary(kv => kv.Key, kv => kv.Value switch { "ok" => "ok", "denied" => "denied", "row cap" => "ok", _ => "failed" });
        var more = raw.SourceStatus.Any(kv => kv.Value == "row cap") ? new[] { $"Only the first {InstalledAppsCollector.EntryCap} registrations were read." } : [];
        var fact = InventoryKit.Fact(Area, "App registrations and packages", items, status,
            $"{InventoryKit.Plural(items.Total, "program", "programs")} listed.",
            new System.Text.Json.Nodes.JsonObject { ["skippedSystemComponents"] = skippedSystem, ["skippedUpdates"] = skippedUpdates });
        return InventoryKit.Result(fact, status, Labels, items, more);
    }
}

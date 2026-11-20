using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Reliability;

namespace OptimizationTool.Windows.Inventory;

/// <summary>
/// win.inventory.drivers (m9 §6.10): the driver of every present device, not only devices with a problem: device name,
/// class, provider (the publisher), version and date. Same SetupAPI read as win.devices.pnp (its seam); the device
/// instance ID it returns is never stored. Devices with no driver are counted, not listed; identical drivers of the same
/// kind merge with a count.
/// </summary>
internal sealed class DriversInventoryCollector(Func<CancellationToken, RawDevices>? read = null, IReadOnlyList<LocalIdentity>? identities = null)
    : InventoryCollector<RawDevices>(read ?? PnpReader.Read, identities)
{
    public const int Cap = 500;
    public override string Area => "drivers";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public override string GapTitle => "Driver list";

    internal override CollectorResult Normalize(RawDevices raw)
    {
        var ids = Identities;
        var items = new ItemList(Cap);
        var noDriver = 0;
        var unnamed = 0;
        // Third-party drivers first, then Microsoft's own; within each, by class and name (stable across scans).
        var ordered = raw.Devices
            .OrderBy(d => IsMicrosoft(d.Provider) ? 1 : 0)
            .ThenBy(d => d.Class ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var d in ordered)
        {
            if (string.IsNullOrWhiteSpace(d.DriverVersion) && string.IsNullOrWhiteSpace(d.Provider)) { noDriver++; continue; }
            var name = InventoryKit.Clean(d.Name, ids);
            if (name is null) { unnamed++; continue; }
            items.Add(InventoryKit.Item(("name", name), ("class", InventoryKit.Clean(d.Class, ids, 40)), ("publisher", InventoryKit.Clean(d.Provider, ids, 60)),
                ("version", InventoryKit.Version(d.DriverVersion, ids)), ("date", d.DriverDate is null ? null : InventoryKit.Iso(d.DriverDate)),
                ("problem", d.ProblemCode != 0 ? d.ProblemCode : (int?)null)));
        }
        var status = new Dictionary<string, string> { ["devices"] = "ok" };
        var more = new List<string>();
        if (raw.Truncated) more.Add($"Only the first {PnpCollector.DeviceCap} devices were read.");
        var fact = InventoryKit.Fact(Area, "Device Manager", items, status,
            $"{InventoryKit.Plural(items.Total, "driver", "drivers")} listed.", new JsonObject { ["noDriver"] = noDriver, ["unnamed"] = unnamed });
        return InventoryKit.Result(fact, status, new Dictionary<string, string>(), items, more);
    }

    internal static bool IsMicrosoft(string? publisher) =>
        publisher is { } p && (p.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase));
}

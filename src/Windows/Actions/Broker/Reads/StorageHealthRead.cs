using System.Globalization;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Windows.Actions.Broker.Reads;

/// <summary>
/// <c>storage.health</c> (reduced per §17.6: only what needs elevation): each physical disk's reliability counters, up to 16.
///
/// REVIEW NOTE (m9 §17.2 rule)
/// - Data: machine-wide only. Two fixed WQL queries in root\Microsoft\Windows\Storage: MSFT_PhysicalDisk (DeviceId,
///   Model: only to name the disk; never FriendlyName, which the user can edit) and MSFT_StorageReliabilityCounter (DeviceId, Temperature, Wear,
///   ReadErrorsTotal, WriteErrorsTotal, PowerOnHours), joined on DeviceId. At most 64 rows each; a 65th row (the cap's
///   sentinel) makes coverage incomplete.
/// - No write, no process start, no COM object beyond WbemScripting. No file or path is opened. Serial numbers are not
///   even queried. Media, bus type, health status and size are NOT read here: they are readable without administrator
///   rights (the scan has them), so they stay out of this check.
/// - Output: exactly the fields of AdminChecks "storage.health": disk (number), model (≤ 80, cleaned; the catalog's free-text rules null it when it carries anything private), counters (whether
///   Windows has a reliability counter for this disk), temperatureC (1–149, 0 or implausible → null), wearPercent (0–100),
///   readErrors, writeErrors, powerOnHours (null when not reported, which is common: NVMe error counts, USB disks).
/// - "Nothing found" = ok, no items: no physical disk listed. "Could not read": the storage namespace or class is missing
///   → unsupported (storage_unavailable); the counter query denied → failed access_denied; other → failed read_error. A disk
///   with no counter row → counters false, all counter fields null, and coverage complete false.
/// - ELEVATION VERDICT: needed for MSFT_StorageReliabilityCounter only (DiskSensors: standard users get access denied);
///   MSFT_PhysicalDisk is readable without it. Confirm on the laptop.
/// </summary>
public sealed class StorageHealthRead(IReadWmi wmi) : IAdminReadAdapter
{
    public const string CheckId = AdminChecks.StorageHealth;
    public const string HandlerVersion = "1";
    public const string Ns = @"root\Microsoft\Windows\Storage";
    public const int Keep = 16;

    public string Id => CheckId;
    public string Version => HandlerVersion;

    private static readonly string[] DiskProps = ["DeviceId", "Model"];
    public const int RowCap = 64;
    private static readonly string[] CounterProps = ["DeviceId", "Temperature", "Wear", "ReadErrorsTotal", "WriteErrorsTotal", "PowerOnHours"];

    public AdminReadOutput Read(AdminReadInput input, CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> disks, counters;
        try
        {
            disks = wmi.Query(Ns, "SELECT " + string.Join(", ", DiskProps) + " FROM MSFT_PhysicalDisk", DiskProps, RowCap + 1);
            ct.ThrowIfCancellationRequested();
            counters = wmi.Query(Ns, "SELECT " + string.Join(", ", CounterProps) + " FROM MSFT_StorageReliabilityCounter", CounterProps, RowCap + 1);
        }
        catch (ReadWmiException ex) { return AdminReadOutput.From(ex, "storage_unavailable"); }

        var byDisk = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        foreach (var c in counters.Take(RowCap))
            if (Number(c["DeviceId"]) is { } n) byDisk.TryAdd(n, c);

        var listed = disks.Take(RowCap).Select(d => (Row: d, N: Number(d["DeviceId"]))).Where(x => x.N is not null).OrderBy(x => x.N).ToList();
        var complete = listed.Count <= Keep && disks.Count <= RowCap && counters.Count <= RowCap;
        var items = new List<JsonObject>();
        foreach (var (row, n) in listed.Take(Keep))
        {
            byDisk.TryGetValue(n!.Value, out var c);
            if (c is null) complete = false;
            items.Add(new JsonObject
            {
                ["disk"] = n,
                ["model"] = ReadValues.Clean(ReadValues.Str(row["Model"]), 80),
                ["counters"] = c is not null,
                ["temperatureC"] = ReadValues.Long(c?["Temperature"]) is { } t and > 0 and < 150 ? t : null,
                ["wearPercent"] = ReadValues.Long(c?["Wear"]) is { } w and >= 0 and <= 100 ? w : null,
                ["readErrors"] = ReadValues.Long(c?["ReadErrorsTotal"]) is { } r and >= 0 ? r : null,
                ["writeErrors"] = ReadValues.Long(c?["WriteErrorsTotal"]) is { } x and >= 0 ? x : null,
                ["powerOnHours"] = ReadValues.Long(c?["PowerOnHours"]) is { } h and >= 0 and <= 10_000_000 ? h : null,
            });
        }
        return AdminReadOutput.Ok(items, listed.Count, complete);
    }

    /// <summary>A disk number from DeviceId ("0", "1", …); anything else null.</summary>
    private static long? Number(object? v) =>
        long.TryParse(ReadValues.Str(v), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 4096 ? n : null;
}

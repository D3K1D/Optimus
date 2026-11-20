using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// m9 §6.10: the scan's inventory of what is on the PC. One evidence item per area (subject <c>inventory.&lt;area&gt;</c>,
/// collector <c>win.inventory.&lt;area&gt;</c>, sensitivity local) whose value is
/// <c>{area, items:[{name, publisher?, version?, …}], total, omitted, sources}</c>. A list, not a detector: no rule reads it.
/// The check-up (CheckupPolicy) reads the names, publishers and versions; nothing else of it leaves the PC.
/// </summary>
public static class ScanInventory
{
    public static readonly string[] Areas = ["hardware", "drivers", "windows", "programs", "background"];

    public static string Subject(string area) => "inventory." + area;

    public static string CollectorId(string area) => "win.inventory." + area;

    public const string CollectorPrefix = "win.inventory.";

    /// <summary>
    /// m9 review R4b: a hardware item's <c>modelSource</c>, set by the collector: <see cref="FromModel"/> when its name was
    /// read from a dedicated model property (the check-up checks it by the model rule), <see cref="FromName"/> for a fallback
    /// or composed name (full rule; absent counts as <see cref="FromName"/>, so older stored scans keep the full rule). Local
    /// evidence only: removed by <see cref="WithoutLocalFields"/> wherever evidence leaves the engine.
    /// </summary>
    public const string ModelSource = "modelSource", FromModel = "model", FromName = "name";

    /// <summary>Item fields kept for the engine's own use only, never sent to the UI or the AI.</summary>
    private static readonly string[] LocalOnly = [ModelSource];

    /// <summary>
    /// Removes the local-only item fields from an inventory evidence item (<c>inventory.&lt;area&gt;</c>) in place; any
    /// other node is left alone. Returns the node.
    /// </summary>
    public static JsonNode? WithoutLocalFields(JsonNode? evidence)
    {
        static bool Starts(JsonNode? n, string prefix) => n is JsonValue j && j.TryGetValue<string>(out var t) && t.StartsWith(prefix, StringComparison.Ordinal);
        if (evidence is JsonObject e && e["value"] is JsonObject v && v["items"] is JsonArray items
            && (Starts(e["subject"], "inventory.") || Starts(e["collectorId"], CollectorPrefix)))
            foreach (var item in items)
                if (item is JsonObject o)
                    foreach (var field in LocalOnly) o.Remove(field);
        return evidence;
    }
}

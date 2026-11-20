using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

// m9 §17.2: the administrator checks' evidence (collectorId admin.<check id>). Every item already matched its check's exact
// field list in the helper and again at intake (AdminChecks.Conforms); this allowlist re-reads each field with its type and
// closed set, so nothing else can leave. Threat names and disk models are names: an alias unless the task shares names.
// Each check has one summary (its state and coverage) so "nothing found" and "could not read all of it" stay different.

internal sealed class AdminChecksProjector : Projector
{
    private static readonly string[] Codes =
        ["defender_not_primary", "defender_unavailable", "bitlocker_unavailable", "storage_unavailable", "access_denied", "read_error", "other"];

    private static readonly Regex MinuteRx = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$", RegexOptions.CultureInvariant);
    private static readonly Regex DriveRx = new("^[A-Z]:$", RegexOptions.CultureInvariant);

    public override IReadOnlyCollection<string> CollectorIds { get; } =
        ["admin." + AdminChecks.DefenderHistory, "admin." + AdminChecks.StorageHealth, "admin." + AdminChecks.Encryption];

    public override string Source(string collectorId) => collectorId switch
    {
        "admin." + AdminChecks.DefenderHistory => "Microsoft Defender (administrator check)",
        "admin." + AdminChecks.StorageHealth => "Drive health counters (administrator check)",
        _ => "BitLocker (administrator check)",
    };

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        var check = collectorId["admin.".Length..];
        if (subject == check + ".summary") return Summary(subject, v, o);
        return check switch
        {
            AdminChecks.DefenderHistory when subject.StartsWith(check + ".detection.", StringComparison.Ordinal) => Detection(subject, v, o),
            AdminChecks.StorageHealth when subject.StartsWith(check + ".disk.", StringComparison.Ordinal) => Disk(subject, v, o),
            AdminChecks.Encryption when subject.StartsWith(check + ".volume.", StringComparison.Ordinal) => Volume(subject, v, o),
            _ => null,
        };
    }

    private static Mapped? Summary(string subject, JsonObject v, Out o)
    {
        o.Enum("state", v["state"], "ok");
        var complete = o.Bool("complete", v["complete"]);
        var total = o.Int("total", v["total"], 0, 100_000);
        var returned = o.Int("returned", v["returned"], 0, AdminChecks.MaxItems);
        if (v["windowDays"] is not null) o.Int("windowDays", v["windowDays"], 1, Diagnostics.MaxAdminWindowDays);
        if (v["code"] is not null) o.Enum("code", v["code"], Codes);
        if (complete is null || returned is null) return null;
        var display = complete == true
            ? returned == 0 ? "Read in full: nothing found." : $"Read in full: {Say.N(returned.Value)} found."
            : $"Could not read all of it: {Say.N(returned.Value)} of {(total is null ? "more" : Say.N(total.Value))} returned.";
        return new(subject, display, "reported");
    }

    private static Mapped? Detection(string subject, JsonObject v, Out o)
    {
        var name = o.Name("threatName", "threat", Read.Str(v["threatName"])) ?? "an unnamed threat";
        var category = o.Enum("category", v["category"], [.. AdminChecks.ThreatCategories]);
        var severity = o.Enum("severity", v["severity"], [.. AdminChecks.ThreatSeverities]) ?? "unknown";
        var action = o.Enum("action", v["action"], [.. AdminChecks.ThreatActions]) ?? "unknown";
        if (Read.Str(v["detectedAt"]) is { } at && MinuteRx.IsMatch(at)) o.V["detectedAt"] = at; else o.Dropped(v["detectedAt"]);
        var succeeded = v["succeeded"] is null ? null : o.Bool("succeeded", v["succeeded"]);
        o.Enum("foundBy", v["foundBy"], [.. AdminChecks.DetectionSources]);
        o.Int("affectedItems", v["affectedItems"], 0, 100_000);
        if (category is null) return null;
        var outcome = succeeded switch { true => "", false => " (it did not succeed)", _ => "" };
        return new(subject, $"Microsoft Defender found {name} ({severity}); action: {action.Replace('_', ' ')}{outcome}.", "reported");
    }

    private static Mapped? Disk(string subject, JsonObject v, Out o)
    {
        var disk = o.Int("disk", v["disk"], 0, 4096);
        var model = v["model"] is null ? null : o.Name("model", "device", Read.Str(v["model"]));
        var counters = o.Bool("counters", v["counters"]);
        var temperature = v["temperatureC"] is null ? null : o.Int("temperatureC", v["temperatureC"], 1, 149);
        var wear = v["wearPercent"] is null ? null : o.Int("wearPercent", v["wearPercent"], 0, 100);
        var reads = v["readErrors"] is null ? null : o.Int("readErrors", v["readErrors"], 0, long.MaxValue);
        var writes = v["writeErrors"] is null ? null : o.Int("writeErrors", v["writeErrors"], 0, long.MaxValue);
        if (v["powerOnHours"] is not null) o.Int("powerOnHours", v["powerOnHours"], 0, 10_000_000);
        if (disk is null || counters is null) return null;
        var label = model ?? $"Disk {Say.N(disk.Value)}";
        if (counters != true) return new(subject, $"{label} reports no health counters.", "reported");
        var parts = new List<string>();
        if (temperature is { } t) parts.Add($"{Say.N(t)} °C");
        if (wear is { } w) parts.Add($"{Say.N(w)}% worn");
        if (reads is { } r) parts.Add(Say.Plural(r, "read error", "read errors"));
        if (writes is { } x) parts.Add(Say.Plural(x, "write error", "write errors"));
        return new(subject, parts.Count == 0 ? $"{label} reports health counters without values." : $"{label}: {string.Join(", ", parts)}.", "reported");
    }

    private static Mapped? Volume(string subject, JsonObject v, Out o)
    {
        var drive = Read.Str(v["drive"]);
        if (drive is null || !DriveRx.IsMatch(drive)) return null;
        o.Const("drive", drive);
        var protection = o.Enum("protection", v["protection"], [.. AdminChecks.ProtectionStates]) ?? "unknown";
        o.Enum("conversion", v["conversion"], [.. AdminChecks.ConversionStates]);
        if (v["percent"] is not null) o.Int("percent", v["percent"], 0, 100);
        o.Enum("method", v["method"], [.. AdminChecks.EncryptionMethods]);
        if (v["protectorTypes"] is JsonArray types)
        {
            var kept = new JsonArray();
            foreach (var t in types)
                if (Read.Str(t) is { } s && AdminChecks.ProtectorTypes.Contains(s)) kept.Add(s); else o.Dropped(t ?? JsonValue.Create(0));
            o.V["protectorTypes"] = kept;
        }
        return new(subject, $"Drive {drive}: BitLocker protection is {protection}.", "reported");
    }
}

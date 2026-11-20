using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

// Administrator checks (contracts/m9-ai-powers.md §17.2, §17.6): the pure catalog compiled into BOTH the host and the
// elevated helper (OptimizationTool.Broker references this assembly). The helper's adapters (src/Windows/Actions/Broker/Reads)
// carry their own version constants; a test asserts they list the same ids and versions as this catalog. The helper shapes
// every item through Shape() before anything leaves the elevated process; the host checks the same field list with
// Conforms() at intake. Labels and "what it reads" lines are the design's words (Design System v79, "Windows dialogs",
// "Administrator checks"); the helper shows them from this compiled table, never from the host.

/// <summary>The type of one output field. Minute = UTC ISO time to the minute, "yyyy-MM-ddTHH:mmZ".</summary>
public enum AdminFieldType { Text, Integer, Boolean, Minute, TextList }

/// <summary>
/// How a Text field that is neither a closed set nor a pattern is checked (free text read from the PC).
/// ThreatName: Microsoft Defender's naming SYNTAX (<see cref="AdminChecks.ThreatNameSyntax"/>), then NeverStore.RejectsName
/// and the identity filter. Model: a hardware model from a dedicated property (allowlist <c>A–Z a–z 0–9 space . _ - + ( ) ,</c>,
/// then NeverStore.RejectsModel, a product-key shape (NeverStore.KeyLike) and the identity filter; serial-shaped runs are
/// kept, as real models look like that).
/// </summary>
public enum AdminFreeText { None, ThreatName, Model }

/// <summary>
/// One output field: name, type, whether null is allowed, and its cap. Text: <see cref="MaxLength"/> and, when given, the
/// closed <see cref="Values"/> or the exact <see cref="Pattern"/>; otherwise it is free text checked by <see cref="Free"/>.
/// Integer: <see cref="Min"/>..<see cref="Max"/>. TextList: at most <see cref="MaxCount"/> distinct elements, each one of
/// <see cref="Values"/>.
/// </summary>
public sealed record AdminField(string Name, AdminFieldType Type, bool Nullable, int MaxLength = 0, IReadOnlyList<string>? Values = null,
    long Min = 0, long Max = long.MaxValue, int MaxCount = 0, string? Pattern = null, AdminFreeText Free = AdminFreeText.None);

/// <summary>A check's time window in days: allowed range and the value used when the request leaves it null.</summary>
public sealed record AdminCheckWindow(int MinDays, int MaxDays, int DefaultDays);

/// <summary>One administrator check: id, handler version, the helper's own label and "what it reads" line, window rule, item cap, exact fields.</summary>
public sealed record AdminCheckDef(string Id, string Version, string Label, string Reads, AdminCheckWindow? Window, int MaxItems, IReadOnlyList<AdminField> Fields);

public static partial class AdminChecks
{
    /// <summary>Per result: at most 200 items and 64 KB (the helper cuts whole items and says so in coverage).</summary>
    public const int MaxItems = 200, MaxResultBytes = 64 * 1024;
    /// <summary>One request reads 1–4 checks (§17.2).</summary>
    public const int MaxChecksPerRequest = 4;
    /// <summary>The helper's watchdog per check (§17.2).</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    public const string DefenderHistory = "security.defender_history", Encryption = "security.encryption", StorageHealth = "storage.health";

    // ---- closed value sets (the adapters map Windows' numeric codes onto these; anything unknown is "unknown")

    public static readonly IReadOnlyList<string> ThreatCategories =
    [
        "adware", "spyware", "password_stealer", "trojan_downloader", "worm", "backdoor", "remote_access_trojan", "trojan", "email_flooder",
        "keylogger", "dialer", "monitoring_software", "browser_modifier", "cookie", "browser_plugin", "exploit", "nuker", "security_disabler",
        "joke_program", "hostile_activex", "software_bundler", "stealth_modifier", "settings_modifier", "toolbar", "remote_control_software",
        "trojan_ftp", "potentially_unwanted", "trojan_telnet", "file_sharing_program", "malware_creation_tool", "tool", "trojan_denial_of_service",
        "trojan_dropper", "trojan_mass_mailer", "trojan_monitoring_software", "trojan_proxy_server", "virus", "known", "spp", "behavior",
        "vulnerability", "policy", "enterprise_unwanted", "ransomware", "asr_rule", "unknown",
    ];
    public static readonly IReadOnlyList<string> ThreatSeverities = ["low", "moderate", "high", "severe", "unknown"];
    public static readonly IReadOnlyList<string> ThreatActions = ["clean", "quarantine", "remove", "allow", "user_defined", "no_action", "block", "unknown"];
    public static readonly IReadOnlyList<string> DetectionSources =
        ["user", "system", "real_time", "downloads_and_attachments", "network_inspection", "early_launch", "local_attestation", "remote_attestation", "unknown"];

    public static readonly IReadOnlyList<string> ProtectionStates = ["on", "off", "unknown"];
    public static readonly IReadOnlyList<string> ConversionStates =
        ["fully_decrypted", "fully_encrypted", "encrypting", "decrypting", "encryption_paused", "decryption_paused", "unknown"];
    public static readonly IReadOnlyList<string> EncryptionMethods =
        ["none", "aes_128_diffuser", "aes_256_diffuser", "aes_128", "aes_256", "hardware", "xts_aes_128", "xts_aes_256", "unknown"];
    public static readonly IReadOnlyList<string> ProtectorTypes =
        ["tpm", "external_key", "recovery_password", "tpm_pin", "tpm_startup_key", "tpm_pin_startup_key", "public_key", "passphrase", "tpm_certificate", "account", "unknown"];

    public static readonly IReadOnlyList<AdminCheckDef> All =
    [
        new(DefenderHistory, "1", "Defender detection history", "Threats Microsoft Defender found and what it did about them.",
            new AdminCheckWindow(1, 90, 30), 50,
        [
            new("threatName", AdminFieldType.Text, true, MaxLength: 120, Free: AdminFreeText.ThreatName),
            new("category", AdminFieldType.Text, false, MaxLength: 40, Values: ThreatCategories),
            new("severity", AdminFieldType.Text, false, MaxLength: 16, Values: ThreatSeverities),
            new("detectedAt", AdminFieldType.Minute, true),
            new("action", AdminFieldType.Text, false, MaxLength: 16, Values: ThreatActions),
            new("succeeded", AdminFieldType.Boolean, true),
            new("foundBy", AdminFieldType.Text, false, MaxLength: 32, Values: DetectionSources),
            new("affectedItems", AdminFieldType.Integer, false, Min: 0, Max: 100_000),
        ]),
        new(Encryption, "1", "Drive encryption status", "Whether BitLocker protects each drive.", null, 26,
        [
            new("drive", AdminFieldType.Text, false, MaxLength: 2, Pattern: "^[A-Z]:$"),
            new("protection", AdminFieldType.Text, false, MaxLength: 8, Values: ProtectionStates),
            new("conversion", AdminFieldType.Text, false, MaxLength: 24, Values: ConversionStates),
            new("percent", AdminFieldType.Integer, true, Min: 0, Max: 100),
            new("method", AdminFieldType.Text, false, MaxLength: 24, Values: EncryptionMethods),
            new("protectorTypes", AdminFieldType.TextList, false, Values: ProtectorTypes, MaxCount: 11),
        ]),
        new(StorageHealth, "1", "Disk health", "Your drives’ own health reports and warnings.", null, 16,
        [
            new("disk", AdminFieldType.Integer, false, Min: 0, Max: 4096),
            new("model", AdminFieldType.Text, true, MaxLength: 80, Free: AdminFreeText.Model),
            new("counters", AdminFieldType.Boolean, false),
            new("temperatureC", AdminFieldType.Integer, true, Min: 1, Max: 149),
            new("wearPercent", AdminFieldType.Integer, true, Min: 0, Max: 100),
            new("readErrors", AdminFieldType.Integer, true, Min: 0, Max: long.MaxValue),
            new("writeErrors", AdminFieldType.Integer, true, Min: 0, Max: long.MaxValue),
            new("powerOnHours", AdminFieldType.Integer, true, Min: 0, Max: 10_000_000),
        ]),
    ];

    public static AdminCheckDef? Find(string? id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>The ids and versions, for the host ↔ helper parity test.</summary>
    public static IReadOnlyDictionary<string, string> Versions { get; } = All.ToDictionary(d => d.Id, d => d.Version, StringComparer.Ordinal);

    /// <summary>
    /// The window a request asked for, checked against the rule: a check without a window takes only null; one with a
    /// window takes null (→ its default) or whole days within its range. Ok false → the request is refused.
    /// </summary>
    public static (bool Ok, int? Days) ResolveWindow(AdminCheckDef def, int? days) => def.Window switch
    {
        null => (days is null, null),
        { } w when days is null => (true, w.DefaultDays),
        { } w => (days >= w.MinDays && days <= w.MaxDays, days),
    };

    /// <summary>
    /// The helper's own "what it reads" line for its window (design v79): for Defender history it carries the resolved
    /// window ("… found in the last 30 days …", "… in the last day …"); the other checks' lines are fixed.
    /// <paramref name="days"/> is the window as <see cref="ResolveWindow"/> returned it.
    /// </summary>
    public static string WindowLine(AdminCheckDef def, int? days) => def.Id == DefenderHistory && days is { } d
        ? $"Threats Microsoft Defender found in the last {Span(d)} and what it did about them."
        : def.Reads;

    /// <summary>The app's label for a check (design v79): "Defender detection history · last 30 days" / "· last day"; others plain.</summary>
    public static string HostLabel(AdminCheckDef def, int? days) => def.Window is not null && days is { } d ? $"{def.Label} · last {Span(d)}" : def.Label;

    /// <summary>By id; null for an id not in the catalog. <paramref name="resolvedWindow"/> as ResolveWindow returned it.</summary>
    public static string? HostLabel(string id, int? resolvedWindow) => Find(id) is { } d ? HostLabel(d, resolvedWindow) : null;

    /// <summary>By id; null for an id not in the catalog.</summary>
    public static string? WindowLine(string id, int? resolvedWindow) => Find(id) is { } d ? WindowLine(d, resolvedWindow) : null;

    private static string Span(int days) => days == 1 ? "day" : days.ToString(CultureInfo.InvariantCulture) + " days";

    /// <summary>
    /// The item rebuilt from the check's field list only, in its order: a field not on the list is DROPPED (so nothing
    /// unnamed can leave the helper); a listed field that is missing reads as null. Null when any listed field has the
    /// wrong type, breaks its cap, closed set or pattern, or is null where null isn't allowed (the item is then rejected
    /// whole). A FREE-TEXT field (<see cref="AdminFreeText"/>) holding a string that does not fit (its length or plain-text
    /// rule, its syntax or allowlist, the never-store check, the identity filter) becomes null when the field is nullable
    /// (and <paramref name="redacted"/> is set), otherwise the item is rejected. The helper and the host run this same code.
    /// </summary>
    public static JsonObject? Shape(AdminCheckDef def, JsonNode? item, out bool redacted, IReadOnlyList<LocalIdentity>? identities = null)
    {
        redacted = false;
        if (item is not JsonObject o) return null;
        var shaped = new JsonObject();
        foreach (var f in def.Fields)
        {
            var v = o[f.Name];
            if (v is null)
            {
                if (!f.Nullable) return null;
                shaped[f.Name] = null;
                continue;
            }
            var free = f.Free != AdminFreeText.None && v is JsonValue fv && fv.TryGetValue<string>(out _);
            var ok = Value(f, v);
            if (ok is null && !free) return null;
            if (free && (ok is null || !FreeTextOk(f.Free, ok.GetValue<string>(), identities ?? Identities.Value)))
            {
                if (!f.Nullable) return null;
                redacted = true;
                shaped[f.Name] = null;
                continue;
            }
            shaped[f.Name] = ok;
        }
        return shaped;
    }

    public static JsonObject? Shape(AdminCheckDef def, JsonNode? item) => Shape(def, item, out _);

    /// <summary>Strict (host intake): exactly the listed fields, each valid (free text included), nothing else, nothing redacted.</summary>
    public static bool Conforms(AdminCheckDef def, JsonNode? item, IReadOnlyList<LocalIdentity>? identities = null) =>
        item is JsonObject o && o.Count == def.Fields.Count && o.All(kv => def.Fields.Any(f => f.Name == kv.Key))
        && Shape(def, o, out var redacted, identities) is { } s && !redacted && JsonNode.DeepEquals(s, o);

    /// <summary>This PC's user, PC and domain names (the helper runs as the same account: a different administrator is refused).</summary>
    private static readonly Lazy<IReadOnlyList<LocalIdentity>> Identities = new(IdentityFilter.Local);

    /// <summary>
    /// Free text from the PC. A threat name must fit Defender's naming syntax (<see cref="ThreatNameSyntax"/>); a model only
    /// the model allowlist. Then the never-store rules (paths, addresses, MAC, SID, e-mail, URL, GUID, credentials, long
    /// tokens; for a name also key and serial shapes; for a model also key shapes), then this PC's user, PC and domain names.
    /// </summary>
    public static bool FreeTextOk(AdminFreeText kind, string s, IReadOnlyList<LocalIdentity> identities)
    {
        var allowed = kind == AdminFreeText.Model ? ModelChars().IsMatch(s) : ThreatNameRegex().IsMatch(s);
        if (!allowed) return false;
        // A model keeps serial-shaped runs (real models look like that), but never a product key or grouped serial.
        if (kind == AdminFreeText.Model ? NeverStore.RejectsModel(s) || NeverStore.KeyLike(s) : NeverStore.RejectsName(s)) return false;
        return !IdentityFilter.Contains(s, identities);
    }

    /// <summary>
    /// Microsoft Defender's threat-name syntax, <c>Type:Platform/Family[.Variant…][!suffix][@m|@mm]</c>: Type letters only;
    /// Platform a letter then letters, digits or '_'; Family and each variant letters, digits, '_' or '-'; an optional
    /// <c>!suffix</c> of letters, digits or '_'; the mass-mailer ending <c>@m</c> / <c>@mm</c> only at the very end. So exactly
    /// one ':' and one '/', in those places, no leading or doubled '/', no spaces. Examples: <c>Trojan:Win32/Wacatac.B!ml</c>,
    /// <c>Worm:Win32/Mydoom.O@mm</c>, <c>Exploit:O97M/CVE-2017-11882.RV!MTB</c>, <c>Virus:DOS/EICAR_Test_File</c>.
    /// </summary>
    public const string ThreatNameSyntax = @"^[A-Za-z]+:[A-Za-z][A-Za-z0-9_]*/[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*(?:![A-Za-z0-9_]+)?(?:@mm?)?$";

    [GeneratedRegex(ThreatNameSyntax)]
    private static partial Regex ThreatNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9 ._\-+(),]+$")]
    private static partial Regex ModelChars();

    /// <summary>UTC time to the minute, the one format of a Minute field.</summary>
    public static string Minute(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);

    private static JsonNode? Value(AdminField f, JsonNode v)
    {
        switch (f.Type)
        {
            case AdminFieldType.Text:
                return v is JsonValue tv && tv.TryGetValue<string>(out var s) && Text(s, f.MaxLength) && (f.Values is null || f.Values.Contains(s))
                    && (f.Pattern is null || Regex.IsMatch(s, f.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) ? JsonValue.Create(s) : null;
            case AdminFieldType.Integer:
                if (v is not JsonValue iv) return null;
                long l;
                if (iv.TryGetValue<long>(out var x)) l = x;
                else if (iv.TryGetValue<int>(out var i)) l = i;
                else return null;
                return l >= f.Min && l <= f.Max ? JsonValue.Create(l) : null;
            case AdminFieldType.Boolean:
                return v is JsonValue bv && bv.TryGetValue<bool>(out var b) ? JsonValue.Create(b) : null;
            case AdminFieldType.Minute:
                return v is JsonValue mv && mv.TryGetValue<string>(out var m) && MinuteRegex().IsMatch(m)
                    && DateTime.TryParseExact(m, "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _) ? JsonValue.Create(m) : null;
            case AdminFieldType.TextList:
                if (v is not JsonArray arr || arr.Count > f.MaxCount) return null;
                var list = new JsonArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in arr)
                {
                    if (e is not JsonValue ev || !ev.TryGetValue<string>(out var es) || f.Values is null || !f.Values.Contains(es) || !seen.Add(es)) return null;
                    list.Add(es);
                }
                return list;
            default:
                return null;
        }
    }

    /// <summary>Plain text within the cap: not empty, no control or format characters (bidi, zero-width).</summary>
    private static bool Text(string s, int max) =>
        s.Length > 0 && s.Length <= max && !s.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$")]
    private static partial Regex MinuteRegex();
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Windows.Actions.Broker.Reads;

/// <summary>
/// <c>security.encryption</c>: BitLocker state of each lettered volume, up to 26.
///
/// REVIEW NOTE (m9 §17.2 rule)
/// - Data: machine-wide only. One fixed WQL query on Win32_EncryptableVolume (root\cimv2\Security\MicrosoftVolumeEncryption:
///   DeviceID, DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod) and, per volume, three read-only BitLocker
///   methods: GetConversionStatus (percent), GetKeyProtectors (protector ids) and GetKeyProtectorType (each id's type). The
///   seam allows no other method (WindowsReadWmi.AllowedMethods). At most 64 volume rows and 16 protector ids per volume
///   are looked at; one more of either (the caps' sentinels) makes coverage incomplete.
/// - No write, no process start, no COM object beyond WbemScripting. No file or path from PC data is opened.
/// - DeviceID (a volume GUID path) and the protector ids are used inside the helper only, to call the methods; they never
///   leave it. No key, recovery password or protector id is ever read or sent: only the protector TYPES (closed set).
/// - Output: exactly the fields of AdminChecks "security.encryption": drive ("C:"), protection on|off|unknown, conversion,
///   percent (0–100 or null), method, protectorTypes (distinct, closed set). Volumes without a drive letter (recovery,
///   system partitions) are left out and not counted.
/// - "Nothing found" = ok, no items: BitLocker's provider answered and no lettered volume exists. "Could not read": the
///   namespace or class is missing (Windows Home without device encryption) → unsupported (bitlocker_unavailable); denied →
///   failed access_denied; other → failed read_error. A per-volume method that fails → that value null or [] and complete false.
/// - ELEVATION VERDICT: needed. The namespace's security allows administrators only (manage-bde and the BitLocker WMI
///   provider both require elevation). Confirm on the laptop.
/// </summary>
public sealed partial class EncryptionRead(IReadWmi wmi) : IAdminReadAdapter
{
    public const string CheckId = AdminChecks.Encryption;
    public const string HandlerVersion = "1";
    public const string Ns = @"root\cimv2\Security\MicrosoftVolumeEncryption";
    public const string Class = "Win32_EncryptableVolume";
    public const int Keep = 26, MaxProtectors = 16, RowCap = 64;

    public string Id => CheckId;
    public string Version => HandlerVersion;

    private static readonly string[] Props = ["DeviceID", "DriveLetter", "ProtectionStatus", "ConversionStatus", "EncryptionMethod"];

    public AdminReadOutput Read(AdminReadInput input, CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try { rows = wmi.Query(Ns, "SELECT " + string.Join(", ", Props) + " FROM " + Class, Props, RowCap + 1); }
        catch (ReadWmiException ex) { return AdminReadOutput.From(ex, "bitlocker_unavailable"); }

        var complete = rows.Count <= RowCap; // a 65th row is the cap's sentinel: there were more volumes
        var lettered = rows.Take(RowCap).Select(r => (Row: r, Drive: Letter(ReadValues.Str(r["DriveLetter"])), Id: ReadValues.Str(r["DeviceID"])))
            .Where(x => x.Drive is not null).OrderBy(x => x.Drive, StringComparer.Ordinal).ToList();
        var items = new List<JsonObject>();
        foreach (var (row, drive, deviceId) in lettered.Take(Keep))
        {
            ct.ThrowIfCancellationRequested();
            long? conversion = ReadValues.Long(row["ConversionStatus"]);
            long? percent = null;
            var types = new List<string>();
            if (deviceId is null) complete = false;
            else
            {
                try
                {
                    var status = wmi.Call(Ns, Class, "DeviceID", deviceId, "GetConversionStatus", new Dictionary<string, object?>(), ["ConversionStatus", "EncryptionPercentage"]);
                    if (ReadValues.Long(status["ReturnValue"]) == 0)
                    {
                        conversion = ReadValues.Long(status["ConversionStatus"]) ?? conversion;
                        percent = ReadValues.Long(status["EncryptionPercentage"]) is { } p and >= 0 and <= 100 ? p : null;
                    }
                    else complete = false;
                }
                catch (ReadWmiException) { complete = false; }
                try
                {
                    var ids = wmi.Call(Ns, Class, "DeviceID", deviceId, "GetKeyProtectors", new Dictionary<string, object?>(), ["VolumeKeyProtectorID"]);
                    if (ReadValues.Long(ids["ReturnValue"]) != 0) complete = false;
                    else
                    {
                        var pids = ReadValues.Strings(ids["VolumeKeyProtectorID"]).Take(MaxProtectors + 1).ToList();
                        if (pids.Count > MaxProtectors) complete = false; // more protectors than are looked at
                        foreach (var pid in pids.Take(MaxProtectors))
                        {
                            var t = wmi.Call(Ns, Class, "DeviceID", deviceId, "GetKeyProtectorType",
                                new Dictionary<string, object?> { ["VolumeKeyProtectorID"] = pid }, ["KeyProtectorType"]);
                            if (ReadValues.Long(t["ReturnValue"]) != 0) { complete = false; continue; }
                            var name = Protector(ReadValues.Long(t["KeyProtectorType"]));
                            if (!types.Contains(name)) types.Add(name);
                        }
                    }
                }
                catch (ReadWmiException) { complete = false; }
            }
            items.Add(new JsonObject
            {
                ["drive"] = drive,
                ["protection"] = ReadValues.Long(row["ProtectionStatus"]) switch { 0 => "off", 1 => "on", _ => "unknown" },
                ["conversion"] = Conversion(conversion),
                ["percent"] = percent,
                ["method"] = Method(ReadValues.Long(row["EncryptionMethod"])),
                ["protectorTypes"] = new JsonArray([.. types.Select(x => (JsonNode?)x)]),
            });
        }
        return AdminReadOutput.Ok(items, lettered.Count, complete && lettered.Count <= Keep);
    }

    internal static string? Letter(string? s) => s is not null && LetterRegex().IsMatch(s) ? s.ToUpperInvariant() : null;

    internal static string Conversion(long? id) => id switch
    {
        0 => "fully_decrypted", 1 => "fully_encrypted", 2 => "encrypting", 3 => "decrypting", 4 => "encryption_paused", 5 => "decryption_paused", _ => "unknown",
    };

    internal static string Method(long? id) => id switch
    {
        0 => "none", 1 => "aes_128_diffuser", 2 => "aes_256_diffuser", 3 => "aes_128", 4 => "aes_256", 5 => "hardware", 6 => "xts_aes_128", 7 => "xts_aes_256", _ => "unknown",
    };

    internal static string Protector(long? id) => id switch
    {
        1 => "tpm", 2 => "external_key", 3 => "recovery_password", 4 => "tpm_pin", 5 => "tpm_startup_key", 6 => "tpm_pin_startup_key",
        7 => "public_key", 8 => "passphrase", 9 => "tpm_certificate", 10 => "account", _ => "unknown",
    };

    [GeneratedRegex("^[A-Za-z]:$")]
    private static partial Regex LetterRegex();
}

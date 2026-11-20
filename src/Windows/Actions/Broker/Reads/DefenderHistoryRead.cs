using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Security.Collectors;

namespace OptimizationTool.Windows.Actions.Broker.Reads;

/// <summary>
/// <c>security.defender_history</c>: Microsoft Defender's detections in a window of 1–90 days (default 30), newest 50.
///
/// REVIEW NOTE (m9 §17.2 rule)
/// - Data: machine-wide only. Two fixed WQL queries in root\Microsoft\Windows\Defender: MSFT_MpThreatDetection (ThreatID,
///   InitialDetectionTime, CleaningActionID, ActionSuccess, DetectionSourceTypeID, Resources) and MSFT_MpThreat (ThreatID,
///   ThreatName, CategoryID, SeverityID), plus MSFT_MpComputerStatus.AMRunningMode to know whether Defender is in charge.
///   At most 1000 rows each.
/// - No write, no process start, no COM object beyond WbemScripting (the WMI client). No file or path from PC data is
///   opened. Resources (the affected files and processes, with paths) is read only to COUNT it; no element is kept or sent.
/// - Output: exactly the fields of AdminChecks "security.defender_history": threatName (≤ 120, cleaned; only Defender's name syntax, else null and coverage incomplete), category,
///   severity, action, foundBy (closed sets, unknown codes → "unknown"), detectedAt (UTC, to the minute), succeeded
///   (bool or null), affectedItems (count). No resource or process path, no user name, no threat id.
/// - "Nothing found" = ok, no items, complete: the elevated query answered and no detection in the window was retained.
///   "Could not read": namespace or class missing → unsupported (defender_unavailable); denied → failed access_denied;
///   other → failed read_error. Defender not the active antivirus (passive, EDR block, not running) → ok with code
///   defender_not_primary and complete false (its history may be empty because another product is in charge). Detections
///   without a time, a row cap hit, or more than 50 in the window → complete false. Retention is Defender's own (often
///   shorter than 90 days); that is not visible here and is noted for the laptop pass.
/// - ELEVATION VERDICT: needed. Defender filters threat history for standard users (an empty list, not an error; see
///   DefenderCollector), so only the elevated read can tell "nothing found" from "not allowed to see". Confirm on the laptop.
/// </summary>
public sealed class DefenderHistoryRead(IReadWmi wmi) : IAdminReadAdapter
{
    public const string CheckId = AdminChecks.DefenderHistory;
    public const string HandlerVersion = "1";
    public const string Ns = @"root\Microsoft\Windows\Defender";
    public const int RowCap = 1000, Keep = 50;

    public string Id => CheckId;
    public string Version => HandlerVersion;

    private static readonly string[] DetectionProps = ["ThreatID", "InitialDetectionTime", "CleaningActionID", "ActionSuccess", "DetectionSourceTypeID", "Resources"];
    private static readonly string[] ThreatProps = ["ThreatID", "ThreatName", "CategoryID", "SeverityID"];

    public AdminReadOutput Read(AdminReadInput input, CancellationToken ct)
    {
        var days = input.WindowDays ?? AdminChecks.Find(CheckId)!.Window!.DefaultDays;
        IReadOnlyList<IReadOnlyDictionary<string, object?>> detections, threats;
        try
        {
            detections = wmi.Query(Ns, "SELECT " + string.Join(", ", DetectionProps) + " FROM MSFT_MpThreatDetection", DetectionProps, RowCap + 1);
            ct.ThrowIfCancellationRequested();
            threats = wmi.Query(Ns, "SELECT " + string.Join(", ", ThreatProps) + " FROM MSFT_MpThreat", ThreatProps, RowCap + 1);
        }
        catch (ReadWmiException ex) { return AdminReadOutput.From(ex, "defender_unavailable"); }
        ct.ThrowIfCancellationRequested();

        string? mode = null;
        try { mode = ReadValues.Str(wmi.Query(Ns, "SELECT AMRunningMode FROM MSFT_MpComputerStatus", ["AMRunningMode"], 1).FirstOrDefault()?["AMRunningMode"]); }
        catch (ReadWmiException) { /* unknown mode: not used to judge completeness */ }
        var notPrimary = mode is not null && !string.Equals(mode.Trim(), "Normal", StringComparison.OrdinalIgnoreCase);

        var capped = detections.Count > RowCap || threats.Count > RowCap;
        var byId = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        foreach (var t in threats.Take(RowCap))
            if (ReadValues.Long(t["ThreatID"]) is { } id) byId.TryAdd(id, t);

        var since = input.Now.UtcDateTime.AddDays(-days);
        var undated = false;
        var inWindow = new List<(DateTime At, JsonObject Item)>();
        foreach (var d in detections.Take(RowCap))
        {
            var at = DefenderCollector.Dmtf(ReadValues.Str(d["InitialDetectionTime"]));
            if (at is null) { undated = true; continue; }
            if (at < since || at > input.Now.UtcDateTime.AddDays(1)) continue;
            byId.TryGetValue(ReadValues.Long(d["ThreatID"]) ?? -1, out var t);
            inWindow.Add((at.Value, new JsonObject
            {
                ["threatName"] = ReadValues.Clean(ReadValues.Str(t?["ThreatName"]), 120), // null when unknown; the catalog nulls a name outside Defender's syntax
                ["category"] = Category(ReadValues.Long(t?["CategoryID"])),
                ["severity"] = Severity(ReadValues.Long(t?["SeverityID"])),
                ["detectedAt"] = AdminChecks.Minute(at.Value),
                ["action"] = Action(ReadValues.Long(d["CleaningActionID"])),
                ["succeeded"] = ReadValues.Bool(d["ActionSuccess"]),
                ["foundBy"] = Source(ReadValues.Long(d["DetectionSourceTypeID"])),
                ["affectedItems"] = Math.Min(ReadValues.Count(d["Resources"]), 100_000),
            }));
        }
        var items = inWindow.OrderByDescending(x => x.At).Take(Keep).Select(x => x.Item).ToList();
        var complete = !capped && !undated && !notPrimary && inWindow.Count <= Keep;
        return AdminReadOutput.Ok(items, inWindow.Count, complete, notPrimary ? "defender_not_primary" : null);
    }

    /// <summary>MSFT_MpThreat.CategoryID → the closed set (Defender's documented ids; others "unknown").</summary>
    internal static string Category(long? id) => id switch
    {
        1 => "adware", 2 => "spyware", 3 => "password_stealer", 4 => "trojan_downloader", 5 => "worm", 6 => "backdoor", 7 => "remote_access_trojan",
        8 => "trojan", 9 => "email_flooder", 10 => "keylogger", 11 => "dialer", 12 => "monitoring_software", 13 => "browser_modifier", 14 => "cookie",
        15 => "browser_plugin", 16 => "exploit", 17 => "nuker", 18 => "security_disabler", 19 => "joke_program", 20 => "hostile_activex",
        21 => "software_bundler", 22 => "stealth_modifier", 23 => "settings_modifier", 24 => "toolbar", 25 => "remote_control_software",
        26 => "trojan_ftp", 27 => "potentially_unwanted", 28 => "exploit", 29 => "trojan_telnet", 30 => "exploit", 31 => "file_sharing_program",
        32 => "malware_creation_tool", 33 => "remote_control_software", 34 => "tool", 36 => "trojan_denial_of_service", 37 => "trojan_dropper",
        38 => "trojan_mass_mailer", 39 => "trojan_monitoring_software", 40 => "trojan_proxy_server", 42 => "virus", 43 => "known", 45 => "spp",
        46 => "behavior", 47 => "vulnerability", 48 => "policy", 49 => "enterprise_unwanted", 50 => "ransomware", 51 => "asr_rule",
        _ => "unknown",
    };

    internal static string Severity(long? id) => id switch { 1 => "low", 2 => "moderate", 4 => "high", 5 => "severe", _ => "unknown" };

    /// <summary>MSFT_MpThreatDetection.CleaningActionID.</summary>
    internal static string Action(long? id) => id switch
    {
        1 => "clean", 2 => "quarantine", 3 => "remove", 6 => "allow", 8 => "user_defined", 9 => "no_action", 10 => "block", _ => "unknown",
    };

    /// <summary>MSFT_MpThreatDetection.DetectionSourceTypeID.</summary>
    internal static string Source(long? id) => id switch
    {
        1 => "user", 2 => "system", 3 => "real_time", 4 => "downloads_and_attachments", 5 => "network_inspection", 7 => "early_launch",
        8 => "local_attestation", 9 => "remote_attestation", _ => "unknown",
    };
}

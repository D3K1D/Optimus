using System.Globalization;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

internal sealed record DefenderStatusRaw(
    string? RunningMode, bool? AmServiceEnabled, bool? AntivirusEnabled, bool? RealTimeProtectionEnabled,
    string? SignatureVersion, DateTime? SignatureUpdatedUtc, long? SignatureAgeDays,
    DateTime? QuickScanEndUtc, DateTime? FullScanEndUtc, bool? TamperProtected);

internal sealed record ThreatDetectionRaw(long ThreatId, int StatusId, long AdditionalActions, DateTime? InitialDetectionUtc, DateTime? LastStatusChangeUtc);

internal sealed record ThreatRaw(long ThreatId, string Name, int CategoryId, int SeverityId, bool IsActive);

internal sealed record DefenderRaw(
    DefenderStatusRaw? Status, string? StatusError,
    IReadOnlyList<ThreatDetectionRaw>? Detections, IReadOnlyList<ThreatRaw>? Threats, string? ThreatError,
    bool Elevated, bool Truncated);

/// <summary>
/// win.security.defender — Microsoft Defender Antivirus state from WMI <c>root\Microsoft\Windows\Defender</c>
/// (<c>MSFT_MpComputerStatus</c>, <c>MSFT_MpThreatDetection</c>, <c>MSFT_MpThreat</c>) through the built-in
/// WbemScripting automation object, so no extra package. Defender filters threat history for standard users
/// (an empty list is returned, not an error), so unless the process is elevated or rows came back, threat history
/// is reported as not readable and coverage is partial.
/// </summary>
internal sealed class DefenderCollector(CollectorOptions options) : SecurityCollector<DefenderRaw>(options)
{
    public const string CollectorId = "win.security.defender";
    private const string Namespace = @"root\Microsoft\Windows\Defender";
    private const int RowCap = 200;
    private const int ThreatFactCap = 20;
    private const int WBEM_E_INVALID_NAMESPACE = unchecked((int)0x8004100E);
    private const int WBEM_E_INVALID_CLASS = unchecked((int)0x80041010);
    private const int WBEM_E_ACCESS_DENIED = unchecked((int)0x80041003);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    public override string Id => CollectorId;
    public override string GapTitle => "Microsoft Defender";
    public override string GapSource => "Microsoft Defender Antivirus";
    public override string GapDisplay => "Microsoft Defender did not answer.";

    internal override DefenderRaw Read(CancellationToken ct)
    {
        var elevated = IsElevated();
        object? service = null;
        try
        {
            var locator = Automation.Create("WbemScripting.SWbemLocator");
            try { service = Automation.Call(locator, "ConnectServer", ".", Namespace); }
            catch (Exception ex) { return new DefenderRaw(null, Automation.Hex(Automation.HResult(ex)), null, null, null, elevated, false); }
            finally { Automation.Release(locator); }

            DefenderStatusRaw? status = null;
            string? statusError = null;
            try
            {
                var row = Query(service!, "SELECT * FROM MSFT_MpComputerStatus", 1).FirstOrDefault();
                status = row is null ? null : new DefenderStatusRaw(
                    Str(row, "AMRunningMode"), Bool(row, "AMServiceEnabled"), Bool(row, "AntivirusEnabled"),
                    Bool(row, "RealTimeProtectionEnabled"), Str(row, "AntivirusSignatureVersion"),
                    Dmtf(Str(row, "AntivirusSignatureLastUpdated")), Long(row, "AntivirusSignatureAge"),
                    Dmtf(Str(row, "QuickScanEndTime")), Dmtf(Str(row, "FullScanEndTime")), Bool(row, "IsTamperProtected"));
                if (status is null) statusError = "no_rows";
            }
            catch (Exception ex) { statusError = Automation.Hex(Automation.HResult(ex)); }
            ct.ThrowIfCancellationRequested();

            List<ThreatDetectionRaw>? detections = null;
            List<ThreatRaw>? threats = null;
            string? threatError = null;
            var truncated = false;
            try
            {
                var det = Query(service!, "SELECT ThreatID, ThreatStatusID, AdditionalActionsBitMask, InitialDetectionTime, LastThreatStatusChangeTime FROM MSFT_MpThreatDetection", RowCap + 1);
                truncated |= det.Count > RowCap;
                detections = det.Take(RowCap).Select(r => new ThreatDetectionRaw(
                    Long(r, "ThreatID") ?? 0, (int)(Long(r, "ThreatStatusID") ?? 0), Long(r, "AdditionalActionsBitMask") ?? 0,
                    Dmtf(Str(r, "InitialDetectionTime")), Dmtf(Str(r, "LastThreatStatusChangeTime")))).ToList();
                ct.ThrowIfCancellationRequested();
                var thr = Query(service!, "SELECT ThreatID, ThreatName, CategoryID, SeverityID, IsActive FROM MSFT_MpThreat", RowCap + 1);
                truncated |= thr.Count > RowCap;
                threats = thr.Take(RowCap).Select(r => new ThreatRaw(
                    Long(r, "ThreatID") ?? 0, Str(r, "ThreatName") ?? "", (int)(Long(r, "CategoryID") ?? 0),
                    (int)(Long(r, "SeverityID") ?? 0), Bool(r, "IsActive") ?? false)).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { threatError = Automation.Hex(Automation.HResult(ex)); }

            return new DefenderRaw(status, statusError, detections, threats, threatError, elevated, truncated);
        }
        finally { Automation.Release(service); }
    }

    internal override CollectorResult Normalize(DefenderRaw raw)
    {
        var facts = new FactSet("Microsoft Defender Antivirus");
        if (raw.Status is null)
        {
            var hr = raw.StatusError;
            var invalid = hr == Automation.Hex(WBEM_E_INVALID_NAMESPACE) || hr == Automation.Hex(WBEM_E_INVALID_CLASS) || hr == "no_rows";
            var denied = hr == Automation.Hex(WBEM_E_ACCESS_DENIED) || hr == Automation.Hex(E_ACCESSDENIED);
            return facts.Result(invalid ? "unsupported" : denied ? "denied" : "failed",
                invalid ? "Microsoft Defender Antivirus is not available on this PC."
                : denied ? "Windows needs administrator rights to read Microsoft Defender status."
                : $"Defender status query failed ({hr}).");
        }

        var s = raw.Status;
        var mode = Mode(s);
        facts.Add("security.defender.status", new JsonObject
        {
            ["runningMode"] = Text.Clean(s.RunningMode, 40),
            ["mode"] = mode,
            ["amServiceEnabled"] = s.AmServiceEnabled,
            ["antivirusEnabled"] = s.AntivirusEnabled,
            ["realTimeProtection"] = s.RealTimeProtectionEnabled,
            ["signatureVersion"] = Text.Clean(s.SignatureVersion, 40),
            ["signatureUpdatedAt"] = Text.IsoOrNull(s.SignatureUpdatedUtc),
            ["signatureAgeDays"] = s.SignatureAgeDays is >= 0 and < 65535 ? s.SignatureAgeDays : null,
            ["quickScanEndAt"] = Text.IsoOrNull(s.QuickScanEndUtc),
            ["fullScanEndAt"] = Text.IsoOrNull(s.FullScanEndUtc),
            ["tamperProtected"] = s.TamperProtected,
        }, StatusDisplay(mode, s));

        // Threats: track detection status separately from the threat itself (catalog: detection vs disposition).
        var readable = raw.ThreatError is null && raw.Detections is not null && raw.Threats is not null
            && (raw.Elevated || raw.Detections.Count > 0 || raw.Threats.Count > 0);
        var byId = (raw.Threats ?? []).GroupBy(t => t.ThreatId).ToDictionary(g => g.Key, g => g.First());
        var open = new List<(ThreatDetectionRaw D, ThreatRaw? T)>();
        var history = 0;
        if (readable)
        {
            foreach (var d in raw.Detections!.GroupBy(d => d.ThreatId).Select(g => g.OrderByDescending(x => x.LastStatusChangeUtc ?? x.InitialDetectionUtc).First()))
            {
                byId.TryGetValue(d.ThreatId, out var t);
                if (NeedsAction(d, t)) open.Add((d, t)); else history++;
            }
        }

        facts.Add("security.defender.threats", new JsonObject
        {
            ["readable"] = readable,
            ["reason"] = readable ? null : raw.ThreatError is not null ? "query_failed" : "needs_admin",
            ["detectionCount"] = readable ? raw.Detections!.Count : null,
            ["actionNeededCount"] = readable ? open.Count : null,
            ["historyCount"] = readable ? history : null,
            ["truncated"] = raw.Truncated,
        }, !readable
            ? "Microsoft Defender's threat history needs administrator rights to read."
            : open.Count == 0
                ? "Microsoft Defender reports no threats that still need action."
                : open.Count == 1 ? "Microsoft Defender reports 1 threat that still needs action." : $"Microsoft Defender reports {open.Count} threats that still need action.");

        foreach (var (d, t) in open.OrderByDescending(o => o.D.InitialDetectionUtc).Take(ThreatFactCap))
        {
            var name = Text.Clean(t?.Name, 100);
            if (name.Length == 0) name = "an unnamed threat";
            var pua = t is not null && (t.CategoryId == 27 || name.StartsWith("PUA", StringComparison.OrdinalIgnoreCase));
            facts.Add($"security.defender.threat.{Subjects.Hash8(d.ThreatId.ToString(CultureInfo.InvariantCulture))}", new JsonObject
            {
                ["threatId"] = d.ThreatId,
                ["threatName"] = name,
                ["classification"] = pua ? "pua" : "malware",
                ["status"] = StatusName(d.StatusId),
                ["actionNeeded"] = true,
                ["active"] = t?.IsActive,
                ["severityId"] = t?.SeverityId,
                ["detectedAt"] = Text.IsoOrNull(d.InitialDetectionUtc),
            }, $"Microsoft Defender detected {name}; its status is {StatusName(d.StatusId).Replace('_', ' ')}.");
        }

        var reasons = new List<string>();
        if (!readable) reasons.Add(raw.ThreatError is not null ? $"Threat history query failed ({raw.ThreatError})." : "Threat history needs administrator rights.");
        if (raw.Truncated) reasons.Add($"Threat rows capped at {RowCap}.");
        return facts.Result(reasons.Count == 0 ? "complete" : "partial", reasons.Count == 0 ? null : string.Join(" ", reasons));
    }

    /// <summary>primary | passive | side_by_side | disabled (catalog: Defender running modes).</summary>
    internal static string Mode(DefenderStatusRaw s)
    {
        var m = (s.RunningMode ?? "").Trim().ToLowerInvariant();
        if (s.AmServiceEnabled == false || m.Contains("not running")) return "disabled";
        if (m.StartsWith("sxs", StringComparison.Ordinal)) return "side_by_side";
        if (m.Contains("passive") || m.Contains("edr")) return "passive";
        if (m == "normal") return s.AntivirusEnabled == false ? "disabled" : "primary";
        return "unknown";
    }

    private static string StatusDisplay(string mode, DefenderStatusRaw s) => mode switch
    {
        "primary" => $"Microsoft Defender is the active antivirus; real-time protection is {(s.RealTimeProtectionEnabled == true ? "on" : s.RealTimeProtectionEnabled == false ? "off" : "unknown")}.",
        "passive" => "Microsoft Defender runs in passive mode; another antivirus is primary.",
        "side_by_side" => "Microsoft Defender runs side by side with another antivirus (limited periodic scanning).",
        "disabled" => "Microsoft Defender Antivirus is turned off or not running.",
        _ => "Microsoft Defender reported an unrecognized running mode.",
    };

    // ThreatStatusID: 0 unknown, 1 detected, 2 cleaned, 3 quarantined, 4 removed, 5 allowed, 6 blocked,
    // 102 quarantine failed, 103 remove failed, 104 allow failed, 105 abandoned, 107 block failed.
    internal static bool NeedsAction(ThreatDetectionRaw d, ThreatRaw? t) =>
        t?.IsActive == true || d.StatusId is 1 or 102 or 103 or 105 or 107 || (d.StatusId is not (2 or 3 or 4 or 5 or 6) && d.AdditionalActions != 0);

    internal static string StatusName(int id) => id switch
    {
        1 => "detected", 2 => "cleaned", 3 => "quarantined", 4 => "removed", 5 => "allowed", 6 => "blocked",
        102 => "quarantine_failed", 103 => "remove_failed", 104 => "allow_failed", 105 => "abandoned", 107 => "block_failed",
        _ => "unknown",
    };

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---- WbemScripting helpers ----

    private static List<object> Query(object service, string wql, int cap)
    {
        // wbemFlagReturnImmediately (0x10); not forward-only, which fails through late binding
        var set = Automation.Call(service, "ExecQuery", wql, "WQL", 0x10)!;
        try { return Automation.Items(set, cap).ToList(); }
        finally { Automation.Release(set); }
    }

    private static object? Prop(object row, string name)
    {
        try
        {
            var props = Automation.Get(row, "Properties_")!;
            try
            {
                var p = Automation.Call(props, "Item", name)!;
                try { return Automation.Get(p, "Value"); }
                finally { Automation.Release(p); }
            }
            finally { Automation.Release(props); }
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or System.Runtime.InteropServices.COMException or MissingMethodException)
        {
            return null;
        }
    }

    private static string? Str(object row, string name) => Prop(row, name) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    private static bool? Bool(object row, string name) => Prop(row, name) is bool b ? b : null;
    private static long? Long(object row, string name) => Prop(row, name) switch
    {
        null => null,
        string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
        IConvertible c => Convert.ToInt64(c, CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>DMTF datetime "yyyyMMddHHmmss.ffffff+UUU" (UUU = minutes offset) → UTC.</summary>
    internal static DateTime? Dmtf(string? s)
    {
        if (s is null || s.Length < 25) return null;
        if (!DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (!int.TryParse(s.AsSpan(22, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)) return null;
        if (local.Year < 1971) return null;
        var sign = s[21] == '-' ? -1 : 1;
        return DateTime.SpecifyKind(local.AddMinutes(-sign * offset), DateTimeKind.Utc);
    }
}

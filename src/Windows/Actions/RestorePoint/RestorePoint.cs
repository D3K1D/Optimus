using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;

namespace OptimizationTool.Windows.Actions.RestorePoint;

/// <summary>What Windows answered to one restore-point request: created (new), reused (one from the last 24 h), or failed.</summary>
public sealed record RestorePointResult(string Result, DateTimeOffset? At, string? Code = null);

/// <summary>
/// The Windows restore-point seam (m9 §3.2 restore_point.create). <see cref="Unavailable"/> is a read that works as the
/// user (null, or "protection_off"); <see cref="Create"/> runs only in the elevated broker. Tests use a fake; the real
/// implementation is written but has never been run.
/// </summary>
public interface IRestorePointSystem
{
    string? Unavailable();
    RestorePointResult Create(string description);
}

/// <summary>Plan code shared by the host handler and the broker handler.</summary>
public static class RestorePointPlan
{
    public const string ActionId = "restore_point.create";
    public const string HandlerVersion = "1.0.0";
    public const string Label = "Make a restore point";
    public const string Description = "Optimus";
    public const string ProtectionOff = "protection_off";
    public const string Resource = "restore_point:system";

    public static JsonObject Target() => new() { ["type"] = "restore_point", ["id"] = "system", ["label"] = "Windows drive" };

    public static bool ValidTarget(JsonObject? t) =>
        t is null || t.Count == 0 || t["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id == "system" && t.All(kv => kv.Key is "type" or "id" or "label");

    public static ActionResolution Resolve(string? unavailable) => unavailable switch
    {
        null => new ActionResolution(true, Target(), new JsonObject { ["protection"] = "on" }, new JsonObject { ["restorePoint"] = "requested" },
            [new PlanRow("Restore point", "None new", "New, or one from the last 24 hours")],
            [new("Covers", "Windows files and settings, not your files")],
            ["Windows may reuse a restore point it made in the last 24 hours."], null, Resources: [Resource]),
        ProtectionOff => ActionResolution.Ineligible(ProtectionOff, "System Protection is off for the Windows drive, so Windows can't make a restore point."),
        _ => ActionResolution.Ineligible("unavailable", "Windows didn't say whether it can make a restore point."),
    };

    /// <summary>created / reused → completed with {result, at}; anything else → failed.</summary>
    public static ActionOutcome Outcome(RestorePointResult r)
    {
        var observed = new JsonObject
        {
            ["result"] = r.Result is "created" or "reused" ? r.Result : "failed",
            ["at"] = r.At?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        return r.Result switch
        {
            "created" => new ActionOutcome("completed", observed, "Windows made a new restore point."),
            "reused" => new ActionOutcome("completed", observed, "Windows already had a restore point from the last 24 hours and kept it."),
            _ => new ActionOutcome("failed", observed, "Windows didn't make a restore point.", r.Code ?? "restore_point_failed",
                r.Code == ProtectionOff ? "System Protection is off." : "Windows refused the request."),
        };
    }
}

/// <summary>
/// restore_point.create in the host (m9 §3.2): an administrator action, always through the broker. The host only reads
/// whether System Protection allows one; the request itself is made by the elevated broker.
/// </summary>
public sealed class RestorePointHandler(IRestorePointSystem system) : IActionHandler
{
    public string ActionId => RestorePointPlan.ActionId;
    public string Version => RestorePointPlan.HandlerVersion;
    public string Label => RestorePointPlan.Label;
    public string Privilege => "admin";
    public bool SupportsUndo => false;
    public bool NothingToUndo => true;
    public string Kind => "provider";
    public string Group => "repair";
    public string What => "Asks Windows for a restore point of system files and settings (not your files); Windows may reuse one from the last day.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() =>
        request.Parameters is { Count: > 0 } || !RestorePointPlan.ValidTarget(request.Target)
            ? ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.")
            : RestorePointPlan.Resolve(system.Unavailable()), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "admin_only", "This runs through the administrator helper."));

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
}

public sealed class RestorePointActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) => [new RestorePointHandler(new WindowsRestorePointSystem())];
}

/// <summary>
/// The scripts' restore-point pre-step (m9 §5.2) through the broker: available unless System Protection is off; a
/// one-step broker session for restore_point.create (UAC and the broker's own window). Only wired with --enable-admin-actions.
/// </summary>
public sealed class BrokerRestorePointStep(IElevation elevation, IRestorePointSystem system) : IRestorePointStep
{
    public RestorePointAvailability Availability()
    {
        string? off;
        try { off = system.Unavailable(); }
        catch (Exception) { off = "unavailable"; }
        return off is null ? new RestorePointAvailability(true, null) : new RestorePointAvailability(false, RestorePointPlan.ProtectionOff);
    }

    public async Task<RestorePointOutcome> CreateAsync(CancellationToken ct)
    {
        var r = RestorePointPlan.Resolve(system.Unavailable());
        if (!r.Eligible) return new RestorePointOutcome("failed", null);
        var planId = "plan-rp" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        var step = new ElevatedStep(1, RestorePointPlan.ActionId, RestorePointPlan.HandlerVersion, r.Target, r.Before, r.After, null, Long: true);
        var result = await elevation.RunAsync(new ElevatedRequest(planId, "script-restore-point", [step]), ct, ct).ConfigureAwait(false);
        // m9 §13 R2: the script's lease stays held until this broker session really ended, whatever its answer.
        return Map(result) with { Ended = result.Ended };
    }

    private static RestorePointOutcome Map(ElevatedResult result)
    {
        var outcome = result.Steps.FirstOrDefault(s => s.N == 1).Outcome;
        if (outcome is null) return new RestorePointOutcome("failed", null);
        if (outcome.State == "declined") return new RestorePointOutcome("declined", null);
        var kind = outcome.Observed["result"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (outcome.State is "completed" or "verified" && kind is "created" or "reused")
        {
            DateTimeOffset? at = outcome.Observed["at"] is JsonValue a && a.TryGetValue<string>(out var t)
                && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            return new RestorePointOutcome(kind, at);
        }
        return new RestorePointOutcome("failed", null);
    }
}

/// <summary>
/// Real implementation (NEVER RUN on the development PC): availability from the System Restore policy and the
/// RPSessionInterval value; create through SRSetRestorePointW, with the restore points listed (WMI root\default
/// SystemRestore) before and after so a reuse inside Windows' 24-hour window is told apart from a new point.
/// </summary>
public sealed class WindowsRestorePointSystem : IRestorePointSystem
{
    private const int BeginSystemChange = 100, EndSystemChange = 101, ModifySettings = 12, ErrorServiceDisabled = 1058, ErrorDisabled = 1060;

    public string? Unavailable()
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var policy = lm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"))
            if (policy?.GetValue("DisableSR") is int p && p == 1) return RestorePointPlan.ProtectionOff;
        using var sr = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
        if (sr?.GetValue("RPSessionInterval") is int interval && interval == 0) return RestorePointPlan.ProtectionOff;
        return null;
    }

    public RestorePointResult Create(string description)
    {
        var before = Points();
        var info = new RESTOREPOINTINFOW { dwEventType = BeginSystemChange, dwRestorePtType = ModifySettings, llSequenceNumber = 0, szDescription = description };
        if (!SRSetRestorePointW(ref info, out var status))
            return new RestorePointResult("failed", null, status.nStatus is ErrorServiceDisabled or ErrorDisabled ? RestorePointPlan.ProtectionOff : "restore_point_failed");
        var end = new RESTOREPOINTINFOW { dwEventType = EndSystemChange, dwRestorePtType = ModifySettings, llSequenceNumber = status.llSequenceNumber, szDescription = description };
        SRSetRestorePointW(ref end, out _);
        var after = Points();
        if (after.FirstOrDefault(p => !before.Any(b => b.Seq == p.Seq)) is { Seq: > 0 } made) return new RestorePointResult("created", made.At);
        var recent = after.Where(p => p.At is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(24)).OrderByDescending(p => p.At).FirstOrDefault();
        return recent.Seq > 0 ? new RestorePointResult("reused", recent.At) : new RestorePointResult("failed", null, "restore_point_failed");
    }

    /// <summary>Restore points through WMI (late-bound COM, no extra package): sequence number and creation time.</summary>
    private static List<(uint Seq, DateTimeOffset? At)> Points()
    {
        var list = new List<(uint, DateTimeOffset?)>();
        object? service = null, set = null;
        try
        {
            service = Marshal.BindToMoniker(@"winmgmts:\\.\root\default");
            set = service.GetType().InvokeMember("ExecQuery", BindingFlags.InvokeMethod, null, service, ["SELECT SequenceNumber, CreationTime FROM SystemRestore"], CultureInfo.InvariantCulture);
            foreach (var item in (System.Collections.IEnumerable)set!)
            {
                var t = item.GetType();
                var seq = Convert.ToUInt32(t.InvokeMember("SequenceNumber", BindingFlags.GetProperty, null, item, null, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                var raw = t.InvokeMember("CreationTime", BindingFlags.GetProperty, null, item, null, CultureInfo.InvariantCulture) as string;
                list.Add((seq, WmiTime(raw)));
                Marshal.ReleaseComObject(item);
                if (list.Count > 500) break;
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or MissingMethodException) { }
        finally
        {
            if (set is not null && Marshal.IsComObject(set)) Marshal.ReleaseComObject(set);
            if (service is not null && Marshal.IsComObject(service)) Marshal.ReleaseComObject(service);
        }
        return list;
    }

    /// <summary>WMI CIM_DATETIME "yyyyMMddHHmmss.ffffff+UUU" (minutes offset) → UTC.</summary>
    private static DateTimeOffset? WmiTime(string? s)
    {
        if (s is null || s.Length < 25 || !DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (!int.TryParse(s[21..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes)) return null;
        return new DateTimeOffset(local, TimeSpan.FromMinutes(minutes)).ToUniversalTime();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOW info, out STATEMGRSTATUS status);
}

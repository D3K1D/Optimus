using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Startup;
using ProtectionResult = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Windows.Actions.Services;

/// <summary>
/// One Windows service as the Service Control Manager reports it. Type: SERVICE_* type bits; StartType 0 boot, 1 system,
/// 2 auto, 3 demand, 4 disabled; State: SERVICE_STOPPED 1 … SERVICE_RUNNING 4. BinaryPath is the configured command line,
/// Image the program it starts (parsed as text), ServiceDll the svchost payload. RunningDependents: active services that
/// depend on it.
/// </summary>
public sealed record ServiceInfo(string Name, string? DisplayName, int Type, int StartType, bool DelayedAuto, string? BinaryPath, string? Image,
    string? ServiceDll, string? Account, int State, IReadOnlyList<string> RunningDependents);

/// <summary>
/// The Service Control Manager seam (m9 §3.2 service.disable): read one service's configuration, state and running
/// dependents, list service key names, set the start type, stop and start. Only the elevated broker calls the three
/// writers. Tests use a fake; the real one (<see cref="WindowsServiceControl"/>) has never been run.
/// </summary>
public interface IServiceControl
{
    /// <summary>Every service key name (Win32 services and drivers), for matching evidence.</summary>
    IReadOnlyList<string> Names();
    /// <summary>The service, or null when it does not exist.</summary>
    ServiceInfo? Read(string name);
    /// <summary>Start type 2 (with the delayed flag), 3 or 4.</summary>
    void SetStartType(string name, int startType, bool delayed);
    /// <summary>Asks the service to stop and waits up to <paramref name="wait"/> for it.</summary>
    void Stop(string name, TimeSpan wait);
    /// <summary>Starts the service and waits up to <paramref name="wait"/> for it to run.</summary>
    void Start(string name, TimeSpan wait);
}

/// <summary>
/// service.disable {targetRef, startType: manual|disabled, stop} (contracts/m9-ai-powers.md §3.2): sets one non-protected
/// Win32 service to Manual or Disabled and optionally stops it. Administrator, always through the broker; the broker half
/// (<see cref="ServiceBrokerHandler"/>) runs this same code and re-checks the protected lists itself.
/// Refused: drivers and every protected service (Windows, security, backup, management, hardware tools), a service other
/// running services depend on (in_use), one already in the requested state. The execution fingerprint (name, command line,
/// image, ServiceDll, account, type and the SHA-256 of the image and ServiceDll) is recorded; undo restores the recorded
/// start type and running state only while it still matches, and is not offered when the binary could not be hashed.
/// </summary>
public sealed class ServiceDisableHandler(IServiceControl scm, IFileFacts files, ISecurityProducts? products = null) : IActionHandler
{
    public const string Id = "service.disable";
    public const string HandlerVersion = "1.1.0", LabelText = "Turn off a service";
    /// <summary>Services has no settings URI; the messages name the manual route (the Services console).</summary>
    public const string? ManualUri = null;
    private const string StartupCollector = "win.security.startup";
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(25);

    public string ActionId => Id;
    public string Version => HandlerVersion;
    public string Label => LabelText;
    public string Privilege => "admin";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "startup";
    public string What => "Sets a background service to start only when needed, or not at all, and can stop it now; Windows and security services are never touched.";
    public IReadOnlyList<string> Flags => [];
    public bool TakesLease => false;
    public string? TargetKind => "service";

    public JsonObject? ParameterSchema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("startType", "stop"),
        ["properties"] = new JsonObject
        {
            ["startType"] = new JsonObject { ["enum"] = new JsonArray("manual", "disabled") },
            ["stop"] = new JsonObject { ["type"] = "boolean" },
        },
    };

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var name = NameOf(target) ?? throw new ArgumentException("Not a service.", nameof(target));
        return State(scm.Read(name));
    }, ct);

    /// <summary>Never in the host: an administrator write runs in the broker.</summary>
    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "admin_only", "This runs through the administrator helper."));

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (NameOf(target) is null || before["fingerprint"] is not JsonObject fp || !Undoable(fp)) return null;
        var t = (JsonObject)target.DeepClone();
        t["expect"] = fp.DeepClone();
        return new ActionRequest(Id, t, new JsonObject { ["restore"] = new JsonObject { ["startType"] = before["startType"]?.DeepClone(), ["running"] = before["running"]?.DeepClone() } }, null)
        {
            Undo = true,
        };
    }

    // ---------------------------------------------------------------- resolve

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Undo != request.Parameters?["restore"] is not null) return ActionResolution.Ineligible("invalid_arguments", "This change isn't supported.");

        string name;
        if (request.Recheck || request.Undo)
        {
            if (request.Target is null || NameOf(request.Target) is not { } n) return Unknown();
            name = n;
        }
        else
        {
            if (request.Evidence is not { } ev) return Unknown();
            var located = Locate(ev);
            if (located.Refusal is { } refusal) return refusal;
            name = located.Name!;
        }

        ServiceInfo? info;
        try { info = scm.Read(name); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the service.", ManualUri); }
        if (info is null) return ActionResolution.Ineligible("not_found", "That service is no longer there. Check again.", ManualUri);
        if (Refuse(info, files, products) is { } p) return ActionResolution.Ineligible(p.Code, p.Reason, ManualUri);

        var live = State(info);
        var label = Label_(info);
        var target = Target(info.Name, label);
        var resources = Resources(info.Name);

        if (request.Undo)
        {
            if (request.Target!["expect"] is not JsonObject expect || !Undoable(expect))
                return ActionResolution.Ineligible("not_supported", "Optimus didn't record this service's file, so it can't turn it back on.", ManualUri);
            if (!JsonNode.DeepEquals(live["fingerprint"], expect))
                return ActionResolution.Ineligible("changed_since", "The service's program or settings changed since Optimus turned it off. Turn it on yourself if you trust it.", ManualUri);
            if (request.Parameters!["restore"] is not JsonObject restore || restore.Count != 2 || StartTypeCode(Str(restore["startType"])) is null
                || restore["running"] is not JsonValue rv || !rv.TryGetValue<bool>(out var wasRunning))
                return ActionResolution.Ineligible("invalid_arguments", "This change isn't supported.");
            var after = With(live, Str(restore["startType"])!, wasRunning);
            if (JsonNode.DeepEquals(after, live)) return ActionResolution.Ineligible("already_set", "That service is already set the way it was.");
            target["expect"] = expect.DeepClone();
            return new ActionResolution(true, target, live, after,
                Rows(label, live, after), [new("Where", "Windows services")],
                ["The service's program can run again."], "You can undo this unless something else changes this service first.",
                Privilege: "admin", Flags: [ActionFlags.RunsCode], Resources: resources);
        }

        if (request.Parameters is not { } prm || prm.Count != 2 || Str(prm["startType"]) is not ("manual" or "disabled")
            || prm["stop"] is not JsonValue sv || !sv.TryGetValue<bool>(out var stop))
            return ActionResolution.Ineligible("invalid_arguments", "Choose manual or disabled, and whether to stop it now.");
        var startType = Str(prm["startType"])!;
        // The ordinary change only ever lets a service run less (Automatic → Manual or Disabled, Manual → Disabled).
        // Turning a disabled service back on runs code: only as Optimus's own undo, against its recorded fingerprint.
        if (Rank(startType) > Rank(Str(live["startType"])))
            return ActionResolution.Ineligible("not_supported", "Optimus only turns services down. Turn this one on in Services if you trust it.", ManualUri);
        if (info.RunningDependents.Count > 0)
            return ActionResolution.Ineligible("in_use", "Other running services depend on this one. Turn it off in Services if you're sure.", ManualUri);
        if (live["running"] is null && stop)
            return ActionResolution.Ineligible("unexpected_value", "The service is starting or stopping right now. Check again in a moment.", ManualUri);
        var disabled = With(live, startType, stop ? false : live["running"]?.GetValue<bool>() ?? false);
        if (JsonNode.DeepEquals(disabled, live)) return ActionResolution.Ineligible("already_set", "That service is already set that way.");
        var undoable = Undoable(live["fingerprint"] as JsonObject);
        var tradeoffs = new List<string> { "Features that rely on it wait until something starts it, or stop working while it is disabled." };
        if (stop) tradeoffs.Add("Stopping it now closes whatever it was doing.");
        return new ActionResolution(true, target, live, disabled,
            Rows(label, live, disabled), [new("Where", "Windows services"), new("Undo", undoable ? "Possible while its program is unchanged" : "Not offered: its program can't be read")],
            tradeoffs, undoable ? "You can undo this unless something else changes this service first." : null,
            Privilege: "admin", Flags: stop ? [ActionFlags.DataLoss] : null, Resources: resources, UndoSupported: undoable);
    }

    /// <summary>The service the evidence names (a startup collector service entry), matched by its local key.</summary>
    private (string? Name, ActionResolution? Refusal) Locate(TargetEvidence ev)
    {
        if (ev.CollectorId != StartupCollector || !ev.Subject.StartsWith("startup.service.", StringComparison.Ordinal) || Str(ev.Value["source"]) != "service")
            return (null, Unknown());
        var key = Str(ev.Value["entryKey"]);
        if (!StartupKeys.IsKey(key)) return (null, Unknown());
        IReadOnlyList<string> names;
        try { names = scm.Names(); }
        catch (Exception) { return (null, ActionResolution.Ineligible("read_failed", "Windows didn't list its services.", ManualUri)); }
        var matches = names.Where(n => StartupKeys.Of(n) == key).ToList();
        if (matches.Count == 0) return (null, ActionResolution.Ineligible("not_found", "That service is no longer there. Check again.", ManualUri));
        return matches.Count > 1 ? (null, Unknown()) : (matches[0], null);
    }

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to one of this PC's services.");

    /// <summary>
    /// Drivers and protected services (host and broker): kernel, file-system and other driver types, the compiled Windows,
    /// security, backup and management lists, the hardware-control list. The publisher is read live from the image (or the
    /// svchost ServiceDll), never taken from a caller.
    /// </summary>
    internal static ProtectionResult? Refuse(ServiceInfo info, IFileFacts files, ISecurityProducts? products = null)
    {
        var driver = (info.Type & 0x0F) != 0 || (info.Type & 0x30) == 0;
        var binary = info.ServiceDll ?? info.Image;
        string? publisher = null;
        if (binary is not null)
            try { publisher = files.Publisher(binary); } catch (Exception) { publisher = null; }
        var wsc = (products ?? NoSecurityProducts.Instance).Names();
        return ProtectedTargets.Service(info.Name, info.DisplayName, publisher, info.Image, driver, wsc)
               ?? (info.ServiceDll is { } dll ? ProtectedTargets.Service(info.Name, info.DisplayName, publisher, dll, securityProducts: wsc) : null);
    }

    // ---------------------------------------------------------------- state

    /// <summary>{startType, running, fingerprint}: running is null while the service is starting or stopping.</summary>
    internal JsonObject State(ServiceInfo? info)
    {
        if (info is null) return new JsonObject { ["startType"] = null, ["running"] = null, ["fingerprint"] = null };
        return new JsonObject
        {
            ["startType"] = StartTypeName(info.StartType, info.DelayedAuto),
            ["running"] = info.State switch { 4 => true, 1 => false, _ => null },
            ["fingerprint"] = new JsonObject
            {
                ["name"] = info.Name, ["type"] = info.Type, ["binaryPath"] = info.BinaryPath, ["image"] = info.Image, ["serviceDll"] = info.ServiceDll,
                ["account"] = info.Account,
                ["imageSha256"] = info.Image is null ? null : files.Sha256(info.Image),
                ["dllSha256"] = info.ServiceDll is null ? null : files.Sha256(info.ServiceDll),
            },
        };
    }

    private static JsonObject With(JsonObject state, string startType, bool running)
    {
        var s = (JsonObject)state.DeepClone();
        s["startType"] = startType;
        s["running"] = running;
        return s;
    }

    /// <summary>Undo needs a recorded hash of what the service starts (the image, and its ServiceDll when it has one).</summary>
    internal static bool Undoable(JsonObject? fp) =>
        fp?["imageSha256"] is JsonValue v && v.TryGetValue<string>(out var h) && h.Length == 64
        && (fp["serviceDll"] is null || fp["dllSha256"] is JsonValue dv && dv.TryGetValue<string>(out var dh) && dh.Length == 64);

    /// <summary>How much a start type lets a service run: disabled 0, manual 1, automatic 2 (anything else 3).</summary>
    private static int Rank(string? startType) => startType switch { "disabled" => 0, "manual" => 1, "auto" or "auto_delayed" => 2, _ => 3 };

    /// <summary>True when a change lets the service run more than before: it starts it, or raises its start type.</summary>
    internal static bool Enables(string? beforeStartType, string? afterStartType, bool wasRunning, bool wantRunning) =>
        wantRunning && !wasRunning || Rank(afterStartType) > Rank(beforeStartType);

    internal static string StartTypeName(int startType, bool delayed) => startType switch
    {
        0 => "boot", 1 => "system", 2 => delayed ? "auto_delayed" : "auto", 3 => "manual", 4 => "disabled", _ => "other",
    };

    /// <summary>The start type a write may set: auto (also delayed), manual or disabled. Boot and system are driver types and never written.</summary>
    internal static (int Code, bool Delayed)? StartTypeCode(string? name) => name switch
    {
        "auto" => (2, false), "auto_delayed" => (2, true), "manual" => (3, false), "disabled" => (4, false), _ => null,
    };

    private static string StartText(string? s) => s switch
    {
        "auto" => "Automatic", "auto_delayed" => "Automatic (delayed)", "manual" => "Manual", "disabled" => "Disabled", _ => "Other",
    };

    private static List<PlanRow> Rows(string label, JsonObject before, JsonObject after)
    {
        var rows = new List<PlanRow> { new("Starts", StartText(Str(before["startType"])), StartText(Str(after["startType"]))) };
        var wasRunning = before["running"]?.GetValue<bool>() ?? false;
        var willRun = after["running"]?.GetValue<bool>() ?? false;
        if (wasRunning != willRun) rows.Add(new PlanRow("Running now", wasRunning ? "Yes" : "No", willRun ? "Yes" : "No"));
        rows.Add(new PlanRow("Service", label, label));
        return rows;
    }

    // ---------------------------------------------------------------- execute (broker only)

    /// <summary>
    /// Writes exactly the resolution's After: the start type first (so a stopped service can't be started again by its
    /// recovery settings), then stop or start. Reads back and classifies: verified | failed | indeterminate.
    /// </summary>
    internal ActionOutcome Execute(ActionExecution ex)
    {
        if (NameOf(ex.Target) is not { } name || StartTypeCode(Str(ex.After["startType"])) is not { } code)
            return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        var wasRunning = ex.Before["running"]?.GetValue<bool>() ?? false;
        var wantRunning = ex.After["running"]?.GetValue<bool>() ?? false;
        // A write that lets the service run more (an undo: started again, or a start type out of Disabled or up to
        // Automatic) runs code: only as an undo, and only while its program is still exactly what Optimus recorded, checked
        // right before the write.
        if (Enables(Str(ex.Before["startType"]), Str(ex.After["startType"]), wasRunning, wantRunning) || ex.Target["expect"] is not null)
        {
            JsonObject pre;
            try { pre = State(scm.Read(name)); }
            catch (Exception) { return new ActionOutcome("failed", new JsonObject(), "Windows didn't report the service. Nothing changed.", "read_failed"); }
            if (ex.Target["expect"] is not JsonObject expect || pre["fingerprint"] is not JsonObject fp || !Undoable(fp) || !JsonNode.DeepEquals(fp, expect)
                || !JsonNode.DeepEquals(fp, ex.Before["fingerprint"]))
                return new ActionOutcome("failed", pre, "The service's program or settings changed, so it wasn't turned back on.", "changed_since",
                    "The service changed since it was approved.");
        }
        string? error = null;
        try
        {
            if (Str(ex.After["startType"]) != Str(ex.Before["startType"])) scm.SetStartType(name, code.Code, code.Delayed);
            if (wasRunning && !wantRunning) scm.Stop(name, StopWait);
            else if (!wasRunning && wantRunning) scm.Start(name, StopWait);
        }
        catch (Exception e) { error = e.GetType().Name; }

        JsonObject observed;
        try { observed = State(scm.Read(name)); }
        catch (Exception) { return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report the service after the change.", "unexpected_state", error); }
        if (JsonNode.DeepEquals(observed, ex.After))
        {
            var text = $"The service is set to {StartText(Str(ex.After["startType"])).ToLowerInvariant()}{(wantRunning ? " and running" : wasRunning ? " and stopped" : "")}, read back from Windows.";
            return new ActionOutcome("verified", observed, text);
        }
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, "The service is unchanged.", error is null ? "not_applied" : "write_failed", error);
        return new ActionOutcome("indeterminate", observed, "The service changed in a way that wasn't expected. Check it in Services.", "unexpected_state", error);
    }

    // ---------------------------------------------------------------- shapes

    internal static JsonObject Target(string name, string label) => new() { ["type"] = "service", ["id"] = name.ToUpperInvariant(), ["name"] = name, ["label"] = label };

    /// <summary>The plan target's exact service key name, when it is a well-formed target of this handler.</summary>
    internal static string? NameOf(JsonObject target) =>
        Str(target["type"]) == "service" && Str(target["name"]) is { Length: > 0 and <= 256 } name && !name.Any(c => char.IsControl(c) || c is '\\' or '/')
        && Str(target["id"]) == name.ToUpperInvariant() ? name : null;

    private static string Label_(ServiceInfo info) =>
        info.DisplayName is { Length: > 0 } d && !d.StartsWith('@') && !d.Any(char.IsControl) ? (d.Length > 120 ? d[..120] : d) : info.Name;

    internal static IReadOnlyList<string> Resources(string name) =>
        ["service:" + name.ToUpperInvariant(), @"reg:64:HKLM\SYSTEM\CURRENTCONTROLSET\SERVICES\" + name.ToUpperInvariant()];

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>service.disable in the broker: the same handler code with the broker's own SCM and file reads, plus its own protected lists.</summary>
public sealed class ServiceBrokerHandler(IServiceControl scm, IFileFacts files, ISecurityProducts? products = null) : IBrokerHandler
{
    private readonly ServiceDisableHandler _handler = new(scm, files, products);

    public string ActionId => ServiceDisableHandler.Id;
    public string Version => ServiceDisableHandler.HandlerVersion;
    public string Label => ServiceDisableHandler.LabelText;
    public IReadOnlyList<string> Flags => [];

    public bool IsUndo(BrokerStepRequest step) => step.Target["expect"] is not null;

    public ActionResolution Resolve(BrokerStepRequest step) =>
        _handler.Resolve(new ActionRequest(ActionId, step.Target, step.Parameters, null) { Recheck = true, Undo = IsUndo(step) });

    /// <summary>Second line: the broker reads the service again and applies its own lists to what the resolution names.</summary>
    public ProtectionResult? Protect(ActionResolution r)
    {
        var name = ServiceDisableHandler.Str(r.Target["name"]);
        if (name is null) return new ProtectionResult(ProtectionResult.ProtectedCode, "Optimus can't tell what this service is.");
        var info = scm.Read(name);
        if (info is not null && ServiceDisableHandler.Refuse(info, files, products) is { } live) return live;
        var fp = r.Before["fingerprint"];
        var image = ServiceDisableHandler.Str(fp?["image"]);
        var binary = ServiceDisableHandler.Str(fp?["serviceDll"]) ?? image;
        string? publisher = null;
        if (binary is not null)
            try { publisher = files.Publisher(binary); } catch (Exception) { publisher = null; }
        return ProtectedTargets.Service(name, ServiceDisableHandler.Str(r.Target["label"]), publisher, image,
            fp?["type"] is JsonValue t && t.TryGetValue<int>(out var type) && ((type & 0x0F) != 0 || (type & 0x30) == 0),
            (products ?? NoSecurityProducts.Instance).Names());
    }

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) =>
        _handler.Execute(new ActionExecution("broker", "broker", r.Target, r.Before, r.After, step.Parameters));
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Settings;

namespace OptimizationTool.Windows.Actions.Startup;

/// <summary>
/// startup.disable {targetRef} (contracts/m9-ai-powers.md §3.2): switches off one per-user startup entry (an HKCU Run
/// value or a file in the user's Startup folder) with Explorer's own switch, the binary StartupApproved value Task
/// Manager and Settings write. The entry itself is never touched or deleted. At disable time it records the execution
/// fingerprint (launch configuration + SHA-256 of what it starts); undo re-enables only while that fingerprint still
/// matches, and is not offered when none could be recorded. Machine-wide entries, RunOnce, tasks and services are
/// refused in this phase. Messages and receipts never name the entry (the AI reads them); the rows do (the user does).
/// </summary>
internal sealed class StartupDisableHandler(IRegistryStore store, IStartupFiles files, TimeProvider? time = null, ISecurityProducts? products = null) : IActionHandler
{
    public const string Id = "startup.disable";
    public const string CollectorId = "win.security.startup";
    private const string ManualUri = "ms-settings:startupapps";
    private const string Cu = @"Software\Microsoft\Windows\CurrentVersion";
    internal const string RunKey = Cu + @"\Run";
    internal const string ApprovedRunKey = Cu + @"\Explorer\StartupApproved\Run";
    internal const string ApprovedFolderKey = Cu + @"\Explorer\StartupApproved\StartupFolder";
    private const int MaxName = 260;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string ActionId => Id;
    public const string HandlerVersion = "1.1.0", LabelText = "Turn off a startup app";
    public string Version => HandlerVersion;
    public string Label => LabelText;
    public string Privilege => "none";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "startup";
    public string What => "Stops an app from starting when you sign in, with Windows' own startup switch; the app stays installed.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    public string? TargetKind => "startup entry";

    internal static RegLocation Run(string name) => Run("run", name);
    internal static RegLocation Run(string kind, string name) => new(Hive(kind), 64, RunKey, name);
    internal static RegLocation Approved(string kind, string name) => new(Hive(kind), 64, IsRun(kind) ? ApprovedRunKey : ApprovedFolderKey, name);

    /// <summary>All-users entries (the 64-bit HKLM Run key, the common Startup folder): administrator scope, through the broker (m9 §3.2).</summary>
    internal static bool IsMachine(string kind) => kind is "machine_run" or "machine_folder";
    internal static bool IsRun(string kind) => kind is "run" or "machine_run";
    private static string Hive(string kind) => IsMachine(kind) ? "HKLM" : "HKCU";
    private string? Folder(string kind) => IsMachine(kind) ? files.CommonStartupFolder() : files.UserStartupFolder();

    /// <summary>All-users entries run elevated: this handler is registered in the broker too (m9 §3.5).</summary>
    public bool CanElevate => true;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var (kind, name) = FromTarget(target) ?? throw new ArgumentException("Not a startup entry.", nameof(target));
        return State(kind, name).State;
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    /// <summary>Undo re-enables the same entry, expecting the fingerprint recorded when it was switched off.</summary>
    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (FromTarget(target) is null || before["fingerprint"] is not JsonObject fp || !Undoable(fp)) return null;
        var t = (JsonObject)target.DeepClone();
        t["expect"] = fp.DeepClone();
        return new ActionRequest(Id, t, null, null) { Undo = true };
    }

    // ---------------------------------------------------------------- resolve

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");

        // The target: from evidence (a fresh request), or this handler's own plan target (recheck, undo). A caller's
        // raw target is never trusted: it could name any registry value or file.
        string kind, name;
        string? publisher;
        if (request.Recheck || request.Undo)
        {
            if (request.Target is null || FromTarget(request.Target) is not { } t) return Unknown();
            (kind, name) = t;
            publisher = Str(request.Target["publisher"]);
        }
        else
        {
            if (request.Evidence is not { } ev) return Unknown();
            var located = Locate(ev);
            if (located.Refusal is { } refusal) return refusal;
            (kind, name, publisher) = (located.Kind!, located.Name!, located.Publisher);
        }

        LiveState live;
        try { live = State(kind, name); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the startup entry.", ManualUri); }
        if (!live.Present) return ActionResolution.Ineligible("not_found", "That startup entry is no longer there. Check again.", ManualUri);
        if (live.Approved is null) return ActionResolution.Ineligible("unexpected_value", "Windows' startup switch for it holds a value Optimus doesn't recognise.", ManualUri);

        try
        {
            if (IsRun(kind) && Managed(kind, name)) return ActionResolution.Ineligible("managed", "A policy on this PC controls startup apps.", ManualUri);
        }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report its startup policy.", ManualUri); }

        var label = Label_(kind, name);
        string? localPublisher = null;
        if (live.Image is { } img)
            try { localPublisher = files.Publisher(img); } catch (Exception) { localPublisher = null; }
        var wsc = (products ?? NoSecurityProducts.Instance).Names();
        if ((ProtectedTargets.StartupEntry(label, live.Image, localPublisher, wsc) ?? ProtectedTargets.StartupEntry(label, live.Image, publisher, wsc)) is { } p)
            return ActionResolution.Ineligible(p.Code, p.Reason, ManualUri);

        var target = Target(kind, name, label, publisher);
        var resources = Resources(kind, name);
        var where = kind switch { "run" => "Your startup apps", "folder" => "Your Startup folder", "machine_run" => "Startup apps for everyone on this PC", _ => "The Startup folder for everyone on this PC" };
        string? privilege = IsMachine(kind) ? "admin" : null;

        if (request.Undo)
        {
            // Re-enable only an entry Optimus switched off, only while its launch configuration and file are unchanged.
            if (request.Target!["expect"] is not JsonObject expect || !Undoable(expect))
                return ActionResolution.Ineligible("not_supported", "Optimus didn't record this entry's file, so it can't turn it back on. Use Settings.", ManualUri);
            if (live.Approved == true) return ActionResolution.Ineligible("already_set", "That startup entry is already on.");
            if (!JsonNode.DeepEquals(live.Fingerprint, expect))
                return ActionResolution.Ineligible("changed_since", "Its command or file changed since Optimus switched it off. Turn it on in Settings if you trust it.", ManualUri);
            target["expect"] = expect.DeepClone(); // the approve recheck checks the fingerprint again, inside the lock
            return new ActionResolution(
                Eligible: true, Target: target,
                Before: live.State, After: Enabled(live.State, true),
                Rows: [new PlanRow("Starts when you sign in", "Off", "On"), new PlanRow("Startup entry", label, label)],
                Context: [new("Where", where)],
                Tradeoffs: ["It runs again every time you sign in."],
                UndoConditions: "You can undo this unless something else changes this startup app first.",
                Flags: [ActionFlags.RunsCode], Privilege: privilege,
                Resources: resources);
        }

        if (live.Approved == false) return ActionResolution.Ineligible("already_set", "That startup entry is already off.");
        var undoable = Undoable(live.Fingerprint);
        return new ActionResolution(
            Eligible: true, Target: target,
            Before: live.State, After: Enabled(live.State, false),
            Rows: [new PlanRow("Starts when you sign in", "On", "Off"), new PlanRow("Startup entry", label, label)],
            Context: [new("Where", where), new("Undo", undoable ? "Possible while its file is unchanged" : "Not offered: its file can't be read")],
            Tradeoffs: ["It won't start by itself; open it when you need it.", "Features that rely on it running in the background wait until you open it."],
            UndoConditions: undoable ? "You can undo this unless something else changes this startup app first." : null,
            Resources: resources, Privilege: privilege,
            UndoSupported: undoable);
    }

    /// <summary>The entry the evidence names: a user Run value or a user Startup-folder file, matched by its local key.</summary>
    private (string? Kind, string? Name, string? Publisher, ActionResolution? Refusal) Locate(TargetEvidence ev)
    {
        var v = ev.Value;
        if (ev.CollectorId != CollectorId || !ev.Subject.StartsWith("startup.", StringComparison.Ordinal) || ev.Subject == "startup.summary")
            return (null, null, null, Unknown());
        var source = Str(v["source"]);
        var location = Str(v["location"]);
        var publisher = Str(v["signer"]);
        string kind;
        switch (source)
        {
            case "run" when location == "hkcu":
            case "folder" when location == "user":
                kind = source;
                break;
            // m9 §3.2: all-users entries (administrator scope, through the broker). Only the 64-bit Run key: the 32-bit
            // view (Run32 switch) is left to Task Manager.
            case "run" when location == "hklm" && Str(v["view"]) == "64":
                kind = "machine_run";
                break;
            case "folder" when location == "common":
                kind = "machine_folder";
                break;
            case "run" when location == "hklm":
                return (null, null, null, ActionResolution.Ineligible("unsupported", "Optimus can't switch this 32-bit startup entry off. Use Task Manager.", ManualUri));
            case "runonce" or "task" or "service":
                return (null, null, null, ActionResolution.Ineligible("unsupported", "Optimus can't switch this kind of startup item off.", ManualUri));
            default:
                return (null, null, null, Unknown());
        }
        var key = Str(v["entryKey"]);
        if (!StartupKeys.IsKey(key)) return (null, null, null, Unknown());

        IReadOnlyList<string> names;
        try
        {
            if (IsRun(kind)) names = store.ValueNames(new RegLocation(Hive(kind), 64, RunKey, null));
            else names = Folder(kind) is { } folder ? files.List(folder) : [];
        }
        catch (Exception) { return (null, null, null, ActionResolution.Ineligible("read_failed", "Windows didn't list the startup entries.", ManualUri)); }
        var matches = names.Where(n => StartupKeys.Of(n) == key).ToList();
        if (matches.Count == 0) return (null, null, null, ActionResolution.Ineligible("not_found", "That startup entry is no longer there. Check again.", ManualUri));
        if (matches.Count > 1 || !ValidName(kind, matches[0])) return (null, null, null, Unknown());
        return (kind, matches[0], publisher, null);
    }

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to one of your startup entries.");

    /// <summary>
    /// A policy lists the same Run entry, or turns the Run list off as a whole (DisableCurrentUserRun for per-user
    /// entries, DisableLocalMachineRun for all-users entries).
    /// </summary>
    private bool Managed(string kind, string name) => IsMachine(kind)
        ? store.Exists(new RegLocation("HKLM", 64, Cu + @"\Policies\Explorer\Run", name))
          || store.Exists(new RegLocation("HKLM", 64, Cu + @"\Policies\Explorer", "DisableLocalMachineRun"))
          || store.Exists(new RegLocation("HKCU", 64, Cu + @"\Policies\Explorer", "DisableLocalMachineRun"))
        :
        store.Exists(new RegLocation("HKCU", 64, Cu + @"\Policies\Explorer\Run", name))
        || store.Exists(new RegLocation("HKLM", 64, Cu + @"\Policies\Explorer\Run", name))
        || store.Exists(new RegLocation("HKCU", 64, Cu + @"\Policies\Explorer", "DisableCurrentUserRun"))
        || store.Exists(new RegLocation("HKLM", 64, Cu + @"\Policies\Explorer", "DisableCurrentUserRun"));

    // ---------------------------------------------------------------- live state

    /// <summary>Live state. Approved: true on, false off, null an unrecognised switch value.</summary>
    private sealed record LiveState(bool Present, bool? Approved, JsonObject? Fingerprint, string? Image, JsonObject State);

    /// <summary>
    /// The entry as plans, receipts and drift checks compare it: {present, enabled, fingerprint}. The fingerprint is the
    /// full launch configuration plus the SHA-256 of the file it starts (null when that file can't be read).
    /// </summary>
    private LiveState State(string kind, string name)
    {
        JsonObject? fingerprint = null;
        string? image = null;
        var present = false;
        if (IsRun(kind))
        {
            var value = store.Read(Run(kind, name));
            if (value.Present && value.Type is "string" or "expand_string" && Str(value.Data) is { } command)
            {
                present = true;
                var launch = files.ParseLaunch(command, null);
                image = launch.Image;
                fingerprint = new JsonObject
                {
                    ["kind"] = kind, ["name"] = name, ["valueType"] = value.Type, ["command"] = command,
                };
                AddLaunch(fingerprint, launch);
            }
        }
        else if (Folder(kind) is { } folder)
        {
            var path = Path.Combine(folder, name);
            if (files.Exists(path))
            {
                present = true;
                fingerprint = new JsonObject { ["kind"] = kind, ["name"] = name, ["fileSha256"] = files.Sha256(path) };
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // The complete launch: the target with its arguments (a rundll32 or script-host shortcut runs its
                    // argument) and its working folder (where a relative payload is found).
                    var link = files.ReadShortcut(path);
                    var launch = files.ParseLaunch(link?.Target is { } t ? "\"" + t + "\"" + (link.Arguments is { Length: > 0 } a ? " " + a : "") : null,
                        link?.WorkingDirectory);
                    image = launch.Image;
                    fingerprint["target"] = link?.Target;
                    fingerprint["arguments"] = link?.Arguments;
                    fingerprint["workingDirectory"] = link?.WorkingDirectory;
                    AddLaunch(fingerprint, launch);
                }
                else
                {
                    // A program or script placed in the folder itself: the file is what starts.
                    image = path;
                    fingerprint["image"] = path;
                    fingerprint["imageSha256"] = fingerprint["fileSha256"]?.DeepClone();
                    fingerprint["payload"] = null;
                    fingerprint["payloadSha256"] = null;
                    fingerprint["payloadIdentified"] = true;
                }
            }
        }

        bool? approved = true;
        if (present)
        {
            var sw = store.Read(Approved(kind, name));
            approved = !sw.Present ? true
                : sw.Type == "binary" && Str(sw.Data) is { } b64 && Bytes(b64) is { } bytes ? bytes.Length == 0 || (bytes[0] & 1) == 0
                : null;
        }
        var state = new JsonObject
        {
            ["present"] = present,
            ["enabled"] = present ? approved : null,
            ["fingerprint"] = fingerprint,
        };
        return new LiveState(present, approved, fingerprint, image, state);
    }

    /// <summary>The image and, for a host process, its payload, each with its SHA-256 (only of a fully qualified path).</summary>
    private void AddLaunch(JsonObject fingerprint, LaunchInfo launch)
    {
        fingerprint["image"] = launch.Image;
        fingerprint["imageSha256"] = Hash(launch.Image);
        fingerprint["payload"] = launch.Payload;
        fingerprint["payloadSha256"] = Hash(launch.Payload);
        fingerprint["payloadIdentified"] = launch.PayloadIdentified;
    }

    private string? Hash(string? path) => path is not null && Path.IsPathFullyQualified(path) ? files.Sha256(path) : null;

    /// <summary>
    /// Undo needs a recorded hash of what the entry starts: the image and, when it is a host process (rundll32, a script
    /// host, …), the payload it runs. A fingerprint whose payload couldn't be identified, or that predates the payload
    /// fields, is not undoable.
    /// </summary>
    internal static bool Undoable(JsonObject? fp) =>
        fp?["imageSha256"] is JsonValue v && v.TryGetValue<string>(out var h) && h.Length == 64
        && fp["payloadIdentified"] is JsonValue iv && iv.TryGetValue<bool>(out var identified) && identified
        && (fp["payload"] is null || fp["payloadSha256"] is JsonValue pv && pv.TryGetValue<string>(out var ph) && ph.Length == 64);

    private static JsonObject Enabled(JsonObject state, bool enabled)
    {
        var s = (JsonObject)state.DeepClone();
        s["enabled"] = enabled;
        return s;
    }

    // ---------------------------------------------------------------- execute

    internal ActionOutcome Execute(ActionExecution ex)
    {
        if (FromTarget(ex.Target) is not { } t || ex.After["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var enable))
            return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        var (kind, name) = t;

        // Turning an entry on runs code: only as an undo, and only while what it starts is still exactly what Optimus
        // recorded, checked right before the write (host and broker), not just at preview and approval.
        if (enable)
        {
            LiveState pre;
            try { pre = State(kind, name); }
            catch (Exception) { return new ActionOutcome("failed", new JsonObject(), "Windows didn't report the startup entry. Nothing changed.", "read_failed"); }
            if (ex.Target["expect"] is not JsonObject expect || !Undoable(pre.Fingerprint) || !JsonNode.DeepEquals(pre.Fingerprint, expect)
                || !JsonNode.DeepEquals(pre.Fingerprint, ex.Before["fingerprint"]))
                return new ActionOutcome("failed", pre.State, "Its command or file changed, so it wasn't turned on.", "changed_since",
                    "The entry changed since it was approved.");
        }

        // Explorer's switch: 12 bytes, first byte 2 = on, 3 = off; the off value carries the time it was switched off.
        var data = new byte[12];
        data[0] = enable ? (byte)2 : (byte)3;
        if (!enable) BitConverter.GetBytes(_time.GetUtcNow().UtcDateTime.ToFileTimeUtc()).CopyTo(data, 4);

        string? error = null;
        try { store.Write(Approved(kind, name), "binary", JsonValue.Create(Convert.ToBase64String(data))); }
        catch (Exception e) { error = e.GetType().Name; }

        JsonObject observed;
        try { observed = State(kind, name).State; }
        catch (Exception) { return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report the startup entry after the change.", "unexpected_state", error); }

        var word = enable ? "on" : "off";
        if (JsonNode.DeepEquals(observed, ex.After))
        {
            var text = $"The startup entry is {word}, read back from Windows.";
            if (!enable && !Undoable(ex.After["fingerprint"] as JsonObject))
                text += " Optimus can't offer Undo for it; you can turn it back on in Settings, Apps, Startup.";
            return new ActionOutcome("verified", observed, text);
        }
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, $"The startup entry is still {(enable ? "off" : "on")}.", error is null ? "not_applied" : "write_failed", error);
        return new ActionOutcome("indeterminate", observed, "The startup entry changed in a way that wasn't expected.", "unexpected_state", error);
    }

    // ---------------------------------------------------------------- shapes

    private static JsonObject Target(string kind, string name, string label, string? publisher) => new()
    {
        ["type"] = "startup_entry", ["id"] = kind + ":" + StartupKeys.Of(name), ["kind"] = kind, ["name"] = name, ["label"] = label, ["publisher"] = publisher,
    };

    /// <summary>The plan target's kind and exact name, when it is a well-formed target of this handler.</summary>
    internal static (string Kind, string Name)? FromTarget(JsonObject target)
    {
        if (Str(target["type"]) != "startup_entry" || Str(target["kind"]) is not ("run" or "folder" or "machine_run" or "machine_folder") || Str(target["name"]) is not { } name) return null;
        var kind = Str(target["kind"])!;
        if (!ValidName(kind, name) || Str(target["id"]) != kind + ":" + StartupKeys.Of(name)) return null;
        return (kind, name);
    }

    /// <summary>A value name, or a plain file name directly in the Startup folder (no separators, no "..", no control characters).</summary>
    private static bool ValidName(string kind, string name) =>
        name.Length <= MaxName && !name.Any(char.IsControl) && name.Trim().Length > 0
        && (IsRun(kind) || name.IndexOfAny(['\\', '/', ':']) < 0 && name is not ("." or "..") && name.Trim() == name);

    private static string Label_(string kind, string name) =>
        !IsRun(kind) ? Path.GetFileNameWithoutExtension(name) is { Length: > 0 } n ? n : name : name.Length == 0 ? "(Default)" : name;

    /// <summary>The switch value it writes and the entry it reads (m9 §3.1 canonical keys).</summary>
    private IReadOnlyList<string> Resources(string kind, string name)
    {
        var approved = Approved(kind, name);
        var keys = new List<string> { "reg:" + $"64:{approved.Hive}\\{approved.Key}\\{name}".ToUpperInvariant() };
        if (IsRun(kind)) keys.Add("reg:" + $"64:{approved.Hive}\\{RunKey}\\{name}".ToUpperInvariant());
        else if (Folder(kind) is { } folder) keys.Add("file:" + Path.Combine(folder, name).ToUpperInvariant());
        return keys;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static byte[]? Bytes(string b64)
    {
        try { return Convert.FromBase64String(b64); } catch (FormatException) { return null; }
    }
}

/// <summary>The startup domain's handlers (startup.disable, user scope).</summary>
public sealed class StartupActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) => [new StartupDisableHandler(new WindowsRegistryStore(), new WindowsStartupFiles(), products: WindowsSecurityProducts.Shared)];
}

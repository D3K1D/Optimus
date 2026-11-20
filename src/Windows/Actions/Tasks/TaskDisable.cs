using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Startup;
using OptimizationTool.Windows.Security.Common;
using ProtectionResult = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Windows.Actions.Tasks;

/// <summary>One action of a scheduled task. Kind: exec | com_handler | other. Image: the program an exec action starts (parsed as text).</summary>
public sealed record TaskActionInfo(string Kind, string? Path, string? Arguments, string? WorkingDirectory, string? Image);

/// <summary>A scheduled task's launch configuration. Triggers: the trigger type names (logon, boot, time, …).</summary>
/// <remarks>
/// GroupId: the principal's group (a group-activated task runs for its members). PrincipalRead: every principal field that
/// decides as whom it runs could be read; false means undo is not offered.
/// </remarks>
public sealed record TaskInfo(string Path, bool Enabled, string? UserId, int RunLevel, int LogonType, IReadOnlyList<TaskActionInfo> Actions, IReadOnlyList<string> Triggers,
    string? GroupId = null, bool PrincipalRead = true);

/// <summary>
/// The Task Scheduler seam (m9 §3.2 task.disable): list task paths, read one task, switch it on or off. Only the elevated
/// broker calls the writer. Tests use a fake; the real one (<see cref="WindowsTaskScheduler"/>) has never been run.
/// </summary>
public interface ITaskScheduler
{
    /// <summary>Full paths of the tasks outside \Microsoft\ (for matching evidence).</summary>
    IReadOnlyList<string> Paths();
    /// <summary>The task, or null when it does not exist.</summary>
    TaskInfo? Read(string path);
    void SetEnabled(string path, bool enabled);
}

/// <summary>
/// task.disable {targetRef} (contracts/m9-ai-powers.md §3.2): switches off one scheduled task outside \Microsoft\ (and not
/// on the protected lists). Administrator, through the broker (also for the user's own tasks in this batch). The execution
/// fingerprint is the task path, every action (program, arguments, folder and the SHA-256 of the program), the principal
/// and a summary of its triggers; undo re-enables only while it matches, and is not offered when an action's program
/// could not be hashed (or the task runs no program, e.g. a COM handler).
/// </summary>
public sealed class TaskDisableHandler(ITaskScheduler scheduler, IFileFacts files, ISecurityProducts? products = null) : IActionHandler
{
    public const string Id = "task.disable";
    public const string HandlerVersion = "1.1.0", LabelText = "Turn off a scheduled task";
    private const string StartupCollector = "win.security.startup";

    public string ActionId => Id;
    public string Version => HandlerVersion;
    public string Label => LabelText;
    public string Privilege => "admin";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "startup";
    public string What => "Turns off a scheduled task that starts a program at sign-in or startup; Windows' own tasks are never touched.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    public string? TargetKind => "scheduled task";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var path = PathOf(target) ?? throw new ArgumentException("Not a task.", nameof(target));
        return State(scheduler.Read(path));
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "admin_only", "This runs through the administrator helper."));

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (PathOf(target) is null || before["fingerprint"] is not JsonObject fp || !Undoable(fp)) return null;
        var t = (JsonObject)target.DeepClone();
        t["expect"] = fp.DeepClone();
        return new ActionRequest(Id, t, null, null) { Undo = true };
    }

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");

        string path;
        if (request.Recheck || request.Undo)
        {
            if (request.Target is null || PathOf(request.Target) is not { } p) return Unknown();
            path = p;
        }
        else
        {
            if (request.Evidence is not { } ev) return Unknown();
            var located = Locate(ev);
            if (located.Refusal is { } refusal) return refusal;
            path = located.Path!;
        }

        TaskInfo? info;
        try { info = scheduler.Read(path); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the task."); }
        if (info is null) return ActionResolution.Ineligible("not_found", "That task is no longer there. Check again.");
        if (Refuse(info, files, products) is { } prot) return ActionResolution.Ineligible(prot.Code, prot.Reason);

        var live = State(info);
        var label = LabelOf(info.Path);
        var target = Target(info.Path, label);
        var resources = new[] { "task:" + info.Path.ToUpperInvariant() };

        if (request.Undo)
        {
            if (request.Target!["expect"] is not JsonObject expect || !Undoable(expect))
                return ActionResolution.Ineligible("not_supported", "Optimus didn't record this task's program, so it can't turn it back on.");
            if (info.Enabled) return ActionResolution.Ineligible("already_set", "That task is already on.");
            if (!JsonNode.DeepEquals(live["fingerprint"], expect))
                return ActionResolution.Ineligible("changed_since", "The task's program or settings changed since Optimus turned it off. Turn it on in Task Scheduler if you trust it.");
            target["expect"] = expect.DeepClone();
            return new ActionResolution(true, target, live, Enabled(live, true),
                [new PlanRow("Runs on schedule", "Off", "On"), new PlanRow("Task", label, label)], [new("Where", "Task Scheduler")],
                ["It runs again on its schedule."], "You can undo this unless something else changes this task first.",
                Privilege: "admin", Flags: [ActionFlags.RunsCode], Resources: resources);
        }

        if (!info.Enabled) return ActionResolution.Ineligible("already_set", "That task is already off.");
        var undoable = Undoable(live["fingerprint"] as JsonObject);
        return new ActionResolution(true, target, live, Enabled(live, false),
            [new PlanRow("Runs on schedule", "On", "Off"), new PlanRow("Task", label, label)],
            [new("Where", "Task Scheduler"), new("Undo", undoable ? "Possible while its program is unchanged" : "Not offered: its program can't be read")],
            ["It won't start by itself; the app it belongs to may expect it."], undoable ? "You can undo this unless something else changes this task first." : null,
            Privilege: "admin", Resources: resources, UndoSupported: undoable);
    }

    private (string? Path, ActionResolution? Refusal) Locate(TargetEvidence ev)
    {
        if (ev.CollectorId != StartupCollector || !ev.Subject.StartsWith("startup.task.", StringComparison.Ordinal) || Str(ev.Value["source"]) != "task")
            return (null, Unknown());
        var key = Str(ev.Value["entryKey"]);
        if (!StartupKeys.IsKey(key)) return (null, Unknown());
        IReadOnlyList<string> paths;
        try { paths = scheduler.Paths(); }
        catch (Exception) { return (null, ActionResolution.Ineligible("read_failed", "Windows didn't list its scheduled tasks.")); }
        var matches = paths.Where(p => StartupKeys.Of(p) == key).ToList();
        if (matches.Count == 0)
            // A \Microsoft\ task is never listed: say so rather than "gone" when the evidence names one.
            return (null, Str(ev.Value["location"]) is { } loc && ProtectedTargets.NormalTaskPath(loc).StartsWith(@"\microsoft", StringComparison.Ordinal)
                ? ActionResolution.Ineligible(ProtectionResult.ProtectedCode, "This is one of Windows' own tasks.")
                : ActionResolution.Ineligible("not_found", "That task is no longer there. Check again."));
        return matches.Count > 1 ? (null, Unknown()) : (matches[0], null);
    }

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to one of this PC's scheduled tasks.");

    /// <summary>\Microsoft\ tasks and protected targets (host and broker): each action's program and its publisher, read live.</summary>
    internal static ProtectionResult? Refuse(TaskInfo info, IFileFacts files, ISecurityProducts? products = null)
    {
        if (ProtectedTargets.Task(info.Path) is { } byPath) return byPath;
        foreach (var a in info.Actions)
        {
            string? publisher = null;
            if (a.Image is { } image)
                try { publisher = files.Publisher(image); } catch (Exception) { publisher = null; }
            if (ProtectedTargets.Task(info.Path, a.Image, publisher, (products ?? NoSecurityProducts.Instance).Names()) is { } p) return p;
        }
        return null;
    }

    /// <summary>{enabled, fingerprint}.</summary>
    internal JsonObject State(TaskInfo? info)
    {
        if (info is null) return new JsonObject { ["enabled"] = null, ["fingerprint"] = null };
        return new JsonObject
        {
            ["enabled"] = info.Enabled,
            ["fingerprint"] = new JsonObject
            {
                ["path"] = info.Path,
                ["actions"] = new JsonArray([.. info.Actions.Take(32).Select(a => (JsonNode?)Action(a))]),
                ["principal"] = new JsonObject
                {
                    ["userId"] = info.UserId, ["groupId"] = info.GroupId, ["runLevel"] = info.RunLevel, ["logonType"] = info.LogonType, ["read"] = info.PrincipalRead,
                },
                ["triggers"] = new JsonArray([.. info.Triggers.Take(32).Select(t => (JsonNode?)t)]),
            },
        };
    }

    private static readonly PathRoots Roots = PathRoots.FromEnvironment();

    /// <summary>
    /// One action: program, arguments, folder, the program's SHA-256 and, when the program is a host process (rundll32,
    /// regsvr32, a script host, PowerShell -File), the payload file it runs and its SHA-256 (identified: false when the
    /// arguments can't be tied to one file).
    /// </summary>
    private JsonObject Action(TaskActionInfo a)
    {
        var (payload, identified) = a.Kind == "exec"
            ? LaunchString.HostedPayload(a.Image, a.Arguments, a.WorkingDirectory, File.Exists, Environment.SystemDirectory, Roots)
            : (null, false);
        return new JsonObject
        {
            ["kind"] = a.Kind, ["path"] = a.Path, ["arguments"] = a.Arguments, ["workingDirectory"] = a.WorkingDirectory, ["image"] = a.Image,
            ["imageSha256"] = a.Image is null ? null : files.Sha256(a.Image),
            ["payload"] = payload, ["payloadSha256"] = payload is null ? null : files.Sha256(payload), ["payloadIdentified"] = identified,
        };
    }

    /// <summary>
    /// Undo needs at least one program action and a recorded hash for every action (a COM handler or unreadable program:
    /// none), for a host process the hash of its identified payload, and a principal read in full, its group included (a
    /// fingerprint without groupId predates it and is not undoable).
    /// </summary>
    internal static bool Undoable(JsonObject? fp) =>
        fp?["principal"] is JsonObject principal && principal.ContainsKey("groupId")
        && principal["read"] is JsonValue rv && rv.TryGetValue<bool>(out var read) && read
        && fp["actions"] is JsonArray { Count: > 0 } actions
        && actions.All(a => a is JsonObject o && Str(o["kind"]) == "exec" && o["imageSha256"] is JsonValue v && v.TryGetValue<string>(out var h) && h.Length == 64
            && o["payloadIdentified"] is JsonValue iv && iv.TryGetValue<bool>(out var identified) && identified
            && (o["payload"] is null || o["payloadSha256"] is JsonValue pv && pv.TryGetValue<string>(out var ph) && ph.Length == 64));

    private static JsonObject Enabled(JsonObject state, bool enabled)
    {
        var s = (JsonObject)state.DeepClone();
        s["enabled"] = enabled;
        return s;
    }

    internal ActionOutcome Execute(ActionExecution ex)
    {
        if (PathOf(ex.Target) is not { } path || ex.After["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var enable))
            return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        // Re-enabling runs code: only as an undo, and only while the task is still exactly what Optimus recorded, checked
        // right before the write.
        if (enable)
        {
            JsonObject pre;
            try { pre = State(scheduler.Read(path)); }
            catch (Exception) { return new ActionOutcome("failed", new JsonObject(), "Task Scheduler didn't report the task. Nothing changed.", "read_failed"); }
            if (ex.Target["expect"] is not JsonObject expect || pre["fingerprint"] is not JsonObject fp || !Undoable(fp) || !JsonNode.DeepEquals(fp, expect)
                || !JsonNode.DeepEquals(fp, ex.Before["fingerprint"]))
                return new ActionOutcome("failed", pre, "The task changed, so it wasn't turned back on.", "changed_since", "The task changed since it was approved.");
        }
        string? error = null;
        try { scheduler.SetEnabled(path, enable); }
        catch (Exception e) { error = e.GetType().Name; }
        JsonObject observed;
        try { observed = State(scheduler.Read(path)); }
        catch (Exception) { return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report the task after the change.", "unexpected_state", error); }
        var word = enable ? "on" : "off";
        if (JsonNode.DeepEquals(observed, ex.After)) return new ActionOutcome("verified", observed, $"The task is {word}, read back from Task Scheduler.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, $"The task is still {(enable ? "off" : "on")}.", error is null ? "not_applied" : "write_failed", error);
        return new ActionOutcome("indeterminate", observed, "The task changed in a way that wasn't expected.", "unexpected_state", error);
    }

    internal static JsonObject Target(string path, string label) => new() { ["type"] = "task", ["id"] = path.ToUpperInvariant(), ["path"] = path, ["label"] = label };

    /// <summary>A well-formed task path: a leading backslash, no empty or dot segments, no control characters.</summary>
    internal static string? PathOf(JsonObject target)
    {
        if (Str(target["type"]) != "task" || Str(target["path"]) is not { Length: > 1 and <= 512 } path || path[0] != '\\' || path.Any(char.IsControl)) return null;
        var parts = path[1..].Split('\\');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Trim() != p)) return null;
        return Str(target["id"]) == path.ToUpperInvariant() ? path : null;
    }

    private static string LabelOf(string path)
    {
        var name = path[(path.LastIndexOf('\\') + 1)..];
        return name.Length > 120 ? name[..120] : name;
    }

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>task.disable in the broker: the same code with the broker's own Task Scheduler and file reads, plus its own lists.</summary>
public sealed class TaskBrokerHandler(ITaskScheduler scheduler, IFileFacts files, ISecurityProducts? products = null) : IBrokerHandler
{
    private readonly TaskDisableHandler _handler = new(scheduler, files, products);

    public string ActionId => TaskDisableHandler.Id;
    public string Version => TaskDisableHandler.HandlerVersion;
    public string Label => TaskDisableHandler.LabelText;
    public IReadOnlyList<string> Flags => [];

    public bool IsUndo(BrokerStepRequest step) => step.Target["expect"] is not null;

    public ActionResolution Resolve(BrokerStepRequest step) =>
        _handler.Resolve(new ActionRequest(ActionId, step.Target, step.Parameters, null) { Recheck = true, Undo = IsUndo(step) });

    public ProtectionResult? Protect(ActionResolution r)
    {
        var path = TaskDisableHandler.Str(r.Target["path"]);
        if (path is null) return new ProtectionResult(ProtectionResult.ProtectedCode, "Optimus can't tell what this task is.");
        if (scheduler.Read(path) is { } live && TaskDisableHandler.Refuse(live, files, products) is { } p) return p;
        foreach (var a in r.Before["fingerprint"]?["actions"] as JsonArray ?? [])
        {
            var image = TaskDisableHandler.Str(a?["image"]);
            string? publisher = null;
            if (image is not null)
                try { publisher = files.Publisher(image); } catch (Exception) { publisher = null; }
            if (ProtectedTargets.Task(path, image, publisher, (products ?? NoSecurityProducts.Instance).Names()) is { } q) return q;
        }
        return ProtectedTargets.Task(path);
    }

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) =>
        _handler.Execute(new ActionExecution("broker", "broker", r.Target, r.Before, r.After, step.Parameters));
}

/// <summary>
/// Task Scheduler 2.0 through its COM API (Schedule.Service, late bound; NEVER RUN on the development PC). Reads the
/// registered task's definition (actions, principal, triggers); the one write is IRegisteredTask.Enabled.
/// </summary>
public sealed class WindowsTaskScheduler : ITaskScheduler
{
    private const int ScanCap = 3000;
    private readonly PathRoots _roots = PathRoots.FromEnvironment();

    public IReadOnlyList<string> Paths()
    {
        var list = new List<string>();
        Session(service =>
        {
            var stack = new Stack<object>();
            stack.Push(Automation.Call(service, "GetFolder", "\\")!);
            while (stack.Count > 0 && list.Count < ScanCap)
            {
                var folder = stack.Pop();
                try
                {
                    var folderPath = Automation.Get(folder, "Path") as string ?? "\\";
                    if (ProtectedTargets.NormalTaskPath(folderPath).StartsWith(@"\microsoft", StringComparison.Ordinal)) continue;
                    var tasks = Automation.Call(folder, "GetTasks", 1 /* TASK_ENUM_HIDDEN */)!;
                    foreach (var t in Automation.Items(tasks, ScanCap))
                    {
                        if (Automation.Get(t, "Path") is string p && p.Length > 0) list.Add(p);
                        Automation.Release(t);
                    }
                    Automation.Release(tasks);
                    var subs = Automation.Call(folder, "GetFolders", 0)!;
                    foreach (var s in Automation.Items(subs, 2000)) stack.Push(s);
                    Automation.Release(subs);
                }
                finally { Automation.Release(folder); }
            }
            while (stack.Count > 0) Automation.Release(stack.Pop());
        });
        return list;
    }

    public TaskInfo? Read(string path)
    {
        TaskInfo? info = null;
        Session(service =>
        {
            var task = Find(service, path);
            if (task is null) return;
            object? def = null;
            try
            {
                def = Automation.Get(task, "Definition")!;
                var actions = new List<TaskActionInfo>();
                var col = Automation.Get(def, "Actions")!;
                foreach (var a in Automation.Items(col, 32))
                {
                    var type = Convert.ToInt32(Automation.Get(a, "Type"), CultureInfo.InvariantCulture);
                    if (type == 0)
                    {
                        var exe = Automation.Get(a, "Path") as string;
                        actions.Add(new TaskActionInfo("exec", exe, Automation.Get(a, "Arguments") as string, Automation.Get(a, "WorkingDirectory") as string,
                            LaunchString.Parse(exe, _roots, File.Exists, Environment.SystemDirectory).Image));
                    }
                    else actions.Add(new TaskActionInfo(type == 5 ? "com_handler" : "other", null, null, null, null));
                    Automation.Release(a);
                }
                Automation.Release(col);
                var triggers = new List<string>();
                var tcol = Automation.Get(def, "Triggers")!;
                foreach (var t in Automation.Items(tcol, 32))
                {
                    triggers.Add(Convert.ToInt32(Automation.Get(t, "Type"), CultureInfo.InvariantCulture) switch
                    {
                        0 => "event", 1 => "time", 2 => "daily", 3 => "weekly", 4 => "monthly", 5 => "monthly_dow", 6 => "idle", 7 => "registration",
                        8 => "boot", 9 => "logon", 11 => "session_state_change", _ => "other",
                    });
                    Automation.Release(t);
                }
                Automation.Release(tcol);
                var principal = Automation.Get(def, "Principal")!;
                var user = Automation.Get(principal, "UserId") as string;
                var runLevel = Convert.ToInt32(Automation.Get(principal, "RunLevel"), CultureInfo.InvariantCulture);
                var logonType = Convert.ToInt32(Automation.Get(principal, "LogonType"), CultureInfo.InvariantCulture);
                string? group = null;
                var principalRead = true;
                try { group = Automation.Get(principal, "GroupId") as string; }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException or MissingMethodException) { principalRead = false; }
                Automation.Release(principal);
                info = new TaskInfo(Automation.Get(task, "Path") as string ?? path, Automation.Get(task, "Enabled") is true, user, runLevel, logonType, actions, triggers,
                    group, principalRead);
            }
            finally
            {
                Automation.Release(def);
                Automation.Release(task);
            }
        });
        return info;
    }

    public void SetEnabled(string path, bool enabled) => Session(service =>
    {
        var task = Find(service, path) ?? throw new FileNotFoundException("The task is gone.");
        try { task.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, task, [enabled], CultureInfo.InvariantCulture); }
        finally { Automation.Release(task); }
    });

    private static object? Find(object service, string path)
    {
        var slash = path.LastIndexOf('\\');
        var folderPath = slash <= 0 ? "\\" : path[..slash];
        object? folder = null;
        try
        {
            folder = Automation.Call(service, "GetFolder", folderPath);
            return folder is null ? null : Automation.Call(folder, "GetTask", path[(slash + 1)..]);
        }
        catch (TargetInvocationException ex) when (Automation.HResult(ex) is unchecked((int)0x80070002) or unchecked((int)0x80070003)) { return null; }
        finally { Automation.Release(folder); }
    }

    private static void Session(Action<object> work)
    {
        object? service = null;
        try
        {
            service = Automation.Create("Schedule.Service");
            Automation.Call(service, "Connect");
            work(service);
        }
        finally { Automation.Release(service); }
    }
}

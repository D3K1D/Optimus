using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Protection;

namespace OptimizationTool.Windows.Actions.Processes;

/// <summary>
/// process.end {targetRef} (contracts/m9-ai-powers.md §3.2): ends one of the user's own processes, named by process
/// evidence from the performance sample. Identity is PID + creation time + image path, checked at preview, again in the
/// lock, and once more on the handle that ends it. Refuses protected processes (Windows, shell, security, Optimus),
/// processes of another account or session, and elevated ones. Irreversible, may lose unsaved work, no undo.
/// Messages and receipts never name the process (the AI reads them); the rows do (the user does).
/// </summary>
internal sealed class EndProcessHandler(IProcessControl processes, ISecurityProducts? products = null, IFileFacts? files = null) : IActionHandler
{
    private string? PublisherOf(string path)
    {
        try { return files?.Publisher(path); } catch (Exception) { return null; }
    }

    public const string Id = "process.end";
    public const string CollectorId = "win.perf.sample";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public string ActionId => Id;
    public string Version => "1.1.0";
    public string Label => "End a process";
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "setting";
    public string Group => "apps";
    public string What => "Ends one of your own running programs; unsaved work in it is lost.";
    public IReadOnlyList<string> Flags => [ActionFlags.Irreversible, ActionFlags.DataLoss];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    public string? TargetKind => "process (from a performance sample)";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var id = FromTarget(target) ?? throw new ArgumentException("Not a process target.", nameof(target));
        return State(id, processes.Query(id.Pid));
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;

    private sealed record Identity(uint Pid, long CreateTime, string? ImagePath);

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");

        // The process: from sample evidence (PID, creation time, image name), or this handler's own plan target in the
        // lock (PID, creation time, full image path). A caller's raw target is never trusted.
        Identity id;
        string? imageName = null;
        if (request.Recheck)
        {
            if (request.Target is null || FromTarget(request.Target) is not { ImagePath: not null } t) return Unknown();
            id = t;
        }
        else
        {
            if (request.Undo || request.Evidence is not { } ev || ev.CollectorId != CollectorId || !ev.Subject.StartsWith("perf.process.", StringComparison.Ordinal))
                return Unknown();
            var v = ev.Value;
            if (Long(v["pid"]) is not { } pid || pid is <= 4 or > uint.MaxValue || Long(v["createTime"]) is not { } created || created <= 0
                || v["image"] is not JsonValue iv || !iv.TryGetValue<string>(out var image) || image.Length is 0 or > 260)
                return Unknown();
            id = new Identity((uint)pid, created, null);
            imageName = image;
        }

        ProcessInfo? live;
        try { live = processes.Query(id.Pid); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the process."); }
        if (live is null || live.CreateTime != id.CreateTime) return ActionResolution.Ineligible("not_found", "That process has already ended.");
        if (id.ImagePath is not null && !string.Equals(live.ImagePath, id.ImagePath, StringComparison.OrdinalIgnoreCase))
            return ActionResolution.Ineligible("not_found", "That process has already ended.");
        if (imageName is not null && !string.Equals(FileName(live.ImagePath), imageName, StringComparison.OrdinalIgnoreCase))
            return Unknown(); // same PID and start, another image: the evidence doesn't describe it

        if (live.Pid == processes.SelfPid) return ActionResolution.Ineligible(Protection.Protection.ProtectedCode, "This is part of Optimus.");
        if (ProtectedTargets.Process(live.ImagePath, PublisherOf(live.ImagePath), (products ?? NoSecurityProducts.Instance).Names()) is { } p) return ActionResolution.Ineligible(p.Code, p.Reason);
        if (live.SessionId != processes.CurrentSession) return ActionResolution.Ineligible("other_session", "That process runs in another session.");
        if (live.UserSid is null || processes.CurrentUserSid is not { } me || !string.Equals(live.UserSid, me, StringComparison.OrdinalIgnoreCase))
            return ActionResolution.Ineligible("other_user", "That process runs under another account.");
        if (live.Elevated != false) return ActionResolution.Ineligible("elevated", "That process runs as administrator; Optimus only ends your own ordinary processes.");

        var label = FileName(live.ImagePath);
        var target = new JsonObject
        {
            ["type"] = "process", ["id"] = $"{live.Pid}:{live.CreateTime}", ["pid"] = live.Pid, ["createTime"] = live.CreateTime,
            ["imagePath"] = live.ImagePath, ["label"] = label,
        };
        return new ActionResolution(
            Eligible: true, Target: target,
            Before: State(new Identity(live.Pid, live.CreateTime, live.ImagePath), live), After: new JsonObject { ["running"] = false },
            Rows: [new PlanRow("Process", label, "Ended")],
            Context: [new("Started", DateTime.FromFileTimeUtc(live.CreateTime).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))],
            Tradeoffs: ["Unsaved work in it is lost.", "It may start again by itself."],
            UndoConditions: null,
            Resources: [$"process:{live.Pid}:{live.CreateTime}"]);
    }

    internal ActionOutcome Execute(ActionExecution ex)
    {
        if (FromTarget(ex.Target) is not { ImagePath: not null } id) return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        TerminateResult result;
        try { result = processes.Terminate(id.Pid, id.CreateTime, id.ImagePath, Wait); }
        catch (Exception e) { result = new TerminateResult("error", e.HResult); }

        JsonObject observed;
        try { observed = State(id, processes.Query(id.Pid)); }
        catch (Exception) { return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report the process after the change.", "unexpected_state", result.Status); }

        if (JsonNode.DeepEquals(observed, ex.After))
            return new ActionOutcome("verified", observed, result.Status == "ended" ? "The process has ended, read back from Windows." : "The process is no longer running, read back from Windows.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, "The process is still running.", result.Status is "denied" ? "access_denied" : result.Status == "timeout" ? "timeout" : "not_applied",
                result.Win32Error == 0 ? null : $"Windows error {result.Win32Error}");
        return new ActionOutcome("indeterminate", observed, "The process changed in a way that wasn't expected.", "unexpected_state");
    }

    /// <summary>{running, pid, createTime, imagePath} while this exact instance runs; {running:false} once it is gone.</summary>
    private static JsonObject State(Identity id, ProcessInfo? live) =>
        live is not null && live.CreateTime == id.CreateTime && (id.ImagePath is null || string.Equals(live.ImagePath, id.ImagePath, StringComparison.OrdinalIgnoreCase))
            ? new JsonObject { ["running"] = true, ["pid"] = live.Pid, ["createTime"] = live.CreateTime, ["imagePath"] = live.ImagePath }
            : new JsonObject { ["running"] = false };

    private static Identity? FromTarget(JsonObject t)
    {
        if (Str(t["type"]) != "process" || Long(t["pid"]) is not { } pid || pid is <= 4 or > uint.MaxValue || Long(t["createTime"]) is not { } created || created <= 0)
            return null;
        var image = t["imagePath"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length is > 0 and <= 1024 ? s : null;
        return Str(t["id"]) == $"{pid}:{created}" ? new Identity((uint)pid, created, image) : null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to one running process from its sample.");

    /// <summary>An integral JSON number, however the node holds it (a parsed element or a CLR uint/long).</summary>
    private static long? Long(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;

    private static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}

/// <summary>The process domain's handlers (process.end).</summary>
public sealed class ProcessActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) => [new EndProcessHandler(new WindowsProcessControl(), WindowsSecurityProducts.Shared, new WindowsFileFacts())];
}

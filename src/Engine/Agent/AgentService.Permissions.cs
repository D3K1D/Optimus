using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M9 §1 (contracts/m9-ai-powers.md): the user's AI permissions record, ai.getPermissions / ai.setPermissions, the grant a
// task gets when agent.start names none, and what a save does to the tasks already running (narrowing ends them all,
// widening updates in-app and headless grants in place, paired tasks never widen).
public sealed partial class AgentService
{
    internal static Permissions ReadPermissions(Db db) => Permissions.FromStored(Settings.Get(db, Permissions.Key));

    /// <summary>
    /// The grant the record gives a new task (m9 §1.3): checks → every diagnostic available on this PC; names from
    /// see.names. <paramref name="toolsOff"/> (an explain-only account) forces checks, changes, scripts and web off.
    /// </summary>
    private Grant GrantFromPermissions(Permissions record, bool toolsOff)
    {
        var diagnostics = record.Checks && !toolsOff ? AvailableDiagnostics().Select(d => d.Text("id")).ToList() : [];
        var changes = toolsOff ? "off" : record.Changes;
        return new Grant(diagnostics, record.Names, new Limits(), 900, changes, record.Specs, record.History,
            Scripts: !toolsOff && changes == "fix" && record.Scripts, Web: !toolsOff && record.Web, PermissionsRevision: record.Revision,
            Admin: !toolsOff && record.Admin);
    }

    /// <summary>
    /// What the selected provider (the onboarding choice) can do with the record. tools: false for an explain-only
    /// account. web: the selected provider's research availability (§6.4). scripts: tools and this host's script path (§5).
    /// admin: the --enable-admin-actions flag (§17.1); the record keeps do.admin whatever it says.
    /// </summary>
    private JsonObject Available(Db db)
    {
        // The chosen kind (ChosenKinds); an older "api" profile without a single key answers for either API kind.
        var kinds = ChosenKinds(db).Where(InAppKinds.Contains).ToArray();
        var tools = kinds.Length == 0 || kinds.Any(k => !ToolsDisabled(k));
        // m9 §5: scripts need tools and this host's script path (a runner, the policy reader and PowerShell's parser).
        return new JsonObject
        {
            ["tools"] = tools, ["web"] = AvailableWeb(db), ["scripts"] = tools && Actions?.ScriptsAvailable == true, ["admin"] = AdminAvailable,
        };
    }

    /// <summary>m9 §17.1: administrator rights can be used in this version (--enable-admin-actions, or --enable-firewall-handler for the firewall).</summary>
    internal bool AdminAvailable => Actions?.AdminActionsEnabled == true;

    private JsonObject PermissionsResult(Db db, Permissions record) => new() { ["permissions"] = record.ToJson(), ["available"] = Available(db) };

    /// <summary>ai.getPermissions {} → {permissions, available}.</summary>
    public JsonNode GetPermissions() => _store.Read(db => (JsonNode)PermissionsResult(db, ReadPermissions(db)));

    /// <summary>
    /// ai.setPermissions {idempotencyKey, revision, permissions} → {permissions, available}. A stale revision → -32002
    /// with the current result. Sets set:true, bumps the revision and emits ai.permissions on the user stream; then the
    /// running tasks follow the change (§1.2).
    /// </summary>
    public JsonNode SetPermissions(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var revision = P.Int(p, "revision")!.Value;
        var restartResearch = false;
        var (result, _) = _store.Write(db => Idempotency.Run(db, "ai.setPermissions", key, p, () =>
        {
            var current = ReadPermissions(db);
            if (revision != current.Revision)
                throw new RpcError(ErrorCodes.StaleRevision, "The permissions changed.", new JsonObject { ["current"] = PermissionsResult(db, current) });
            var next = Permissions.Parse(p["permissions"], current.Revision + 1, set: true);
            Settings.Set(db, Permissions.Key, next.ToJson());
            db.AppendEvent("user", "ai.permissions", new JsonObject { ["permissions"] = next.ToJson() });
            if (current.NarrowedBy(next)) EndActiveTasks(db);
            else if (current.WidenedBy(next)) WidenActiveTasks(db, next);
            restartResearch = ResearchOnPermissionsSaved(db, current, next); // m9 §6.5: any save cancels a running run
            return (JsonNode)PermissionsResult(db, next);
        }));
        if (restartResearch) RestartResearchForCurrentReport();
        return result;
    }

    /// <summary>
    /// A narrowing save ends every active task, paired ones included, through the stop path: plans still awaiting the
    /// user are cancelled and a running turn is cancelled. An operation the user already approved is not the task's and
    /// runs on. What was already sent to the AI cannot be recalled; the next ask starts a fresh task.
    /// </summary>
    private void EndActiveTasks(Db db)
    {
        foreach (var id in db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active'", r => r.GetString(0)))
        {
            EndTaskChecks(db, id, "permissions"); // m9 §17.2: cancelled with their cause
            SetStatus(db, LoadTask(db, id), "stopped");
        }
    }

    /// <summary>
    /// A widening save updates the grant of active in-app and headless tasks in place (agent.task carries the new grant).
    /// The task keeps its own limits and anything it already had; a paired (user-window) task is never widened.
    /// </summary>
    private void WidenActiveTasks(Db db, Permissions next)
    {
        foreach (var id in db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active' AND connection_id IS NULL", r => r.GetString(0)))
        {
            var task = LoadLive(db, id);
            if (task.Text("status") != "active" || task["pairing"] is JsonObject) continue;
            var kind = task["recipient"].Text("kind");
            var old = GrantOf(task);
            var fresh = GrantFromPermissions(next, InAppKinds.Contains(kind) && ToolsDisabled(kind));
            var widened = old with
            {
                Diagnostics = [.. old.Diagnostics.Union(fresh.Diagnostics)],
                ShareNames = old.ShareNames || fresh.ShareNames,
                Changes = Permissions.Level(fresh.Changes) > Permissions.Level(old.Changes) ? fresh.Changes : old.Changes,
                SeeSpecs = old.SeeSpecs || fresh.SeeSpecs, SeeHistory = old.SeeHistory || fresh.SeeHistory,
                Scripts = old.Scripts || fresh.Scripts, Web = old.Web || fresh.Web, Admin = old.Admin || fresh.Admin,
                PermissionsRevision = next.Revision,
            };
            task["grant"] = widened.ToJson();
            if (widened.ShareNames) task["everSharedNames"] = true;
            SaveTask(db, task);
            db.AppendEvent(Stream(id), "agent.task", new JsonObject
            {
                ["status"] = task.Text("status"), ["used"] = Used(task).DeepClone(), ["grant"] = widened.ToJson(),
            });
        }
    }

    /// <summary>The sticky exposure mark (m9 §1.2): did this task ever share names? Tasks stored before m9 answer from their grant.</summary>
    internal static bool EverSharedNames(JsonObject task) =>
        (task["everSharedNames"] is JsonValue v && v.TryGetValue<bool>(out var b) && b) || (task["grant"]?["share"]?["names"]?.GetValue<bool>() ?? true);

    /// <summary>
    /// Why this task's grant refuses a tool call (m9 §1.1), or null. get_pc_specs needs see.specs; get_report_history and
    /// get_benchmarks need see.history, except that the benchmark run named by the ask's own context stays readable;
    /// request_action needs changes != off. Checked before the call runs and again before its result is released.
    /// </summary>
    private static string? GrantRefusal(Db db, string taskId, Grant grant, string tool, JsonObject args) => tool switch
    {
        Diagnostics.GetPcSpecs when !grant.SeeSpecs => "This task may not read the PC specs.",
        Diagnostics.GetReportHistory when !grant.SeeHistory => "This task may not read past reports.",
        Diagnostics.GetBenchmarks when !grant.SeeHistory && !(args["runId"]?.GetValue<string>() is { } runId && runId == AskBenchRun(db, taskId))
            => "This task may not read past benchmarks.",
        Diagnostics.RequestAction when !grant.CanRequestChanges => "This task may not ask for changes.",
        Diagnostics.ProposeFix when grant.Changes != "fix" => "This task may not propose multi-step fixes.",
        Diagnostics.WebLookup when !grant.Web => "This task may not look things up online.",
        Diagnostics.ProposeScript when !grant.Scripts || grant.Changes != "fix" => "This task may not propose scripts.",
        _ => null,
    };

    /// <summary>The benchmark run the task's latest ask is about (agent.ask context.benchRunId), or null.</summary>
    internal static string? AskBenchRun(Db db, string taskId)
    {
        var payload = db.Scalar("SELECT payload FROM events WHERE stream = $s AND type = 'agent.question' ORDER BY seq DESC LIMIT 1", ("$s", Stream(taskId))) as string;
        return payload is not null && Json.Parse(payload)?["context"]?["benchRunId"] is JsonValue v && v.TryGetValue<string>(out var runId) ? runId : null;
    }
}

using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// The chosen AI is the one used: the onboarding profile's provider names the exact recipient kind the user chose
// (provider.kind). In-app and headless tasks, their questions and in-app reviews go only to that kind; research, the
// check-up and ai.getPermissions.available read it through ChosenKinds too. A paired task (an outside Claude Code
// window) is the user's own window and is never affected.
public sealed partial class AgentService
{
    /// <summary>
    /// The recipient kinds the saved choice allows: provider.kind when present; for an older profile without it,
    /// claude-code → claude_code_mcp, chatgpt → chatgpt_plan, api → the API kind with a stored key when exactly one has
    /// one, else either API kind (legacy); local, later or no provider → none.
    /// </summary>
    internal string[] ChosenKinds(Profile profile)
    {
        var provider = profile.Provider;
        if (provider?["kind"] is JsonValue kv && kv.TryGetValue<string>(out var kind))
            return RecipientKinds.Contains(kind) ? [kind] : [];
        return (provider?["value"] is JsonValue vv && vv.TryGetValue<string>(out var value) ? value : null) switch
        {
            "claude-code" => ["claude_code_mcp"],
            "chatgpt" => ["chatgpt_plan"],
            "api" => ApiKeyKinds.Where(HasCredential).ToArray() is { Length: 1 } one ? one : ApiKeyKinds,
            _ => [],
        };
    }

    internal string[] ChosenKinds(Db db) => ChosenKinds(OnboardingService.Read(db).Profile);

    /// <summary>The one chosen kind, or null when none is chosen or an older "api" profile leaves it open.</summary>
    private static string? SingleChosen(string[] chosen) => chosen.Length == 1 ? chosen[0] : null;

    /// <summary>-32013 not_chosen unless <paramref name="kind"/> is a chosen kind (in-app and headless tasks, in-app reviews).</summary>
    private void RequireChosen(Db db, string kind)
    {
        var chosen = ChosenKinds(db);
        if (chosen.Contains(kind)) return;
        throw new RpcError(ErrorCodes.InvalidState, "That AI is not the one chosen.",
            new JsonObject { ["reason"] = "not_chosen", ["chosen"] = SingleChosen(chosen) });
    }

    /// <summary>
    /// Inside the onboarding.save transaction: when the chosen kind changed, every active in-app and headless task of
    /// another kind ends through the stop path (plans awaiting the user are cancelled, a running turn is cancelled;
    /// approved operations run on), and a running research run or check-up is cancelled. Paired tasks are left alone.
    /// </summary>
    internal void OnProfileSaved(Db db, Profile before, Profile after)
    {
        var was = ChosenKinds(before);
        var now = ChosenKinds(after);
        if (was.SequenceEqual(now)) return;
        foreach (var id in db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active' AND connection_id IS NULL", r => r.GetString(0)))
        {
            var task = LoadLive(db, id);
            if (task.Text("status") != "active" || task["pairing"] is JsonObject) continue;
            if (!now.Contains(task["recipient"].Text("kind")))
            {
                EndTaskChecks(db, id, "ai"); // m9 §17.2: cancelled with their cause
                SetStatus(db, task, "stopped");
            }
        }
        CancelActiveResearch(db, null);
    }
}

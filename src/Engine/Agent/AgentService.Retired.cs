using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M9 "Drop Codex": the product no longer has a Codex provider. This file holds the only legacy spots in the engine that
// still name it: the pairing refusal, and the start-up step that ends what an older version left active. Stored history
// (tasks, events, plans, notes) is never rewritten: it keeps the kind, origin and label it was written with.
public sealed partial class AgentService
{
    /// <summary>The client dropped from the product (the bridge still names it, so it can be refused with a message).</summary>
    public const string RetiredClient = "codex";

    /// <summary>The provider kind of that client, as older versions stored it on tasks and research availability.</summary>
    public const string RetiredKind = "codex_mcp";

    /// <summary>agent.pair's -32013 reason and the message the bridge shows the model for a retired client.</summary>
    public const string ClientNotSupported = "client_not_supported",
        RetiredClientMessage = "Optimus no longer works with Codex. Use Claude Code or the Optimus app.";

    /// <summary>
    /// On start-up, once: active tasks of the retired kind stop (their pending plans are cancelled with them), its pending
    /// or approved pairings expire, and its research availability rows go. Nothing is evented for the pairings (no card
    /// was ever shown for them in this version). One info line with the counts when anything changed.
    /// </summary>
    private void RetireDroppedProvider()
    {
        var (tasks, pairings, rows) = _store.Write(db =>
        {
            var ids = db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active' AND recipient_kind = $k", r => r.GetString(0), ("$k", RetiredKind));
            // The pairings first, without events: the stop below then finds no approved pairing to expire (and to event).
            var expired = db.Exec("UPDATE agent_pairings SET status = 'expired' WHERE client = $c AND status IN ('pending','approved')", ("$c", RetiredClient));
            foreach (var id in ids) SetStatus(db, LoadTask(db, id), "stopped");

            var removed = 0;
            if (Settings.Get(db, ResearchWebKey) is JsonObject map)
            {
                foreach (var key in map.Select(x => x.Key).Where(k => k.StartsWith(RetiredKind + "|", StringComparison.Ordinal)).ToList())
                {
                    map.Remove(key);
                    removed++;
                }
                if (removed > 0) Settings.Set(db, ResearchWebKey, map);
            }
            return (ids.Count, expired, removed);
        });
        if (tasks + pairings + rows > 0)
            _log($"retired provider: stopped {tasks} task(s), expired {pairings} pairing(s), dropped {rows} research availability row(s)");
    }
}

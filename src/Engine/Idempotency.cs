using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// (method, idempotencyKey) → canonical params digest + recorded result (protocol §4). Runs inside the caller's
/// write transaction, so the record commits atomically with the change it describes. The key is checked before
/// anything else, including revisions and leases.
/// </summary>
internal static class Idempotency
{
    public static (JsonNode Result, bool Replayed) Run(Db db, string method, string key, JsonObject parameters, Func<JsonNode> execute)
    {
        var digest = Digest(parameters);
        var row = db.Query("SELECT digest, result FROM idempotency WHERE method = $m AND key = $k",
            r => (Digest: r.GetString(0), Result: r.GetString(1)), ("$m", method), ("$k", key)).FirstOrDefault();
        if (row.Digest is not null)
        {
            if (row.Digest != digest)
                throw new RpcError(ErrorCodes.IdempotencyMismatch, "This idempotencyKey was used with different parameters.",
                    new JsonObject { ["idempotencyKey"] = key });
            return (Json.Parse(row.Result)!, true);
        }

        var result = execute();
        db.Exec("INSERT INTO idempotency (method, key, digest, result, created_at) VALUES ($m, $k, $d, $r, $t)",
            ("$m", method), ("$k", key), ("$d", digest), ("$r", Json.Write(result)), ("$t", db.Now()));
        return (result, false);
    }

    /// <summary>Digest of the params without the key itself, with object keys sorted.</summary>
    public static string Digest(JsonObject parameters)
    {
        var copy = (JsonObject)parameters.DeepClone();
        copy.Remove("idempotencyKey");
        return Json.Digest(copy);
    }
}

internal sealed class EventService(Store store)
{
    public const int MaxLimit = 200;

    /// <summary>events.read: replay after a cursor. M1 retains every event, so stale is true only for a cursor before the oldest row.</summary>
    public JsonObject Read(JsonObject p)
    {
        var stream = P.Req(p, "stream", 128);
        var after = P.Int(p, "afterSeq", required: false) ?? 0;
        var limit = P.Int(p, "limit", required: false) ?? 100;
        if (after < 0) throw RpcError.Params("afterSeq", "afterSeq must be zero or more.");
        if (limit is < 1 or > MaxLimit) throw RpcError.Params("limit", $"limit must be 1 to {MaxLimit}.");

        return store.Read(db =>
        {
            if (!StreamExists(db, stream)) throw RpcError.NotFound("stream", stream);
            var min = db.Scalar("SELECT MIN(seq) FROM events WHERE stream = $s", ("$s", stream)) as long?;
            if (min is long oldest && after < oldest - 1)
                return new JsonObject { ["events"] = new JsonArray(), ["nextSeq"] = after, ["stale"] = true };

            var events = db.Query("SELECT seq, type, at, payload FROM events WHERE stream = $s AND seq > $a ORDER BY seq LIMIT $l",
                r => new EventRecord(stream, r.GetInt64(0), r.GetString(1), r.GetString(2), Json.Parse(r.GetString(3))!),
                ("$s", stream), ("$a", after), ("$l", limit));
            return new JsonObject
            {
                ["events"] = new JsonArray(events.Select(e => (JsonNode?)e.ToJson()).ToArray()),
                ["nextSeq"] = events.Count > 0 ? events[^1].Seq : after,
                ["stale"] = false,
            };
        });
    }

    private static bool StreamExists(Db db, string stream)
    {
        if (stream.StartsWith("scan:", StringComparison.Ordinal))
            return db.Scalar("SELECT 1 FROM scans WHERE scan_id = $i", ("$i", stream[5..])) is not null;
        if (stream.StartsWith("inventory:", StringComparison.Ordinal))
            return db.Scalar("SELECT 1 FROM inventories WHERE inventory_id = $i", ("$i", stream[10..])) is not null;
        if (stream.StartsWith("bench:", StringComparison.Ordinal))
            return db.Scalar("SELECT 1 FROM bench_runs WHERE run_id = $i", ("$i", stream[6..])) is not null;
        if (stream.StartsWith(Agent.AgentService.TaskStreamPrefix, StringComparison.Ordinal))
            return db.Scalar("SELECT 1 FROM agent_tasks WHERE task_id = $i", ("$i", stream[Agent.AgentService.TaskStreamPrefix.Length..])) is not null;
        return stream == "user";
    }
}

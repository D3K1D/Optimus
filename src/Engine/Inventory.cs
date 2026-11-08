using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// Consented first-run specs pass (protocol §6 inventory.*). Fixture mode replays the fixture's steps; module mode
/// runs each <see cref="IInventoryStep"/> in order under its own timeout. Each step's wave labels (at most
/// <see cref="MaxWaveFacts"/>) commit with their inventory.fact events; its structured facts, coverage and
/// capabilities are stored with the inventory. Capabilities merge across steps (later steps win per key).
/// </summary>
internal sealed class InventoryService(Store store, Fixture fixture, AuditCatalog catalog, double speed, Action<string> log)
{
    public const int MaxWaveFacts = 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _runs = [];

    public bool HasActiveWork
    {
        get { lock (_gate) return _runs.Count > 0; }
    }

    public static string Stream(string inventoryId) => "inventory:" + inventoryId;

    public JsonNode Start(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        string? startedId = null;
        var (result, _) = store.Write(db => Idempotency.Run(db, "inventory.start", key, p, () =>
        {
            var (_, profile) = OnboardingService.Read(db);
            if (profile.InventoryConsent != "granted")
                throw RpcError.State("inventory_consent_required", "Inventory needs consent first.");
            if (db.Scalar("SELECT inventory_id FROM inventories WHERE status = 'running' LIMIT 1") is string)
                throw RpcError.State("inventory_active", "An inventory is already running.");
            startedId = Ids.NewInventory();
            db.Exec("INSERT INTO inventories (inventory_id, status, started_at, coverage) VALUES ($i, 'running', $t, '{}')",
                ("$i", startedId), ("$t", db.Now()));
            return new JsonObject { ["inventoryId"] = startedId };
        }));

        if (startedId is { } id)
        {
            var cts = new CancellationTokenSource();
            lock (_gate) _runs[id] = (cts, Task.Run(() => RunAsync(id, cts.Token)));
        }
        var inventoryId = result["inventoryId"]!.GetValue<string>();
        var view = Get(inventoryId);
        return new JsonObject { ["inventoryId"] = inventoryId, ["status"] = view["status"]!.DeepClone() };
    }

    public JsonObject Get(string inventoryId) => store.Read(db =>
    {
        var row = db.Query("SELECT status, coverage, capabilities FROM inventories WHERE inventory_id = $i",
            r => (Status: r.GetString(0), Coverage: r.GetString(1), Caps: r.IsDBNull(2) ? null : r.GetString(2)), ("$i", inventoryId)).FirstOrDefault();
        if (row.Status is null) throw RpcError.NotFound("inventory", inventoryId);
        var facts = db.Query("SELECT seq, kind, label, grp FROM inventory_facts WHERE inventory_id = $i ORDER BY seq",
            r => (JsonNode?)new JsonObject
            {
                ["seq"] = r.GetInt64(0), ["kind"] = r.GetString(1), ["label"] = r.GetString(2), ["group"] = r.IsDBNull(3) ? null : r.GetString(3),
            }, ("$i", inventoryId));
        return new JsonObject
        {
            ["inventoryId"] = inventoryId, ["status"] = row.Status,
            ["facts"] = new JsonArray(facts.ToArray()),
            ["coverage"] = Json.Parse(row.Coverage) as JsonObject ?? throw RpcError.Corrupt("inventory", inventoryId),
            ["capabilities"] = row.Caps is null ? new JsonObject() : Json.Parse(row.Caps) as JsonObject ?? throw RpcError.Corrupt("inventory", inventoryId),
        };
    });

    public async Task<JsonNode> CancelAsync(string inventoryId)
    {
        (CancellationTokenSource Cts, Task Task) run;
        bool active;
        lock (_gate) active = _runs.TryGetValue(inventoryId, out run);
        if (active)
        {
            await run.Cts.CancelAsync().ConfigureAwait(false);
            await run.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        return Get(inventoryId);
    }

    public void RecoverInterrupted() => store.Write(db =>
    {
        foreach (var id in db.Query("SELECT inventory_id FROM inventories WHERE status = 'running'", r => r.GetString(0)))
        {
            db.Exec("UPDATE inventories SET status = 'cancelled', finished_at = $t WHERE inventory_id = $i", ("$t", db.Now()), ("$i", id));
            db.AppendEvent(Stream(id), "inventory.finished", new JsonObject { ["status"] = "cancelled" });
        }
    });

    public async Task StopAllAsync()
    {
        (CancellationTokenSource Cts, Task Task)[] runs;
        lock (_gate) runs = [.. _runs.Values];
        foreach (var r in runs) await r.Cts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(runs.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception ex) { log($"stopping inventory: {ex.Message}"); }
    }

    private async Task RunAsync(string inventoryId, CancellationToken ct)
    {
        try
        {
            if (catalog.InventorySteps is { } steps) await RunStepsAsync(inventoryId, steps, ct).ConfigureAwait(false);
            else await RunFixtureAsync(inventoryId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(inventoryId, "cancelled", null, null);
        }
        catch (Exception ex)
        {
            log($"inventory {inventoryId} failed: {ex}");
            Finish(inventoryId, "partial", null, null);
        }
        finally
        {
            lock (_gate) _runs.Remove(inventoryId);
        }
    }

    private async Task RunFixtureAsync(string inventoryId, CancellationToken ct)
    {
        var def = fixture.Inventory;
        var seq = 0;
        foreach (var step in def.Steps)
        {
            var delay = TimeSpan.FromMilliseconds(def.StepDelayMs * Math.Max(0, speed));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            store.Write(db => seq = CommitWave(db, inventoryId, step.Name, step.Facts.Select(f => new InventoryFact(f.Kind, f.Label)), seq));
        }
        Finish(inventoryId, def.Status, def.Coverage, def.Capabilities);
    }

    private async Task RunStepsAsync(string inventoryId, IReadOnlyList<IInventoryStep> steps, CancellationToken ct)
    {
        var seq = 0;
        var ord = 0;
        var coverage = new JsonObject();
        var capabilities = new JsonObject();
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            InventoryStepResult? result = null;
            string status;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(step.Timeout);
                try
                {
                    result = await step.RunAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    status = result.Coverage.Status;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { status = "timed_out"; }
                catch (Exception ex) { status = "failed"; log($"inventory step {step.Group}: {ex.GetType().Name}"); }
            }

            coverage[step.Group.ToLowerInvariant()] = status;
            if (result is not null)
                foreach (var (k, v) in result.Capabilities) capabilities[k] = v?.DeepClone();
            store.Write(db =>
            {
                if (result is null) return;
                seq = CommitWave(db, inventoryId, step.Group, result.WaveFacts, seq);
                var observedAt = db.Now();
                foreach (var fact in result.Facts.OrderBy(f => f.Subject, StringComparer.Ordinal))
                    db.Exec("INSERT INTO inventory_items (inventory_id, ord, grp, subject, body) VALUES ($i, $o, $g, $s, $b)",
                        ("$i", inventoryId), ("$o", ord++), ("$g", step.Group), ("$s", fact.Subject), ("$b", Json.Write(new JsonObject
                        {
                            ["subject"] = fact.Subject, ["source"] = fact.Source, ["quality"] = fact.Quality, ["value"] = fact.Value.DeepClone(),
                            ["display"] = fact.Display, ["interval"] = fact.Interval, ["observedAt"] = observedAt,
                        })));
            });
        }
        var all = coverage.Select(c => c.Value!.GetValue<string>()).ToList();
        Finish(inventoryId, all.All(s => s == "complete") ? "complete" : "partial", coverage, capabilities);
    }

    /// <summary>Commits one step's wave labels (at most <see cref="MaxWaveFacts"/>) and their events; returns the last seq.</summary>
    private static int CommitWave(Db db, string inventoryId, string group, IEnumerable<InventoryFact> facts, int seq)
    {
        foreach (var fact in facts.Take(MaxWaveFacts))
        {
            seq++;
            db.Exec("INSERT INTO inventory_facts (inventory_id, seq, kind, label, grp) VALUES ($i, $s, $k, $l, $g)",
                ("$i", inventoryId), ("$s", seq), ("$k", fact.Kind), ("$l", fact.Label), ("$g", group));
            db.AppendEvent(Stream(inventoryId), "inventory.fact", new JsonObject { ["kind"] = fact.Kind, ["label"] = fact.Label, ["group"] = group });
        }
        return seq;
    }

    private void Finish(string inventoryId, string status, JsonObject? coverage, JsonObject? capabilities) => store.Write(db =>
    {
        db.Exec("UPDATE inventories SET status = $s, finished_at = $t, coverage = $c, capabilities = $p WHERE inventory_id = $i",
            ("$s", status), ("$t", db.Now()), ("$c", Json.Write(coverage ?? [])),
            ("$p", capabilities is null ? null : Json.Write(capabilities)), ("$i", inventoryId));
        db.AppendEvent(Stream(inventoryId), "inventory.finished", new JsonObject { ["status"] = status });
    });
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine;

/// <summary>
/// Scan lifecycle (docs/backend.md "Scan lifecycle"): one lease per user; expensive collectors share two slots and
/// cheap ones four; each collector has its own deadline inside an overall scan budget; every collector result commits
/// with its evidence, coverage and events in one transaction; the finish step builds the report from whatever was
/// committed. A skippable collector (the sampler) can be skipped on its own (protocol §9 scan.skipCollector).
/// </summary>
internal sealed class ScanCoordinator(Store store, Fixture fixture, AuditCatalog catalog, TimeSpan scanDeadline, TimeProvider time, Action<string> log, WorkLease? lease = null)
{
    public const int ExpensiveSlots = 2, CheapSlots = 4;
    public static readonly TimeSpan SkipGrace = TimeSpan.FromSeconds(5);
    private static readonly string[] Active = ["queued", "running"];

    private readonly object _gate = new();
    private readonly Dictionary<string, Run> _runs = [];
    private readonly WorkLease _lease = lease ?? new WorkLease();

    private sealed class Run(string scanId, IReadOnlyList<ICollector> plan, TimeSpan deadline)
    {
        public string ScanId { get; } = scanId;
        public IReadOnlyList<ICollector> Plan { get; } = plan;
        public CancellationTokenSource UserCancel { get; } = new();
        public CancellationTokenSource Deadline { get; } = new(deadline);
        public CancellationTokenSource Combined { get; private set; } = null!;
        public Task Task { get; set; } = Task.CompletedTask;
        public volatile bool ShuttingDown;
        public Dictionary<string, Running> Running { get; } = [];

        public void Link() => Combined = CancellationTokenSource.CreateLinkedTokenSource(UserCancel.Token, Deadline.Token);
    }

    private sealed record Running(ICollector Collector, DateTimeOffset StartedAt, CancellationTokenSource Skip, TaskCompletionSource Done);

    public bool HasActiveWork
    {
        get { lock (_gate) return _runs.Count > 0; }
    }

    public JsonNode Start(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var intent = P.Req(p, "intent", 64);
        var text = P.Str(p, "text", required: false, maxLength: 500) ?? "";
        var baseReportId = P.Str(p, "baseReportId", required: false, maxLength: 64);
        if (intent == "rescan" && baseReportId is null) throw RpcError.Params("baseReportId", "rescan needs baseReportId.");
        if (intent != "rescan" && fixture.CategoriesFor(intent) is null) throw RpcError.Params("intent", $"Unknown intent {intent}.");

        Run? started = null;
        string? leased = null;
        JsonNode result;
        try
        {
        (result, _) = store.Write(db => Idempotency.Run(db, "scan.start", key, p, () =>
        {
            if (db.Scalar("SELECT scan_id FROM scans WHERE status IN ('queued','running') LIMIT 1") is string busy)
                throw new RpcError(ErrorCodes.ScanActive, "A scan is already running.", new JsonObject { ["scanId"] = busy });

            var effective = intent;
            if (baseReportId is not null)
            {
                var baseIntent = db.Scalar("SELECT intent FROM reports WHERE report_id = $r", ("$r", baseReportId)) as string
                    ?? throw RpcError.NotFound("report", baseReportId);
                if (intent == "rescan") effective = baseIntent;
            }
            var plan = catalog.Plan(fixture.CategoriesFor(effective)!);

            var scanId = Ids.NewScan();
            var (profileRevision, _) = OnboardingService.Read(db);
            db.Exec("INSERT INTO scans (scan_id, intent, text, status, started_at, profile_revision, base_report_id) VALUES ($i, $n, $t, 'running', $s, $r, $b)",
                ("$i", scanId), ("$n", effective), ("$t", text), ("$s", db.Now()), ("$r", profileRevision), ("$b", baseReportId));
            for (var i = 0; i < plan.Count; i++)
                db.Exec("INSERT INTO scan_collectors (scan_id, collector_id, ord, category, version, status) VALUES ($s, $c, $o, $g, $v, 'queued')",
                    ("$s", scanId), ("$c", plan[i].Id), ("$o", i), ("$g", plan[i].Category), ("$v", plan[i].Version));
            db.AppendEvent(Stream(scanId), "scan.progress", ProgressEvent(db, scanId, null));
            // m6 §2: one lease shared with benchmarks, sensor snapshots and performance actions. Taken last, so a
            // refusal rolls the scan rows back.
            _lease.Acquire(WorkLease.Scan, scanId);
            leased = scanId;
            started = new Run(scanId, plan, scanDeadline);
            return new JsonObject { ["scanId"] = scanId };
        }));
        }
        catch when (leased is not null)
        {
            _lease.Release(WorkLease.Scan, leased);
            throw;
        }

        if (started is not null)
        {
            started.Link();
            lock (_gate)
            {
                _runs[started.ScanId] = started;
                started.Task = Task.Run(() => RunAsync(started));
            }
        }
        // A replay resolves to the same scan and reports its current state.
        return Get(result["scanId"]!.GetValue<string>());
    }

    public JsonObject Get(string scanId)
    {
        Run? run;
        lock (_gate) _runs.TryGetValue(scanId, out run);
        return store.Read(db => View(db, scanId, run));
    }

    public async Task<JsonNode> CancelAsync(string scanId)
    {
        Run? run;
        lock (_gate) _runs.TryGetValue(scanId, out run);
        if (run is not null)
        {
            await run.UserCancel.CancelAsync().ConfigureAwait(false);
            await run.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        return Get(scanId); // an already finished scan is returned as it is
    }

    /// <summary>scan.skipCollector: cancels one running skippable collector; its samples so far are kept.</summary>
    public async Task<JsonNode> SkipAsync(JsonObject p)
    {
        var scanId = P.Req(p, "scanId", 64);
        var collectorId = P.Req(p, "collectorId", 128);
        Run? run;
        lock (_gate) _runs.TryGetValue(scanId, out run);
        if (run is null)
        {
            Get(scanId); // not found → -32001
            throw RpcError.State("not_running", "That scan is not running.");
        }
        Running? running;
        lock (run.Running) run.Running.TryGetValue(collectorId, out running);
        if (running is null) throw RpcError.State("collector_not_running", "That check is not running.");
        if (!AuditCatalog.IsSkippable(running.Collector)) throw RpcError.State("not_skippable", "That check cannot be skipped.");

        await running.Skip.CancelAsync().ConfigureAwait(false);
        await running.Done.Task.WaitAsync(SkipGrace + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return Get(scanId);
    }

    /// <summary>On startup: scans left running by a previous host become interrupted, with a partial report.</summary>
    public void RecoverInterrupted()
    {
        var stale = store.Read(db => db.Query("SELECT scan_id FROM scans WHERE status IN ('queued','running')", r => r.GetString(0)));
        foreach (var scanId in stale)
        {
            store.Write(db => Finish(db, scanId, "interrupted", deadlineHit: false));
            log($"scan {scanId} marked interrupted");
        }
    }

    public async Task StopAllAsync()
    {
        Run[] runs;
        lock (_gate) runs = [.. _runs.Values];
        foreach (var run in runs) { run.ShuttingDown = true; await run.UserCancel.CancelAsync().ConfigureAwait(false); }
        try { await Task.WhenAll(runs.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception ex) { log($"stopping scans: {ex.Message}"); }
    }

    private async Task RunAsync(Run run)
    {
        try
        {
            using var expensive = new SemaphoreSlim(ExpensiveSlots);
            using var cheap = new SemaphoreSlim(CheapSlots);
            await Task.WhenAll(run.Plan.Select(c => RunCollectorAsync(run, c, AuditCatalog.IsExpensive(c) ? expensive : cheap))).ConfigureAwait(false);
            var forced = !run.UserCancel.IsCancellationRequested ? null : run.ShuttingDown ? "interrupted" : "cancelled";
            store.Write(db => Finish(db, run.ScanId, forced, run.Deadline.IsCancellationRequested));
        }
        catch (Exception ex)
        {
            log($"scan {run.ScanId} failed: {ex}");
            try { store.Write(db => Finish(db, run.ScanId, "failed", deadlineHit: false)); }
            catch (Exception inner) { log($"scan {run.ScanId} could not be finished: {inner.Message}"); }
        }
        finally
        {
            lock (_gate) _runs.Remove(run.ScanId);
            _lease.Release(WorkLease.Scan, run.ScanId);
            run.Deadline.Dispose();
        }
    }

    private async Task RunCollectorAsync(Run run, ICollector collector, SemaphoreSlim slots)
    {
        var token = run.Combined.Token;
        try { await slots.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; } // never started: Finish records it as skipped
        var running = new Running(collector, time.GetUtcNow(), new CancellationTokenSource(), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            if (token.IsCancellationRequested) return;
            lock (run.Running) run.Running[collector.Id] = running;
            store.Write(db =>
            {
                SetStatus(db, run.ScanId, collector.Id, "running", 0, null);
                db.AppendEvent(Stream(run.ScanId), "scan.progress", ProgressEvent(db, run.ScanId, run));
            });

            using var tick = new CancellationTokenSource();
            var ticker = AuditCatalog.IsSkippable(collector) ? TickAsync(run, tick.Token) : Task.CompletedTask;
            var (facts, receipt) = await CollectAsync(collector, run.UserCancel.Token, run.Deadline.Token, running.Skip.Token).ConfigureAwait(false);
            await tick.CancelAsync().ConfigureAwait(false);
            await ticker.ConfigureAwait(false);

            store.Write(db =>
            {
                var observedAt = db.Now();
                for (var i = 0; i < facts.Count; i++) InsertEvidence(db, run.ScanId, collector, facts[i], observedAt, i);
                SetStatus(db, run.ScanId, collector.Id, receipt.Status, receipt.Observations, receipt.Reason);
                lock (run.Running) run.Running.Remove(collector.Id);
                db.AppendEvent(Stream(run.ScanId), "scan.coverage", new JsonObject { ["collectorId"] = collector.Id, ["status"] = receipt.Status });
                db.AppendEvent(Stream(run.ScanId), "scan.progress", ProgressEvent(db, run.ScanId, run));
            });
        }
        finally
        {
            lock (run.Running) run.Running.Remove(collector.Id);
            running.Done.TrySetResult(); // Skip is not disposed: a concurrent skip may still cancel it
            slots.Release();
        }
    }

    /// <summary>Interval of scan.progress events while a long (skippable) collector runs, so countdowns stay honest.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private async Task TickAsync(Run run, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
                store.Write(db => { db.AppendEvent(Stream(run.ScanId), "scan.progress", ProgressEvent(db, run.ScanId, run)); });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log($"progress tick: {ex.Message}"); }
    }

    /// <summary>
    /// Runs one collector under its own deadline, linked to the user's cancel, the overall scan budget and (for a
    /// skippable collector) its skip token. On skip the collector gets <see cref="SkipGrace"/> to return what it has.
    /// </summary>
    internal static async Task<(IReadOnlyList<Fact> Facts, CoverageReceipt Receipt)> CollectAsync(ICollector collector,
        CancellationToken userCancel, CancellationToken scanDeadline = default, CancellationToken skip = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCancel, scanDeadline, skip);
        linked.CancelAfter(collector.Timeout);
        var task = collector.CollectAsync(linked.Token);
        try
        {
            var result = await task.WaitAsync(linked.Token).ConfigureAwait(false);
            return (result.Facts, result.Coverage);
        }
        catch (OperationCanceledException) when (userCancel.IsCancellationRequested)
        {
            return ([], new CoverageReceipt("cancelled", 0, "Scan cancelled."));
        }
        catch (OperationCanceledException) when (skip.IsCancellationRequested)
        {
            try
            {
                var partial = await task.WaitAsync(SkipGrace).ConfigureAwait(false);
                return (partial.Facts, new CoverageReceipt("skipped", partial.Facts.Count, "Skipped; samples so far are kept."));
            }
            catch (Exception) { return ([], new CoverageReceipt("skipped", 0, "Skipped.")); }
        }
        catch (OperationCanceledException) when (scanDeadline.IsCancellationRequested)
        {
            return ([], new CoverageReceipt("timed_out", 0, "The scan's time budget ran out."));
        }
        catch (OperationCanceledException)
        {
            return ([], new CoverageReceipt("timed_out", 0, $"No answer within {collector.Timeout.TotalMilliseconds:0} ms."));
        }
        catch (Exception ex)
        {
            return ([], new CoverageReceipt("failed", 0, ex.GetType().Name));
        }
    }

    private static readonly Regex LocalValue = new(@"^%[A-Za-z]+%|[A-Za-z]:\\|\\\\", RegexOptions.CultureInvariant);

    /// <summary>local when the value carries a path or image (shown on this PC only), otherwise public.</summary>
    internal static string InferSensitivity(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(p => InferSensitivity(p.Value) == "local") ? "local" : "public",
        JsonArray a => a.Any(x => InferSensitivity(x) == "local") ? "local" : "public",
        JsonValue v when v.TryGetValue<string>(out var s) && LocalValue.IsMatch(s) => "local",
        _ => "public",
    };

    private static void InsertEvidence(Db db, string scanId, ICollector collector, Fact fact, string observedAt, int index)
    {
        var id = Ids.Evidence(scanId, collector.Id, fact.Subject);
        var body = new JsonObject
        {
            ["id"] = id, ["subject"] = fact.Subject, ["category"] = collector.Category, ["source"] = fact.Source, ["observedAt"] = observedAt,
            ["interval"] = fact.Interval, ["display"] = fact.Display, ["value"] = fact.Value.DeepClone(),
            ["collectorId"] = collector.Id, ["collectorVersion"] = collector.Version, ["quality"] = fact.Quality,
            ["sensitivity"] = fact.Sensitivity is "public" or "local" ? fact.Sensitivity : InferSensitivity(fact.Value),
            // declared by the collector, or inferred by the engine (an inferred "local" may still be projected; m3 decision)
            ["sensitivityBasis"] = fact.Sensitivity is "public" or "local" ? "declared" : "inferred",
        };
        var ord = (long)db.Scalar("SELECT ord FROM scan_collectors WHERE scan_id = $s AND collector_id = $c", ("$s", scanId), ("$c", collector.Id))!;
        db.Exec("INSERT OR IGNORE INTO evidence (evidence_id, scan_id, collector_id, ord, subject, body) VALUES ($i, $s, $c, $o, $j, $b)",
            ("$i", id), ("$s", scanId), ("$c", collector.Id), ("$o", ord * 1000 + index), ("$j", fact.Subject), ("$b", Json.Write(body)));
    }

    private static void SetStatus(Db db, string scanId, string collectorId, string status, int observations, string? reason) =>
        db.Exec("UPDATE scan_collectors SET status = $st, observations = $o, reason = $r WHERE scan_id = $s AND collector_id = $c",
            ("$st", status), ("$o", observations), ("$r", reason), ("$s", scanId), ("$c", collectorId));

    /// <summary>
    /// Closes a scan: unfinished collectors become cancelled (in flight) or skipped (never started), the status is
    /// decided, and a report is built from committed evidence. <paramref name="forced"/> is cancelled, interrupted,
    /// failed or null (decide from coverage).
    /// </summary>
    private void Finish(Db db, string scanId, string? forced, bool deadlineHit)
    {
        var why = forced == "interrupted" ? "The backend stopped before this check finished."
            : deadlineHit && forced is null ? "The scan's time budget ran out." : "Scan stopped before this check finished.";
        db.Exec("UPDATE scan_collectors SET status = $st, reason = $r WHERE scan_id = $s AND status = 'running'",
            ("$st", deadlineHit && forced is null ? "timed_out" : "cancelled"), ("$r", why), ("$s", scanId));
        db.Exec("UPDATE scan_collectors SET status = 'skipped', reason = $r WHERE scan_id = $s AND status = 'queued'", ("$r", why), ("$s", scanId));

        var statuses = db.Query("SELECT status FROM scan_collectors WHERE scan_id = $s", r => r.GetString(0), ("$s", scanId));
        var status = forced ?? (statuses.All(s => s == "complete") ? "complete" : "complete_with_gaps");
        var finishedAt = db.Now();

        string? reportId = null;
        if (status != "failed")
        {
            var reportStatus = status is "complete" or "complete_with_gaps" ? status : "partial";
            reportId = new ReportBuilder(fixture, catalog).Build(db, scanId, reportStatus, finishedAt);
        }
        db.Exec("UPDATE scans SET status = $st, finished_at = $f, report_id = $r WHERE scan_id = $s",
            ("$st", status), ("$f", finishedAt), ("$r", reportId), ("$s", scanId));
        db.AppendEvent(Stream(scanId), "scan.progress", ProgressEvent(db, scanId, null));
        db.AppendEvent(Stream(scanId), "scan.finished", new JsonObject { ["status"] = status, ["reportId"] = reportId });
        // Released before the events publish, so a client reacting to scan.finished can start the next run at once.
        _lease.Release(WorkLease.Scan, scanId);
    }

    public static string Stream(string scanId) => "scan:" + scanId;

    private JsonObject View(Db db, string scanId, Run? run)
    {
        var scan = db.Query("SELECT intent, status, started_at, finished_at, report_id FROM scans WHERE scan_id = $s",
            r => (Intent: r.GetString(0), Status: r.GetString(1), Started: r.GetString(2),
                Finished: r.IsDBNull(3) ? null : r.GetString(3), Report: r.IsDBNull(4) ? null : r.GetString(4)),
            ("$s", scanId)).FirstOrDefault();
        if (scan.Intent is null) throw RpcError.NotFound("scan", scanId);

        var progress = Progress(db, scanId);
        progress["current"] = Current(run);
        var planned = db.Query("SELECT DISTINCT category FROM scan_collectors WHERE scan_id = $s", r => r.GetString(0), ("$s", scanId));
        return new JsonObject
        {
            ["scanId"] = scanId, ["intent"] = scan.Intent, ["status"] = scan.Status,
            ["startedAt"] = scan.Started, ["finishedAt"] = scan.Finished,
            // Fixture words describe the synthetic PC; live scans have none (they would claim results not observed).
            ["waveWords"] = Json.Array(catalog.IsFixture ? fixture.WaveWordsFor(planned, scan.Intent) : []),
            ["progress"] = progress,
            ["coverage"] = Coverage(db, scanId, finishedOnly: true),
            ["reportId"] = scan.Report,
            ["eventCursor"] = new JsonObject { ["stream"] = Stream(scanId), ["seq"] = db.LastSeq(Stream(scanId)) },
        };
    }

    /// <summary>
    /// progress.current: the running skippable collector if any (the sampler), else the first running one;
    /// null when nothing runs. remainingSeconds only for a collector that declares its expected duration.
    /// </summary>
    private JsonObject? Current(Run? run)
    {
        if (run is null) return null;
        Running? pick;
        lock (run.Running)
            pick = run.Running.Values.FirstOrDefault(r => AuditCatalog.IsSkippable(r.Collector))
                ?? run.Running.Values.OrderBy(r => r.StartedAt).FirstOrDefault();
        if (pick is null) return null;
        var expected = pick.Collector is ISkippableCollector s ? s.ExpectedDuration
            : pick.Collector.Id == AuditCatalog.SamplerId ? catalog.Options.SampleWindow : (TimeSpan?)null;
        long? remaining = expected is { } e ? Math.Max(0, (long)Math.Ceiling((pick.StartedAt + e - time.GetUtcNow()).TotalSeconds)) : null;
        return new JsonObject
        {
            ["collectorId"] = pick.Collector.Id,
            ["label"] = pick.Collector.Label,
            ["skippable"] = AuditCatalog.IsSkippable(pick.Collector),
            ["remainingSeconds"] = remaining,
        };
    }

    internal static JsonArray Coverage(Db db, string scanId, bool finishedOnly)
    {
        var rows = db.Query("SELECT collector_id, category, status, observations, reason FROM scan_collectors WHERE scan_id = $s ORDER BY ord",
            r => (Id: r.GetString(0), Category: r.GetString(1), Status: r.GetString(2), Obs: r.GetInt32(3), Reason: r.IsDBNull(4) ? null : r.GetString(4)),
            ("$s", scanId));
        return new JsonArray(rows.Where(r => !finishedOnly || !Active.Contains(r.Status)).Select(r => (JsonNode?)new JsonObject
        {
            ["collectorId"] = r.Id, ["category"] = r.Category, ["status"] = r.Status, ["observations"] = r.Obs, ["reason"] = r.Reason,
        }).ToArray());
    }

    /// <summary>Per scan category: queued (nothing started), running, done (all complete) or gap (finished with any shortfall).</summary>
    private List<(string Id, string Label, string Status)> CategoryStates(Db db, string scanId)
    {
        var rows = db.Query("SELECT category, status FROM scan_collectors WHERE scan_id = $s", r => (Cat: r.GetString(0), Status: r.GetString(1)), ("$s", scanId));
        var planned = rows.Select(r => r.Cat).Distinct().ToHashSet();
        var cats = fixture.ScanCategories.Where(c => planned.Contains(c.Id)).ToList();
        cats.AddRange(planned.Where(id => cats.All(c => c.Id != id)).OrderBy(fixture.CategoryOrder).Select(id => new Category(id, fixture.CategoryLabel(id))));

        return cats.Select(c =>
        {
            var mine = rows.Where(r => r.Cat == c.Id).Select(r => r.Status).ToList();
            var status = mine.All(s => s == "queued") ? "queued"
                : mine.Any(s => Active.Contains(s)) ? "running"
                : mine.All(s => s == "complete") ? "done" : "gap";
            return (c.Id, c.Label, status);
        }).ToList();
    }

    private JsonObject Progress(Db db, string scanId)
    {
        var cats = CategoryStates(db, scanId);
        return new JsonObject
        {
            ["done"] = cats.Count(c => c.Status is "done" or "gap"),
            ["total"] = cats.Count,
            ["categories"] = new JsonArray(cats.Select(c => (JsonNode?)new JsonObject { ["id"] = c.Id, ["label"] = c.Label, ["status"] = c.Status }).ToArray()),
        };
    }

    /// <summary>scan.progress payload: v1.0 {done,total,current(category label)} plus v1.1 collector (= Scan.progress.current).</summary>
    private JsonObject ProgressEvent(Db db, string scanId, Run? run)
    {
        var cats = CategoryStates(db, scanId);
        return new JsonObject
        {
            ["done"] = cats.Count(c => c.Status is "done" or "gap"),
            ["total"] = cats.Count,
            ["current"] = cats.FirstOrDefault(c => c.Status == "running").Label,
            ["collector"] = Current(run),
        };
    }
}

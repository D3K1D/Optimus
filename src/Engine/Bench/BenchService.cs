using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Bench;

/// <summary>What the engine needs to run benchmarks. With any of the three seams missing, benchmarks are unsupported.</summary>
public sealed class BenchOptions
{
    /// <summary>Driver-free sensors (src/Windows/Sensors), or <see cref="FakeSensorHubFactory"/> for the fixture source.</summary>
    public ISensorHubFactory? Sensors { get; init; }
    public IBenchHardware? Hardware { get; init; }

    /// <summary>Starts OptimizationTool.Bench.exe (src/Host), or <see cref="FakeBenchWorkerLauncher"/>.</summary>
    public IBenchWorkerLauncher? Launcher { get; init; }

    public BenchTimings Timings { get; init; } = new();

    /// <summary>The Optimus build in the configId; defaults to this assembly's version.</summary>
    public string? OptimusVersion { get; init; }

    public bool Available => Sensors is not null && Hardware is not null && Launcher is not null;
}

/// <summary>Run timings (§1, §4). Tests shrink them; the fixture source scales them.</summary>
public sealed record BenchTimings
{
    public TimeSpan CoolDown { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>After "stop", how long the worker has to exit before it is killed.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Per-call timeout for one sensor read on the sampler thread.</summary>
    public TimeSpan SensorReadTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HardwareTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>A slower_than_before confirmation repeats one subtest for this long.</summary>
    public TimeSpan Repeat { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>sensors.snapshot samples per part (CPU then GPU), at <see cref="SampleInterval"/>: ≤ 10 s in all.</summary>
    public int SnapshotSamples { get; init; } = 4;
    /// <summary>The whole sensors.snapshot call (hardware read, preparation and sampling) ends by this deadline (m6 §8).</summary>
    public TimeSpan SnapshotDeadline { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Planned time per part; the hard deadline is twice this.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Planned { get; init; } = new Dictionary<string, TimeSpan>
    {
        [BenchParts.Ram] = TimeSpan.FromSeconds(15),
        [BenchParts.Cpu] = TimeSpan.FromSeconds(45),
        [BenchParts.Gpu] = TimeSpan.FromSeconds(20),
        [BenchParts.Disk] = TimeSpan.FromSeconds(22),
    };

    public TimeSpan PlannedFor(string part) => Planned.TryGetValue(part, out var t) ? t : TimeSpan.FromSeconds(30);
}

/// <summary>
/// Benchmarks (contracts/m6-benchmark.md §2, §4–§7): the bench.* methods, the run coordinator (lease, lifecycle,
/// worker supervision, 1 Hz sampler thread, thermal and power stops), the store (bench_runs, bench_evidence), scores,
/// verdicts, fan guidance and evidence. Durable events on bench:&lt;runId&gt; are state changes only; the live view is
/// an in-memory ring buffer returned by bench.get.
/// </summary>
public sealed partial class BenchService
{
    public const int LiveSamples = 60, MaxList = 50;
    public static readonly TimeSpan KeepUnfinished = TimeSpan.FromDays(7);
    public static readonly string[] RunStates = ["starting", "running", "stopping", "done", "cancelled", "stopped_on_battery", "failed", "interrupted"];
    public static readonly string[] Active = ["starting", "running", "stopping"];
    public static readonly string[] FailReasons = ["check_failed", "gpu_reset", "too_slow", "low_memory", "needs_space", "unsupported", "error"];

    private readonly Store _store;
    private readonly WorkLease _lease;
    private readonly BenchOptions _options;
    private readonly string _dataDir;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Run> _runs = [];

    internal BenchService(Store store, WorkLease lease, BenchOptions options, string dataDir, Action<string> log)
    {
        _store = store;
        _lease = lease;
        _options = options;
        _dataDir = dataDir;
        _log = log;
    }

    private BenchTimings T => _options.Timings;

    public string OptimusVersion => _options.OptimusVersion ?? typeof(BenchService).Assembly.GetName().Version?.ToString(3) ?? "0";

    public bool HasActiveWork
    {
        get { lock (_gate) return _runs.Count > 0; }
    }

    public static string Stream(string runId) => "bench:" + runId;

    private static string Now(Db db) => db.Now();

    // ================================================================ capabilities

    public async Task<JsonNode> CapabilitiesAsync()
    {
        var busy = _lease.Current;
        var activeRunId = busy is { Kind: WorkLease.Bench } ? busy.Id : null;
        if (!_options.Available)
            return new JsonObject
            {
                ["power"] = new JsonObject { ["ac"] = "unknown", ["battery"] = "unknown" },
                ["parts"] = new JsonArray(BenchParts.RunOrder.Select(p => (JsonNode?)PartInfoJson(new BenchPartInfo(p, "", false, "unsupported"))).ToArray()),
                ["sensors"] = new JsonArray(), ["estSeconds"] = 0, ["benchVersion"] = BenchParts.BenchVersion, ["activeRunId"] = activeRunId,
            };

        PowerState power;
        using (var hub = _options.Sensors!.Create()) power = SafePower(hub); // power is read fresh every call
        var (hw, folderOk, sensors) = await CachedDescribeAsync().ConfigureAwait(false);
        var parts = hw is null
            ? BenchParts.RunOrder.Select(p => new BenchPartInfo(p, "", false, "unsupported")).ToList()
            : PartInfos(hw, folderOk);
        var est = Estimate(parts.Where(p => p.Testable).Select(p => p.Id).ToList());
        if (busy is not null) parts = parts.Select(p => p.Testable ? p with { Testable = false, Reason = "busy" } : p).ToList();
        return new JsonObject
        {
            ["power"] = new JsonObject { ["ac"] = power.Ac, ["battery"] = KnownBattery(power) ? "present" : power.Battery },
            ["parts"] = new JsonArray(parts.Select(p => (JsonNode?)PartInfoJson(p)).ToArray()),
            ["sensors"] = new JsonArray(sensors.Select(s => (JsonNode?)s.DeepClone()).ToArray()),
            ["estSeconds"] = est, ["benchVersion"] = BenchParts.BenchVersion, ["activeRunId"] = activeRunId,
        };
    }

    // ---------------------------------------------------------------- capabilities cache (hardware + sensor slots)

    /// <summary>How long a hardware description serves bench.capabilities before it is read again.</summary>
    public static readonly TimeSpan CapabilitiesCacheLifetime = TimeSpan.FromSeconds(60);

    private sealed record Described(BenchHardware? Hw, bool FolderOk, IReadOnlyList<JsonObject> Sensors);

    private readonly object _describeGate = new();
    private Task<Described>? _described;
    private DateTimeOffset _describedAt;

    /// <summary>One shared describe + sensor prepare per host session, refreshed after 60 s or after a run; concurrent callers share it.</summary>
    private Task<Described> CachedDescribeAsync()
    {
        lock (_describeGate)
        {
            if (_described is { } task && (!task.IsCompleted || (task.IsCompletedSuccessfully && DateTimeOffset.UtcNow - _describedAt < CapabilitiesCacheLifetime)))
                return task;
            _describedAt = DateTimeOffset.UtcNow;
            return _described = Task.Run(DescribeForCapabilitiesAsync);
        }
    }

    /// <summary>A run may change what the PC reports (drivers load, free space): the next capabilities call reads again.</summary>
    private void InvalidateCapabilities()
    {
        lock (_describeGate) if (_described is { IsCompleted: true }) _described = null;
    }

    private async Task<Described> DescribeForCapabilitiesAsync()
    {
        var (hw, folderOk) = await DescribeAsync().ConfigureAwait(false);
        var sensors = new List<JsonObject>();
        if (hw is null) return new Described(null, folderOk, sensors);
        using var hub = _options.Sensors!.Create();
        foreach (var part in PartInfos(hw, folderOk).Where(p => p.Testable))
        {
            IReadOnlyList<MetricSlot> slots;
            try { slots = hub.Prepare(part.Id, hw); }
            catch (Exception ex) { _log($"bench sensors {part.Id}: {ex.GetType().Name}"); continue; }
            foreach (var s in slots.Where(s => s.Available && s.Source != "worker"))
                sensors.Add(new JsonObject { ["id"] = $"{part.Id}.{s.Id}", ["source"] = s.Source, ["unit"] = s.Unit });
        }
        return new Described(hw, folderOk, sensors);
    }

    // ---------------------------------------------------------------- power

    private volatile bool _sawBattery;

    /// <summary>
    /// A battery is known when this reading says present, or any reading this host session did (a failed
    /// GetSystemPowerStatus reads unknown/unknown and must not hide a laptop's battery).
    /// </summary>
    private bool KnownBattery(PowerState p)
    {
        if (p.Battery == "present") _sawBattery = true;
        return _sawBattery;
    }

    /// <summary>§2: AC offline, or AC unknown on a PC known to have a battery. No battery known and AC unknown → allowed.</summary>
    internal static bool Blocks(PowerState p, bool knownBattery) => p.Ac == "offline" || (p.Ac == "unknown" && (knownBattery || p.Battery == "present"));

    private static JsonObject PartInfoJson(BenchPartInfo p) => new()
    {
        ["id"] = p.Id, ["device"] = p.Device, ["testable"] = p.Testable, ["reason"] = p.Reason,
    };

    private PowerState SafePower(ISensorHub hub)
    {
        try { return hub.Power(); }
        catch (Exception ex) { _log($"bench power: {ex.GetType().Name}"); return new PowerState("unknown", "unknown"); }
    }

    /// <summary>Hardware facts (≤ 5 s) and whether the bench folder is usable (not a reparse point).</summary>
    private async Task<(BenchHardware? Hw, bool FolderOk)> DescribeAsync(CancellationToken ct = default)
    {
        string folder;
        var folderOk = true;
        try { folder = BenchFolder.Prepare(_dataDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"bench folder refused: {ex.GetType().Name}");
            folder = _dataDir;
            folderOk = false;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(T.HardwareTimeout);
        try
        {
            var hw = await _options.Hardware!.DescribeAsync(folder, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            return (hw, folderOk);
        }
        catch (Exception ex)
        {
            _log($"bench hardware: {ex.GetType().Name}");
            return (null, folderOk);
        }
    }

    public const long MinFreeAfterFile = 4L << 30, DiskFileBytes = 1L << 30, RamCap = 1L << 30, RamMinimum = 256L << 20;

    /// <summary>RAM test budget (§4): the smaller of 1 GiB and 20% of available memory.</summary>
    public static long RamBudget(BenchHardware hw) => Math.Min(RamCap, hw.AvailableMemoryBytes / 5);

    /// <summary>Which parts this PC can test, with the start screen's device names.</summary>
    public static List<BenchPartInfo> PartInfos(BenchHardware hw, bool folderOk) =>
    [
        new(BenchParts.Ram, RamDevice(hw), RamBudget(hw) >= RamMinimum, RamBudget(hw) >= RamMinimum ? null : "low_memory"),
        new(BenchParts.Cpu, hw.CpuModel, true, null),
        hw.GpuModel is { Length: > 0 } gpu && hw.GpuLuid is not null
            ? new(BenchParts.Gpu, gpu, true, null)
            : new(BenchParts.Gpu, hw.GpuModel ?? "", false, "no_gpu"),
        !folderOk ? new(BenchParts.Disk, DiskDevice(hw), false, "unsupported")
            : hw.DiskFreeBytes < MinFreeAfterFile + DiskFileBytes ? new(BenchParts.Disk, DiskDevice(hw), false, "needs_space")
            : new(BenchParts.Disk, DiskDevice(hw), true, null),
    ];

    private static string RamDevice(BenchHardware hw)
    {
        var gb = Math.Round(hw.RamBytes / (double)(1L << 30));
        var text = string.Create(CultureInfo.InvariantCulture, $"{gb:0} GB");
        if (hw.RamConfiguredMTs is { } mts and > 0) text += string.Create(CultureInfo.InvariantCulture, $" {mts} MT/s"); // no module model is read
        return text;
    }

    /// <summary>Model names only (lead, m6 UI): the drive model, else its media type.</summary>
    private static string DiskDevice(BenchHardware hw) => hw.DiskModel is { Length: > 0 } model ? model : hw.DiskMedia switch
    {
        "nvme" => "NVMe SSD", "ssd" => "SSD", "hdd" => "HDD", _ => "Drive",
    };

    private int Estimate(IReadOnlyList<string> parts) =>
        (int)Math.Ceiling(parts.Sum(p => T.PlannedFor(p).TotalSeconds) + Math.Max(0, parts.Count - 1) * T.CoolDown.TotalSeconds);

    // ================================================================ start / get / cancel / list / evidence

    public async Task<JsonNode> StartAsync(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        // The key is checked before anything else (protocol §4): a replay returns the same run as it is now.
        if (_store.Read(db => db.Scalar("SELECT 1 FROM idempotency WHERE method = 'bench.start' AND key = $k", ("$k", key))) is not null)
        {
            var (replayed, _) = _store.Write(db => Idempotency.Run(db, "bench.start", key, p, () => throw new InvalidOperationException("replay expected")));
            return Get(replayed["runId"]!.GetValue<string>());
        }
        var requested = ParseParts(p);
        if (!_options.Available) throw RpcError.State("unsupported", "Benchmarks are not available in this build.");

        var hub = _options.Sensors!.Create();
        Run? started = null;
        string? leased = null;
        var owned = false; // true once the runner task has the run (and with it the lease and hub)
        try
        {
            var power = SafePower(hub);
            var knownBattery = KnownBattery(power);
            if (Blocks(power, knownBattery)) throw RpcError.State("on_battery", "Plug in to run a benchmark.");
            if (_lease.Current is { } holder) throw WorkLease.BusyError(holder); // early; checked again atomically below
            var (hw, folderOk) = await DescribeAsync().ConfigureAwait(false);
            if (hw is null) throw RpcError.State("unavailable", "The hardware could not be read.");
            var infos = PartInfos(hw, folderOk);
            List<string> parts;
            if (requested is null)
            {
                parts = infos.Where(i => i.Testable).Select(i => i.Id).ToList();
                if (parts.Count == 0) throw RpcError.State("nothing_testable", "No part can be tested on this PC.");
            }
            else
            {
                var bad = infos.FirstOrDefault(i => requested.Contains(i.Id) && !i.Testable);
                if (bad is not null) throw RpcError.Params("parts", $"{bad.Id} cannot be tested here ({bad.Reason}).");
                parts = BenchParts.RunOrder.Where(requested.Contains).ToList();
            }
            var configId = BenchScoring.ConfigId(hw, OptimusVersion);
            var est = Estimate(parts);

            var (result, _) = _store.Write(db => Idempotency.Run(db, "bench.start", key, p, () =>
            {
                var runId = "bench-" + Ids.RandomHex();
                _lease.Acquire(WorkLease.Bench, runId); // -32004 scan_active / -32013 bench_active | action_active
                leased = runId;
                var body = new JsonObject
                {
                    ["runId"] = runId, ["state"] = "starting", ["reason"] = null, ["startedAt"] = Now(db), ["finishedAt"] = null,
                    ["benchVersion"] = BenchParts.BenchVersion, ["configId"] = configId, ["estSeconds"] = est,
                    ["power"] = new JsonObject { ["ac"] = power.Ac, ["battery"] = knownBattery ? "present" : power.Battery },
                    ["tests"] = new JsonArray(parts.Select(id => (JsonNode?)new JsonObject
                    {
                        ["part"] = id, ["device"] = infos.First(i => i.Id == id).Device, ["state"] = "queued", ["reason"] = null,
                    }).ToArray()),
                    ["metrics"] = new JsonObject(),
                    ["result"] = null,
                };
                db.Exec("INSERT INTO bench_runs (run_id, state, started_at, bench_version, config_id, body) VALUES ($r, 'starting', $s, $v, $c, $b)",
                    ("$r", runId), ("$s", body.Text("startedAt")), ("$v", BenchParts.BenchVersion), ("$c", configId), ("$b", Json.Write(body)));
                db.AppendEvent(Stream(runId), "bench.progress", ProgressPayload(body));
                started = new Run(runId, body, hub, hw, parts, DateTimeOffset.UtcNow.AddSeconds(2 * est), T.SampleInterval.TotalSeconds) { KnownBattery = knownBattery };
                return new JsonObject { ["runId"] = runId };
            }));

            if (started is not null)
            {
                lock (_gate)
                {
                    _runs[started.RunId] = started;
                    started.Task = Task.Run(() => RunAsync(started));
                    owned = true;
                }
            }
            else hub.Dispose(); // a concurrent replay won
            return Get(result["runId"]!.GetValue<string>());
        }
        catch
        {
            // Until the runner owns the run, every failure (the idempotency insert or the commit included, after the
            // lambda already set started) releases the lease and the hub; the rolled-back run never existed.
            if (!owned)
            {
                if (leased is not null) _lease.Release(WorkLease.Bench, leased);
                hub.Dispose();
            }
            throw;
        }
    }

    /// <summary>parts: a non-empty array of distinct part ids; null when omitted (all testable parts).</summary>
    private static List<string>? ParseParts(JsonObject p)
    {
        if (p["parts"] is null) return null;
        if (p["parts"] is not JsonArray a || a.Count == 0 || a.Count > BenchParts.RunOrder.Count)
            throw RpcError.Params("parts", "parts must be a non-empty array of cpu, gpu, ram or disk.");
        var ids = new List<string>();
        foreach (var x in a)
        {
            if (x is not JsonValue v || !v.TryGetValue<string>(out var s) || !BenchParts.RunOrder.Contains(s))
                throw RpcError.Params("parts", "parts must be a non-empty array of cpu, gpu, ram or disk.");
            if (ids.Contains(s)) throw RpcError.Params("parts", "parts must not repeat a part.");
            ids.Add(s);
        }
        return ids;
    }

    public JsonObject Get(JsonObject p) => Get(P.Req(p, "runId", 64));

    /// <summary>bench.get: the stored run plus, while it runs, the live view and the current part's progress.</summary>
    public JsonObject Get(string runId)
    {
        Run? run;
        lock (_gate) _runs.TryGetValue(runId, out run);
        var (body, seq) = _store.Read(db => (LoadBody(db, runId), db.LastSeq(Stream(runId))));
        var view = (JsonObject)body.DeepClone();
        view["live"] = run?.Live.View();
        var progress = run?.Live.Progress();
        foreach (var test in view["tests"]!.AsArray().Select(t => t!.AsObject()))
        {
            var live = progress is not null && test.Text("part") == progress.Part && test.Text("state") == "running";
            test["progress"] = live ? progress!.Value : test.Text("state") is "done" or "failed" or "cancelled" or "skipped" ? 1.0 : 0.0;
            test["liveScore"] = live ? progress!.LiveScore : null;
            test["etaSec"] = live ? progress!.Eta : null;
            if (live) test["subtest"] = progress!.Subtest;
        }
        view["eventCursor"] = new JsonObject { ["stream"] = Stream(runId), ["seq"] = seq };
        return view;
    }

    private static JsonObject LoadBody(Db db, string runId)
    {
        var text = db.Scalar("SELECT body FROM bench_runs WHERE run_id = $r", ("$r", runId)) as string ?? throw RpcError.NotFound("benchmark", runId);
        try { return Json.Parse(text) as JsonObject ?? throw RpcError.Corrupt("benchmark", runId); }
        catch (System.Text.Json.JsonException) { throw RpcError.Corrupt("benchmark", runId); }
    }

    /// <summary>bench.cancel: starting|running → stopping at once; the coordinator stops the worker and ends cancelled.</summary>
    public JsonNode Cancel(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var runId = P.Req(p, "runId", 64);
        Run? signal = null;
        _store.Write(db => Idempotency.Run(db, "bench.cancel", key, p, () =>
        {
            LoadBody(db, runId); // not found → -32001
            Run? run;
            lock (_gate) _runs.TryGetValue(runId, out run);
            if (run is not null && run.TryStop("cancel"))
            {
                SetStoppingInTx(db, run);
                signal = run;
            }
            return new JsonObject { ["runId"] = runId };
        }));
        signal?.SignalStop(); // outside the store lock: continuations may run inline
        return Get(runId);
    }

    /// <summary>bench.list: done runs only, newest first.</summary>
    public JsonNode List(JsonObject p)
    {
        var limit = P.Int(p, "limit", required: false) ?? 20;
        if (limit is < 1 or > MaxList) throw RpcError.Params("limit", $"limit must be 1 to {MaxList}.");
        var before = P.Str(p, "before", required: false, maxLength: 64);
        return _store.Read(db =>
        {
            long? beforeRow = null;
            if (before is not null)
                beforeRow = db.Scalar("SELECT rowid FROM bench_runs WHERE run_id = $r AND state = 'done'", ("$r", before)) as long? ?? throw RpcError.NotFound("benchmark", before);
            var rows = db.Query("SELECT run_id, finished_at, score, partial, parts, bench_version, config_id FROM bench_runs WHERE state = 'done' AND ($b IS NULL OR rowid < $b) ORDER BY rowid DESC LIMIT $l",
                r => (Id: r.GetString(0), Finished: r.IsDBNull(1) ? null : r.GetString(1), Score: r.IsDBNull(2) ? (double?)null : r.GetDouble(2),
                    Partial: r.GetInt64(3) != 0, Parts: r.GetString(4), Version: r.GetString(5), Config: r.IsDBNull(6) ? null : r.GetString(6)),
                ("$b", beforeRow), ("$l", limit + 1));
            return (JsonNode)new JsonObject
            {
                ["runs"] = new JsonArray(rows.Take((int)limit).Select(r => (JsonNode?)new JsonObject
                {
                    ["runId"] = r.Id, ["finishedAt"] = r.Finished, ["score"] = r.Score, ["partial"] = r.Partial,
                    ["parts"] = Json.Array(SplitParts(r.Parts)), ["benchVersion"] = r.Version, ["configId"] = r.Config,
                }).ToArray()),
                ["next"] = rows.Count > limit ? rows[(int)limit - 1].Id : null,
            };
        });
    }

    /// <summary>bench.evidence: one benchmark evidence item (the citation target for [ev-…] from a benchmark).</summary>
    public JsonNode Evidence(JsonObject p)
    {
        var runId = P.Req(p, "runId", 64);
        var evidenceId = P.Req(p, "evidenceId", 64);
        return _store.Read(db =>
        {
            LoadBody(db, runId);
            var body = db.Scalar("SELECT body FROM bench_evidence WHERE evidence_id = $e AND run_id = $r", ("$e", evidenceId), ("$r", runId)) as string
                ?? throw RpcError.NotFound("evidence", evidenceId);
            return Json.Parse(body)!;
        });
    }

    private static List<string> SplitParts(string parts) => parts.Length == 0 ? [] : [.. parts.Split(',')];

    // ================================================================ restart, shutdown, pruning

    /// <summary>Host start: any starting|running|stopping row becomes interrupted (the worker died with the old host).</summary>
    public void RecoverInterrupted()
    {
        var stale = _store.Read(db => db.Query("SELECT run_id FROM bench_runs WHERE state IN ('starting','running','stopping')", r => r.GetString(0)));
        foreach (var runId in stale)
        {
            _store.Write(db =>
            {
                var body = LoadBody(db, runId);
                CloseParts(body, "interrupted");
                body["state"] = "interrupted";
                body["reason"] = "interrupted";
                body["finishedAt"] = Now(db);
                Save(db, body);
                db.AppendEvent(Stream(runId), "bench.progress", ProgressPayload(body));
                db.AppendEvent(Stream(runId), "bench.finished", new JsonObject { ["state"] = "interrupted", ["score"] = null });
            });
            _log($"benchmark {runId} marked interrupted");
        }
        _store.Write(Prune);
    }

    /// <summary>Runs that never finished are kept 7 days for diagnostics, then removed with their events.</summary>
    private static void Prune(Db db)
    {
        var cutoff = DateTimeOffset.Parse(db.Now(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) - KeepUnfinished;
        var old = db.Query("SELECT run_id FROM bench_runs WHERE state NOT IN ('done','starting','running','stopping') AND started_at < $c",
            r => r.GetString(0), ("$c", Time.Format(cutoff)));
        foreach (var id in old)
        {
            db.Exec("DELETE FROM bench_evidence WHERE run_id = $r", ("$r", id));
            db.Exec("DELETE FROM events WHERE stream = $s", ("$s", Stream(id)));
            db.Exec("DELETE FROM bench_runs WHERE run_id = $r", ("$r", id));
        }
    }

    public async Task StopAllAsync()
    {
        Run[] runs;
        lock (_gate) runs = [.. _runs.Values];
        foreach (var run in runs)
            if (run.TryStop("shutdown")) run.SignalStop();
        try { await Task.WhenAll(runs.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (Exception ex) { _log($"stopping benchmarks: {ex.GetType().Name}"); }
    }

    // ================================================================ persistence helpers

    private static void Save(Db db, JsonObject body)
    {
        var result = body["result"] as JsonObject;
        var parts = result?["partsScored"] is JsonArray scored ? string.Join(',', Json.Strings(scored).Order(StringComparer.Ordinal)) : "";
        db.Exec("UPDATE bench_runs SET state = $s, finished_at = $f, score = $sc, partial = $p, parts = $pa, body = $b WHERE run_id = $r",
            ("$s", body.Text("state")), ("$f", body["finishedAt"]?.GetValue<string>()), ("$sc", result?["score"] is JsonValue v ? Json.Number(v) : null),
            ("$p", result?["partial"]?.GetValue<bool>() == true ? 1 : 0), ("$pa", parts), ("$b", Json.Write(body)), ("$r", body.Text("runId")));
    }

    private static JsonObject ProgressPayload(JsonObject body) => new()
    {
        ["state"] = body.Text("state"), ["reason"] = body["reason"]?.DeepClone(),
        ["tests"] = new JsonArray(body["tests"]!.AsArray().Select(x => (JsonNode?)new JsonObject
        {
            ["part"] = x.Text("part"), ["state"] = x.Text("state"), ["reason"] = x!["reason"]?.DeepClone(),
        }).ToArray()),
    };

    /// <summary>Mutates the run's body under its lock and saves it with one bench.progress event, in one transaction.</summary>
    private void Update(Run run, Action<JsonObject> mutate) => _store.Write(db =>
    {
        lock (run.Gate)
        {
            mutate(run.Body);
            Save(db, run.Body);
            db.AppendEvent(Stream(run.RunId), "bench.progress", ProgressPayload(run.Body));
        }
    });

    private static void SetStoppingInTx(Db db, Run run)
    {
        lock (run.Gate)
        {
            if (run.Body.Text("state") is not ("starting" or "running")) return;
            run.Body["state"] = "stopping";
            Save(db, run.Body);
            db.AppendEvent(Stream(run.RunId), "bench.progress", ProgressPayload(run.Body));
        }
    }

    private static JsonObject PartBody(JsonObject body, string part) =>
        body["tests"]!.AsArray().Select(x => x!.AsObject()).First(x => x.Text("part") == part);

    /// <summary>Unfinished tests at the end of a run: running → cancelled, queued → skipped, both with the reason.</summary>
    private static void CloseParts(JsonObject body, string reason)
    {
        foreach (var part in body["tests"]!.AsArray().Select(x => x!.AsObject()))
        {
            if (part.Text("state") == "running") { part["state"] = "cancelled"; part["reason"] = reason; }
            else if (part.Text("state") == "queued") { part["state"] = "skipped"; part["reason"] = reason; }
            part.Remove("progress");
        }
    }
}

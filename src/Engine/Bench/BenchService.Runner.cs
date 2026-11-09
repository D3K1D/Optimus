using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace OptimizationTool.Engine.Bench;

// The run coordinator (§2 lifecycle, §4 supervision, §6 stop rules): parts in order RAM → CPU → GPU → disk with a
// cool-down between them, one worker per run (relaunched after a kill), a heartbeat watchdog, a hard deadline per part,
// a dedicated 1 Hz sampler thread per part with a per-call timeout, thermal and power stops, and at most one repeat to
// confirm a regression.
public sealed partial class BenchService
{
    /// <summary>The live view's current progress (not persisted).</summary>
    private sealed record LiveProgress(string Part, double Value, string? Subtest, int? Eta, double? LiveScore);

    /// <summary>In-memory ring buffer: ≤ 60 samples per metric for the part being tested.</summary>
    private sealed class LiveBuffer
    {
        private readonly object _g = new();
        private string? _part, _t0;
        private IReadOnlyList<MetricSlot> _slots = [];
        private readonly Dictionary<string, List<double?>> _samples = [];
        private readonly Dictionary<string, double> _worker = [];
        private LiveProgress? _progress;

        public void Reset(string part, IReadOnlyList<MetricSlot> slots, string t0)
        {
            lock (_g)
            {
                _part = part;
                _t0 = t0;
                _slots = slots;
                _samples.Clear();
                _worker.Clear();
                _progress = null;
                foreach (var s in slots) _samples[s.Id] = [];
            }
        }

        /// <summary>A worker "live" event: ram read|write|copy|latency, disk read|write|random4k.</summary>
        public void SetWorker(JsonObject? values)
        {
            if (values is null) return;
            lock (_g)
                foreach (var (k, v) in values)
                    if (v is JsonValue jv && Json.Number(jv) is { } d && double.IsFinite(d)) _worker[k] = d;
        }

        public void SetProgress(string part, double value, string? subtest, int? eta, double? liveScore)
        {
            lock (_g) _progress = new LiveProgress(part, Math.Clamp(value, 0, 1), subtest, eta, liveScore);
        }

        public LiveProgress? Progress()
        {
            lock (_g) return _progress;
        }

        public void Push(SensorSample sample)
        {
            lock (_g)
            {
                if (_part is null) return;
                foreach (var slot in _slots)
                {
                    double? value = slot.Source == "worker"
                        ? _worker.TryGetValue(slot.Id, out var w) ? w : null
                        : slot.Available ? BenchTelemetry.Value(sample, _part, slot.Id) : null;
                    if (value is { } v && !double.IsFinite(v)) value = null;
                    var list = _samples[slot.Id];
                    list.Add(value is { } x ? Math.Round(x, 2) : null);
                    if (list.Count > LiveSamples) list.RemoveAt(0);
                }
            }
        }

        public JsonObject? View()
        {
            lock (_g)
            {
                if (_part is null) return null;
                var samples = new JsonObject();
                foreach (var (id, list) in _samples) samples[_part + "." + id] = new JsonArray(list.Select(v => (JsonNode?)(v is { } d ? JsonValue.Create(d) : null)).ToArray());
                return new JsonObject { ["part"] = _part, ["t0"] = _t0, ["score"] = _progress?.LiveScore, ["samples"] = samples };
            }
        }
    }

    /// <summary>One part's outcome as the coordinator saw it.</summary>
    private sealed class PartResult(string state, string? reason, PartTelemetry telemetry)
    {
        public string State { get; } = state;
        public string? Reason { get; } = reason;
        public Dictionary<string, double> Subtests { get; } = [];
        public PartTelemetry Telemetry { get; set; } = telemetry;
        public IReadOnlyList<MetricSlot> Slots { get; set; } = [];
    }

    private sealed record WorkerMsg(JsonObject? Msg, string? Error);

    /// <summary>A launched worker with its reader: heartbeats update the watchdog clock, other messages queue.</summary>
    private sealed class WorkerConn : IDisposable
    {
        private readonly CancellationTokenSource _life = new();
        private long _lastBeat = Stopwatch.GetTimestamp();

        public IBenchWorker Worker { get; }
        public Channel<WorkerMsg> Messages { get; } = Channel.CreateUnbounded<WorkerMsg>();

        public WorkerConn(IBenchWorker worker)
        {
            Worker = worker;
            _ = Task.Run(ReadLoopAsync);
        }

        public TimeSpan SinceHeartbeat => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastBeat));
        public void Beat() => Interlocked.Exchange(ref _lastBeat, Stopwatch.GetTimestamp());

        private async Task ReadLoopAsync()
        {
            try
            {
                while (true)
                {
                    var line = await Worker.ReadLineAsync(_life.Token).ConfigureAwait(false);
                    if (line is null) { Messages.Writer.TryWrite(new(null, "worker_exited")); return; }
                    Beat(); // any line is a sign of life
                    JsonObject? msg;
                    try { msg = Json.Parse(line) as JsonObject; }
                    catch (System.Text.Json.JsonException) { msg = null; }
                    if (msg?["ev"] is not JsonValue ev || !ev.TryGetValue<string>(out var kind))
                    {
                        Messages.Writer.TryWrite(new(null, "protocol"));
                        Worker.Kill();
                        return;
                    }
                    if (kind == "hb") continue;
                    Messages.Writer.TryWrite(new(msg, null));
                }
            }
            catch (OperationCanceledException) { }
            catch (InvalidDataException)
            {
                Messages.Writer.TryWrite(new(null, "protocol")); // a line over 64 KiB
                Worker.Kill();
            }
            catch (Exception)
            {
                Messages.Writer.TryWrite(new(null, "worker_exited"));
            }
        }

        /// <summary>The next message, or null when none arrives within <paramref name="wait"/>.</summary>
        public async Task<WorkerMsg?> NextAsync(TimeSpan wait)
        {
            using var cts = new CancellationTokenSource(wait);
            try { return await Messages.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (ChannelClosedException) { return new(null, "worker_exited"); }
        }

        public void Dispose()
        {
            _life.Cancel();
            Worker.Kill();
            Worker.Dispose();
        }
    }

    private sealed class Run(string runId, JsonObject body, ISensorHub hub, BenchHardware hw, IReadOnlyList<string> parts, DateTimeOffset deadline, double sampleSeconds)
    {
        public object Gate { get; } = new();
        public string RunId { get; } = runId;
        public JsonObject Body { get; } = body;
        public ISensorHub Hub { get; } = hub;
        public BenchHardware Hw { get; } = hw;
        public IReadOnlyList<string> Parts { get; } = parts;
        public DateTimeOffset Deadline { get; } = deadline;
        public double SampleSeconds { get; } = sampleSeconds;
        public Task Task { get; set; } = Task.CompletedTask;
        public LiveBuffer Live { get; } = new();
        public WorkerConn? Worker { get; set; }
        public Task<SensorSample>? PendingRead { get; set; }
        public Dictionary<string, PartResult> Results { get; } = [];
        public Dictionary<string, RepeatOutcome> Repeats { get; } = [];
        public bool RepeatUsed { get; set; }
        /// <summary>A battery was seen at the start or by any reading during the run.</summary>
        public volatile bool KnownBattery;
        public CancellationTokenSource StopCts { get; } = new();

        private string? _stop;
        /// <summary>cancel | battery | shutdown, first one wins.</summary>
        public string? StopKind => Volatile.Read(ref _stop);
        public bool TryStop(string kind) => Interlocked.CompareExchange(ref _stop, kind, null) is null;

        /// <summary>Wakes the coordinator. Cancelled on the thread pool: continuations never run on the caller's thread.</summary>
        public void SignalStop() => _ = Task.Run(StopCts.Cancel);
    }

    // ================================================================ the run

    private async Task RunAsync(Run run)
    {
        void OnPowerChanged(PowerState s) => OnPower(run, s);
        run.Hub.PowerChanged += OnPowerChanged;
        string? forced = null;
        try
        {
            if (run.StopKind is null) Update(run, b => { if (b.Text("state") == "starting") b["state"] = "running"; });
            for (var i = 0; i < run.Parts.Count; i++)
            {
                if (run.StopKind is not null) break;
                if (i > 0 && !await CoolDownAsync(run).ConfigureAwait(false)) break;
                var part = run.Parts[i];
                var result = await RunPartAsync(run, part, repeat: null).ConfigureAwait(false);
                run.Results[part] = result;
                if (run.StopKind is not null && result.State == "cancelled") break; // Finish closes it with the stop reason
                Update(run, b =>
                {
                    var pb = PartBody(b, part);
                    pb["state"] = result.State;
                    pb["reason"] = result.Reason;
                    pb.Remove("progress");
                });
            }
            if (run.StopKind is null) await RepeatAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"benchmark {run.RunId} failed: {ex}");
            forced = "failed";
        }
        finally
        {
            run.Hub.PowerChanged -= OnPowerChanged;
            await CloseWorkerAsync(run).ConfigureAwait(false);
        }
        try { Finish(run, forced); }
        catch (Exception ex) { _log($"benchmark {run.RunId} could not be finished: {ex.GetType().Name}"); }
        finally
        {
            lock (_gate) _runs.Remove(run.RunId);
            _lease.Release(WorkLease.Bench, run.RunId);
            InvalidateCapabilities(); // the next bench.capabilities reads the hardware again
            try { run.Hub.Dispose(); } catch (Exception ex) { _log($"sensor hub dispose: {ex.GetType().Name}"); }
        }
    }

    /// <summary>AC offline (or status lost with a battery) → stopping at once, then stopped_on_battery.</summary>
    private void OnPower(Run run, PowerState state)
    {
        if (state.Battery == "present") { run.KnownBattery = true; _sawBattery = true; }
        if (_sawBattery) run.KnownBattery = true; // a battery found by any reading this session (e.g. bench.capabilities) counts for the run
        // AC unknown on a PC known to have a battery (from the start or any reading since) stops too.
        if (!Blocks(state, run.KnownBattery) || !run.TryStop("battery")) return;
        try { _store.Write(db => SetStoppingInTx(db, run)); }
        catch (Exception ex) { _log($"benchmark {run.RunId} stopping: {ex.GetType().Name}"); }
        run.SignalStop();
    }

    /// <summary>The 5 s cool-down between parts, watching power. False when the run is stopping.</summary>
    private async Task<bool> CoolDownAsync(Run run)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < T.CoolDown)
        {
            if (run.StopKind is not null) return false;
            var power = SafePower(run.Hub);
            OnPower(run, power);
            if (run.StopKind is not null) return false;
            var wait = T.CoolDown - sw.Elapsed;
            try { await Task.Delay(wait < T.SampleInterval ? wait : T.SampleInterval, run.StopCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return run.StopKind is null;
    }

    private IReadOnlyList<MetricSlot> PrepareSlots(Run run, string part)
    {
        try
        {
            var task = Task.Run(() => run.Hub.Prepare(part, run.Hw));
            if (task.Wait(T.HardwareTimeout)) return task.Result;
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // abandoned: observe a late failure
        }
        catch (Exception ex) { _log($"bench prepare {part}: {(ex is AggregateException a ? a.InnerException?.GetType().Name : ex.GetType().Name)}"); }
        return [];
    }

    /// <summary>BenchRun.metrics[part]: the 4 slots with part-prefixed ids (cpu.clock, disk.random4k).</summary>
    private static JsonArray SlotsJson(string part, IReadOnlyList<MetricSlot> slots) => new(slots.Select(s => (JsonNode?)new JsonObject
    {
        ["id"] = part + "." + s.Id, ["unit"] = s.Unit, ["source"] = s.Source, ["max"] = s.Max, ["expected"] = s.Expected, ["limit"] = s.Limit,
        ["available"] = s.Available, ["reason"] = s.Reason,
    }).ToArray());

    /// <summary>One part (or, with <paramref name="repeat"/>, one subtest repeated): sampler thread + worker supervision.</summary>
    private async Task<PartResult> RunPartAsync(Run run, string part, string? repeat)
    {
        var telemetry = new PartTelemetry(part) { SampleSeconds = run.SampleSeconds };
        var slots = PrepareSlots(run, part);
        if (repeat is null)
            Update(run, b =>
            {
                PartBody(b, part)["state"] = "running";
                b["metrics"]![part] = SlotsJson(part, slots);
            });
        run.Live.Reset(part, slots, Time.Format(DateTimeOffset.UtcNow));

        using var partStop = new CancellationTokenSource();
        using var samplerStop = new CancellationTokenSource();
        var thermal = new ThermalStopRule();
        var slowdownLimit = slots.FirstOrDefault(s => s.Id == "temp" && s.Available)?.Limit; // the hub puts NVML slowdown there
        var sampler = new Thread(() => SamplerLoop(run, part, telemetry, slots, thermal, slowdownLimit, partStop, samplerStop.Token))
        {
            IsBackground = true, Name = "bench-sampler",
        };
        sampler.Start();
        PartResult result;
        try
        {
            result = await DrivePartAsync(run, part, repeat, telemetry, partStop).ConfigureAwait(false);
        }
        finally
        {
            samplerStop.Cancel();
            sampler.Join(T.SensorReadTimeout + T.SampleInterval + TimeSpan.FromSeconds(1));
        }
        lock (telemetry.Samples) telemetry.ThermalStop = thermal.Tripped;
        result.Telemetry = telemetry;
        result.Slots = slots;
        return result;
    }

    /// <summary>
    /// The sampler thread (§4): 1 Hz, independent of RPC and the watchdog. Each read has its own timeout; a read that
    /// is still running is not called again until it returns (the hub marks a hung source unavailable itself).
    /// </summary>
    private void SamplerLoop(Run run, string part, PartTelemetry telemetry, IReadOnlyList<MetricSlot> slots, ThermalStopRule thermal, double? slowdownLimit,
        CancellationTokenSource partStop, CancellationToken stop)
    {
        var empty = new SensorSample(new Dictionary<string, double?>(), null, null);
        while (!stop.IsCancellationRequested)
        {
            var tick = Stopwatch.StartNew();
            try
            {
                var power = SafePower(run.Hub);
                OnPower(run, power);

                SensorSample sample = empty;
                run.PendingRead ??= Task.Run(run.Hub.Read);
                var pending = run.PendingRead;
                if (pending.Wait(T.SensorReadTimeout))
                {
                    run.PendingRead = null;
                    if (pending.IsCompletedSuccessfully) sample = pending.Result;
                }
                lock (telemetry.Samples) telemetry.Samples.Add(sample);
                run.Live.Push(sample);
                if (sample.Failed is { Count: > 0 } failed) NoteFailedSlots(run, part, telemetry, slots, failed);
                if (part == BenchParts.Gpu
                    && thermal.Observe(BenchTelemetry.Value(sample, part, "temp"), sample.Gpu?.SlowdownTempC ?? slowdownLimit)
                    && !partStop.IsCancellationRequested)
                    partStop.Cancel(); // polled by the coordinator; never repeated
            }
            catch (AggregateException) { run.PendingRead = null; }
            catch (Exception ex) { _log($"bench sampler: {ex.GetType().Name}"); }
            var rest = T.SampleInterval - tick.Elapsed;
            if (rest > TimeSpan.Zero) stop.WaitHandle.WaitOne(rest);
        }
    }

    /// <summary>A source the hub disabled mid-run: its slots turn unavailable (source_failed) in the live metric metadata
    /// at once, and the final result lists them under missingSensors.</summary>
    private void NoteFailedSlots(Run run, string part, PartTelemetry telemetry, IReadOnlyList<MetricSlot> slots, IReadOnlyCollection<string> failed)
    {
        var added = false;
        HashSet<string> all;
        lock (telemetry.Samples)
        {
            foreach (var id in failed) added |= telemetry.FailedSlots.Add(id);
            all = [.. telemetry.FailedSlots];
        }
        if (added) Update(run, b => b["metrics"]![part] = SlotsJson(part, WithFailures(slots, all)));
    }

    private static List<MetricSlot> WithFailures(IReadOnlyList<MetricSlot> slots, HashSet<string> failed) =>
        slots.Select(s => s.Available && failed.Contains(s.Id) ? s with { Available = false, Reason = MetricSlot.SourceFailed } : s).ToList();

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private async Task<PartResult> DrivePartAsync(Run run, string part, string? repeat, PartTelemetry telemetry, CancellationTokenSource partStop)
    {
        PartResult Failed(string reason) => new("failed", reason, telemetry);
        var planned = repeat is null ? T.PlannedFor(part) : T.Repeat;
        var conn = await EnsureWorkerAsync(run).ConfigureAwait(false);
        if (conn is null) return Failed("worker_unavailable");

        var cmd = new JsonObject
        {
            ["cmd"] = "run", ["part"] = part, ["benchVersion"] = BenchParts.BenchVersion,
            ["folder"] = part == BenchParts.Disk ? Path.Combine(Path.GetFullPath(_dataDir), BenchFolder.Name) : null,
            ["ramBudgetBytes"] = part == BenchParts.Ram ? RamBudget(run.Hw) : null,
            ["gpuLuid"] = part == BenchParts.Gpu ? run.Hw.GpuLuid : null,
            ["repeat"] = repeat,
        };
        if (!await SendAsync(conn, cmd).ConfigureAwait(false)) { KillWorker(run); return Failed("worker_exited"); }
        conn.Beat(); // the watchdog counts from the command

        var clock = Stopwatch.StartNew();
        string stopReason;
        while (true)
        {
            if (run.StopKind is { } kind) { stopReason = kind; break; }
            if (partStop.IsCancellationRequested) { stopReason = "thermal_stop"; break; }
            if (clock.Elapsed > 2 * planned) { KillWorker(run); return Failed("deadline"); }
            if (conn.SinceHeartbeat > T.HeartbeatTimeout) { KillWorker(run); return Failed("no_heartbeat"); }
            var next = await conn.NextAsync(Tick).ConfigureAwait(false);
            if (next is null) continue;
            if (next.Error is { } error) { KillWorker(run); return Failed(error); }
            var msg = next.Msg!;
            switch (msg["ev"]?.GetValue<string>())
            {
                case "progress":
                    var progress = msg["progress"] is JsonValue pv ? Json.Number(pv) ?? 0 : 0;
                    var eta = msg["etaSec"] is JsonValue ev && Json.Number(ev) is { } e ? (int?)Math.Clamp((int)e, 0, 3600) : null;
                    var subtest = msg["subtest"] is JsonValue sv && sv.TryGetValue<string>(out var st) && st.Length <= 32 ? st : null;
                    // The worker reports a raw measurement (ops/s, GFLOPS, GB/s, MB/s, ns): shown as points for the current subtest.
                    var raw = msg["liveScore"] is JsonValue lv ? Json.Number(lv) : null;
                    var liveScore = subtest is not null && BenchScoring.SubtestScore(part, subtest, raw) is { } pts ? Math.Round(pts) : (double?)null;
                    run.Live.SetProgress(part, progress, subtest, eta, liveScore);
                    break;
                case "live":
                    run.Live.SetWorker(msg["values"] as JsonObject);
                    break;
                case "result":
                    return ParseResult(part, repeat, msg, telemetry);
                case "failed":
                    var reason = msg["reason"] is JsonValue rv && rv.TryGetValue<string>(out var r) && FailReasons.Contains(r) ? r : "error";
                    return Failed(reason);
                case "stopped":
                    return Failed("error"); // stopped without being asked
            }
        }

        // Stopping: tell the worker, give it the grace period, then kill it.
        await SendAsync(conn, new JsonObject { ["cmd"] = "stop" }).ConfigureAwait(false);
        var grace = Stopwatch.StartNew();
        var ended = false;
        while (grace.Elapsed < T.StopGrace && !ended)
        {
            var next = await conn.NextAsync(Tick).ConfigureAwait(false);
            if (next is null) continue;
            if (next.Error is not null) break;
            ended = next.Msg!["ev"]?.GetValue<string>() is "stopped" or "result" or "failed";
        }
        if (!ended) KillWorker(run);
        var stoppedReason = stopReason switch
        {
            "cancel" => "cancelled", "battery" => "on_battery", "shutdown" => "interrupted", _ => stopReason,
        };
        return new PartResult("cancelled", stoppedReason, telemetry);
    }

    private static PartResult ParseResult(string part, string? repeat, JsonObject msg, PartTelemetry telemetry)
    {
        var result = new PartResult("done", null, telemetry);
        var subtests = msg["subtests"] as JsonObject;
        IEnumerable<string> wanted = repeat is null ? BenchSubtests.ByPart[part] : [repeat];
        foreach (var id in wanted)
        {
            if (subtests?[id]?["value"] is not JsonValue v || Json.Number(v) is not { } d || !double.IsFinite(d) || d <= 0)
                return new PartResult("failed", "invalid_result", telemetry);
            result.Subtests[id] = d;
        }
        return result;
    }

    private static async Task<bool> SendAsync(WorkerConn conn, JsonObject cmd)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await conn.Worker.SendAsync(Json.Write(cmd), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The run's worker, launching one (and waiting for its hello) when there is none.</summary>
    private async Task<WorkerConn?> EnsureWorkerAsync(Run run)
    {
        if (run.Worker is { } existing) return existing;
        IBenchWorker worker;
        try { worker = _options.Launcher!.Launch(); }
        catch (Exception ex)
        {
            _log($"bench worker launch failed: {ex.GetType().Name}");
            return null;
        }
        var conn = new WorkerConn(worker);
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < T.HelloTimeout)
        {
            var next = await conn.NextAsync(Tick).ConfigureAwait(false);
            if (next is null) continue;
            if (next.Error is not null) break;
            if (next.Msg!["ev"]?.GetValue<string>() == "hello")
            {
                run.Worker = conn;
                return conn;
            }
        }
        _log("bench worker: no hello");
        conn.Dispose();
        return null;
    }

    private static void KillWorker(Run run)
    {
        var conn = run.Worker;
        run.Worker = null;
        conn?.Dispose();
    }

    private static async Task CloseWorkerAsync(Run run)
    {
        if (run.Worker is not { } conn) return;
        await SendAsync(conn, new JsonObject { ["cmd"] = "exit" }).ConfigureAwait(false);
        KillWorker(run); // the job ends whatever is left
    }

    // ================================================================ slower_than_before confirmation

    /// <summary>A comparable earlier done run's per-part scores and subtests (same benchVersion and configId).</summary>
    private sealed record PastRun(string RunId, Dictionary<string, (double? Score, Dictionary<string, double> Subtests)> Parts);

    /// <summary>The parts this run measured (done and not thermally stopped), sorted: the bench_runs.parts key.</summary>
    private static string ScoredKey(Run run) => string.Join(',', run.Results.Where(kv => Scored(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal));

    /// <summary>A part counts as measured only when it finished and no thermal stop (also in its repeat) invalidated it.</summary>
    private static bool Scored(PartResult r) => r.State == "done" && !r.Telemetry.ThermalStop;

    /// <summary>Comparable history (§1.4): same benchVersion, configId and set of successful parts, as the overall comparison.</summary>
    private static List<PastRun> History(Db db, Run run)
    {
        var rows = db.Query("SELECT run_id, body FROM bench_runs WHERE state = 'done' AND run_id <> $r AND bench_version = $v AND config_id = $c AND parts = $p ORDER BY finished_at DESC, rowid DESC LIMIT 10",
            r => (Id: r.GetString(0), Body: r.GetString(1)), ("$r", run.RunId), ("$v", BenchParts.BenchVersion), ("$c", run.Body.Text("configId")), ("$p", ScoredKey(run)));
        var list = new List<PastRun>();
        foreach (var (id, text) in rows)
        {
            var parts = new Dictionary<string, (double?, Dictionary<string, double>)>();
            try
            {
                foreach (var p in (Json.Parse(text)?["result"]?["parts"] as JsonArray) ?? [])
                {
                    var subtests = new Dictionary<string, double>();
                    if (p?["subtests"] is JsonObject s)
                        foreach (var (k, v) in s) if (v is JsonValue jv && Json.Number(jv) is { } d) subtests[k] = d;
                    parts[p.Text("part")] = (p?["score"] is JsonValue sv ? Json.Number(sv) : null, subtests);
                }
            }
            catch (System.Text.Json.JsonException) { continue; }
            list.Add(new PastRun(id, parts));
        }
        return list;
    }

    private static List<double> PartHistory(List<PastRun> history, string part) =>
        history.Select(h => h.Parts.TryGetValue(part, out var x) ? x.Score : null).OfType<double>().Take(BenchScoring.HistoryRuns).ToList();

    /// <summary>
    /// §6 rule 3: a regressed part's worst subtest is repeated once for 10 s. At most one repeat per run, never for disk,
    /// never after a thermal stop, and only when it fits the run's deadline.
    /// </summary>
    private async Task RepeatAsync(Run run)
    {
        var history = _store.Read(db => History(db, run));
        foreach (var part in run.Parts)
        {
            if (!run.Results.TryGetValue(part, out var r) || r.State != "done" || r.Telemetry.ThermalStop) continue;
            if (!BenchVerdicts.IsRegressed(BenchScoring.PartScore(part, r.Subtests), PartHistory(history, part))) continue;
            if (part == BenchParts.Disk || run.RepeatUsed) { run.Repeats[part] = RepeatOutcome.NotRepeatable; continue; }
            var subtestHistory = history.Select(h => h.Parts.TryGetValue(part, out var x) ? (IReadOnlyDictionary<string, double>)x.Subtests : null)
                .OfType<IReadOnlyDictionary<string, double>>().Where(d => d.Count > 0).ToList();
            var subtest = BenchVerdicts.RegressedSubtest(part, r.Subtests, subtestHistory);
            if (subtest is null || DateTimeOffset.UtcNow + T.CoolDown + 2 * T.Repeat > run.Deadline)
            {
                run.Repeats[part] = RepeatOutcome.NotRepeatable;
                continue;
            }
            run.RepeatUsed = true;
            if (!await CoolDownAsync(run).ConfigureAwait(false)) { run.Repeats[part] = RepeatOutcome.NotRepeatable; return; }
            var again = await RunPartAsync(run, part, subtest).ConfigureAwait(false);
            if (again.Telemetry.ThermalStop)
            {
                // Specific thermal evidence: the repeat's readings (including the ones that tripped the rule) join the part's
                // telemetry, so its figures and evidence show the peak; the part is unscored and its test says why.
                lock (r.Telemetry.Samples)
                lock (again.Telemetry.Samples)
                    r.Telemetry.Samples.AddRange(again.Telemetry.Samples);
                r.Telemetry.ThermalStop = true;
                Update(run, b => PartBody(b, part)["reason"] = "thermal_stop");
            }
            run.Repeats[part] = again.State == "done" && again.Subtests.TryGetValue(subtest, out var value) && !again.Telemetry.ThermalStop
                ? BenchVerdicts.RepeatConfirms(part, subtest, value, subtestHistory.Select(h => h.TryGetValue(subtest, out var x) ? x : double.NaN).ToList())
                    ? RepeatOutcome.Confirmed : RepeatOutcome.Unconfirmed
                : RepeatOutcome.NotRepeatable;
            if (run.StopKind is not null) return;
        }
    }

    // ================================================================ finish, result, evidence

    private void Finish(Run run, string? forced)
    {
        InvalidateCapabilities(); // before bench.finished publishes, so a capabilities call reacting to it reads afresh
        _store.Write(db =>
        {
            lock (run.Gate)
            {
                var body = run.Body;
                var stopReason = run.StopKind switch { "cancel" => "cancelled", "battery" => "on_battery", "shutdown" => "interrupted", _ => null };
                var anyDone = run.Results.Values.Any(r => r.State == "done" || r.Telemetry.ThermalStop); // a thermal stop still has a result (running_hot)
                var state = forced ?? run.StopKind switch
                {
                    "cancel" => "cancelled", "battery" => "stopped_on_battery", "shutdown" => "interrupted", _ => anyDone ? "done" : "failed",
                };
                CloseParts(body, stopReason ?? (state == "failed" ? "error" : "not_run"));
                body["state"] = state;
                body["finishedAt"] = Now(db);
                body["reason"] = state switch
                {
                    "failed" => forced is null ? "nothing_measured" : "error",
                    "stopped_on_battery" => "on_battery", "cancelled" => "cancelled", "interrupted" => "interrupted", _ => null,
                };
                if (state == "done") body["result"] = BuildResult(db, run);
                Save(db, body);
                db.AppendEvent(Stream(run.RunId), "bench.progress", ProgressPayload(body));
                db.AppendEvent(Stream(run.RunId), "bench.finished", new JsonObject
                {
                    ["state"] = state, ["score"] = body["result"]?["score"]?.DeepClone(), ["partial"] = body["result"]?["partial"]?.DeepClone(),
                });
            }
            Prune(db);
            _lease.Release(WorkLease.Bench, run.RunId); // before the events publish: the next run can start at once
        });
    }

    private JsonObject BuildResult(Db db, Run run)
    {
        var history = History(db, run);
        var partScores = new Dictionary<string, double>();
        foreach (var (part, r) in run.Results)
            if (Scored(r) && BenchScoring.PartScore(part, r.Subtests) is { } s) partScores[part] = s; // a thermally stopped repeat unscores its part
        var (score, partial) = BenchScoring.Overall(partScores);
        var scored = BenchParts.RunOrder.Where(partScores.ContainsKey).ToList();

        // Comparison (§1.4, §5): same benchVersion, configId and set of successful parts; a partial run is never compared.
        JsonObject? comparison = null;
        if (!partial && score is not null)
        {
            var prev = db.Query("SELECT run_id, score, finished_at FROM bench_runs WHERE state = 'done' AND run_id <> $r AND bench_version = $v AND config_id = $c AND partial = 0 AND parts = $p ORDER BY finished_at DESC, rowid DESC LIMIT 1",
                r => (Id: r.GetString(0), Score: r.IsDBNull(1) ? (double?)null : r.GetDouble(1), Finished: r.IsDBNull(2) ? null : r.GetString(2)),
                ("$r", run.RunId), ("$v", BenchParts.BenchVersion), ("$c", run.Body.Text("configId")), ("$p", string.Join(',', scored.Order(StringComparer.Ordinal)))).FirstOrDefault();
            if (prev.Id is not null && prev.Score is { } prevScore && prevScore > 0)
            {
                var past = history.FirstOrDefault(h => h.RunId == prev.Id);
                var deltas = new JsonObject();
                foreach (var part in scored)
                    if (past?.Parts.TryGetValue(part, out var pp) == true && pp.Score is { } ps && ps > 0)
                        deltas[part] = new JsonObject { ["prev"] = Math.Round(ps), ["pct"] = Math.Round((partScores[part] - ps) / ps * 100, 1) };
                comparison = new JsonObject
                {
                    ["prevRunId"] = prev.Id, ["prevScore"] = prevScore, ["prevFinishedAt"] = prev.Finished,
                    ["delta"] = Math.Round((score.Value - prevScore) / prevScore * 100, 1), ["deltas"] = deltas,
                };
            }
        }

        var observedAt = Now(db);
        var parts = new JsonArray();
        var hot = false;
        foreach (var part in run.Parts)
        {
            var r = run.Results.GetValueOrDefault(part) ?? new PartResult("skipped", "not_run", new PartTelemetry(part));
            double? partScore = partScores.TryGetValue(part, out var ps2) ? ps2 : null;
            PartVerdict verdict;
            lock (r.Telemetry.Samples)
                verdict = BenchVerdicts.Decide(new VerdictInput(part, r.State, r.Reason, partScore, r.Subtests, r.Telemetry,
                    PartHistory(history, part), run.Repeats.GetValueOrDefault(part, RepeatOutcome.NotNeeded), run.Hw));
            hot |= verdict.Verdict == BenchVerdicts.RunningHot;

            var figures = new JsonObject();
            var evidenceIds = new JsonArray();
            void Figure(string id, double value, string unit, string source)
            {
                var rounded = Math.Round(value, 2);
                figures[id] = new JsonObject { ["value"] = rounded, ["unit"] = unit, ["source"] = source };
                var evidenceId = "ev-" + Ids.Hash16(run.RunId, part, id);
                evidenceIds.Add(evidenceId);
                InsertEvidence(db, run, part, id, rounded, unit, source, observedAt, evidenceId);
            }
            foreach (var id in BenchSubtests.ByPart[part])
                if (r.Subtests.TryGetValue(id, out var v)) Figure(FigureKey(part, id), v, FakeBenchWorker.Unit(part, id), "worker");
            lock (r.Telemetry.Samples)
                foreach (var slot in r.Slots.Where(s => s.Available && s.Source != "worker"))
                {
                    var values = r.Telemetry.Values(slot.Id);
                    if (values.Count == 0) continue;
                    if (figures.ContainsKey(slot.Id)) continue; // a worker figure of the same name (disk read/write) wins
                    Figure(slot.Id, slot.Id == "clock" ? values.Average() : values.Max(), slot.Unit, slot.Source); // clock: average; others: peak
                }
            lock (r.Telemetry.Samples)
            {
                var limits = r.Telemetry.Samples.Select(s => s.CpuPerfLimitPercent).OfType<double>().Where(double.IsFinite).ToList();
                if (part == BenchParts.Cpu && limits.Count > 0) Figure("limit", limits.Min(), "%", "pdh");
            }

            HashSet<string> failedSlots;
            lock (r.Telemetry.Samples) failedSlots = [.. r.Telemetry.FailedSlots];
            var subtests = new JsonObject();
            foreach (var (k, v) in r.Subtests) subtests[k] = v;
            parts.Add(new JsonObject
            {
                ["part"] = part, ["device"] = PartBody(run.Body, part).Text("device"),
                ["score"] = partScore is { } x ? Math.Round(x) : null,
                ["verdict"] = verdict.Verdict, ["reason"] = verdict.Reason, ["why"] = Json.Array(verdict.Why),
                ["figures"] = figures,
                ["observations"] = new JsonArray(verdict.Observations.Select(o => (JsonNode?)o.DeepClone()).ToArray()),
                ["evidenceIds"] = evidenceIds,
                ["missingSensors"] = new JsonArray(WithFailures(r.Slots, failedSlots).Where(s => !s.Available)
                    .Select(s => (JsonNode?)new JsonObject { ["id"] = s.Id, ["reason"] = s.Reason }).ToArray()),
                ["subtests"] = subtests,
            });
        }

        return new JsonObject
        {
            ["score"] = score, ["partial"] = partial, ["partsScored"] = Json.Array(scored), ["comparison"] = comparison,
            // "Different setup": the latest earlier done run of this benchVersion has another configId.
            ["differentSetup"] = db.Scalar("SELECT config_id FROM bench_runs WHERE state = 'done' AND run_id <> $r AND bench_version = $v ORDER BY finished_at DESC, rowid DESC LIMIT 1",
                ("$r", run.RunId), ("$v", BenchParts.BenchVersion)) is string lastConfig && lastConfig != run.Body.Text("configId"),
            ["parts"] = parts,
            ["fanGuidance"] = hot ? FanGuidanceFor(db, run.Body) : null,
        };
    }

    /// <summary>result.parts[].figures key for a subtest: disk seqRead|seqWrite|rand4k → read|write|random; others keep their id.</summary>
    internal static string FigureKey(string part, string subtest) => (part, subtest) switch
    {
        (BenchParts.Disk, "seqRead") => "read", (BenchParts.Disk, "seqWrite") => "write", (BenchParts.Disk, "rand4k") => "random", _ => subtest,
    };

    /// <summary>§7 guidance for this PC: brand from the latest firmware evidence (cheap, if a scan read it), pump line on desktops.</summary>
    internal static JsonObject FanGuidanceFor(Db db, JsonObject runBody)
    {
        var laptop = runBody["power"]?["battery"]?.GetValue<string>() == "present";
        string? maker = null, product = null, family = null;
        if (db.Scalar("SELECT body FROM evidence WHERE subject = 'system.firmware' ORDER BY rowid DESC LIMIT 1") is string text)
        {
            try
            {
                var v = Json.Parse(text)?["value"];
                maker = v?["systemManufacturer"]?.GetValue<string>();
                product = v?["systemProductName"]?.GetValue<string>();
                family = v?["systemFamily"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
        }
        return FanGuidance.Build(FanGuidance.Brand(maker, product, family, laptop), laptop);
    }

    private static readonly IReadOnlyDictionary<string, string> PartLabels = new Dictionary<string, string>
    {
        [BenchParts.Cpu] = "CPU", [BenchParts.Gpu] = "GPU", [BenchParts.Ram] = "Memory", [BenchParts.Disk] = "Disk",
    };

    /// <summary>bench_evidence: ev- + 16 hex of SHA-256(runId ‖ part ‖ metric); category performance, collector bench.&lt;part&gt;, measured.</summary>
    private static void InsertEvidence(Db db, Run run, string part, string metric, double value, string unit, string source, string observedAt, string evidenceId)
    {
        var body = new JsonObject
        {
            ["id"] = evidenceId, ["subject"] = $"bench.{part}.{metric.ToLowerInvariant()}", ["category"] = "performance",
            ["source"] = source, ["observedAt"] = observedAt, ["interval"] = null,
            ["display"] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{PartLabels[part]} {metric}: {value:0.##} {unit}."),
            ["value"] = new JsonObject
            {
                ["runId"] = run.RunId, ["part"] = part, ["metric"] = metric, ["value"] = value, ["unit"] = unit, ["source"] = source,
                ["device"] = PartBody(run.Body, part).Text("device"), ["benchVersion"] = BenchParts.BenchVersion,
            },
            ["collectorId"] = "bench." + part, ["collectorVersion"] = BenchParts.BenchVersion, ["quality"] = "measured",
            ["sensitivity"] = "public", ["sensitivityBasis"] = "declared",
        };
        db.Exec("INSERT OR REPLACE INTO bench_evidence (evidence_id, run_id, part, metric, body) VALUES ($e, $r, $p, $m, $b)",
            ("$e", evidenceId), ("$r", run.RunId), ("$p", part), ("$m", metric), ("$b", Json.Write(body)));
    }
}

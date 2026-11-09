using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Bench;

/// <summary>sensors.snapshot was refused because the shared lease is held (a benchmark, scan or action is running).</summary>
public sealed class BenchBusyException(WorkLease.Holder holder) : Exception("The shared work lease is held.")
{
    public WorkLease.Holder Holder { get; } = holder;
}

/// <summary>The sensors.snapshot diagnostic as a collector, so its facts are stored and projected like any task evidence.</summary>
internal sealed class SnapshotCollector : ICollector
{
    public const string CollectorId = "bench.sensors";
    public static readonly SnapshotCollector Instance = new();
    public string Id => CollectorId;
    public string Version => "1";
    public string Category => "performance";
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public string Label => "Sensors";
    public Task<CollectorResult> CollectAsync(CancellationToken ct) => throw new NotSupportedException("Run through BenchService.SnapshotAsync.");
}

// AI access (§8): done runs and their evidence for get_benchmarks, fan guidance, and the sensors.snapshot diagnostic.
public sealed partial class BenchService
{
    public sealed record SnapshotResult(string Status, string? Reason, IReadOnlyList<Fact> Facts);

    /// <summary>
    /// sensors.snapshot (≤ 10 s, 1 Hz): CPU then GPU sensors, min/avg/max per available metric. Takes the shared
    /// lease, so it is refused (<see cref="BenchBusyException"/>) while a benchmark, scan or action runs. Null when
    /// sensors are not available in this build.
    /// </summary>
    /// <summary>True when this build has the benchmark sensors (sensors.snapshot can run).</summary>
    internal bool SensorsAvailable => _options.Available;

    internal async Task<SnapshotResult?> SnapshotAsync(string holderId, CancellationToken ct)
    {
        if (!_options.Available) return null;
        if (_lease.TryAcquire(WorkLease.Snapshot, holderId) is { } busy) throw new BenchBusyException(busy);
        // One deadline for the whole call (m6 §8: ≤ 10 s), linked to the caller's token: the hardware read, each
        // preparation and every sample take it, so neither a slow probe nor a cancelled caller keeps the lease.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(T.SnapshotDeadline);
        var token = deadline.Token;
        var hub = _options.Sensors!.Create();
        var calls = new List<Task>(); // hub calls that may outlive this method (abandoned at the deadline)
        try
        {
            var (hw, _) = await DescribeAsync(token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (hw is null)
                return new SnapshotResult("failed", token.IsCancellationRequested ? "The sensors took too long." : "The hardware could not be read.", []);
            var facts = new List<Fact>();
            foreach (var part in new[] { BenchParts.Cpu, BenchParts.Gpu })
            {
                if (token.IsCancellationRequested) break;
                if (part == BenchParts.Gpu && hw.GpuLuid is null) continue;
                IReadOnlyList<MetricSlot> slots;
                var prepare = Task.Run(() => hub.Prepare(part, hw, token), CancellationToken.None);
                calls.Add(prepare);
                try { slots = await prepare.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log($"snapshot prepare {part}: {ex.GetType().Name}"); continue; }
                var telemetry = new PartTelemetry(part) { SampleSeconds = T.SampleInterval.TotalSeconds };
                for (var i = 0; i < T.SnapshotSamples && !token.IsCancellationRequested; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var read = Task.Run(hub.Read, CancellationToken.None);
                    calls.Add(read);
                    try { telemetry.Samples.Add(await read.WaitAsync(T.SensorReadTimeout, token).ConfigureAwait(false)); }
                    catch (TimeoutException) { break; } // a hung source: stop this part
                    catch (OperationCanceledException) { break; }
                    catch (Exception) { /* a failed read: no sample this time */ }
                    var rest = T.SampleInterval - sw.Elapsed;
                    if (i + 1 < T.SnapshotSamples && rest > TimeSpan.Zero)
                        try { await Task.Delay(rest, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                }
                ct.ThrowIfCancellationRequested(); // the caller cancelled: no result. At the deadline: report what was read.
                var device = part == BenchParts.Cpu ? hw.CpuModel : hw.GpuModel;
                foreach (var slot in slots.Where(s => s.Available && s.Source != "worker"))
                {
                    var values = telemetry.Values(slot.Id);
                    if (values.Count == 0) continue;
                    var value = new JsonObject
                    {
                        ["part"] = part, ["metric"] = slot.Id, ["unit"] = slot.Unit, ["source"] = slot.Source, ["device"] = device,
                        ["min"] = Math.Round(values.Min(), 2), ["avg"] = Math.Round(values.Average(), 2), ["max"] = Math.Round(values.Max(), 2),
                        ["samples"] = values.Count,
                    };
                    facts.Add(new Fact($"sensors.{part}.{slot.Id}", slot.Source, "measured", value,
                        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{part.ToUpperInvariant()} {slot.Id}: {values.Average():0.##} {slot.Unit} on average."),
                        $"{telemetry.Samples.Count}s", "public"));
                }
                if (part == BenchParts.Gpu && telemetry.Samples.Any(s => s.Gpu is not null))
                    facts.Add(new Fact("sensors.gpu.throttle", "nvml", "measured", new JsonObject
                    {
                        ["part"] = part, ["metric"] = "throttle", ["thermalSlowdownSamples"] = telemetry.ThermalSlowdownSamples,
                        ["powerCapSamples"] = telemetry.PowerCapSamples, ["slowdownTempC"] = telemetry.Samples.Select(s => s.Gpu?.SlowdownTempC).LastOrDefault(v => v is not null),
                        ["samples"] = telemetry.Samples.Count,
                    }, "GPU slowdown reasons over the snapshot.", $"{telemetry.Samples.Count}s", "public"));
                var limits = telemetry.Samples.Select(s => s.CpuPerfLimitPercent).OfType<double>().ToList();
                if (part == BenchParts.Cpu && limits.Count > 0)
                    facts.Add(new Fact("sensors.cpu.limit", "pdh", "measured", new JsonObject
                    {
                        ["part"] = part, ["metric"] = "limit", ["unit"] = "%", ["min"] = Math.Round(limits.Min(), 1), ["avg"] = Math.Round(limits.Average(), 1),
                        ["samples"] = limits.Count,
                    }, "CPU performance limit over the snapshot.", $"{limits.Count}s", "public"));
            }
            ct.ThrowIfCancellationRequested();
            if (facts.Count > 0) return new SnapshotResult("complete", null, facts);
            return token.IsCancellationRequested
                ? new SnapshotResult("failed", "The sensors took too long.", facts)
                : new SnapshotResult("unsupported", "No sensor could be read.", facts);
        }
        finally
        {
            _lease.Release(WorkLease.Snapshot, holderId);
            // A hub call abandoned above may still be running: dispose the hub after it returns, and observe its fault.
            var open = calls.Where(c => !c.IsCompleted).ToArray();
            foreach (var c in calls) if (c.IsFaulted) _ = c.Exception;
            if (open.Length == 0) hub.Dispose();
            else
                _ = Task.WhenAll(open).ContinueWith(t =>
                {
                    _ = t.Exception;
                    hub.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Done runs, newest first.</summary>
    internal static List<JsonObject> DoneRuns(Db db, int limit) =>
        db.Query("SELECT body FROM bench_runs WHERE state = 'done' ORDER BY rowid DESC LIMIT $l", r => r.GetString(0), ("$l", limit))
            .Select(t => Json.Parse(t) as JsonObject).OfType<JsonObject>().ToList();

    /// <summary>A done run, or null (unknown or not done).</summary>
    internal static JsonObject? DoneRun(Db db, string runId) =>
        db.Scalar("SELECT body FROM bench_runs WHERE run_id = $r AND state = 'done'", ("$r", runId)) is string t ? Json.Parse(t) as JsonObject : null;

    internal static List<JsonObject> EvidenceBodies(Db db, string runId) =>
        db.Query("SELECT body FROM bench_evidence WHERE run_id = $r ORDER BY rowid", r => r.GetString(0), ("$r", runId))
            .Select(t => Json.Parse(t) as JsonObject).OfType<JsonObject>().ToList();

    internal static List<string> EvidenceIds(Db db, string runId) =>
        db.Query("SELECT evidence_id FROM bench_evidence WHERE run_id = $r", r => r.GetString(0), ("$r", runId));

    /// <summary>
    /// agent.ask context {benchRunId, part}: the run exists and is done, and the part is in it. Throws -32602 (field
    /// context.*) or -32001 for an unknown run.
    /// </summary>
    internal static void ValidateContext(Db db, string runId, string part)
    {
        if (!BenchParts.RunOrder.Contains(part)) throw RpcError.Params("context.part", "context.part must be cpu, gpu, ram or disk.");
        var state = db.Scalar("SELECT state FROM bench_runs WHERE run_id = $r", ("$r", runId)) as string ?? throw RpcError.NotFound("benchmark", runId);
        if (state != "done") throw RpcError.Params("context.benchRunId", "That benchmark did not finish.");
        var body = DoneRun(db, runId)!;
        if (!(body["tests"] as JsonArray ?? []).Any(x => x.Text("part") == part)) throw RpcError.Params("context.part", "That part is not in the benchmark.");
    }

    /// <summary>get_fan_guidance {part}: the host's guidance codes, with whether the latest run found that part running hot.</summary>
    internal static JsonObject FanGuidanceTool(Db db, string part)
    {
        var latest = DoneRuns(db, 10).FirstOrDefault(b => (b["result"]?["parts"] as JsonArray ?? []).Any(x => x.Text("part") == part));
        var verdict = latest?["result"]?["parts"]?.AsArray().FirstOrDefault(x => x.Text("part") == part)?["verdict"]?.GetValue<string>();
        var guidance = latest is not null ? FanGuidanceFor(db, latest) : FanGuidance.Build("any", laptop: false);
        return new JsonObject
        {
            ["part"] = part,
            ["runId"] = latest?.Text("runId"),
            ["runningHot"] = verdict == BenchVerdicts.RunningHot,
            ["guidance"] = guidance,
            ["tipMeanings"] = new JsonObject
            {
                ["bios_fan_mode"] = "Check the BIOS fan mode (for example Standard or Performance instead of Silent).",
                ["clean_filters"] = "Clean the dust filters.",
                ["check_fans_spin"] = "Check that the fans spin.",
                ["gpu_default_curve"] = "Keep the GPU on its default fan curve.",
            },
            ["apps"] = new JsonObject
            {
                ["fancontrol"] = "FanControl, for desktops.",
                ["g_helper"] = "G-Helper, for ASUS laptops.",
                ["legion_toolkit"] = "Lenovo Legion Toolkit, for Legion and LOQ.",
                ["maker_tool"] = "Otherwise the PC maker's own tool.",
            },
            ["pumpLine"] = "Use your cooler maker's pump setting.",
            ["note"] = "Guidance only: Optimus never sets fan speeds and has no numeric fan curves.",
        };
    }
}

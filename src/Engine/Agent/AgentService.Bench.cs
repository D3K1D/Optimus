using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Engine.Agent;

// M6a (contracts/m6-benchmark.md §8): get_benchmarks and get_fan_guidance, the sensors.snapshot diagnostic, the
// agent.ask benchmark context, the "Do: benchmark" suggestion tag and the voltage warning on answers.
public sealed partial class AgentService
{
    /// <summary>M6a: benchmark runs, evidence, fan guidance and sensors.snapshot.</summary>
    internal BenchService? Bench { get; init; }

    // ---------------------------------------------------------------- get_benchmarks

    /// <summary>
    /// get_benchmarks {runId} | {limit}: done runs with scores, verdicts, neutral observations and their evidence,
    /// projected (only allowlisted fields and model names leave). Returning a run releases its evidence to the task,
    /// so the model may cite it.
    /// </summary>
    private (JsonObject? Body, string ArrayKey, JsonObject? Error) Benchmarks(Db db, JsonObject task, Grant grant, JsonObject args)
    {
        List<JsonObject> runs;
        if (args["runId"]?.GetValue<string>() is { } runId)
        {
            if (BenchService.DoneRun(db, runId) is not { } one) return (null, "", ToolError("invalid_arguments", "Unknown or unfinished runId."));
            runs = [one];
        }
        else runs = BenchService.DoneRuns(db, args["limit"]!.GetValue<int>());

        var share = new ShareOptions(grant.ShareNames);
        var stats = new ProjectionStats();
        var items = new JsonArray();
        foreach (var run in runs)
        {
            var id = run.Text("runId");
            var evidence = BenchService.EvidenceBodies(db, id).ToDictionary(b => b.Text("id"));
            var result = run["result"] as JsonObject;
            var parts = new JsonArray();
            foreach (var part in (result?["parts"] as JsonArray ?? []).Select(p => p!.AsObject()))
            {
                var bodies = Json.Strings(part["evidenceIds"]).Where(evidence.ContainsKey).Select(e => evidence[e]);
                var (projected, withheld) = Project(db, task.Text("taskId"), bodies, share, stats);
                parts.Add(new JsonObject
                {
                    ["part"] = part.Text("part"), ["score"] = part["score"]?.DeepClone(), ["verdict"] = part.Text("verdict"),
                    ["reason"] = part["reason"]?.DeepClone(), ["why"] = part["why"]?.DeepClone(),
                    ["observations"] = part["observations"]?.DeepClone(), ["missingSensors"] = part["missingSensors"]?.DeepClone(),
                    ["evidence"] = projected, ["withheld"] = withheld,
                });
            }
            items.Add(new JsonObject
            {
                ["runId"] = id, ["finishedAt"] = run["finishedAt"]?.DeepClone(), ["benchVersion"] = run.Text("benchVersion"),
                ["configId"] = run["configId"]?.DeepClone(),
                ["score"] = result?["score"]?.DeepClone(), ["partial"] = result?["partial"]?.DeepClone() ?? false,
                ["partsScored"] = result?["partsScored"]?.DeepClone(), ["comparison"] = result?["comparison"]?.DeepClone(),
                ["fanGuidance"] = result?["fanGuidance"]?.DeepClone(),
                ["parts"] = parts,
            });
        }
        return (new JsonObject
        {
            ["runs"] = items,
            ["note"] = runs.Count == 0 ? "No finished benchmark yet. The user can run one from the Scan menu; you cannot start it." : null,
            ["redaction"] = Redaction(stats),
        }, "runs", null);
    }

    // ---------------------------------------------------------------- sensors.snapshot

    private async Task<(DiagnosticRun? Run, JsonObject? Busy)> SnapshotDiagnosticAsync(string invocationId, CancellationToken ct)
    {
        if (Bench is null) return (null, null);
        try
        {
            var r = await Bench.SnapshotAsync(invocationId, ct).ConfigureAwait(false);
            if (r is null) return (null, null);
            return (new DiagnosticRun(r.Status, r.Reason, r.Facts.Select(f => ((ICollector)SnapshotCollector.Instance, f)).ToList(), null), null);
        }
        catch (BenchBusyException e)
        {
            return (null, ToolError("busy", e.Holder.Kind == WorkLease.Bench
                ? "A benchmark is running. Try again after it finishes."
                : "The PC is busy with a scan or a change. Try again shortly."));
        }
        catch (OperationCanceledException)
        {
            return (new DiagnosticRun("cancelled", null, [], null), null);
        }
    }

    // ---------------------------------------------------------------- agent.ask context

    /// <summary>agent.ask context {benchRunId, part}: validated (run exists, done, part in it); null when absent.</summary>
    internal static JsonObject? ParseAskContext(JsonObject p)
    {
        if (p["context"] is null) return null;
        if (p["context"] is not JsonObject c) throw RpcError.Params("context", "context must be an object.");
        var extra = c.Select(kv => kv.Key).FirstOrDefault(k => k is not ("benchRunId" or "part"));
        if (extra is not null) throw RpcError.Params("context." + extra, $"Unknown context field {extra}.");
        var runId = P.Req(c, "benchRunId", 64);
        var part = P.Req(c, "part", 8);
        return new JsonObject { ["benchRunId"] = runId, ["part"] = part };
    }

    /// <summary>In the ask's transaction: the run is valid, and its evidence is released to the task (always citable).</summary>
    private static void AcceptAskContext(Db db, string taskId, JsonObject context)
    {
        var runId = context.Text("benchRunId");
        BenchService.ValidateContext(db, runId, context.Text("part"));
        foreach (var id in BenchService.EvidenceIds(db, runId))
            db.Exec("INSERT OR IGNORE INTO agent_released (task_id, evidence_id) VALUES ($t, $e)", ("$t", taskId), ("$e", id));
    }

    /// <summary>The prompt line for the benchmark the user is looking at (from the latest agent.question of the task).</summary>
    private static string? BenchContextLine(Db db, string taskId)
    {
        var payload = db.Scalar("SELECT payload FROM events WHERE stream = $s AND type = 'agent.question' ORDER BY seq DESC LIMIT 1", ("$s", Stream(taskId))) as string;
        if (payload is null || Json.Parse(payload)?["context"] is not JsonObject context) return null;
        var part = context.Text("part");
        if (!BenchParts.RunOrder.Contains(part)) return null;
        var label = part == BenchParts.Ram ? "RAM" : part.ToUpperInvariant();
        return $"The user is looking at the benchmark result for the {label}.\nBenchmark run: {context.Text("benchRunId")} (read it with get_benchmarks).";
    }

    // ---------------------------------------------------------------- answer post-processing

    private static readonly Regex DoBenchmark = new(@"^\s*\**\s*do\s*:\s*benchmark\s*\**\s*\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The answer's final "Do: benchmark" line (before or after the Next: line) is removed; true when it was there. The
    /// app then shows a "Run benchmark" button. The AI cannot start a benchmark itself.
    /// </summary>
    internal static (string Text, bool Suggest) StripBenchmarkTag(string text)
    {
        var lines = text.TrimEnd().Split('\n').ToList();
        var suggest = false;
        // The tag is the last line, or the one just before a final Next: line.
        for (var back = 1; back <= 2 && lines.Count >= back; back++)
        {
            var i = lines.Count - back;
            if (DoBenchmark.IsMatch(lines[i]))
            {
                lines.RemoveAt(i);
                suggest = true;
                break;
            }
            if (back == 1 && !lines[i].TrimStart('*', ' ').StartsWith("next", StringComparison.OrdinalIgnoreCase)) break;
        }
        return (suggest ? string.Join('\n', lines).TrimEnd() : text, suggest);
    }

    /// <summary>Words that make the app show the voltage warning under an answer (a display aid, not enforcement).</summary>
    internal static readonly string[] VoltageTerms =
    [
        "undervolt", "overclock", "voltage offset", "offset voltage", "Curve Optimizer", "Vcore", "PBO", "Precision Boost Overdrive",
        "XTU", "Extreme Tuning Utility", "ThrottleStop", "Ryzen Master", "core voltage", "voltage curve", "load-line calibration",
    ];

    private static readonly Regex Voltage = new(
        @"\b(?:under-?volt\w*|over-?clock\w*|voltage[- ]offsets?|offset[- ]voltages?|curve[- ]optimi[sz]er|v-?core|pbo2?|precision[- ]boost[- ]overdrive|xtu|extreme[- ]tuning[- ]utility|throttle[- ]?stop|ryzen[- ]master|core[- ]voltages?|voltage[- ]curves?|load[- ]line[- ]calibration|llc)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the answer mentions undervolting, overclocking, voltage offsets, Curve Optimizer, Vcore or similar.</summary>
    internal static bool MentionsVoltage(string text) => Voltage.IsMatch(text);
}

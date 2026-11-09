using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// bench.cpu|gpu|ram|disk (benchmark figures) and bench.sensors (sensors.snapshot), contracts/m6-benchmark.md §2:
/// part, metric, numbers and units from fixed lists, plus the device's model name, which is a public spec and may
/// leave as text (marked untrusted; one that looks sensitive falls back to the alias). Nothing else is new.
/// </summary>
internal sealed class BenchProjector : Projector
{
    private static readonly string[] Parts = ["cpu", "gpu", "ram", "disk"];
    private static readonly string[] Metrics =
    [
        "single", "multi", "fma", "copy", "read", "write", "latency", "seqWrite", "seqRead", "rand4k", "random",
        "clock", "temp", "power", "fan", "random4k", "limit", "throttle",
        "clock.avg", "temp.max", "power.max", "fan.max",
    ];
    private static readonly string[] Units = ["ops/s", "GFLOPS", "GB/s", "MB/s", "ns", "GHz", "MHz", "°C", "W", "%", "RPM"];
    private static readonly string[] Sources = ["worker", "pdh", "emi", "d3dkmt", "nvml", "wmi", "none"];

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["bench.cpu", "bench.gpu", "bench.ram", "bench.disk", "bench.sensors"];

    public override string Source(string collectorId) => collectorId == "bench.sensors" ? "Optimus sensor snapshot" : "Optimus benchmark";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        var part = o.Enum("part", v["part"], Parts);
        var metric = o.Enum("metric", v["metric"], Metrics);
        if (part is null || metric is null) return null;
        if (collectorId != "bench.sensors" && collectorId != "bench." + part) return null;
        var unit = o.Enum("unit", v["unit"], Units);
        o.Enum("source", v["source"], Sources);
        Device(v, o);

        if (collectorId == "bench.sensors")
        {
            if (metric == "throttle")
            {
                o.Int("thermalSlowdownSamples", v["thermalSlowdownSamples"], 0, 600);
                o.Int("powerCapSamples", v["powerCapSamples"], 0, 600);
                o.Num("slowdownTempC", v["slowdownTempC"], 0, 0, 150);
                o.Int("samples", v["samples"], 0, 600);
                return new($"sensors.{part}.throttle", "GPU slowdown reasons during the snapshot.");
            }
            var min = o.Num("min", v["min"], 2, -50, 1e7);
            var avg = o.Num("avg", v["avg"], 2, -50, 1e7);
            var max = o.Num("max", v["max"], 2, -50, 1e7);
            o.Int("samples", v["samples"], 0, 600);
            if (avg is null) return null;
            return new($"sensors.{part}.{metric}", $"{part.ToUpperInvariant()} {metric}: {Say.N(avg.Value)} {unit} on average (min {Say.N(min ?? avg.Value)}, max {Say.N(max ?? avg.Value)}).");
        }

        var value = o.Num("value", v["value"], 2, 0, 1e9);
        o.Enum("benchVersion", v["benchVersion"], "1");
        if (value is null) return null;
        return new($"bench.{part}.{metric.ToLowerInvariant()}", $"{part.ToUpperInvariant()} {metric}: {Say.N(value.Value)} {unit}.");
    }

    /// <summary>Model names are public specs (§2 projection): shared as sanitized text, or the alias when unsafe.</summary>
    private static void Device(JsonObject v, Out o)
    {
        var raw = Read.Str(v["device"]);
        if (raw is null) return;
        if (Sanitize.Name(raw) is { } clean && clean.Length <= 80)
        {
            o.V["device"] = clean;
            o.MarkUntrusted("device");
        }
        else o.Name("device", "device", raw);
    }
}

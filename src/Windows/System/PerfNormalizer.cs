using System.Globalization;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

/// <summary>
/// Pure: snapshots → facts. Rate values come from deltas between consecutive snapshots. A process contributes an
/// interval only when the same PID+start time appears at both ends of it (a process that appears, exits or has its
/// PID reused invalidates that interval for it). Process CPU is normalized to all logical processors (0–100%).
/// </summary>
internal static class PerfNormalizer
{
    public const int TopCount = 5;
    private static readonly string[] DefenderImages = ["msmpeng.exe", "mpdefendercoreservice.exe", "nissrv.exe"];

    private sealed class Agg(ProcKey key, string image)
    {
        public ProcKey Key { get; } = key;
        public string Image { get; } = image;
        public List<double> Cpu { get; } = [];
        public int Foreground { get; set; }
        public double WsSum { get; set; }
        public int WsCount { get; set; }
        public ulong WsPeak { get; set; }
        public double CpuAvg => Cpu.Count == 0 ? 0 : Cpu.Average();
        public double WsAvg => WsCount == 0 ? 0 : WsSum / WsCount;
    }

    public static bool IsThisTool(string image, uint pid, uint selfPid) =>
        pid == selfPid || image.StartsWith("OptimizationTool", StringComparison.OrdinalIgnoreCase);

    public static bool IsDefender(string image) => DefenderImages.Contains(image.ToLowerInvariant());

    public static CollectorResult Normalize(PerfRaw raw)
    {
        var facts = new List<Fact>();
        var snaps = raw.Snapshots;
        var intervals = Math.Max(0, snaps.Count - 1);
        var iso = $"PT{raw.IntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}S";
        var gaps = new List<string>();

        // Machine CPU.
        var cpu = new List<double>();
        var sysDelta = new double[snaps.Count];
        for (var i = 1; i < snaps.Count; i++)
        {
            double total = (snaps[i].SysKernel - snaps[i - 1].SysKernel) + (snaps[i].SysUser - snaps[i - 1].SysUser);
            double idle = snaps[i].SysIdle - snaps[i - 1].SysIdle;
            sysDelta[i] = total;
            if (total > 0) cpu.Add(Sys.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100)));
        }
        if (cpu.Count > 0)
            facts.Add(new Fact("perf.cpu", "System processor times", "measured", new JsonObject
            {
                ["avgPercent"] = Sys.Round(cpu.Average()), ["maxPercent"] = cpu.Max(), ["sampleCount"] = cpu.Count,
                ["intervalSeconds"] = raw.IntervalSeconds, ["normalization"] = "percentOfAllLogicalProcessors",
                ["percentSamples"] = Sys.Array(cpu),
            }, $"CPU averaged {Sys.Round(cpu.Average()):0.#}% over {cpu.Count} samples.", iso));

        // Memory (point samples: one per snapshot).
        var mem = snaps.Where(s => s.AvailPhysBytes is not null && s.TotalPhysBytes is > 0).ToList();
        if (mem.Count > 0)
        {
            var avail = mem.Select(s => Sys.Round(100.0 * s.AvailPhysBytes!.Value / s.TotalPhysBytes!.Value)).ToList();
            var commit = snaps.Where(s => s.CommitTotalBytes is not null && s.CommitLimitBytes is > 0)
                .Select(s => Sys.Round(100.0 * s.CommitTotalBytes!.Value / s.CommitLimitBytes!.Value)).ToList();
            var last = mem[^1];
            var value = new JsonObject
            {
                ["totalBytes"] = last.TotalPhysBytes, ["availableMinBytes"] = mem.Min(s => s.AvailPhysBytes!.Value),
                ["availableAvgPercent"] = Sys.Round(avail.Average()), ["availablePercentSamples"] = Sys.Array(avail),
                ["commitLimitBytes"] = snaps.LastOrDefault(s => s.CommitLimitBytes is not null)?.CommitLimitBytes,
                ["commitMaxPercent"] = commit.Count > 0 ? commit.Max() : null, ["commitPercentSamples"] = Sys.Array(commit),
                ["sampleCount"] = mem.Count, ["intervalSeconds"] = raw.IntervalSeconds,
            };
            facts.Add(new Fact("perf.memory", "Memory status and commit charge", "measured", value,
                $"Available memory averaged {Sys.Round(avail.Average()):0.#}% over {mem.Count} samples.", iso));
        }

        // Disk (PDH rates; the first collect only primes the counters).
        if (raw.DiskError is not null) gaps.Add("disk counters");
        else
        {
            var busy = snaps.Skip(1).Where(s => s.DiskIdlePercent is not null).Select(s => Sys.Round(Math.Clamp(100 - s.DiskIdlePercent!.Value, 0, 100))).ToList();
            var bytes = snaps.Skip(1).Where(s => s.DiskBytesPerSec is not null).Select(s => s.DiskBytesPerSec!.Value).ToList();
            var lat = snaps.Skip(1).Where(s => s.DiskSecPerTransfer is not null).Select(s => s.DiskSecPerTransfer!.Value * 1000).ToList();
            if (busy.Count > 0)
                facts.Add(new Fact("perf.disk", "Physical disk counters", "measured", new JsonObject
                {
                    ["busyAvgPercent"] = Sys.Round(busy.Average()), ["busyMaxPercent"] = busy.Max(), ["busyPercentSamples"] = Sys.Array(busy),
                    ["bytesPerSecAvg"] = bytes.Count > 0 ? Math.Round(bytes.Average()) : null,
                    ["bytesPerSecMax"] = bytes.Count > 0 ? Math.Round(bytes.Max()) : null,
                    ["latencyAvgMs"] = lat.Count > 0 ? Sys.Round(lat.Average(), 2) : null,
                    ["sampleCount"] = busy.Count, ["intervalSeconds"] = raw.IntervalSeconds, ["instance"] = "_Total",
                }, $"Disks were busy {Sys.Round(busy.Average()):0.#}% of the time on average over {busy.Count} samples.", iso));
            else if (intervals > 0) gaps.Add("disk counters");
        }

        // Processes.
        var aggs = new Dictionary<ProcKey, Agg>();
        var invalidated = 0;
        if (raw.ProcessError is not null) gaps.Add("process list");
        for (var i = 0; i < snaps.Count; i++)
        {
            var cur = snaps[i].Processes;
            if (cur is null) continue;
            var prev = i > 0 ? snaps[i - 1].Processes : null;
            foreach (var (key, p) in cur)
            {
                if (key.Pid == 0) continue; // Idle is not a consumer.
                if (!aggs.TryGetValue(key, out var a)) aggs[key] = a = new Agg(key, p.Image);
                a.WsSum += p.WorkingSetBytes;
                a.WsCount++;
                if (p.WorkingSetBytes > a.WsPeak) a.WsPeak = p.WorkingSetBytes;
                if (i == 0 || sysDelta[i] <= 0) continue;
                if (prev is null || !prev.TryGetValue(key, out var before)) { invalidated++; continue; }
                var share = 100.0 * Math.Max(0, p.CpuTime100ns - before.CpuTime100ns) / sysDelta[i];
                a.Cpu.Add(Sys.Round(Math.Clamp(share, 0, 100)));
                if (snaps[i].ForegroundPid == key.Pid) a.Foreground++;
            }
            if (prev is not null && i > 0)
                invalidated += prev.Keys.Count(k => k.Pid != 0 && !cur.ContainsKey(k)); // exited during the interval
        }

        var byCpu = aggs.Values.Where(a => a.Cpu.Count > 0)
            .OrderByDescending(a => a.CpuAvg).ThenBy(a => a.Image, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key.Pid)
            .Take(TopCount).ToList();
        var byMem = aggs.Values.Where(a => a.WsCount > 0)
            .OrderByDescending(a => a.WsAvg).ThenBy(a => a.Image, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Key.Pid)
            .Take(TopCount).ToList();

        var ordered = byCpu.Concat(byMem).Distinct().ToList();
        var instanceNo = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in ordered)
        {
            var baseSubject = $"perf.process.{Sys.Hash8(a.Image)}";
            var n = instanceNo[baseSubject] = instanceNo.GetValueOrDefault(baseSubject) + 1;
            var subject = n == 1 ? baseSubject : $"{baseSubject}.{n}";
            var cpuRank = byCpu.IndexOf(a);
            var memRank = byMem.IndexOf(a);
            var value = new JsonObject
            {
                ["image"] = a.Image, ["pid"] = a.Key.Pid,
                ["startTime"] = a.Key.CreateTime > 0 ? DateTime.FromFileTimeUtc(a.Key.CreateTime).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : null,
                ["cpuAvgPercent"] = Sys.Round(a.CpuAvg), ["cpuMaxPercent"] = a.Cpu.Count > 0 ? a.Cpu.Max() : 0,
                ["cpuAvgLogicalProcessors"] = Sys.Round(a.CpuAvg / 100 * raw.LogicalProcessors, 2),
                ["cpuPercentSamples"] = Sys.Array(a.Cpu),["validIntervals"] = a.Cpu.Count, ["totalIntervals"] = intervals,
                ["foregroundIntervals"] = a.Foreground,
                ["workingSetAvgBytes"] = (ulong)a.WsAvg, ["workingSetPeakBytes"] = a.WsPeak,
                ["cpuRank"] = cpuRank >= 0 ? cpuRank + 1 : null, ["memoryRank"] = memRank >= 0 ? memRank + 1 : null,
                ["isThisTool"] = IsThisTool(a.Image, a.Key.Pid, raw.SelfPid), ["isDefender"] = IsDefender(a.Image),
                ["normalization"] = "percentOfAllLogicalProcessors",
            };
            var mib = (a.WsAvg / (1 << 20)).ToString("0", CultureInfo.InvariantCulture);
            facts.Add(new Fact(subject, "Process list", "measured", value,
                $"{a.Image} averaged {Sys.Round(a.CpuAvg):0.#}% CPU over {a.Cpu.Count} samples and used about {mib} MiB.", iso));
        }

        var top = byCpu.Concat(byMem).ToList();
        var elapsed = snaps.Count > 0 ? snaps[^1].ElapsedSeconds : 0;
        facts.Add(new Fact("perf.sample", "Performance sampler", "measured", new JsonObject
        {
            ["skippable"] = true,
            ["windowSeconds"] = raw.WindowSeconds, ["intervalSeconds"] = raw.IntervalSeconds,
            ["elapsedSeconds"] = Sys.Round(elapsed, 2), ["snapshots"] = snaps.Count, ["intervals"] = intervals,
            ["logicalProcessors"] = raw.LogicalProcessors, ["stoppedEarly"] = raw.StoppedEarly,
            ["processesTracked"] = aggs.Count, ["processIntervalsInvalidated"] = invalidated,
            ["thisToolTopConsumer"] = top.Any(a => IsThisTool(a.Image, a.Key.Pid, raw.SelfPid)),
            ["defenderTopConsumer"] = top.Any(a => IsDefender(a.Image)),
            ["overheadCpuMs"] = Sys.Round(raw.OverheadCpuMs), ["overheadWallMs"] = Sys.Round(raw.OverheadWallMs),
            ["overheadPercentOfOneCore"] = elapsed > 0 ? Sys.Round(100 * raw.OverheadCpuMs / 1000 / elapsed, 2) : null,
            ["diskSource"] = raw.DiskError is null ? "pdh" : null,
        }, raw.StoppedEarly
            ? $"Sampling stopped early after {intervals} samples."
            : $"Sampled for {Sys.Round(elapsed):0} s at {raw.IntervalSeconds:0.#} s intervals ({intervals} samples).", iso));

        if (raw.StoppedEarly) gaps.Insert(0, $"stopped early after {intervals} of {(int)Math.Round(raw.WindowSeconds / raw.IntervalSeconds)} samples");
        else if (intervals == 0) gaps.Insert(0, "fewer than two snapshots");
        return gaps.Count == 0
            ? Sys.Result(facts, "complete", null)
            : Sys.Result(facts, "partial", string.Join("; ", gaps.Distinct()));
    }
}

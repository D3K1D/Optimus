using System.Diagnostics;

namespace OptimizationTool.Bench.Cpu;

/// <summary>
/// CPU part (§4): 3 s single-thread warm-up (not scored, lets tiered JIT settle), single-thread 10 s, then all logical
/// processors 30 s. Kernel threads run at below-normal priority. Each thread cycles rounds of 8× SHA-256 → Deflate →
/// sort and checks every iteration. Repeat: 1 s warm-up, then only the asked subtest for 10 s.
/// </summary>
internal sealed class CpuPart : IBenchPart
{
    private sealed record Phase(string Subtest, int Threads, TimeSpan Time, bool Scored);

    public PartResult Run(PartContext ctx)
    {
        var inputs = CpuInputs.Create();
        if (!inputs.MatchesPins()) throw new BenchFailedException(BenchIds.CheckFailed);

        var s = ctx.Scale;
        var all = Environment.ProcessorCount;
        var phases = ctx.Repeat switch
        {
            null => new[]
            {
                new Phase("single", 1, s.Time(3), false),
                new Phase("single", 1, s.Time(10), true),
                new Phase("multi", all, s.Time(30), true),
            },
            var r => [new Phase(r, r == "multi" ? all : 1, s.Time(1), false), new Phase(r, r == "multi" ? all : 1, s.Time(10), true)],
        };

        var total = phases.Sum(p => p.Time.TotalSeconds);
        var before = 0.0;
        var results = new List<SubtestValue>();
        foreach (var p in phases)
        {
            var score = RunPhase(ctx, inputs, p, before, total);
            before += p.Time.TotalSeconds;
            if (p.Scored) results.Add(new SubtestValue(p.Subtest, score, "ops/s"));
        }
        return new PartResult(results, [new("threads", (long)all)]);
    }

    /// <summary>Runs one phase and returns weighted ops/s. Polls stop every 50 ms; threads stop after their current kernel.</summary>
    internal static double RunPhase(PartContext ctx, CpuInputs inputs, string subtest, int threads, TimeSpan time) =>
        RunPhase(ctx, inputs, new Phase(subtest, threads, time, true), 0, time.TotalSeconds);

    private static double RunPhase(PartContext ctx, CpuInputs inputs, Phase phase, double before, double total)
    {
        const int Stride = 16; // longs per thread slot: 3 counters, padded past a cache line
        var counts = new long[phase.Threads * Stride];
        var stop = 0;
        string? failed = null;
        Exception? error = null;
        using var go = new ManualResetEventSlim(false);

        var workers = new Thread[phase.Threads];
        for (var t = 0; t < workers.Length; t++)
        {
            var slot = t * Stride;
            workers[t] = new Thread(() =>
            {
                try
                {
                    var k = new CpuKernelState(inputs);
                    go.Wait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        for (var i = 0; i < CpuKernels.ShaPerRound && Volatile.Read(ref stop) == 0; i++)
                        {
                            if (!k.RunSha()) { failed = "sha256"; break; }
                            Interlocked.Increment(ref counts[slot]);
                        }
                        if (failed is not null || Volatile.Read(ref stop) != 0) break;
                        if (!k.RunDeflate()) { failed = "deflate"; break; }
                        Interlocked.Increment(ref counts[slot + 1]);
                        if (Volatile.Read(ref stop) != 0) break;
                        if (!k.RunSort()) { failed = "sort"; break; }
                        Interlocked.Increment(ref counts[slot + 2]);
                    }
                }
                catch (Exception ex) { error = ex; }
                finally { Volatile.Write(ref stop, 1); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "bench-cpu" };
            workers[t].Start();
        }

        double Score(double seconds)
        {
            double sha = 0, def = 0, sort = 0;
            for (var t = 0; t < phase.Threads; t++)
            {
                sha += Volatile.Read(ref counts[t * Stride]);
                def += Volatile.Read(ref counts[t * Stride + 1]);
                sort += Volatile.Read(ref counts[t * Stride + 2]);
            }
            return seconds <= 0 ? 0 : (sha * CpuKernels.ShaWeight + def * CpuKernels.DeflateWeight + sort * CpuKernels.SortWeight) / seconds;
        }

        var sw = Stopwatch.StartNew();
        go.Set();
        var nextProgress = TimeSpan.Zero;
        try
        {
            while (Volatile.Read(ref stop) == 0 && sw.Elapsed < phase.Time)
            {
                if (ctx.Token.WaitHandle.WaitOne(50)) break;
                if (sw.Elapsed >= nextProgress)
                {
                    var done = before + sw.Elapsed.TotalSeconds;
                    ctx.Progress(phase.Subtest, done / total, phase.Scored ? Score(sw.Elapsed.TotalSeconds) : null, total - done);
                    nextProgress += TimeSpan.FromMilliseconds(500);
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var w in workers) w.Join();
        }
        var elapsed = sw.Elapsed.TotalSeconds;
        ctx.Token.ThrowIfCancellationRequested();
        if (failed is not null) throw new BenchFailedException(BenchIds.CheckFailed, phase.Subtest);
        if (error is not null) throw new BenchFailedException(BenchIds.Error, phase.Subtest);
        return Score(elapsed);
    }
}

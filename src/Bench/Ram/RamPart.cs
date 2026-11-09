using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OptimizationTool.Bench.Ram;

/// <summary>
/// RAM part (§4), ~15 s. One budget for all buffers (host: min(1 GiB, 20 % of available)); buffers are allocated in
/// turn so the bytes held at any time stay inside it. Bandwidth (read, write, copy) uses all logical processors with
/// Vector&lt;T&gt; loads/stores over two buffers of up to budget/2 each; latency chases pointers around one random cycle
/// (fixed seed) over min(256 MiB, budget/4), one node per 64-byte line. Skipped as low_memory when the budget is under
/// 256 MiB or available memory falls under 10 % of total during the test.
/// Units: GB/s = 1e9 bytes/s. Copy counts bytes read + bytes written (STREAM convention), like the GPU copy.
/// </summary>
internal sealed unsafe class RamPart : IBenchPart
{
    public const long MinBudget = 256L << 20;
    public const ulong LatencySeed = 0x4C4154454E435901UL;
    public const int NodeBytes = 64;

    public PartResult Run(PartContext ctx)
    {
        var s = ctx.Scale;
        var budget = ctx.Command.RamBudgetBytes ?? 0;
        if (budget < s.Bytes(MinBudget, 16L << 20)) throw new BenchFailedException(BenchIds.LowMemory);
        if (Native.MemoryLow()) throw new BenchFailedException(BenchIds.LowMemory);

        var phaseTime = ctx.Repeat is null ? s.Time(3) : s.Time(10);
        var latencyTime = ctx.Repeat is null ? s.Time(4) : s.Time(10);
        var plan = new List<string>();
        foreach (var id in BenchIds.Subtests[BenchIds.Ram]) if (ctx.Runs(id)) plan.Add(id);
        var total = plan.Sum(id => id == "latency" ? latencyTime.TotalSeconds + 0.5 * s.Factor : phaseTime.TotalSeconds) + 0.5 * s.Factor;
        var clock = Stopwatch.StartNew();
        var results = new List<SubtestValue>();

        var bw = plan.Where(p => p != "latency").ToList();
        if (bw.Count > 0)
        {
            var half = AlignDown(Math.Min(budget / 2, 512L << 20), 1 << 16);
            using var src = new NativeBlock(half);
            using var dst = new NativeBlock(half);
            Fill(src, dst, ctx); // first touch: commits both buffers before anything is timed
            foreach (var id in bw)
            {
                var gbs = Bandwidth(ctx, id, src, dst, phaseTime, clock, total);
                results.Add(new SubtestValue(id, gbs, "GB/s"));
                ctx.Live(PartContext.V(id, gbs));
            }
        }
        if (plan.Contains("latency"))
        {
            var bytes = AlignDown(Math.Min(256L << 20, budget / 4), NodeBytes);
            using var block = new NativeBlock(bytes);
            var ns = Latency(ctx, block, latencyTime, clock, total);
            results.Add(new SubtestValue("latency", ns, "ns"));
            ctx.Live(PartContext.V("latency", ns));
        }
        return new PartResult(results, [new("budget", budget)]);
    }

    private static long AlignDown(long v, long a) => v / a * a;

    private static void Guard(PartContext ctx)
    {
        ctx.Token.ThrowIfCancellationRequested();
        if (Native.MemoryLow()) throw new BenchFailedException(BenchIds.LowMemory);
    }

    private static void Fill(NativeBlock src, NativeBlock dst, PartContext ctx)
    {
        for (long off = 0; off < src.Length; off += 1 << 20)
        {
            if ((off & ((64 << 20) - 1)) == 0) Guard(ctx);
            var n = (nuint)Math.Min(1 << 20, src.Length - off);
            NativeMemory.Fill(src.Ptr + off, n, 0xA5);
            NativeMemory.Clear(dst.Ptr + off, n);
        }
    }

    /// <summary>
    /// Multi-threaded bandwidth for one of read|write|copy over <paramref name="time"/>. Every thread owns a slice and
    /// sweeps it repeatedly; bytes are summed and divided by wall time from release to the last thread's exit.
    /// </summary>
    private static double Bandwidth(PartContext ctx, string kind, NativeBlock src, NativeBlock dst, TimeSpan time, Stopwatch clock, double total)
    {
        var threads = Environment.ProcessorCount;
        var slice = src.Length / threads / 4096 * 4096;
        if (slice == 0) { threads = 1; slice = src.Length; }
        var bytes = new long[threads * 16];
        var stop = 0;
        using var go = new ManualResetEventSlim(false);
        var workers = new Thread[threads];
        var sink = new ulong[threads * 8];
        for (var t = 0; t < threads; t++)
        {
            var idx = t;
            var a = src.Ptr + (long)idx * slice;
            var b = dst.Ptr + (long)idx * slice;
            workers[t] = new Thread(() =>
            {
                go.Wait();
                var acc = 0UL;
                while (Volatile.Read(ref stop) == 0)
                {
                    switch (kind)
                    {
                        case "read": acc += ReadSweep(a, slice); break;
                        case "write": WriteSweep(b, slice, (ulong)idx); break;
                        default: CopySweep(a, b, slice); break;
                    }
                    Interlocked.Add(ref bytes[idx * 16], kind == "copy" ? 2 * slice : slice);
                }
                sink[idx * 8] = acc;
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "bench-ram" };
            workers[t].Start();
        }

        double Rate(double sec)
        {
            long sum = 0;
            for (var t = 0; t < threads; t++) sum += Volatile.Read(ref bytes[t * 16]);
            return sec <= 0 ? 0 : sum / sec / 1e9;
        }

        var sw = Stopwatch.StartNew();
        go.Set();
        var next = TimeSpan.FromMilliseconds(250);
        try
        {
            while (sw.Elapsed < time)
            {
                if (ctx.Token.WaitHandle.WaitOne(50)) break;
                if (sw.Elapsed >= next)
                {
                    if (Native.MemoryLow()) throw new BenchFailedException(BenchIds.LowMemory, kind);
                    var live = Rate(sw.Elapsed.TotalSeconds);
                    ctx.Progress(kind, clock.Elapsed.TotalSeconds / total, live, total - clock.Elapsed.TotalSeconds);
                    ctx.Live(PartContext.V(kind, live));
                    next += TimeSpan.FromMilliseconds(500);
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
        return Rate(elapsed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ReadSweep(byte* p, long len)
    {
        var step = Vector<byte>.Count * 4;
        Vector<ulong> a0 = default, a1 = default, a2 = default, a3 = default;
        var vc = Vector<byte>.Count;
        for (long i = 0; i + step <= len; i += step)
        {
            a0 += Vector.Load((ulong*)(p + i));
            a1 += Vector.Load((ulong*)(p + i + vc));
            a2 += Vector.Load((ulong*)(p + i + 2 * vc));
            a3 += Vector.Load((ulong*)(p + i + 3 * vc));
        }
        return Vector.Sum(a0 + a1 + a2 + a3);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteSweep(byte* p, long len, ulong value)
    {
        var v = new Vector<ulong>(value);
        var step = Vector<byte>.Count * 4;
        var vc = Vector<byte>.Count;
        for (long i = 0; i + step <= len; i += step)
        {
            Vector.Store(v, (ulong*)(p + i));
            Vector.Store(v, (ulong*)(p + i + vc));
            Vector.Store(v, (ulong*)(p + i + 2 * vc));
            Vector.Store(v, (ulong*)(p + i + 3 * vc));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopySweep(byte* a, byte* b, long len)
    {
        var step = Vector<byte>.Count * 4;
        var vc = Vector<byte>.Count;
        for (long i = 0; i + step <= len; i += step)
        {
            Vector.Store(Vector.Load((ulong*)(a + i)), (ulong*)(b + i));
            Vector.Store(Vector.Load((ulong*)(a + i + vc)), (ulong*)(b + i + vc));
            Vector.Store(Vector.Load((ulong*)(a + i + 2 * vc)), (ulong*)(b + i + 2 * vc));
            Vector.Store(Vector.Load((ulong*)(a + i + 3 * vc)), (ulong*)(b + i + 3 * vc));
        }
    }

    /// <summary>Average ns per dependent load around the single cycle; measured in chunks so stop is seen within ~0.1 s.</summary>
    private static double Latency(PartContext ctx, NativeBlock block, TimeSpan time, Stopwatch clock, double total)
    {
        var nodes = block.Length / NodeBytes;
        LatencyCycle.Build(block.Ptr, nodes, NodeBytes, LatencySeed, ctx.Token);
        LatencyCycle.ToPointers(block.Ptr, nodes, NodeBytes);
        Guard(ctx);

        var p = (void*)block.Ptr;
        p = LatencyCycle.Chase(p, Math.Min(nodes, 1 << 20)); // warm the TLB/cache state a little, not timed
        long loads = 0;
        const long Chunk = 1 << 18;
        var sw = Stopwatch.StartNew();
        var next = TimeSpan.FromMilliseconds(250);
        while (sw.Elapsed < time)
        {
            p = LatencyCycle.Chase(p, Chunk);
            loads += Chunk;
            if (sw.Elapsed >= next)
            {
                Guard(ctx);
                var live = sw.Elapsed.TotalNanoseconds / loads;
                ctx.Progress("latency", clock.Elapsed.TotalSeconds / total, live, total - clock.Elapsed.TotalSeconds);
                ctx.Live(PartContext.V("latency", live));
                next += TimeSpan.FromMilliseconds(500);
            }
            ctx.Token.ThrowIfCancellationRequested();
        }
        var ns = sw.Elapsed.TotalNanoseconds / loads;
        GC.KeepAlive((IntPtr)p);
        return ns;
    }
}

/// <summary>Pointer-chase cycle over nodes laid out every <c>stride</c> bytes (first 8 bytes of a node = next).</summary>
internal static unsafe class LatencyCycle
{
    /// <summary>
    /// Sattolo's algorithm in place: node i first holds i, then the shuffle leaves a permutation that is one single cycle
    /// through all nodes (no short loops a cache could keep). Values are node indices until <see cref="ToPointers"/>.
    /// </summary>
    public static void Build(byte* basePtr, long nodes, int stride, ulong seed, CancellationToken ct)
    {
        for (long i = 0; i < nodes; i++) *(ulong*)(basePtr + i * stride) = (ulong)i;
        var r = new SplitMix64(seed);
        for (var i = nodes - 1; i > 0; i--)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var j = (long)r.Below((ulong)i); // j in [0, i): Sattolo, not Fisher–Yates
            var pi = (ulong*)(basePtr + i * stride);
            var pj = (ulong*)(basePtr + j * stride);
            (*pi, *pj) = (*pj, *pi);
        }
    }

    /// <summary>Turns next-indices into absolute addresses so the chase is one dependent load per step.</summary>
    public static void ToPointers(byte* basePtr, long nodes, int stride)
    {
        for (long i = 0; i < nodes; i++)
        {
            var slot = (ulong*)(basePtr + i * stride);
            *slot = (ulong)(basePtr + (long)*slot * stride);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void* Chase(void* p, long count)
    {
        for (long i = 0; i < count; i += 8)
        {
            p = *(void**)p; p = *(void**)p; p = *(void**)p; p = *(void**)p;
            p = *(void**)p; p = *(void**)p; p = *(void**)p; p = *(void**)p;
        }
        return p;
    }
}

/// <summary>Page-aligned native memory, freed on dispose (never left to the GC).</summary>
internal sealed unsafe class NativeBlock : IDisposable
{
    public byte* Ptr { get; private set; }
    public long Length { get; }

    public NativeBlock(long length, int alignment = 4096)
    {
        Length = length;
        try { Ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment); }
        catch (OutOfMemoryException) { throw new BenchFailedException(BenchIds.LowMemory); }
    }

    public void Dispose()
    {
        if (Ptr is null) return;
        NativeMemory.AlignedFree(Ptr);
        Ptr = null;
    }
}

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Bench.Disk;

/// <summary>
/// Disk phase budgets and minimums. Minimums (a phase that misses one fails the part with too_slow, unscored):
/// seqWrite ≥ 256 MiB written; seqRead ≥ 256 MiB read; rand4k ≥ 5,000 completed reads (≈ 20 MiB; any SSD does that in
/// well under a second, a healthy HDD at QD 32 does ~150–400 IOPS × 6 s ≈ 1,000–2,500, so a 6 s HDD run fails — HDD
/// random reads are reported as too_slow rather than as a misleading number). Free-space reserve: 4 GiB after the file,
/// never scaled.
/// </summary>
internal sealed record DiskOptions(long MaxBytes, long MinWriteBytes, TimeSpan WriteTime, long MinReadBytes, TimeSpan ReadTime,
    long MinRandIos, TimeSpan RandTime, long FreeReserveBytes, TimeSpan RepeatTime, int BlockBytes = 1 << 20, int SeqQueueDepth = 8,
    int RandQueueDepth = 32)
{
    public static DiskOptions For(BenchScale s) => new(
        MaxBytes: s.Bytes(1L << 30, 8L << 20), MinWriteBytes: s.Bytes(256L << 20, 2L << 20), WriteTime: s.Time(8),
        MinReadBytes: s.Bytes(256L << 20, 2L << 20), ReadTime: s.Time(6),
        MinRandIos: s.Count(5000, 50), RandTime: s.Time(6),
        FreeReserveBytes: 4L << 30, RepeatTime: s.Time(10));
}

/// <summary>
/// Disk part (§4) in the host's bench folder. One uniquely named file opened CREATE_NEW | FILE_FLAG_DELETE_ON_CLOSE |
/// NO_BUFFERING | WRITE_THROUGH | OVERLAPPED with no sharing: Windows deletes it when the handle closes, including on a
/// crash or kill. Nothing is ever deleted by path. I/O goes through RandomAccess (thread-pool bound overlapped I/O) with
/// sector-aligned native buffers, offsets and lengths; every outstanding operation is drained before the handle closes.
/// Live ids: write, read, random4k (MB/s = 1e6 bytes/s).
/// </summary>
internal sealed class DiskPart(DiskOptions? options = null) : IBenchPart
{
    public PartResult Run(PartContext ctx)
    {
        var o = options ?? DiskOptions.For(ctx.Scale);
        var folder = ctx.Command.Folder;
        if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new BenchFailedException(BenchIds.Error);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new BenchFailedException(BenchIds.Error);
        var volume = Native.VolumeOf(folder) ?? Path.GetPathRoot(folder)!.TrimEnd('\\');

        if (!Native.GetDiskFreeSpaceEx(folder, out var free, out _, out _)) throw new BenchFailedException(BenchIds.Error);
        if ((long)free - o.MaxBytes < o.FreeReserveBytes) throw new BenchFailedException(BenchIds.NeedsSpace);

        var path = Path.Combine(folder, "ot-bench-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var file = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero, Native.CREATE_NEW,
            Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_DELETE_ON_CLOSE | Native.FILE_FLAG_NO_BUFFERING
            | Native.FILE_FLAG_WRITE_THROUGH | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid)
        {
            Diag.Code("disk_open:" + Marshal.GetLastPInvokeError());
            throw new BenchFailedException(BenchIds.Error);
        }

        var align = SectorAlignment(file);
        var randSize = Math.Max(4096, align);           // "4k" reads; larger only on a >4 KiB-sector disk
        var block = (int)Math.Max(o.BlockBytes, align) / align * align;
        var buffers = new List<AlignedBuffer>();
        var drained = true;
        try
        {
            for (var i = 0; i < o.SeqQueueDepth; i++) buffers.Add(new AlignedBuffer(block, align, fillSeed: (ulong)i + 1));
            var randBuffers = new List<AlignedBuffer>();
            for (var i = 0; i < o.RandQueueDepth; i++) randBuffers.Add(new AlignedBuffer(randSize, align, fillSeed: 0));
            buffers.AddRange(randBuffers);
            var seqBuffers = buffers.Take(o.SeqQueueDepth).ToArray();

            var planned = ctx.Repeat switch
            {
                null => o.WriteTime + o.ReadTime + o.RandTime,
                "seqWrite" => o.RepeatTime,
                _ => o.WriteTime + o.RepeatTime,
            };
            var run = new Runner(ctx, planned.TotalSeconds);
            var results = new List<SubtestValue>();
            long written;

            // Sequential write (also the preparation for a repeated seqRead/rand4k, then unscored).
            {
                var repeatWrite = ctx.Repeat == "seqWrite";
                var cap = o.MaxBytes / block * block;
                long next = 0;
                var r = run.Phase("seqWrite", "write", repeatWrite ? o.RepeatTime : o.WriteTime, seqBuffers.Length, async (w, ct) =>
                {
                    var off = Interlocked.Add(ref next, block) - block;
                    if (off + block > cap)
                    {
                        if (!repeatWrite) return 0;
                        off %= cap; // repeat: keep writing for the whole time, wrapping inside the 1 GiB region
                    }
                    await RandomAccess.WriteAsync(file, seqBuffers[w].Memory, off, ct).ConfigureAwait(false);
                    return block;
                }, ref drained);
                written = Math.Min(r.Bytes, cap) / block * block;
                if (r.Bytes < o.MinWriteBytes) throw new BenchFailedException(BenchIds.TooSlow, "seqWrite");
                if (ctx.Runs("seqWrite")) results.Add(new SubtestValue("seqWrite", r.MBps, "MB/s"));
            }

            if (ctx.Runs("seqRead"))
            {
                long n = 0;
                var r = run.Phase("seqRead", "read", ctx.Repeat is null ? o.ReadTime : o.RepeatTime, seqBuffers.Length, async (w, ct) =>
                {
                    var off = (Interlocked.Increment(ref n) - 1) * block % written;
                    var got = await RandomAccess.ReadAsync(file, seqBuffers[w].Memory, off, ct).ConfigureAwait(false);
                    return got;
                }, ref drained);
                if (r.Bytes < Math.Min(o.MinReadBytes, Math.Max(written, block))) throw new BenchFailedException(BenchIds.TooSlow, "seqRead");
                results.Add(new SubtestValue("seqRead", r.MBps, "MB/s"));
            }

            if (ctx.Runs("rand4k"))
            {
                var slots = (ulong)(written / randSize);
                var rngs = new SplitMix64[randBuffers.Count];
                for (var i = 0; i < rngs.Length; i++) rngs[i] = new SplitMix64(0x52414E44344B0000UL + (ulong)i);
                var r = run.Phase("rand4k", "random4k", ctx.Repeat is null ? o.RandTime : o.RepeatTime, randBuffers.Count, async (w, ct) =>
                {
                    var off = (long)rngs[w].Below(slots) * randSize;
                    return await RandomAccess.ReadAsync(file, randBuffers[w].Memory, off, ct).ConfigureAwait(false);
                }, ref drained);
                if (r.Ios < o.MinRandIos) throw new BenchFailedException(BenchIds.TooSlow, "rand4k");
                results.Add(new SubtestValue("rand4k", r.MBps, "MB/s"));
            }

            return new PartResult(results, [new("volume", volume), new("written", written)]);
        }
        finally
        {
            file.Dispose(); // FILE_FLAG_DELETE_ON_CLOSE: Windows deletes the file now
            // Buffers may be freed only once the kernel is done with them; if a drain timed out, leak rather than risk it.
            if (drained) foreach (var b in buffers) ((IDisposable)b).Dispose();
        }
    }

    /// <summary>max(4096, logical, physical-for-performance, file-system effective physical sector size).</summary>
    internal static int SectorAlignment(SafeFileHandle file)
    {
        var a = 4096u;
        if (Native.GetFileInformationByHandleEx(file, Native.FileStorageInfo, out var info, (uint)Marshal.SizeOf<Native.FILE_STORAGE_INFO>()))
        {
            a = Math.Max(a, info.LogicalBytesPerSector);
            a = Math.Max(a, info.PhysicalBytesPerSectorForPerformance);
            a = Math.Max(a, info.FileSystemEffectivePhysicalBytesPerSectorForAtomicity);
        }
        if (a > 1 << 20 || (a & (a - 1)) != 0) a = 4096; // nonsense values: fall back to the common size
        return (int)a;
    }

    internal readonly record struct PhaseResult(long Bytes, long Ios, double Seconds)
    {
        public double MBps => Seconds <= 0 ? 0 : Bytes / Seconds / 1e6;
    }

    /// <summary>Runs queue-depth workers and reports progress; owns the timing and the drain.</summary>
    private sealed class Runner(PartContext ctx, double plannedSeconds)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public PhaseResult Phase(string subtest, string liveId, TimeSpan time, int depth, Func<int, CancellationToken, Task<int>> io,
            ref bool drained)
        {
            long bytes = 0, ios = 0;
            var timeUp = 0;
            var ct = ctx.Token;
            var sw = Stopwatch.StartNew();
            var tasks = new Task[depth];
            for (var w = 0; w < depth; w++)
            {
                var worker = w;
                tasks[w] = Task.Run(async () =>
                {
                    while (Volatile.Read(ref timeUp) == 0 && !ct.IsCancellationRequested)
                    {
                        var n = await io(worker, ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        Interlocked.Add(ref bytes, n);
                        Interlocked.Increment(ref ios);
                    }
                });
            }
            var all = Task.WhenAll(tasks);
            var next = TimeSpan.FromMilliseconds(250);
            while (!all.IsCompleted)
            {
                if (sw.Elapsed >= time) Volatile.Write(ref timeUp, 1);
                try { all.Wait(50); } catch (AggregateException) { }
                if (sw.Elapsed >= next && !all.IsCompleted)
                {
                    var live = Interlocked.Read(ref bytes) / sw.Elapsed.TotalSeconds / 1e6;
                    var done = _clock.Elapsed.TotalSeconds;
                    ctx.Progress(subtest, done / plannedSeconds, live, plannedSeconds - done);
                    ctx.Live(PartContext.V(liveId, live));
                    next += TimeSpan.FromMilliseconds(500);
                }
                // On stop the token has cancelled every in-flight operation (CancelIoEx); we still wait for each to
                // complete so no buffer is freed under the kernel. Bounded: give up after 5 s and leak the buffers.
                if (ct.IsCancellationRequested && !all.Wait(TimeSpan.FromSeconds(5)))
                {
                    drained = false;
                    break;
                }
            }
            var elapsed = sw.Elapsed.TotalSeconds;
            ct.ThrowIfCancellationRequested();
            if (all.IsFaulted)
            {
                var ex = all.Exception!.GetBaseException();
                Diag.Code("disk_io:" + ex.GetType().Name + ":" + (ex.HResult & 0xFFFF));
                if ((ex.HResult & 0xFFFF) == 112) throw new BenchFailedException(BenchIds.NeedsSpace, subtest); // ERROR_DISK_FULL
                throw new BenchFailedException(BenchIds.Error, subtest);
            }
            var result = new PhaseResult(Interlocked.Read(ref bytes), Interlocked.Read(ref ios), elapsed);
            ctx.Live(PartContext.V(liveId, result.MBps));
            return result;
        }
    }
}

/// <summary>Sector-aligned native buffer exposed as Memory&lt;byte&gt; for RandomAccess. Freed explicitly.</summary>
internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private byte* _ptr;
    private readonly int _length;

    public AlignedBuffer(int length, int alignment, ulong fillSeed)
    {
        _length = length;
        _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        // Incompressible content for writes (some SSDs compress); zeros for read targets.
        if (fillSeed == 0) NativeMemory.Clear(_ptr, (nuint)length);
        else
        {
            var r = new SplitMix64(fillSeed);
            var s = new Span<ulong>(_ptr, length / 8);
            for (var i = 0; i < s.Length; i++) s[i] = r.Next();
        }
    }

    public override Span<byte> GetSpan() => new(_ptr, _length);
    public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex);
    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (_ptr is null) return;
        NativeMemory.AlignedFree(_ptr);
        _ptr = null;
    }
}

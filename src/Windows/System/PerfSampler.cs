using System.Diagnostics;
using System.Runtime.InteropServices;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

/// <summary>One process instance: PID plus creation time (FILETIME), so a reused PID is a different key.</summary>
internal readonly record struct ProcKey(uint Pid, long CreateTime);

internal sealed record ProcSample(string Image, long CpuTime100ns, ulong WorkingSetBytes);

/// <summary>
/// One snapshot. System times are cumulative (deltas are taken in normalize). Disk values are PDH rates over the
/// interval that ended at this snapshot (null on the first snapshot or when PDH has no valid value).
/// </summary>
internal sealed record PerfSnapshot(
    double ElapsedSeconds, long SysIdle, long SysKernel, long SysUser,
    ulong? AvailPhysBytes, ulong? TotalPhysBytes, ulong? CommitTotalBytes, ulong? CommitLimitBytes,
    double? DiskIdlePercent, double? DiskBytesPerSec, double? DiskSecPerTransfer,
    uint ForegroundPid, IReadOnlyDictionary<ProcKey, ProcSample>? Processes);

internal sealed record PerfRaw(
    IReadOnlyList<PerfSnapshot> Snapshots, double WindowSeconds, double IntervalSeconds, int LogicalProcessors,
    uint SelfPid, bool StoppedEarly, string? DiskError, string? ProcessError, double OverheadCpuMs, double OverheadWallMs);

/// <summary>
/// win.perf.sample: a bounded observation (window/interval from <see cref="CollectorOptions"/>; default 60 s / 2 s).
/// One snapshot per interval: GetSystemTimes, GlobalMemoryStatusEx, GetPerformanceInfo, one PDH collect (disk), one
/// NtQuerySystemInformation process list (no per-process handles, no WMI). Cancellation or skip stops the loop and
/// returns what was measured as partial. Skippable.
/// </summary>
internal sealed class PerfSampler(CollectorOptions options) : SystemCollector<PerfRaw>(options)
{
    private const int MaxSnapshots = 1000;

    public override string Id => "win.perf.sample";
    public override string Category => "performance";
    /// <summary>Shown on the scan line while the sample runs ("Measuring · 42 s").</summary>
    public override string Label => "Measuring";
    public override TimeSpan Timeout => Window + TimeSpan.FromSeconds(15);
    public override string GapTitle => "Live performance sample";
    public override string GapSource => "Windows performance counters";
    public override string GapDisplay => "The live performance sample was not taken.";

    private TimeSpan Interval => Options.SampleInterval < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : Options.SampleInterval;
    private TimeSpan Window => Options.SampleWindow < Interval ? Interval : Options.SampleWindow;

    /// <summary>Sampling owns its own cancellation: a stop returns the partial measurement instead of throwing.</summary>
    public override async Task<CollectorResult> CollectAsync(CancellationToken ct)
    {
        try
        {
            var raw = await SampleAsync(ct).ConfigureAwait(false);
            return Normalize(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new([], new("failed", 0, ex.GetType().Name)); }
    }

    protected override PerfRaw Read(CancellationToken ct) => SampleAsync(ct).GetAwaiter().GetResult();

    private async Task<PerfRaw> SampleAsync(CancellationToken ct)
    {
        var interval = Interval;
        var expected = Math.Min(MaxSnapshots, (int)Math.Floor(Window.TotalMilliseconds / interval.TotalMilliseconds) + 1);
        var snapshots = new List<PerfSnapshot>(expected);
        var logical = (int)Native.GetActiveProcessorCount(Native.AllProcessorGroups);
        if (logical <= 0) logical = Environment.ProcessorCount;

        using var reader = new SnapshotReader();
        var clock = Stopwatch.StartNew();
        var stoppedEarly = false;
        for (var k = 0; k < expected; k++)
        {
            var due = interval * k - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                try { await Task.Delay(due, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { stoppedEarly = true; break; }
            }
            if (ct.IsCancellationRequested) { stoppedEarly = true; break; }
            snapshots.Add(await Task.Run(() => reader.Take(clock.Elapsed.TotalSeconds), CancellationToken.None).ConfigureAwait(false));
        }

        return new PerfRaw(snapshots, Window.TotalSeconds, interval.TotalSeconds, logical, (uint)Environment.ProcessId,
            stoppedEarly, reader.DiskError, reader.ProcessError, reader.OverheadCpuMs, reader.OverheadWallMs);
    }

    public override CollectorResult Normalize(PerfRaw raw) => PerfNormalizer.Normalize(raw);

    /// <summary>Holds the PDH query and the reusable process-list buffer for one sampling run.</summary>
    private sealed unsafe class SnapshotReader : IDisposable
    {
        private nint _query, _idle, _bytes, _latency;
        private byte* _buf;
        private uint _bufSize;

        public string? DiskError { get; private set; }
        public string? ProcessError { get; private set; }
        public double OverheadCpuMs { get; private set; }
        public double OverheadWallMs { get; private set; }

        public SnapshotReader()
        {
            var rc = Native.PdhOpenQueryW(null, 0, out _query);
            if (rc != 0) { DiskError = $"pdh 0x{rc:x8}"; _query = 0; return; }
            rc = Native.PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\% Idle Time", 0, out _idle);
            if (rc == 0) rc = Native.PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", 0, out _bytes);
            if (rc == 0) rc = Native.PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer", 0, out _latency);
            if (rc != 0) { DiskError = $"pdh 0x{rc:x8}"; Native.PdhCloseQuery(_query); _query = 0; }
        }

        public PerfSnapshot Take(double elapsed)
        {
            var wall = Stopwatch.GetTimestamp();
            var thread = Native.GetCurrentThread();
            Native.GetThreadTimes(thread, out _, out _, out var k0, out var u0);

            Native.GetSystemTimes(out var idle, out var kernel, out var user);

            var ms = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            var memOk = Native.GlobalMemoryStatusEx(ref ms);
            ulong? commitTotal = null, commitLimit = null;
            if (Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PerformanceInformation>()))
            {
                commitTotal = (ulong)pi.CommitTotal * (ulong)pi.PageSize;
                commitLimit = (ulong)pi.CommitLimit * (ulong)pi.PageSize;
            }

            double? diskIdle = null, diskBytes = null, diskLatency = null;
            if (_query != 0 && Native.PdhCollectQueryData(_query) == 0)
            {
                diskIdle = Value(_idle);
                diskBytes = Value(_bytes);
                diskLatency = Value(_latency);
            }

            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var fg);
            var procs = ReadProcesses();

            Native.GetThreadTimes(thread, out _, out _, out var k1, out var u1);
            OverheadCpuMs += (k1 - k0 + (u1 - u0)) / 10_000.0;
            OverheadWallMs += Stopwatch.GetElapsedTime(wall).TotalMilliseconds;

            return new PerfSnapshot(elapsed, idle, kernel, user,
                memOk ? ms.AvailPhys : null, memOk ? ms.TotalPhys : null, commitTotal, commitLimit,
                diskIdle, diskBytes, diskLatency, fg, procs);
        }

        private static double? Value(nint counter)
        {
            if (Native.PdhGetFormattedCounterValue(counter, Native.PdhFmtDouble | Native.PdhFmtNoCap100, out _, out var v) != 0) return null;
            // PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1.
            return v.CStatus <= 1 && double.IsFinite(v.DoubleValue) ? v.DoubleValue : null;
        }

        private Dictionary<ProcKey, ProcSample>? ReadProcesses()
        {
            if (!Environment.Is64BitProcess) { ProcessError = "x86 layout unsupported"; return null; }
            if (_buf == null) { _bufSize = 1 << 20; _buf = (byte*)NativeMemory.Alloc(_bufSize); }
            int status;
            uint needed;
            while ((status = Native.NtQuerySystemInformation(Native.SystemProcessInformationClass, _buf, _bufSize, out needed)) == Native.StatusInfoLengthMismatch)
            {
                var next = Math.Max(needed + (64u << 10), _bufSize * 2);
                if (next > 64u << 20) { ProcessError = "process list too large"; return null; }
                NativeMemory.Free(_buf);
                _bufSize = next;
                _buf = (byte*)NativeMemory.Alloc(_bufSize);
            }
            if (status < 0) { ProcessError = $"ntstatus 0x{status:x8}"; return null; }

            var map = new Dictionary<ProcKey, ProcSample>(512);
            uint off = 0;
            while (off < needed)
            {
                var e = _buf + off;
                var pid = (uint)*(nint*)(e + Native.SpiUniqueProcessId);
                var create = *(long*)(e + Native.SpiCreateTime);
                var cpu = *(long*)(e + Native.SpiUserTime) + *(long*)(e + Native.SpiKernelTime);
                var ws = (ulong)*(nuint*)(e + Native.SpiWorkingSetSize);
                var len = *(ushort*)(e + Native.SpiImageNameLength);
                var namePtr = *(char**)(e + Native.SpiImageNameBuffer);
                var image = pid == 0 ? "Idle" : namePtr != null && len > 0 ? new string(namePtr, 0, len / 2) : "System";
                map.TryAdd(new ProcKey(pid, create), new ProcSample(image, cpu, ws));
                var nextOff = *(uint*)(e + Native.SpiNextEntryOffset);
                if (nextOff == 0) break;
                off += nextOff;
            }
            return map;
        }

        public void Dispose()
        {
            if (_query != 0) { Native.PdhCloseQuery(_query); _query = 0; }
            if (_buf != null) { NativeMemory.Free(_buf); _buf = null; }
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OptimizationTool.Bench.Gpu;

internal sealed record GpuOptions(TimeSpan FmaTime, TimeSpan CopyTime, TimeSpan MaxCalibration, TimeSpan RepeatTime, long CopyBytes)
{
    public static GpuOptions For(BenchScale s) =>
        new(s.Time(15), s.Time(15), TimeSpan.FromSeconds(Math.Max(0.5, 3 * s.Factor)), s.Time(10), 128L << 20);
}

/// <summary>
/// GPU part (§4): Direct3D 11 compute, feature level 11_0 required, on the adapter whose LUID the host passed (else the
/// DXGI high-performance adapter). Two kernels from embedded precompiled bytecode (Gpu/Shaders): FP32 FMA throughput
/// (GFLOPS = 1e9 flop/s, an FMA counted as 2) and buffer copy (GB/s = 1e9 bytes/s, read + written). ~30 s total.
/// Dispatch sizing starts tiny and doubles until one measured submission takes ~20 ms (10–40 ms band), with at most 2
/// submissions in flight (event queries). Each submission is timed by GPU timestamps inside a TIMESTAMP_DISJOINT
/// bracket; disjoint intervals are discarded; calibration intervals are not scored. A removed/reset device → gpu_reset.
/// </summary>
internal sealed class GpuPart(GpuOptions? options = null) : IBenchPart
{
    private const int MaxFmaGroups = 16384, ThreadsPerGroup = 256;

    public PartResult Run(PartContext ctx)
    {
        var o = options ?? GpuOptions.For(ctx.Scale);
        using var factory = TryCreateFactory() ?? throw new BenchFailedException(BenchIds.Unsupported);
        using var adapter = PickAdapter(factory, ctx.Command.GpuLuid) ?? throw new BenchFailedException(BenchIds.Unsupported);
        var desc = adapter.Description1;
        if ((desc.Flags & AdapterFlags.Software) != 0) throw new BenchFailedException(BenchIds.Unsupported);

        if (D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device, out var level, out var context).Failure
            || device is null || context is null || level < FeatureLevel.Level_11_0)
        {
            device?.Dispose();
            context?.Dispose();
            throw new BenchFailedException(BenchIds.Unsupported);
        }

        string? current = null;
        try
        {
            using (device)
            using (context)
            using (var gpu = new GpuSession(device, context, ctx, o))
            {
                var planned = ctx.Repeat is null ? (o.FmaTime + o.CopyTime).TotalSeconds : o.RepeatTime.TotalSeconds;
                var clock = Stopwatch.StartNew();
                var results = new List<SubtestValue>();
                if (ctx.Runs("fma"))
                {
                    current = "fma";
                    var v = gpu.Fma(ctx.Repeat is null ? o.FmaTime : o.RepeatTime, clock, planned);
                    results.Add(new SubtestValue("fma", v, "GFLOPS"));
                }
                if (ctx.Runs("copy"))
                {
                    current = "copy";
                    var v = gpu.Copy(ctx.Repeat is null ? o.CopyTime : o.RepeatTime, clock, planned);
                    results.Add(new SubtestValue("copy", v, "GB/s"));
                }
                return new PartResult(results, [new("adapterLuid", LuidHex(desc.Luid))]);
            }
        }
        catch (BenchFailedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // SharpGenException carries the HRESULT; anything from a removed device is gpu_reset.
            if (GpuSession.IsDeviceLost(ex.HResult)) throw new BenchFailedException(BenchIds.GpuReset, current);
            Diag.Code("gpu_error:" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            throw new BenchFailedException(BenchIds.Error, current);
        }
    }

    private static IDXGIFactory6? TryCreateFactory()
    {
        try { return DXGI.CreateDXGIFactory2<IDXGIFactory6>(false); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// With a LUID from the host: exactly that adapter, or null (→ unsupported) when it is malformed or gone, e.g. an
    /// eGPU unplugged since the host described the hardware. Measuring another GPU would mislabel the score, configId and
    /// sensors. Only without a LUID: the DXGI high-performance adapter.
    /// </summary>
    private static IDXGIAdapter1? PickAdapter(IDXGIFactory6 factory, string? luidHex)
    {
        if (luidHex is not null)
        {
            if (ParseLuid(luidHex) is not { } luid) return null;
            return factory.EnumAdapterByLuid(luid, out IDXGIAdapter1? byLuid).Success ? byLuid : null;
        }
        return factory.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out IDXGIAdapter1? hp).Success ? hp : null;
    }

    /// <summary>LUID as 16 upper-case hex chars: HighPart (8) then LowPart (8).</summary>
    internal static string LuidHex(Luid luid) => (((ulong)(uint)luid.HighPart << 32) | luid.LowPart).ToString("X16", CultureInfo.InvariantCulture);

    internal static Luid? ParseLuid(string? hex)
    {
        if (hex is null || hex.Length != 16 || !ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return null;
        return new Luid((uint)(v & 0xFFFFFFFF), (int)(v >> 32));
    }
}

/// <summary>Device-side state and the two measured kernels. Not thread-safe; used from the part thread only.</summary>
internal sealed unsafe class GpuSession : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly PartContext _part;
    private readonly GpuOptions _o;
    private readonly List<IDisposable> _owned = [];
    private readonly Slot[] _slots;
    private readonly ID3D11Buffer _params;

    private const int ThreadsPerGroup = 256, MaxFmaGroups = 16384;
    private const double TargetLowMs = 10, TargetHighMs = 40;

    public GpuSession(ID3D11Device device, ID3D11DeviceContext ctx, PartContext part, GpuOptions o)
    {
        _device = device;
        _ctx = ctx;
        _part = part;
        _o = o;
        _slots = [new Slot(device), new Slot(device)];
        _params = Own(device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default)));
    }

    private T Own<T>(T d) where T : IDisposable { _owned.Add(d); return d; }

    public static bool IsDeviceLost(int hr) => (uint)hr is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params { public uint A; public uint B; public uint C; public uint D; }

    private void SetParams(uint a, uint b, uint c, uint d)
    {
        var p = new Params { A = a, B = b, C = c, D = d };
        _ctx.UpdateSubresource(in p, _params);
    }

    // ---------------------------------------------------------------- FMA

    public double Fma(TimeSpan time, Stopwatch clock, double planned)
    {
        var shader = Own(_device.CreateComputeShader(Shaders.Load("fma")));
        var output = Own(_device.CreateBuffer(new BufferDescription((uint)(MaxFmaGroups * ThreadsPerGroup * 4),
            BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4)));
        var uav = Own(_device.CreateUnorderedAccessView(output));
        _ctx.CSSetShader(shader);
        _ctx.CSSetUnorderedAccessViews(0, [uav]);
        _ctx.CSSetConstantBuffers(0, [_params]);

        ValidateFma(output);
        uint groups = 8, iterations = 16;
        uint setIterations = 0;
        var gflops = Measure("fma", time, clock, planned,
            issue: () =>
            {
                if (setIterations != iterations)
                {
                    SetParams(iterations, BitConverter.SingleToUInt32Bits(GpuValidator.A), BitConverter.SingleToUInt32Bits(GpuValidator.B), 0);
                    setIterations = iterations;
                }
                _ctx.Dispatch(groups, 1, 1);
                return (double)groups * ThreadsPerGroup * iterations * 8 * 8 * 2; // flops
            },
            grow: () =>
            {
                if (groups < MaxFmaGroups) groups *= 2;
                else if (iterations < 1 << 20) iterations *= 2;
                else return false;
                return true;
            },
            shrink: () => { if (iterations > 16) iterations /= 2; else if (groups > 1) groups /= 2; });
        ValidateFma(output);
        return gflops;
    }

    private void ValidateFma(ID3D11Buffer output)
    {
        var bytes = GpuValidator.ValidateThreads * 4;
        SetParams(GpuValidator.ValidateIterations, BitConverter.SingleToUInt32Bits(GpuValidator.A), BitConverter.SingleToUInt32Bits(GpuValidator.B), 0);
        _ctx.Dispatch(GpuValidator.ValidateThreads / ThreadsPerGroup, 1, 1);
        using var staging = _device.CreateBuffer(new BufferDescription(bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, output, 0, new Box(0, 0, 0, (int)bytes, 1, 1));
        var map = _ctx.Map(staging, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var values = new ReadOnlySpan<float>((void*)map.DataPointer, (int)GpuValidator.ValidateThreads);
            if (GpuValidator.CheckFma(values, GpuValidator.ValidateIterations) >= 0)
                throw new BenchFailedException(BenchIds.CheckFailed, "fma");
        }
        finally { _ctx.Unmap(staging, 0); }
        // Parameters must be re-sent before the next measured dispatch.
        SetParams(0, 0, 0, 0);
    }

    // ---------------------------------------------------------------- copy

    public double Copy(TimeSpan time, Stopwatch clock, double planned)
    {
        var bytes = _o.CopyBytes / (ThreadsPerGroup * 16) * (ThreadsPerGroup * 16);
        var shader = Own(_device.CreateComputeShader(Shaders.Load("copy")));
        var src = Own(_device.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.ShaderResource, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16)));
        var dst = Own(_device.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16)));
        var srv = Own(_device.CreateShaderResourceView(src));
        var uav = Own(_device.CreateUnorderedAccessView(dst));

        // Fill the source with the integer pattern in 4 MiB chunks.
        const int Chunk = 4 << 20;
        var words = new uint[Chunk / 4];
        for (long off = 0; off < bytes; off += Chunk)
        {
            _part.Token.ThrowIfCancellationRequested();
            var n = (int)Math.Min(Chunk, bytes - off);
            for (var i = 0; i < n / 4; i++) words[i] = GpuValidator.CopyPattern(off / 4 + i);
            _ctx.UpdateSubresource(new ReadOnlySpan<uint>(words, 0, n / 4), src, 0, 0, 0, new Box((int)off, 0, 0, (int)(off + n), 1, 1));
        }

        _ctx.CSSetShader(shader);
        _ctx.CSSetShaderResources(0, [srv]);
        _ctx.CSSetUnorderedAccessViews(0, [uav]);
        _ctx.CSSetConstantBuffers(0, [_params]);

        var maxCount = (uint)(bytes / 16);
        uint count = 64 * ThreadsPerGroup, repeats = 1, setCount = 0;
        var gbs = Measure("copy", time, clock, planned,
            issue: () =>
            {
                if (setCount != count) { SetParams(count, 0, 0, 0); setCount = count; }
                var groups = (count + ThreadsPerGroup - 1) / ThreadsPerGroup;
                for (var r = 0; r < repeats; r++) _ctx.Dispatch(groups, 1, 1);
                return (double)count * 16 * 2 * repeats; // bytes read + written
            },
            grow: () =>
            {
                if (count < maxCount) count = Math.Min(maxCount, count * 2);
                else if (repeats < 256) repeats *= 2;
                else return false;
                return true;
            },
            shrink: () => { if (repeats > 1) repeats /= 2; else if (count > ThreadsPerGroup) count /= 2; });

        // Exact check of the whole destination: one last full-size copy, then compare chunk by chunk.
        SetParams(maxCount, 0, 0, 0);
        _ctx.Dispatch(maxCount / ThreadsPerGroup, 1, 1);
        using var staging = _device.CreateBuffer(new BufferDescription(Chunk, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        for (long off = 0; off < bytes; off += Chunk)
        {
            _part.Token.ThrowIfCancellationRequested();
            var n = (int)Math.Min(Chunk, bytes - off);
            _ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, dst, 0, new Box((int)off, 0, 0, (int)(off + n), 1, 1));
            var map = _ctx.Map(staging, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                if (GpuValidator.CheckCopy(new ReadOnlySpan<uint>((void*)map.DataPointer, n / 4), off / 4) >= 0)
                    throw new BenchFailedException(BenchIds.CheckFailed, "copy");
            }
            finally { _ctx.Unmap(staging, 0); }
        }
        _ctx.CSSetShaderResources(0, [null!]);
        return gbs;
    }

    // ---------------------------------------------------------------- timing

    private sealed class Slot : IDisposable
    {
        public readonly ID3D11Query Disjoint, T0, T1, Done;
        public bool Pending, Scored;
        public double Work;

        public Slot(ID3D11Device d)
        {
            Disjoint = d.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            T0 = d.CreateQuery(new QueryDescription(QueryType.Timestamp));
            T1 = d.CreateQuery(new QueryDescription(QueryType.Timestamp));
            Done = d.CreateQuery(new QueryDescription(QueryType.Event));
        }

        public void Dispose()
        {
            Disjoint.Dispose();
            T0.Dispose();
            T1.Dispose();
            Done.Dispose();
        }
    }

    /// <summary>
    /// Submits timed batches with at most two in flight until <paramref name="time"/> is up. Calibrates first (doubling
    /// via <paramref name="grow"/> until a batch takes 10–40 ms, or <see cref="GpuOptions.MaxCalibration"/> passes);
    /// only batches submitted after calibration count. Returns work / GPU seconds / 1e9.
    /// </summary>
    private double Measure(string subtest, TimeSpan time, Stopwatch clock, double planned, Func<double> issue, Func<bool> grow, Action shrink)
    {
        var calibrated = false;
        double work = 0, seconds = 0;
        var sw = Stopwatch.StartNew();
        var next = TimeSpan.FromMilliseconds(250);
        var i = 0;
        while (sw.Elapsed < time)
        {
            _part.Token.ThrowIfCancellationRequested();
            if (!calibrated && sw.Elapsed > _o.MaxCalibration) calibrated = true;
            var slot = _slots[i++ & 1];
            if (slot.Pending) Collect(slot);
            // While calibrating, keep one batch in flight so each size change is seen before the next submission.
            if (!calibrated && _slots[i & 1].Pending) Collect(_slots[i & 1]);
            Submit(slot, issue, calibrated);

            void Collect(Slot s)
            {
                var dt = Wait(s);
                if (dt is not { } d || d <= 0) return;
                if (s.Scored) { work += s.Work; seconds += d; }
                else if (!calibrated)
                {
                    var ms = d * 1000;
                    if (ms < TargetLowMs) { if (!grow()) calibrated = true; }
                    else if (ms > TargetHighMs) shrink();
                    else calibrated = true;
                }
            }

            if (sw.Elapsed >= next)
            {
                double? live = seconds > 0 ? work / seconds / 1e9 : null;
                var done = clock.Elapsed.TotalSeconds;
                _part.Progress(subtest, done / planned, live, planned - done);
                next += TimeSpan.FromMilliseconds(500);
            }
        }
        foreach (var s in _slots)
        {
            if (!s.Pending) continue;
            var dt = Wait(s);
            if (dt is { } d && d > 0 && s.Scored) { work += s.Work; seconds += d; }
        }
        if (seconds <= 0) throw new BenchFailedException(BenchIds.Error, subtest);
        return work / seconds / 1e9;
    }

    private void Submit(Slot s, Func<double> issue, bool scored)
    {
        _ctx.Begin(s.Disjoint);
        _ctx.End(s.T0);
        s.Work = issue();
        _ctx.End(s.T1);
        _ctx.End(s.Disjoint);
        _ctx.End(s.Done);
        _ctx.Flush();
        s.Pending = true;
        s.Scored = scored;
    }

    /// <summary>Waits for a slot's event, then reads its timestamps. Null when the interval was disjoint.</summary>
    private double? Wait(Slot s)
    {
        s.Pending = false;
        var waited = Stopwatch.StartNew();
        int done = 0;
        while (Poll(s.Done, &done, 4) == 1)
        {
            if (_part.Token.IsCancellationRequested) return null; // device teardown waits for the GPU; nothing to free here
            if (waited.Elapsed > TimeSpan.FromSeconds(10)) throw new BenchFailedException(BenchIds.GpuReset);
            Thread.Sleep(1);
        }
        QueryDataTimestampDisjoint dj;
        ulong t0, t1;
        while (Poll(s.Disjoint, &dj, (uint)sizeof(QueryDataTimestampDisjoint)) == 1) Thread.Sleep(0);
        while (Poll(s.T0, &t0, 8) == 1) Thread.Sleep(0);
        while (Poll(s.T1, &t1, 8) == 1) Thread.Sleep(0);
        if (dj.Disjoint || dj.Frequency == 0 || t1 <= t0) return null;
        return (t1 - t0) / (double)dj.Frequency;
    }

    /// <summary>0 = data, 1 = not yet. A device-lost HRESULT throws gpu_reset; other failures throw error.</summary>
    private int Poll(ID3D11Asynchronous q, void* data, uint size)
    {
        var r = _ctx.GetData(q, (IntPtr)data, size, AsyncGetDataFlags.None);
        if (r.Code == 0) return 0;
        if (r.Code == 1) return 1;
        if (IsDeviceLost(r.Code) || IsDeviceLost(_device.DeviceRemovedReason.Code)) throw new BenchFailedException(BenchIds.GpuReset);
        Diag.Code("gpu_getdata:" + r.Code.ToString("X8", CultureInfo.InvariantCulture));
        throw new BenchFailedException(BenchIds.Error);
    }

    public void Dispose()
    {
        try { _ctx.ClearState(); _ctx.Flush(); } catch (Exception) { }
        foreach (var s in _slots) s.Dispose();
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}

/// <summary>
/// Embedded shader bytecode (cs_5_0). Built from Gpu/Shaders/*.hlsl with %SystemRoot%\System32\d3dcompiler_47.dll
/// (flags OPTIMIZATION_LEVEL3 | IEEE_STRICTNESS; no fxc/dxc is needed). The test ShaderBytecodeTests recompiles the
/// sources and fails if the embedded .cso differ, writing fresh ones to its output folder for copying back.
/// </summary>
internal static class Shaders
{
    public static byte[] Load(string name)
    {
        using var s = typeof(Shaders).Assembly.GetManifestResourceStream("OptimizationTool.Bench.Shaders." + name + ".cso")
            ?? throw new InvalidOperationException("shader_missing:" + name);
        var b = new byte[s.Length];
        s.ReadExactly(b);
        return b;
    }
}

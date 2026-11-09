using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>What one source read: slot id → value (null = no valid value this time), plus optional extras.</summary>
internal sealed record SourceReading(IReadOnlyDictionary<string, double?> Values, GpuThrottle? Gpu = null, double? CpuLimitPercent = null);

/// <summary>One sensor source (PDH query, D3DKMT adapter, NVML device, …). Read is called on the runner's own thread.</summary>
internal interface ISensorSource : IDisposable
{
    string Name { get; }
    /// <summary>The slot ids this source fills (they read null while the source is disabled).</summary>
    IReadOnlyList<string> SlotIds { get; }
    SourceReading Read();
}

/// <summary>
/// Runs one source on a dedicated thread. A read that throws, or that has not finished when the hub's deadline
/// passes (or is still hung at the next read), is a strike; the second strike disables the source for the rest of the
/// hub's life. The source is disposed on its own thread, after any hung call returns.
/// </summary>
internal sealed class SourceRunner : IDisposable
{
    public const int MaxStrikes = 2;

    private readonly ISensorSource _source;
    private readonly SemaphoreSlim _go = new(0);
    private readonly Thread _thread;
    private TaskCompletionSource<SourceReading>? _pending;
    private int _strikes;
    private volatile bool _stop;

    public SourceRunner(ISensorSource source)
    {
        _source = source;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Optimus sensor " + source.Name };
        _thread.Start();
    }

    public string Name => _source.Name;
    public IReadOnlyList<string> SlotIds => _source.SlotIds;
    public bool Disabled { get; private set; }
    public int Strikes => _strikes;

    /// <summary>Starts a read, or returns null when disabled or when the previous read is still running (a strike).</summary>
    public Task<SourceReading>? Begin()
    {
        if (Disabled || _stop) return null;
        if (_pending is { Task.IsCompleted: false })
        {
            Strike();
            return null;
        }
        var tcs = new TaskCompletionSource<SourceReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pending, tcs);
        _go.Release();
        return tcs.Task;
    }

    public void Strike()
    {
        if (++_strikes >= MaxStrikes) Disabled = true;
    }

    private void Loop()
    {
        try
        {
            while (true)
            {
                _go.Wait();
                if (_stop) break;
                var tcs = Volatile.Read(ref _pending)!;
                try { tcs.TrySetResult(_source.Read()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }
        }
        finally
        {
            try { _source.Dispose(); } catch (Exception) { /* best effort */ }
        }
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _go.Release();
    }
}

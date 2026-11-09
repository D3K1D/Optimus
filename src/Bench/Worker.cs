namespace OptimizationTool.Bench;

/// <summary>
/// Command loop state: at most one part runs at a time on its own thread. "stop" cancels it; the part thread replies
/// "stopped" once the part has released everything (parts poll the token at least every ~50 ms, so well inside 1 s).
/// </summary>
internal sealed class Worker : IDisposable
{
    private readonly EventWriter _events;
    private readonly BenchScale _scale;
    private readonly Func<RunCommand, IBenchPart?> _factory;
    private readonly Lock _lock = new();
    private Thread? _runner;
    private CancellationTokenSource? _cts;
    private string? _lastPart;
    private Thread? _heartbeat;
    private readonly CancellationTokenSource _hbStop = new();

    public Worker(EventWriter events, BenchScale scale, Func<RunCommand, IBenchPart?>? factory = null)
    {
        _events = events;
        _scale = scale;
        _factory = factory ?? DefaultFactory;
    }

    internal static IBenchPart? DefaultFactory(RunCommand cmd) => cmd.Part switch
    {
        BenchIds.Cpu => new Cpu.CpuPart(),
        BenchIds.Ram => new Ram.RamPart(),
        BenchIds.Disk => new Disk.DiskPart(),
        BenchIds.Gpu => new Gpu.GpuPart(),
        _ => null,
    };

    public bool IsRunning { get { lock (_lock) return _runner is not null; } }

    /// <summary>Heartbeat every 250 ms on its own above-normal thread, so a fully loaded CPU can't starve it.</summary>
    public void StartHeartbeat(TimeSpan? interval = null)
    {
        var every = interval ?? TimeSpan.FromMilliseconds(250);
        _heartbeat = new Thread(() =>
        {
            while (!_hbStop.Token.WaitHandle.WaitOne(every)) _events.Heartbeat();
        }) { IsBackground = true, Name = "bench-hb", Priority = ThreadPriority.AboveNormal };
        _heartbeat.Start();
    }

    /// <summary>Handles one stdin line. Returns false when the worker should exit.</summary>
    public bool Handle(string line)
    {
        switch (Wire.Parse(line))
        {
            case ExitCommand: return false;
            case StopCommand: Stop(); return true;
            case RunCommand run: Start(run); return true;
            default: Diag.Code("bad_command"); return true;
        }
    }

    private void Stop()
    {
        lock (_lock)
        {
            if (_runner is not null) { _cts!.Cancel(); return; } // the part thread replies "stopped"
        }
        _events.Stopped(_lastPart);
    }

    private void Start(RunCommand run)
    {
        lock (_lock)
        {
            if (_runner is not null) { Diag.Code("busy"); return; }
            _lastPart = run.Part;
            if (!BenchIds.Subtests.TryGetValue(run.Part, out var subtests)) { _events.Failed(run.Part, BenchIds.Error, null); return; }
            if (run.BenchVersion != BenchIds.BenchVersion) { _events.Failed(run.Part, BenchIds.Unsupported, null); return; }
            if (run.Repeat is not null && !subtests.Contains(run.Repeat)) { _events.Failed(run.Part, BenchIds.Error, run.Repeat); return; }
            var part = _factory(run);
            if (part is null) { _events.Failed(run.Part, BenchIds.Error, null); return; }
            var cts = new CancellationTokenSource();
            _cts = cts;
            _runner = new Thread(() => RunPart(part, run, cts)) { IsBackground = true, Name = "bench-part" };
            _runner.Start();
        }
    }

    private void RunPart(IBenchPart part, RunCommand run, CancellationTokenSource cts)
    {
        try
        {
            var ctx = new PartContext(run, cts.Token, _scale, _events);
            PartResult? result = null;
            string? failReason = null, failSubtest = null;
            try
            {
                result = part.Run(ctx);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (BenchFailedException f) { failReason = f.Reason; failSubtest = f.Subtest; }
            catch (Exception ex)
            {
                Diag.Code("part_error:" + ex.GetType().Name);
                failReason = BenchIds.Error;
            }
            // A stop request wins: the host asked to stop and waits for "stopped".
            if (cts.IsCancellationRequested) _events.Stopped(run.Part);
            else if (failReason is not null) _events.Failed(run.Part, failReason, failSubtest);
            else _events.Result(run.Part, result!);
        }
        finally
        {
            lock (_lock)
            {
                _runner = null;
                _cts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>Stops the running part (if any) and waits up to <paramref name="wait"/> for it to release resources.</summary>
    public void Shutdown(TimeSpan wait)
    {
        Thread? t;
        lock (_lock)
        {
            t = _runner;
            _cts?.Cancel();
        }
        t?.Join(wait);
    }

    public void Dispose()
    {
        _hbStop.Cancel();
        _heartbeat?.Join(TimeSpan.FromSeconds(1));
        _hbStop.Dispose();
    }
}

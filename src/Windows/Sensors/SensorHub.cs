using System.Diagnostics;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>The 4 slots of a part plus the sources that fill the sensor-backed ones.</summary>
internal sealed record PartPlan(IReadOnlyList<MetricSlot> Slots, IReadOnlyList<ISensorSource> Sources);

/// <summary>Probes this PC's sources for a part (the Windows-facing half of the hub).</summary>
internal interface ISensorBackend : IDisposable
{
    PartPlan Prepare(string part, BenchHardware hardware, CancellationToken ct = default);
}

/// <summary>Current AC/battery state plus change notifications.</summary>
internal interface IPowerReader : IDisposable
{
    PowerState Current();
    event Action<PowerState>? Changed;
}

/// <summary>
/// <see cref="ISensorHub"/> over a backend. Each prepared source runs on its own thread; <see cref="Read"/> starts all
/// of them, waits until a shared deadline (default 180 ms) and merges what came back. Sources that throw or miss the
/// deadline twice are disabled for the rest of the hub's life (their slots read null).
/// </summary>
internal sealed class SensorHub : ISensorHub
{
    public static readonly TimeSpan DefaultReadDeadline = TimeSpan.FromMilliseconds(180);

    private readonly ISensorBackend _backend;
    private readonly IPowerReader _power;
    private readonly TimeSpan _deadline;
    private readonly Lock _gate = new();
    private List<SourceRunner> _runners = [];
    private IReadOnlyList<MetricSlot> _slots = [];
    private bool _disposed;

    public SensorHub(ISensorBackend backend, IPowerReader power, TimeSpan? readDeadline = null)
    {
        _backend = backend;
        _power = power;
        _deadline = readDeadline ?? DefaultReadDeadline;
        _power.Changed += OnPowerChanged;
    }

    public event Action<PowerState>? PowerChanged;

    private void OnPowerChanged(PowerState s) => PowerChanged?.Invoke(s);

    /// <summary>Names of sources disabled so far (for diagnostics and tests).</summary>
    public IReadOnlyCollection<string> DisabledSources
    {
        get { lock (_gate) return _disabled.ToArray(); }
    }

    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    /// <summary>Slot ids of the prepared part whose source was disabled while it ran (reported on every sample).</summary>
    private readonly HashSet<string> _failedSlots = new(StringComparer.Ordinal);

    public IReadOnlyList<MetricSlot> Prepare(string part, BenchHardware hardware, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopRunners();
            _failedSlots.Clear();
            _slots = [];
            var plan = _backend.Prepare(part, hardware, ct);
            var slots = plan.Slots;
            foreach (var s in plan.Sources)
            {
                // A source disabled earlier in this hub's life stays disabled (same name = same physical source), and
                // its slots are reported unavailable rather than as readable slots that never get a value.
                if (_disabled.Contains(s.Name))
                {
                    var ids = s.SlotIds;
                    slots = slots.Select(x => x.Available && ids.Contains(x.Id) ? x with { Available = false, Reason = SlotReasons.SourceFailed } : x).ToList();
                    s.Dispose();
                    continue;
                }
                var r = new SourceRunner(s);
                _runners.Add(r);
            }
            _slots = slots;
            return _slots;
        }
    }

    public SensorSample Read()
    {
        lock (_gate)
        {
            var values = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var s in _slots)
                if (s.Available && s.Source != SlotSources.Worker) values[s.Id] = null;
            if (_disposed || _runners.Count == 0) return new SensorSample(values, null, null);

            var clock = Stopwatch.StartNew();
            var started = new List<(SourceRunner Runner, Task<SourceReading> Task)>(_runners.Count);
            foreach (var r in _runners)
                if (r.Begin() is { } t) started.Add((r, t));

            var remaining = _deadline - clock.Elapsed;
            if (started.Count > 0 && remaining > TimeSpan.Zero)
            {
                try { Task.WaitAll(started.Select(x => (Task)x.Task).ToArray(), remaining); }
                catch (AggregateException) { /* faulted reads are handled per source below */ }
            }

            GpuThrottle? gpu = null;
            double? limit = null;
            foreach (var (runner, task) in started)
            {
                if (task.IsCompletedSuccessfully)
                {
                    var reading = task.Result;
                    foreach (var (k, v) in reading.Values)
                        if (values.ContainsKey(k)) values[k] = v is { } d && double.IsFinite(d) ? d : null;
                    gpu ??= reading.Gpu;
                    limit ??= reading.CpuLimitPercent;
                }
                else
                {
                    if (task.IsFaulted) _ = task.Exception; // observed
                    runner.Strike();
                }
            }
            foreach (var r in _runners)
            {
                if (!r.Disabled) continue;
                _disabled.Add(r.Name);
                foreach (var id in r.SlotIds)
                    if (values.ContainsKey(id)) _failedSlots.Add(id);
            }

            return new SensorSample(values, gpu, limit, _failedSlots.Count > 0 ? _failedSlots.ToArray() : null);
        }
    }

    public PowerState Power()
    {
        try { return _power.Current(); }
        catch (Exception) { return new PowerState(PowerStates.Unknown, PowerStates.Unknown); }
    }

    private void StopRunners()
    {
        foreach (var r in _runners)
        {
            if (r.Disabled) _disabled.Add(r.Name);
            r.Dispose();
        }
        _runners = [];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopRunners();
            _slots = [];
        }
        _power.Changed -= OnPowerChanged;
        _power.Dispose();
        _backend.Dispose();
    }
}

/// <summary>Source labels (MetricSlot.Source) and power words.</summary>
internal static class SlotSources
{
    public const string Pdh = "pdh", Emi = "emi", D3dkmt = "d3dkmt", Nvml = "nvml", Wmi = "wmi", Worker = "worker", None = "none";
}

internal static class SlotReasons
{
    public const string NeedsAdvancedSensors = "needs_advanced_sensors", NotSupported = "not_supported", NeedsAdmin = "needs_admin", TimedOut = "timed_out",
        SourceFailed = MetricSlot.SourceFailed;
}

internal static class PowerStates
{
    public const string Online = "online", Offline = "offline", Unknown = "unknown", Present = "present", Absent = "absent";
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace OptimizationTool.Engine.Bench;

// In-process fakes for the fixture source (--source fixture) and the tests: synthetic hardware, sensors and a worker
// that speaks the seam's JSON lines without running any workload. Nothing here loads the PC.

/// <summary>Fixed synthetic hardware (no serials, user or PC names).</summary>
public sealed class FakeBenchHardware : IBenchHardware
{
    public BenchHardware Hardware { get; set; } = Default;

    public static BenchHardware Default { get; } = new(
        "Fixture 8-Core Processor", 16, 3800,
        "Fixture Graphics 4060", "32.0.15.6094", "0000000000001234",
        16L << 30, 2, 3200, 3600,
        "C:", "Fixture NVMe SSD 1TB", 1_000_000_000_000, "nvme", 200L << 30,
        8L << 30, "26200");

    /// <summary>How many times DescribeAsync ran (the capabilities cache keeps this low).</summary>
    public int Describes => Volatile.Read(ref _describes);
    private int _describes;

    /// <summary>Simulates the real reader's cost.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<BenchHardware> DescribeAsync(string benchFolder, CancellationToken ct)
    {
        Interlocked.Increment(ref _describes);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct).ConfigureAwait(false);
        return Hardware;
    }
}

/// <summary>A scriptable sensor hub: per-part slots, a sample function, power state and change notifications.</summary>
public sealed class FakeSensorHub : ISensorHub
{
    private string _part = BenchParts.Cpu;
    private int _index;

    public PowerState PowerState { get; set; } = new("online", "absent");

    /// <summary>Sample for (part, index since Prepare). Default: steady plausible values, GPU well below slowdown.</summary>
    public Func<string, int, SensorSample> Sample { get; set; } = DefaultSample;

    /// <summary>Simulates a hung source: Read blocks this long.</summary>
    public TimeSpan ReadDelay { get; set; }

    public int Reads => Volatile.Read(ref _reads);
    private int _reads;
    public bool Disposed { get; private set; }

    public event Action<PowerState>? PowerChanged;

    /// <summary>Changes the power state and raises <see cref="PowerChanged"/> (as RegisterPowerSettingNotification would).</summary>
    public void SetPower(PowerState state)
    {
        PowerState = state;
        PowerChanged?.Invoke(state);
    }

    /// <summary>How many times Prepare ran on this hub (a confirmation repeat prepares its part again).</summary>
    public int Prepares { get; private set; }

    /// <summary>Simulates slow probes: Prepare waits this long, or until its token is cancelled.</summary>
    public TimeSpan PrepareDelay { get; set; }

    public IReadOnlyList<MetricSlot> Prepare(string part, BenchHardware hardware, CancellationToken ct = default)
    {
        Prepares++;
        if (PrepareDelay > TimeSpan.Zero) ct.WaitHandle.WaitOne(PrepareDelay);
        ct.ThrowIfCancellationRequested();
        _part = part;
        _index = 0;
        return Slots(part);
    }

    public static IReadOnlyList<MetricSlot> Slots(string part) => part switch
    {
        BenchParts.Cpu =>
        [
            new("clock", "GHz", "pdh", true, Max: 6), new("temp", "°C", "none", false, Reason: "needs_advanced_sensors"),
            new("power", "W", "emi", false, Reason: "not_supported"), new("fan", "RPM", "none", false, Reason: "needs_advanced_sensors"),
        ],
        BenchParts.Gpu =>
        [
            new("clock", "GHz", "nvml", true, Max: 3), new("temp", "°C", "d3dkmt", true, Max: 100, Limit: 90),
            new("power", "%", "d3dkmt", true, Max: 120), new("fan", "RPM", "d3dkmt", true, Max: 4000),
        ],
        BenchParts.Ram =>
        [
            new("read", "GB/s", "worker", true, Max: 100), new("write", "GB/s", "worker", true, Max: 100),
            new("copy", "GB/s", "worker", true, Max: 100), new("latency", "ns", "worker", true, Max: 200),
        ],
        _ =>
        [
            new("read", "MB/s", "worker", true, Max: 8000), new("write", "MB/s", "worker", true, Max: 8000),
            new("random4k", "MB/s", "worker", true, Max: 200), new("temp", "°C", "wmi", false, Reason: "needs_admin"),
        ],
    };

    public static SensorSample DefaultSample(string part, int i) => part switch
    {
        BenchParts.Cpu => new(new Dictionary<string, double?> { ["clock"] = 4.2 + (i % 3) * 0.05 }, null, 100),
        BenchParts.Gpu => new(new Dictionary<string, double?> { ["clock"] = 2.4, ["temp"] = 62 + Math.Min(i, 8), ["power"] = 85, ["fan"] = 1600 },
            new GpuThrottle(90, false, false), null),
        _ => new(new Dictionary<string, double?>(), null, null),
    };

    public SensorSample Read()
    {
        Interlocked.Increment(ref _reads);
        if (ReadDelay > TimeSpan.Zero) Thread.Sleep(ReadDelay);
        return Sample(_part, _index++);
    }

    public PowerState Power() => PowerState;

    public void Dispose() => Disposed = true;
}

/// <summary>Hands out <see cref="FakeSensorHub"/>s; <see cref="Configure"/> sets each one up as it is created.</summary>
public sealed class FakeSensorHubFactory : ISensorHubFactory
{
    private readonly List<FakeSensorHub> _created = [];
    public Action<FakeSensorHub>? Configure { get; set; }

    public IReadOnlyList<FakeSensorHub> Created
    {
        get { lock (_created) return [.. _created]; }
    }

    public ISensorHub Create()
    {
        var hub = new FakeSensorHub();
        Configure?.Invoke(hub);
        lock (_created) _created.Add(hub);
        return hub;
    }
}

/// <summary>What the fake worker does per part (tests script failures, crashes, silence and hangs).</summary>
public sealed class FakeWorkerScript
{
    public TimeSpan PartDuration { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Subtest values per part. Default: the reference values (each part scores 1000).</summary>
    public Func<string, IReadOnlyDictionary<string, double>> Results { get; set; } = part => BenchScoring.References[part];

    /// <summary>Value a repeat of (part, subtest) reports. Default: the part's normal result for that subtest.</summary>
    public Func<string, string, double>? RepeatValue { get; set; }

    public Dictionary<string, string> Fail { get; } = [];     // part → failed reason
    public HashSet<string> Silent { get; } = [];              // part → heartbeats stop while it runs
    public HashSet<string> Crash { get; } = [];               // part → the worker exits when it starts
    public HashSet<string> Hang { get; } = [];                // part → never finishes (still heartbeats)
    public bool IgnoreStop { get; set; }                      // never answers "stop"

    /// <summary>The raw live measurement a progress message carries for (part, subtest); null by default.</summary>
    public Func<string, string, double?>? LiveScore { get; set; }

    private readonly List<string> _commands = [];
    public IReadOnlyList<string> Commands { get { lock (_commands) return [.. _commands]; } }
    internal void Record(string line) { lock (_commands) _commands.Add(line); }

    public int Launches => Volatile.Read(ref _launches);
    private int _launches;
    internal void Launched() => Interlocked.Increment(ref _launches);
    public int Kills => Volatile.Read(ref _kills);
    private int _kills;
    internal void Killed() => Interlocked.Increment(ref _kills);
}

/// <summary>Launches in-process <see cref="FakeBenchWorker"/>s. Fails to launch when <see cref="FailLaunch"/> is set.</summary>
public sealed class FakeBenchWorkerLauncher(FakeWorkerScript? script = null) : IBenchWorkerLauncher
{
    public FakeWorkerScript Script { get; } = script ?? new FakeWorkerScript();
    public bool FailLaunch { get; set; }

    public IBenchWorker Launch()
    {
        if (FailLaunch) throw new IOException("The fake worker could not start.");
        Script.Launched();
        return new FakeBenchWorker(Script);
    }
}

/// <summary>An in-process worker speaking the seam's JSON lines. No workload runs.</summary>
public sealed class FakeBenchWorker : IBenchWorker
{
    private readonly FakeWorkerScript _script;
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _part;
    private volatile bool _silent;
    private int _dead;

    public FakeBenchWorker(FakeWorkerScript script)
    {
        _script = script;
        Emit(new JsonObject { ["ev"] = "hello", ["version"] = "1", ["pid"] = 4242 });
        _ = Task.Run(HeartbeatAsync);
    }

    private void Emit(JsonObject message)
    {
        if (Volatile.Read(ref _dead) == 0) _out.Writer.TryWrite(message.ToJsonString());
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                await Task.Delay(_script.HeartbeatInterval, _life.Token).ConfigureAwait(false);
                if (!_silent) Emit(new JsonObject { ["ev"] = "hb" });
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task SendAsync(string line, CancellationToken ct)
    {
        if (Volatile.Read(ref _dead) != 0) throw new IOException("The worker has exited.");
        _script.Record(line);
        var cmd = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        switch (cmd["cmd"]?.GetValue<string>())
        {
            case "run":
                var part = cmd["part"]!.GetValue<string>();
                var repeat = cmd["repeat"]?.GetValue<string>();
                if (_script.Crash.Contains(part)) { Kill(countKill: false); break; }
                _part = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                var token = _part.Token;
                _ = Task.Run(() => RunPartAsync(part, repeat, token));
                break;
            case "stop":
                if (_script.IgnoreStop) break;
                _part?.Cancel();
                break;
            case "exit":
                Exit();
                break;
        }
        return Task.CompletedTask;
    }

    private async Task RunPartAsync(string part, string? repeat, CancellationToken ct)
    {
        _silent = _script.Silent.Contains(part);
        try
        {
            var subtests = BenchSubtests.ByPart[part];
            var steps = 3;
            for (var i = 0; i < steps; i++)
            {
                var current = repeat ?? subtests[Math.Min(i, subtests.Count - 1)];
                Emit(new JsonObject
                {
                    ["ev"] = "progress", ["part"] = part, ["subtest"] = current,
                    ["progress"] = (double)i / steps, ["liveScore"] = _script.LiveScore?.Invoke(part, current), ["etaSec"] = 1,
                });
                if (part is BenchParts.Ram or BenchParts.Disk)
                {
                    var r = _script.Results(part);
                    var live = new JsonObject();
                    foreach (var (k, v) in r) live[part == BenchParts.Disk ? DiskLive(k) : k] = v;
                    Emit(new JsonObject { ["ev"] = "live", ["part"] = part, ["values"] = live });
                }
                await Task.Delay(_script.PartDuration / steps, ct).ConfigureAwait(false);
            }
            while (_script.Hang.Contains(part)) await Task.Delay(50, ct).ConfigureAwait(false);
            if (_script.Fail.TryGetValue(part, out var reason))
            {
                Emit(new JsonObject { ["ev"] = "failed", ["part"] = part, ["reason"] = reason, ["subtest"] = null });
                return;
            }
            var values = _script.Results(part);
            var result = new JsonObject();
            if (repeat is not null)
            {
                var v = _script.RepeatValue?.Invoke(part, repeat) ?? values[repeat];
                result[repeat] = new JsonObject { ["value"] = v, ["unit"] = Unit(part, repeat) };
            }
            else
                foreach (var id in subtests) result[id] = new JsonObject { ["value"] = values[id], ["unit"] = Unit(part, id) };
            Emit(new JsonObject { ["ev"] = "result", ["part"] = part, ["subtests"] = result, ["info"] = new JsonObject { ["volume"] = "C:" } });
        }
        catch (OperationCanceledException)
        {
            Emit(new JsonObject { ["ev"] = "stopped", ["part"] = part });
        }
        finally
        {
            _silent = false;
        }
    }

    private static string DiskLive(string subtest) => subtest switch { "seqWrite" => "write", "seqRead" => "read", _ => "random4k" };

    public static string Unit(string part, string subtest) => (part, subtest) switch
    {
        (BenchParts.Cpu, _) => "ops/s",
        (BenchParts.Gpu, "fma") => "GFLOPS",
        (BenchParts.Gpu, _) => "GB/s",
        (BenchParts.Ram, "latency") => "ns",
        (BenchParts.Ram, _) => "GB/s",
        _ => "MB/s",
    };

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try { return await _out.Reader.ReadAsync(ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { return null; }
    }

    private void Exit()
    {
        if (Interlocked.Exchange(ref _dead, 1) != 0) return;
        _life.Cancel();
        _out.Writer.TryComplete();
    }

    public void Kill() => Kill(countKill: true);

    private void Kill(bool countKill)
    {
        if (Volatile.Read(ref _dead) != 0) return;
        if (countKill) _script.Killed();
        Exit();
    }

    public void Dispose() => Exit();
}

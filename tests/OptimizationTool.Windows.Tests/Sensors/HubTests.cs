using OptimizationTool.Engine.Bench;
using OptimizationTool.Windows.Sensors;

namespace OptimizationTool.Windows.Tests.Sensors;

/// <summary>SensorHub with fake sources: merging, per-source timeouts and disabling.</summary>
public class HubTests
{
    private sealed class FakeSource(string name, string slot, Func<int, double?> value, int sleepMs = 0, bool throws = false) : ISensorSource
    {
        private int _calls;
        public volatile bool Disposed;
        public int Calls => _calls;
        public string Name => name;
        public IReadOnlyList<string> SlotIds => [slot];

        public SourceReading Read()
        {
            var n = Interlocked.Increment(ref _calls);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
            if (throws) throw new InvalidOperationException("fake failure");
            return new SourceReading(new Dictionary<string, double?> { [slot] = value(n) }, null, slot == "clock" ? 42 : null);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackend(Func<string, PartPlan> plan) : ISensorBackend
    {
        public PartPlan Prepare(string part, BenchHardware hardware, CancellationToken ct = default) => plan(part);
        public void Dispose() { }
    }

    private sealed class FakePower : IPowerReader
    {
        public PowerState State = new("online", "absent");
        public event Action<PowerState>? Changed;
        public PowerState Current() => State;
        public void Fire(PowerState s) { State = s; Changed?.Invoke(s); }
        public void Dispose() { }
    }

    private static readonly BenchHardware Hw = new("cpu", 8, 3000, null, null, null, 1L << 34, 2, null, null, "C:", null, null, "unknown", 1L << 36, 1L << 33, "10.0.1");

    private static IReadOnlyList<MetricSlot> CpuSlots() => SlotPlanner.Cpu(true, 3000, null, true, null);

    [Fact]
    public void Values_are_merged_from_sources()
    {
        var clock = new FakeSource("cpu.pdh", "clock", n => 3.0 + n);
        var power = new FakeSource("cpu.emi", "power", _ => 55.5);
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan(CpuSlots(), [clock, power])), new FakePower());
        var slots = hub.Prepare(BenchParts.Cpu, Hw);
        Assert.Equal(4, slots.Count);
        var s = hub.Read();
        Assert.Equal(4.0, s.Values["clock"]);
        Assert.Equal(55.5, s.Values["power"]);
        Assert.False(s.Values.ContainsKey("temp")); // unavailable slots are not reported
        Assert.Equal(42, s.CpuPerfLimitPercent);
        Assert.Null(s.Gpu);
    }

    [Fact]
    public void Throwing_source_is_disabled_after_two_failures()
    {
        var bad = new FakeSource("cpu.emi", "power", _ => 1, throws: true);
        var good = new FakeSource("cpu.pdh", "clock", _ => 2.5);
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan(CpuSlots(), [good, bad])), new FakePower());
        hub.Prepare(BenchParts.Cpu, Hw);
        Assert.Null(hub.Read().Values["power"]);
        Assert.Empty(hub.DisabledSources);
        Assert.Null(hub.Read().Values["power"]);
        Assert.Equal(["cpu.emi"], hub.DisabledSources);
        var third = hub.Read();
        Assert.Null(third.Values["power"]);
        Assert.Equal(2.5, third.Values["clock"]);
        Assert.Equal(2, bad.Calls); // never called again
    }

    [Fact]
    public void Hung_source_is_disabled_and_does_not_block_reads()
    {
        // Generous margins: the full solution runs test projects in parallel, so threads can start late.
        var slow = new FakeSource("gpu.nvml", "power", _ => 100, sleepMs: 4000);
        var good = new FakeSource("cpu.pdh", "clock", _ => 2.5);
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan(CpuSlots(), [good, slow])), new FakePower(), TimeSpan.FromMilliseconds(400));
        hub.Prepare(BenchParts.Cpu, Hw);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = hub.Read();
        var second = hub.Read(); // still hung → second strike
        Assert.True(watch.ElapsedMilliseconds < 3000, $"reads took {watch.ElapsedMilliseconds} ms");
        Assert.Null(first.Values["power"]);
        Assert.Equal(2.5, second.Values["clock"]);
        Assert.Contains("gpu.nvml", hub.DisabledSources);
        Assert.Equal(1, slow.Calls);
    }

    [Fact]
    public void Disabled_source_stays_disabled_across_parts()
    {
        var bad = new FakeSource("cpu.emi", "power", _ => 1, throws: true);
        var again = new FakeSource("cpu.emi", "power", _ => 1);
        var calls = 0;
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan(CpuSlots(), [calls++ == 0 ? bad : again])), new FakePower());
        hub.Prepare(BenchParts.Cpu, Hw);
        hub.Read();
        hub.Read();
        var slots = hub.Prepare(BenchParts.Cpu, Hw);
        Assert.True(again.Disposed);
        // The slot is reported unavailable (source_failed), not as a readable slot that never gets a value.
        var power = slots.Single(s => s.Id == "power");
        Assert.False(power.Available);
        Assert.Equal(MetricSlot.SourceFailed, power.Reason);
        Assert.False(hub.Read().Values.ContainsKey("power"));
        Assert.Equal(0, again.Calls);
    }

    [Fact]
    public void Source_disabled_mid_run_is_reported_on_the_samples()
    {
        var bad = new FakeSource("cpu.emi", "power", _ => 1, throws: true);
        var good = new FakeSource("cpu.pdh", "clock", _ => 2.5);
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan(CpuSlots(), [good, bad])), new FakePower());
        hub.Prepare(BenchParts.Cpu, Hw);
        Assert.Null(hub.Read().Failed);        // first strike: not disabled yet
        Assert.Equal(["power"], hub.Read().Failed); // second strike: disabled, and said so
        Assert.Equal(["power"], hub.Read().Failed); // and on every later sample
    }

    private sealed class Counted : IDisposable
    {
        public int Disposes;
        public void Dispose() => Interlocked.Increment(ref Disposes);
    }

    [Fact]
    public void Bounded_result_has_exactly_one_owner_when_the_timeout_races_the_work()
    {
        // The work finishes right around the timeout, many times over: the result is either returned (the caller
        // owns it, never disposed behind its back) or abandoned (disposed once by the worker). Never both, never twice.
        for (var i = 0; i < 300; i++)
        {
            var made = new Counted();
            var spins = i % 30;
            var got = Bounded.Run(() => { Thread.SpinWait(spins * 2000); return made; }, TimeSpan.FromMilliseconds(i % 3), CancellationToken.None, out var timedOut);
            if (timedOut)
            {
                Assert.Null(got);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref made.Disposes) >= 1, 5000), "an abandoned result is disposed by the worker");
            }
            else
            {
                Assert.Same(made, got);
                Assert.Equal(0, Volatile.Read(ref made.Disposes));
                got!.Dispose();
            }
            Assert.Equal(1, Volatile.Read(ref made.Disposes));
        }
    }

    [Fact]
    public void Bounded_cancel_abandons_the_work_and_disposes_its_late_result_once()
    {
        var made = new Counted();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var started = new ManualResetEventSlim();
        var call = Task.Run(() => Bounded.Run(() => { started.Set(); release.Wait(); return made; }, TimeSpan.FromMinutes(1), cts.Token, out _));
        started.Wait();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => call.GetAwaiter().GetResult());
        Assert.Equal(0, Volatile.Read(ref made.Disposes));
        release.Set(); // the abandoned work finishes late
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref made.Disposes) == 1, 5000));
    }

    [Fact]
    public void Bounded_late_failure_of_abandoned_work_is_dropped()
    {
        using var release = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        var got = Bounded.Run<Counted>(() =>
        {
            try { release.Wait(); throw new InvalidOperationException("late"); }
            finally { done.Set(); }
        }, TimeSpan.FromMilliseconds(20), CancellationToken.None, out var timedOut);
        Assert.True(timedOut);
        Assert.Null(got);
        release.Set();
        Assert.True(done.Wait(5000)); // the worker thread ends without an unhandled or unobserved failure
    }

    [Fact]
    public void Free_space_is_read_for_the_folder_itself_even_before_it_exists()
    {
        // A volume mounted in a folder has no drive letter: free space comes from the path, not from "X:\".
        var missing = Path.Combine(Path.GetTempPath(), "ot-tests", Guid.NewGuid().ToString("N"), "bench");
        Assert.False(Directory.Exists(missing));
        Assert.True(Directory.Exists(BenchHardwareReader.ExistingAncestor(missing)));
        Assert.True(BenchHardwareReader.FreeBytesOf(missing) > 0);
        Assert.Null(VolumeDisk.Letter(@"C:\mount\data\"));
    }

    [Fact]
    public void Worker_slots_are_not_read_by_the_hub()
    {
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan(SlotPlanner.Ram(), [])), new FakePower());
        Assert.Equal(4, hub.Prepare(BenchParts.Ram, Hw).Count);
        Assert.Empty(hub.Read().Values);
    }

    [Fact]
    public void Dispose_disposes_sources_on_their_thread()
    {
        var src = new FakeSource("cpu.pdh", "clock", _ => 1);
        var hub = new SensorHub(new FakeBackend(_ => new PartPlan(CpuSlots(), [src])), new FakePower());
        hub.Prepare(BenchParts.Cpu, Hw);
        hub.Read();
        hub.Dispose();
        SpinWait.SpinUntil(() => src.Disposed, 2000);
        Assert.True(src.Disposed);
        Assert.Empty(hub.Read().Values);
    }

    [Fact]
    public void Power_changes_are_forwarded()
    {
        var power = new FakePower();
        using var hub = new SensorHub(new FakeBackend(_ => new PartPlan([], [])), power);
        PowerState? seen = null;
        hub.PowerChanged += s => seen = s;
        power.Fire(new PowerState("offline", "present"));
        Assert.Equal(new PowerState("offline", "present"), seen);
        Assert.True(hub.Power().BlocksBenchmark);
    }
}

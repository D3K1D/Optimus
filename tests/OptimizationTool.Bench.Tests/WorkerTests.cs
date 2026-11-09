using System.Diagnostics;

namespace OptimizationTool.Bench.Tests;

public class WorkerTests
{
    /// <summary>A kernel that never finishes on its own; polls the token like the real parts do.</summary>
    private sealed class EndlessPart : IBenchPart
    {
        public volatile bool Released;
        public PartResult Run(PartContext ctx)
        {
            try
            {
                while (true)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    ctx.Progress("single", 0.1, null, 30);
                    Thread.Sleep(20);
                }
            }
            finally { Released = true; }
        }
    }

    private sealed class FixedPart(Func<PartContext, PartResult> run) : IBenchPart
    {
        public PartResult Run(PartContext ctx) => run(ctx);
    }

    private const string RunCpu = """{"cmd":"run","part":"cpu","benchVersion":"1"}""";

    [Fact]
    public void Stop_replies_stopped_within_one_second_after_release()
    {
        var sink = new EventSink();
        var part = new EndlessPart();
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => part);
        Assert.True(w.Handle(RunCpu));
        Assert.NotNull(sink.WaitFor("progress", TimeSpan.FromSeconds(2)));
        var sent = DateTime.UtcNow;
        Assert.True(w.Handle("""{"cmd":"stop"}"""));
        var stopped = sink.WaitFor("stopped", TimeSpan.FromSeconds(1));
        Assert.NotNull(stopped);
        Assert.True(stopped.Value.At - sent < TimeSpan.FromSeconds(1));
        Assert.Equal("cpu", stopped.Value.Ev.GetProperty("part").GetString());
        Assert.True(part.Released);
        SpinWait.SpinUntil(() => !w.IsRunning, 1000);
        Assert.False(w.IsRunning);
    }

    [Fact]
    public void Real_cpu_part_stops_within_one_second()
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full);
        Assert.True(w.Handle(RunCpu));
        Thread.Sleep(600);
        var sw = Stopwatch.StartNew();
        w.Handle("""{"cmd":"stop"}""");
        Assert.NotNull(sink.WaitFor("stopped", TimeSpan.FromSeconds(1)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), sw.Elapsed.ToString());
    }

    [Fact]
    public void Exit_returns_false_and_unknown_lines_are_ignored()
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => new EndlessPart());
        Assert.True(w.Handle("garbage"));
        Assert.False(w.Handle("""{"cmd":"exit"}"""));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Stop_when_idle_still_replies()
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => new EndlessPart());
        w.Handle("""{"cmd":"stop"}""");
        Assert.Equal("stopped", sink.Events.Single().GetProperty("ev").GetString());
    }

    [Theory]
    [InlineData("""{"cmd":"run","part":"tpu","benchVersion":"1"}""", "error")]
    [InlineData("""{"cmd":"run","part":"cpu","benchVersion":"2"}""", "unsupported")]
    [InlineData("""{"cmd":"run","part":"cpu","benchVersion":"1","repeat":"fma"}""", "error")]
    public void Bad_run_commands_fail_immediately(string line, string reason)
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => new EndlessPart());
        w.Handle(line);
        var ev = sink.Events.Single();
        Assert.Equal("failed", ev.GetProperty("ev").GetString());
        Assert.Equal(reason, ev.GetProperty("reason").GetString());
        Assert.False(w.IsRunning);
    }

    [Fact]
    public void Result_failure_and_crash_map_to_events()
    {
        var sink = new EventSink();
        var parts = new Queue<IBenchPart>([
            new FixedPart(_ => new PartResult([new SubtestValue("single", 1, "ops/s"), new SubtestValue("multi", 2, "ops/s")], [])),
            new FixedPart(_ => throw new BenchFailedException("check_failed", "multi")),
            new FixedPart(_ => throw new InvalidOperationException("boom")),
        ]);
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => parts.Dequeue());
        foreach (var _ in Enumerable.Range(0, 3))
        {
            w.Handle(RunCpu);
            SpinWait.SpinUntil(() => !w.IsRunning, 2000);
        }
        var evs = sink.Events;
        Assert.Equal(["result", "failed", "failed"], evs.Select(e => e.GetProperty("ev").GetString()));
        Assert.Equal("check_failed", evs[1].GetProperty("reason").GetString());
        Assert.Equal("multi", evs[1].GetProperty("subtest").GetString());
        Assert.Equal("error", evs[2].GetProperty("reason").GetString());
    }

    [Fact]
    public void Second_run_while_busy_is_ignored()
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => new EndlessPart());
        w.Handle(RunCpu);
        w.Handle(RunCpu);
        w.Handle("""{"cmd":"stop"}""");
        Assert.NotNull(sink.WaitFor("stopped", TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(sink.Events, e => e.GetProperty("ev").GetString() is "failed" or "result");
    }

    [Fact]
    public void Heartbeat_ticks_while_a_part_runs()
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full, _ => new EndlessPart());
        w.StartHeartbeat(TimeSpan.FromMilliseconds(50));
        w.Handle(RunCpu);
        Thread.Sleep(400);
        w.Handle("""{"cmd":"stop"}""");
        Assert.True(sink.Events.Count(e => e.GetProperty("ev").GetString() == "hb") >= 4);
    }
}

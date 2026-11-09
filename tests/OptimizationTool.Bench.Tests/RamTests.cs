using OptimizationTool.Bench.Ram;

namespace OptimizationTool.Bench.Tests;

public unsafe class RamTests
{
    [Theory]
    [InlineData(2, 64)]
    [InlineData(3, 64)]
    [InlineData(1000, 64)]
    [InlineData(65536, 64)]
    [InlineData(4097, 8)]
    public void Latency_cycle_is_one_single_cycle(int nodes, int stride)
    {
        var mem = new byte[(long)nodes * stride];
        fixed (byte* p = mem)
        {
            LatencyCycle.Build(p, nodes, stride, RamPart.LatencySeed, CancellationToken.None);
            var seen = new bool[nodes];
            long at = 0, steps = 0;
            do
            {
                Assert.False(seen[at]);
                seen[at] = true;
                at = (long)*(ulong*)(p + at * stride);
                Assert.InRange(at, 0, nodes - 1);
                steps++;
            } while (at != 0 && steps <= nodes);
            Assert.Equal(nodes, steps);
            Assert.All(seen, Assert.True);
        }
    }

    [Fact]
    public void Latency_cycle_is_deterministic_and_pointer_form_follows_it()
    {
        const int Nodes = 512, Stride = 64;
        var a = new byte[Nodes * Stride];
        var b = new byte[Nodes * Stride];
        fixed (byte* pa = a, pb = b)
        {
            LatencyCycle.Build(pa, Nodes, Stride, 7, CancellationToken.None);
            LatencyCycle.Build(pb, Nodes, Stride, 7, CancellationToken.None);
            Assert.Equal(a, b);
            var next0 = *(ulong*)pa;
            LatencyCycle.ToPointers(pa, Nodes, Stride);
            Assert.Equal((ulong)(pa + (long)next0 * Stride), *(ulong*)pa);
            // Chasing Nodes steps returns to the start.
            Assert.Equal((nint)pa, (nint)LatencyCycle.Chase(pa, Nodes));
        }
    }

    [Fact]
    public void Budget_under_the_minimum_is_low_memory()
    {
        var part = new RamPart();
        var ex = Assert.Throws<BenchFailedException>(() =>
            part.Run(Ctx.For(new RunCommand("ram", "1", null, 200L << 20, null, null))));
        Assert.Equal("low_memory", ex.Reason);
        Assert.Throws<BenchFailedException>(() => part.Run(Ctx.For(new RunCommand("ram", "1", null, null, null, null))));
    }

    [Fact]
    public void Small_scaled_run_reports_all_subtests_and_live_values()
    {
        var sink = new EventSink();
        var r = new RamPart().Run(Ctx.For(new RunCommand("ram", "1", null, 64L << 20, null, null), sink, scale: 0.05));
        Assert.Equal(["read", "write", "copy", "latency"], r.Subtests.Select(s => s.Id));
        Assert.All(r.Subtests, s => Assert.True(s.Value > 0));
        Assert.Contains(sink.Events, e => e.GetProperty("ev").GetString() == "live" && e.GetProperty("values").TryGetProperty("latency", out _));
    }
}

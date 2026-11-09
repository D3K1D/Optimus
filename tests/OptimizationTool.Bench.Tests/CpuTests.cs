using OptimizationTool.Bench.Cpu;

namespace OptimizationTool.Bench.Tests;

public class CpuTests
{
    [Fact]
    public void Checksum_constants_are_stable()
    {
        var i = CpuInputs.Create();
        Assert.Equal(CpuKernels.ExpectedShaHex, CpuKernels.Hex(i.ShaExpected));
        Assert.Equal(CpuKernels.ExpectedTextFnv, i.TextExpected);
        Assert.Equal(CpuKernels.ExpectedSortChecksum, i.SortExpected);
        Assert.True(i.MatchesPins());
    }

    [Fact]
    public void Inputs_have_the_documented_sizes()
    {
        Assert.Equal(1 << 20, CpuKernels.ShaInput().Length);
        Assert.Equal(128 << 10, CpuKernels.DeflateInput().Length);
        Assert.Equal(1 << 16, CpuKernels.SortInput().Length);
        Assert.Equal(CpuKernels.ShaInput(), CpuKernels.ShaInput());
    }

    [Fact]
    public void Kernels_produce_the_expected_checksums()
    {
        var k = new CpuKernelState(CpuInputs.Create());
        for (var n = 0; n < 3; n++)
        {
            Assert.True(k.RunSha());
            Assert.True(k.RunDeflate());
            Assert.True(k.RunSort());
        }
    }

    [Fact]
    public void Corrupted_inputs_fail_their_check()
    {
        var good = CpuInputs.Create();
        var sha = (byte[])good.Sha.Clone(); sha[12345] ^= 1;
        var text = (byte[])good.Text.Clone(); text[777] ^= 0x20;
        var sort = (int[])good.Sort.Clone(); sort[5] += 1;
        var k = new CpuKernelState(good with { Sha = sha, Text = text, Sort = sort });
        Assert.False(k.RunSha());
        Assert.False(k.RunDeflate());
        Assert.False(k.RunSort());
    }

    [Fact]
    public void Sort_checksum_detects_order()
    {
        Assert.NotEqual(CpuKernels.SortChecksum([1, 2, 3]), CpuKernels.SortChecksum([2, 1, 3]));
        Assert.Equal(1UL * 1 + 2UL * 2 + 3UL * 3, CpuKernels.SortChecksum([1, 2, 3]));
    }

    [Fact]
    public void Phase_returns_a_positive_score_and_emits_progress()
    {
        var sink = new EventSink();
        var ctx = Ctx.For(new RunCommand("cpu", "1", null, null, null, null), sink);
        var score = CpuPart.RunPhase(ctx, CpuInputs.Create(), "single", 1, TimeSpan.FromMilliseconds(700));
        Assert.True(score > 0);
        Assert.Contains(sink.Events, e => e.GetProperty("ev").GetString() == "progress");
    }
}

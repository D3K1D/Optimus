using OptimizationTool.Bench.Gpu;
using Vortice;

namespace OptimizationTool.Bench.Tests;

public class GpuTests
{
    [Theory]
    [InlineData(1.0, 1.0, true)]
    [InlineData(1.0009, 1.0, true)]
    [InlineData(1.0011, 1.0, false)]
    [InlineData(-5.004, -5.0, true)]
    [InlineData(-5.006, -5.0, false)]
    [InlineData(1000.9, 1000.0, true)]
    [InlineData(1001.1, 1000.0, false)]
    [InlineData(0.0, 0.0, true)]
    [InlineData(1e-30, 0.0, false)]
    [InlineData(double.NaN, 1.0, false)]
    [InlineData(double.PositiveInfinity, 1.0, false)]
    public void Fma_tolerance_is_relative_1e3(double gpu, double reference, bool ok) =>
        Assert.Equal(ok, GpuValidator.WithinTolerance(gpu, reference));

    [Fact]
    public void Fma_reference_matches_a_float_simulation_and_depends_on_id()
    {
        // Simulate the shader in float on the CPU: within tolerance of the double reference.
        for (uint id = 0; id < 2048; id += 97)
        {
            var x0 = (id & 1023u) * (1.0f / 1024.0f);
            var x = new[] { x0, x0 + 0.125f, x0 + 0.25f, x0 + 0.375f, x0 + 0.5f, x0 + 0.625f, x0 + 0.75f, x0 + 0.875f };
            for (var i = 0; i < GpuValidator.ValidateIterations * 8; i++)
                for (var c = 0; c < 8; c++) x[c] = MathF.FusedMultiplyAdd(x[c], GpuValidator.A, GpuValidator.B);
            var f = ((x[0] + x[1]) + (x[2] + x[3])) + ((x[4] + x[5]) + (x[6] + x[7]));
            Assert.True(GpuValidator.WithinTolerance(f, GpuValidator.FmaReference(id, GpuValidator.ValidateIterations)));
        }
        Assert.NotEqual(GpuValidator.FmaReference(1, 64), GpuValidator.FmaReference(500, 64), 3);
    }

    [Fact]
    public void Fma_check_finds_the_bad_output()
    {
        var outputs = new float[256];
        for (uint i = 0; i < outputs.Length; i++) outputs[i] = (float)GpuValidator.FmaReference(i, 4);
        Assert.Equal(-1, GpuValidator.CheckFma(outputs, 4));
        outputs[77] *= 1.01f;
        Assert.Equal(77, GpuValidator.CheckFma(outputs, 4));
    }

    [Fact]
    public void Copy_check_is_exact()
    {
        var words = new uint[1024];
        for (var i = 0; i < words.Length; i++) words[i] = GpuValidator.CopyPattern(5000 + i);
        Assert.Equal(-1, GpuValidator.CheckCopy(words, 5000));
        words[3] ^= 1;
        Assert.Equal(5003, GpuValidator.CheckCopy(words, 5000));
        Assert.NotEqual(GpuValidator.CopyPattern(0), GpuValidator.CopyPattern(1L << 32));
    }

    [Fact]
    public void Luid_hex_round_trips()
    {
        var l = new Luid(0x0001ABCD, 0x12);
        Assert.Equal("000000120001ABCD", GpuPart.LuidHex(l));
        Assert.Equal(l, GpuPart.ParseLuid("000000120001abcd"));
        Assert.Null(GpuPart.ParseLuid(null));
        Assert.Null(GpuPart.ParseLuid("12"));
        Assert.Null(GpuPart.ParseLuid("zz00000000000000"));
    }

    /// <summary>A LUID the host sent but no adapter has (unplugged eGPU) or a malformed one: unsupported, never another GPU.
    /// Fails before any device is created, so nothing runs on a GPU.</summary>
    [Theory]
    [InlineData("FFFFFFFF7FFFFFFE")]
    [InlineData("not-a-luid")]
    [InlineData("")]
    public void Unknown_luid_is_unsupported_without_fallback(string luid)
    {
        var sink = new EventSink();
        var ex = Assert.Throws<BenchFailedException>(() =>
            new GpuPart().Run(Ctx.For(new RunCommand("gpu", "1", null, null, luid, null), sink)));
        Assert.Equal("unsupported", ex.Reason);
        Assert.DoesNotContain(sink.Events, e => e.GetProperty("ev").GetString() == "progress");
    }

    [Fact]
    public void Unknown_luid_reaches_the_wire_as_failed_unsupported()
    {
        var sink = new EventSink();
        using var w = new Worker(sink.Writer, BenchScale.Full);
        w.Handle("""{"cmd":"run","part":"gpu","benchVersion":"1","gpuLuid":"FFFFFFFF7FFFFFFE"}""");
        var ev = sink.WaitFor("failed", TimeSpan.FromSeconds(5));
        Assert.NotNull(ev);
        Assert.Equal("unsupported", ev.Value.Ev.GetProperty("reason").GetString());
        Assert.Equal("gpu", ev.Value.Ev.GetProperty("part").GetString());
    }

    [Theory]
    [InlineData("fma")]
    [InlineData("copy")]
    public void Embedded_bytecode_matches_the_hlsl(string name)
    {
        var embedded = Shaders.Load(name);
        var fresh = ShaderCompiler.Compile(Path.Combine(AppContext.BaseDirectory, "shaders", name + ".hlsl"));
        if (!fresh.AsSpan().SequenceEqual(embedded))
        {
            var outPath = Path.Combine(AppContext.BaseDirectory, name + ".cso");
            File.WriteAllBytes(outPath, fresh);
            Assert.Fail($"{name}.cso is stale: copy {outPath} to src/Bench/Gpu/Shaders/");
        }
        Assert.Equal("DXBC"u8.ToArray(), embedded.AsSpan(0, 4).ToArray());
    }
}

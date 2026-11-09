using System.Text;

namespace OptimizationTool.Bench.Tests;

public class WireTests
{
    [Fact]
    public void Parses_run_with_all_fields()
    {
        var c = Assert.IsType<RunCommand>(Wire.Parse(
            """{"cmd":"run","part":"disk","benchVersion":"1","folder":"C:\\x\\bench","ramBudgetBytes":1073741824,"gpuLuid":"00000000000ABCDE","repeat":"rand4k"}"""));
        Assert.Equal("disk", c.Part);
        Assert.Equal("1", c.BenchVersion);
        Assert.Equal(@"C:\x\bench", c.Folder);
        Assert.Equal(1L << 30, c.RamBudgetBytes);
        Assert.Equal("00000000000ABCDE", c.GpuLuid);
        Assert.Equal("rand4k", c.Repeat);
    }

    [Fact]
    public void Parses_run_with_nulls_and_missing_fields()
    {
        var c = Assert.IsType<RunCommand>(Wire.Parse("""{"cmd":"run","part":"cpu","benchVersion":"1","gpuLuid":null,"repeat":null}"""));
        Assert.Null(c.Folder);
        Assert.Null(c.RamBudgetBytes);
        Assert.Null(c.GpuLuid);
        Assert.Null(c.Repeat);
    }

    [Fact]
    public void Parses_stop_and_exit()
    {
        Assert.IsType<StopCommand>(Wire.Parse("""{"cmd":"stop"}"""));
        Assert.IsType<ExitCommand>(Wire.Parse("""{"cmd":"exit"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"cmd":"format"}""")]
    [InlineData("""{"cmd":"run"}""")]
    [InlineData("""{"cmd":"run","part":"cpu","ramBudgetBytes":1.5}""")]
    [InlineData("""{"cmd":"run","part":"cpu","x":{"a":{"b":{"c":{"d":1}}}}}""")]
    public void Rejects_invalid_lines(string line) => Assert.Null(Wire.Parse(line));

    [Fact]
    public void Rejects_oversize_line() =>
        Assert.Null(Wire.Parse("{\"cmd\":\"stop\",\"pad\":\"" + new string('x', Wire.MaxLineBytes) + "\"}"));

    private static string Line(Action<EventWriter> write)
    {
        var sink = new EventSink();
        write(sink.Writer);
        return Assert.Single(sink.Lines);
    }

    [Fact]
    public void Serializes_every_event_shape()
    {
        Assert.Equal("""{"ev":"hello","version":"1","pid":42}""", Line(w => w.Hello(42, null)));
        Assert.Equal("""{"ev":"hello","version":"1","pid":42,"testScale":0.1}""", Line(w => w.Hello(42, 0.1)));
        Assert.Equal("""{"ev":"hb"}""", Line(w => w.Heartbeat()));
        Assert.Equal("""{"ev":"progress","part":"cpu","subtest":"multi","progress":0.5,"liveScore":2410.123,"etaSec":3}""",
            Line(w => w.Progress("cpu", "multi", 0.5, 2410.12345, 2.2)));
        Assert.Equal("""{"ev":"progress","part":"cpu","subtest":"single","progress":1,"liveScore":null,"etaSec":0}""",
            Line(w => w.Progress("cpu", "single", 1.7, null, -4)));
        Assert.Equal("""{"ev":"live","part":"ram","values":{"read":31.2,"latency":null}}""",
            Line(w => w.Live("ram", [new("read", 31.2), new("latency", double.NaN)])));
        Assert.Equal("""{"ev":"result","part":"disk","subtests":{"seqWrite":{"value":2410.5,"unit":"MB/s"}},"info":{"volume":"C:","written":1048576}}""",
            Line(w => w.Result("disk", new PartResult([new SubtestValue("seqWrite", 2410.5, "MB/s")], [new("volume", "C:"), new("written", 1048576L)]))));
        Assert.Equal("""{"ev":"failed","part":"gpu","reason":"gpu_reset","subtest":"fma"}""", Line(w => w.Failed("gpu", "gpu_reset", "fma")));
        Assert.Equal("""{"ev":"failed","part":"ram","reason":"low_memory","subtest":null}""", Line(w => w.Failed("ram", "low_memory", null)));
        Assert.Equal("""{"ev":"stopped","part":"cpu"}""", Line(w => w.Stopped("cpu")));
        Assert.Equal("""{"ev":"stopped","part":null}""", Line(w => w.Stopped(null)));
    }

    [Fact]
    public void Subtest_ids_match_the_seam()
    {
        Assert.Equal(["single", "multi"], BenchIds.Subtests["cpu"]);
        Assert.Equal(["fma", "copy"], BenchIds.Subtests["gpu"]);
        Assert.Equal(["read", "write", "copy", "latency"], BenchIds.Subtests["ram"]);
        Assert.Equal(["seqWrite", "seqRead", "rand4k"], BenchIds.Subtests["disk"]);
    }

    [Fact]
    public void Line_reader_splits_lines_strips_cr_and_skips_oversize()
    {
        var big = new string('x', Wire.MaxLineBytes + 10);
        var input = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{\"cmd\":\"stop\"}\r\n" + big + "\n{\"cmd\":\"exit\"}")]);
        var r = new BoundedLineReader(input);
        Assert.Equal("{\"cmd\":\"stop\"}", r.ReadLine());
        Assert.Equal("", r.ReadLine());
        Assert.Equal("{\"cmd\":\"exit\"}", r.ReadLine());
        Assert.Null(r.ReadLine());
    }

    [Theory]
    [InlineData(new string[0], 1.0)]
    [InlineData(new[] { "--test-scale=0.1" }, 0.1)]
    [InlineData(new[] { "--test-scale=5" }, 1.0)]
    [InlineData(new[] { "--test-scale=0" }, 1.0)]
    [InlineData(new[] { "--test-scale=abc" }, 1.0)]
    public void Test_scale_only_from_a_valid_argument(string[] args, double expected) =>
        Assert.Equal(expected, BenchScale.FromArgs(args).Factor);
}

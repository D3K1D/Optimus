using System.Diagnostics;
using OptimizationTool.Bench.Disk;

namespace OptimizationTool.Bench.Tests;

public class DiskTests
{
    /// <summary>16 MiB file, short phases, no free-space reserve: only ever inside a temp test folder.</summary>
    private static DiskOptions Tiny(TimeSpan? phase = null) => new(
        MaxBytes: 16L << 20, MinWriteBytes: 4L << 20, WriteTime: phase ?? TimeSpan.FromMilliseconds(400),
        MinReadBytes: 1L << 20, ReadTime: phase ?? TimeSpan.FromMilliseconds(300),
        MinRandIos: 20, RandTime: phase ?? TimeSpan.FromMilliseconds(300), FreeReserveBytes: 0, RepeatTime: TimeSpan.FromMilliseconds(400));

    [Fact]
    public void Tiny_run_reports_all_phases_and_leaves_no_file()
    {
        using var dir = new TempFolder();
        var sink = new EventSink();
        var r = new DiskPart(Tiny()).Run(Ctx.For(new RunCommand("disk", "1", dir.Path, null, null, null), sink));
        Assert.Equal(["seqWrite", "seqRead", "rand4k"], r.Subtests.Select(s => s.Id));
        Assert.All(r.Subtests, s => Assert.True(s.Value > 0));
        var info = r.Info.ToDictionary(k => k.Key, k => k.Value);
        Assert.Equal(16L << 20, info["written"]);
        Assert.Matches("^[A-Z]:", (string)info["volume"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void File_exists_only_while_the_handle_is_open()
    {
        using var dir = new TempFolder();
        using var cts = new CancellationTokenSource();
        var sawFile = false;
        var part = new DiskPart(Tiny(TimeSpan.FromSeconds(5)));
        var t = new Thread(() =>
        {
            try { part.Run(Ctx.For(new RunCommand("disk", "1", dir.Path, null, null, null), null, cts.Token)); }
            catch (OperationCanceledException) { }
        });
        t.Start();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !sawFile)
        {
            sawFile = Directory.EnumerateFiles(dir.Path, "ot-bench-*.tmp").Any();
            Thread.Sleep(10);
        }
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        Assert.True(t.Join(TimeSpan.FromSeconds(1)), "disk part did not stop within 1 s");
        Assert.True(sawFile);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void Refuses_when_free_space_would_drop_under_the_reserve()
    {
        using var dir = new TempFolder();
        var o = Tiny() with { FreeReserveBytes = long.MaxValue / 2 };
        var ex = Assert.Throws<BenchFailedException>(() =>
            new DiskPart(o).Run(Ctx.For(new RunCommand("disk", "1", dir.Path, null, null, null))));
        Assert.Equal("needs_space", ex.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void Missing_minimum_is_too_slow()
    {
        using var dir = new TempFolder();
        var o = Tiny() with { MinWriteBytes = 1L << 40 };
        var ex = Assert.Throws<BenchFailedException>(() =>
            new DiskPart(o).Run(Ctx.For(new RunCommand("disk", "1", dir.Path, null, null, null))));
        Assert.Equal("too_slow", ex.Reason);
        Assert.Equal("seqWrite", ex.Subtest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative\\path")]
    [InlineData("C:\\definitely-not-here-ot-bench-0f3c")]
    public void Bad_folder_is_error(string? folder)
    {
        var ex = Assert.Throws<BenchFailedException>(() =>
            new DiskPart(Tiny()).Run(Ctx.For(new RunCommand("disk", "1", folder, null, null, null))));
        Assert.Equal("error", ex.Reason);
    }

    [Fact]
    public void Repeat_runs_only_the_asked_subtest()
    {
        using var dir = new TempFolder();
        var r = new DiskPart(Tiny()).Run(Ctx.For(new RunCommand("disk", "1", dir.Path, null, null, "rand4k")));
        Assert.Equal("rand4k", Assert.Single(r.Subtests).Id);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }
}

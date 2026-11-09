using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace OptimizationTool.Bench.Tests;

/// <summary>
/// Starts the real OptimizationTool.Bench.exe with <c>--test-scale=0.1</c> (test-only flag the Host never passes) and
/// checks the shape of what it says. Numbers are printed, not asserted. Disk writes only inside a temp test folder.
/// </summary>
[Trait("Category", "Live")]
public class LiveTests(ITestOutputHelper output)
{
    private const double Scale = 0.1;

    private sealed class Proc : IDisposable
    {
        private readonly Process _p;
        public readonly List<JsonElement> Events = [];

        public Proc()
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Bench.exe");
            Assert.True(File.Exists(exe), exe);
            _p = Process.Start(new ProcessStartInfo(exe, $"--test-scale={Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            _p.ErrorDataReceived += (_, _) => { };
            _p.BeginErrorReadLine();
        }

        public void Send(string line) { _p.StandardInput.WriteLine(line); _p.StandardInput.Flush(); }

        /// <summary>Reads events until one with ev in <paramref name="until"/>; every line must be JSON.</summary>
        public JsonElement ReadUntil(TimeSpan timeout, params string[] until)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var task = _p.StandardOutput.ReadLineAsync();
                if (!task.Wait(deadline - DateTime.UtcNow) || task.Result is null) break;
                var ev = JsonDocument.Parse(task.Result).RootElement.Clone();
                Events.Add(ev);
                if (until.Contains(ev.GetProperty("ev").GetString())) return ev;
            }
            throw new TimeoutException("no " + string.Join("|", until));
        }

        public int Exit()
        {
            Send("""{"cmd":"exit"}""");
            Assert.True(_p.WaitForExit(5000));
            return _p.ExitCode;
        }

        public void Dispose()
        {
            if (!_p.HasExited) _p.Kill();
            _p.Dispose();
        }
    }

    private JsonElement RunPart(string runLine, TimeSpan timeout, out List<JsonElement> events)
    {
        using var p = new Proc();
        var hello = p.ReadUntil(TimeSpan.FromSeconds(10), "hello");
        Assert.Equal("1", hello.GetProperty("version").GetString());
        Assert.True(hello.GetProperty("pid").GetInt32() > 0);
        Assert.Equal(Scale, hello.GetProperty("testScale").GetDouble());
        p.Send(runLine);
        var end = p.ReadUntil(timeout, "result", "failed");
        Assert.Equal(0, p.Exit());
        events = p.Events;
        output.WriteLine(end.GetRawText());
        return end;
    }

    private static void AssertResult(JsonElement end, string part, string[] subtests, string[] units, List<JsonElement> events)
    {
        Assert.Equal("result", end.GetProperty("ev").GetString());
        Assert.Equal(part, end.GetProperty("part").GetString());
        var s = end.GetProperty("subtests");
        for (var i = 0; i < subtests.Length; i++)
        {
            var st = s.GetProperty(subtests[i]);
            Assert.True(st.GetProperty("value").GetDouble() > 0);
            Assert.Equal(units[i], st.GetProperty("unit").GetString());
        }
        Assert.Contains(events, e => e.GetProperty("ev").GetString() == "hb");
        Assert.Contains(events, e => e.GetProperty("ev").GetString() == "progress");
    }

    [Fact]
    public void Cpu()
    {
        var end = RunPart("""{"cmd":"run","part":"cpu","benchVersion":"1"}""", TimeSpan.FromSeconds(30), out var ev);
        AssertResult(end, "cpu", ["single", "multi"], ["ops/s", "ops/s"], ev);
    }

    [Fact]
    public void Ram()
    {
        var end = RunPart("""{"cmd":"run","part":"ram","benchVersion":"1","ramBudgetBytes":536870912}""", TimeSpan.FromSeconds(30), out var ev);
        AssertResult(end, "ram", ["read", "write", "copy", "latency"], ["GB/s", "GB/s", "GB/s", "ns"], ev);
        Assert.Contains(ev, e => e.GetProperty("ev").GetString() == "live");
    }

    [Fact]
    public void Disk()
    {
        using var dir = new TempFolder();
        var folder = JsonSerializer.Serialize(dir.Path);
        var end = RunPart($$"""{"cmd":"run","part":"disk","benchVersion":"1","folder":{{folder}}}""", TimeSpan.FromSeconds(30), out var ev);
        AssertResult(end, "disk", ["seqWrite", "seqRead", "rand4k"], ["MB/s", "MB/s", "MB/s"], ev);
        Assert.Equal(dir.Path[..2], end.GetProperty("info").GetProperty("volume").GetString());
        Assert.True(end.GetProperty("info").GetProperty("written").GetInt64() > 0);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void Gpu()
    {
        var end = RunPart("""{"cmd":"run","part":"gpu","benchVersion":"1","gpuLuid":null}""", TimeSpan.FromSeconds(60), out var ev);
        if (end.GetProperty("ev").GetString() == "failed")
        {
            // A PC without a feature-level 11_0 GPU reports unsupported; anything else is a bug.
            Assert.Equal("unsupported", end.GetProperty("reason").GetString());
            return;
        }
        AssertResult(end, "gpu", ["fma", "copy"], ["GFLOPS", "GB/s"], ev);
        Assert.Matches("^[0-9A-F]{16}$", end.GetProperty("info").GetProperty("adapterLuid").GetString());
    }

    /// <summary>Every hardware adapter, addressed by its LUID (lower-case on the wire to check case-insensitivity), is the
    /// one the worker measures: the reported adapterLuid must be that LUID, never the high-performance default.</summary>
    [Fact]
    public void Gpu_picks_the_adapter_by_luid()
    {
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
        var luids = new List<string>();
        for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
        {
            using (a)
            {
                var d = a.Description1;
                if ((d.Flags & Vortice.DXGI.AdapterFlags.Software) != 0) continue;
                luids.Add(OptimizationTool.Bench.Gpu.GpuPart.LuidHex(d.Luid));
                output.WriteLine($"{OptimizationTool.Bench.Gpu.GpuPart.LuidHex(d.Luid)} {d.Description}");
            }
        }
        foreach (var luid in luids.Distinct())
        {
            var end = RunPart($$"""{"cmd":"run","part":"gpu","benchVersion":"1","gpuLuid":"{{luid.ToLowerInvariant()}}","repeat":"fma"}""",
                TimeSpan.FromSeconds(60), out _);
            if (end.GetProperty("ev").GetString() == "failed") { Assert.Equal("unsupported", end.GetProperty("reason").GetString()); continue; }
            Assert.Equal(luid, end.GetProperty("info").GetProperty("adapterLuid").GetString());
        }
    }

    [Fact]
    public void Repeat_and_stop()
    {
        using var p = new Proc();
        p.ReadUntil(TimeSpan.FromSeconds(10), "hello");
        p.Send("""{"cmd":"run","part":"cpu","benchVersion":"1","repeat":"multi"}""");
        var end = p.ReadUntil(TimeSpan.FromSeconds(30), "result", "failed");
        Assert.Equal("multi", Assert.Single(end.GetProperty("subtests").EnumerateObject()).Name);
        p.Send("""{"cmd":"run","part":"cpu","benchVersion":"1"}""");
        p.ReadUntil(TimeSpan.FromSeconds(10), "progress");
        var sw = Stopwatch.StartNew();
        p.Send("""{"cmd":"stop"}""");
        var stopped = p.ReadUntil(TimeSpan.FromSeconds(2), "stopped");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), sw.Elapsed.ToString());
        Assert.Equal("cpu", stopped.GetProperty("part").GetString());
        Assert.Equal(0, p.Exit());
    }
}

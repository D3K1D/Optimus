using System.Diagnostics;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Host;

namespace OptimizationTool.Tests;

/// <summary>The host's headless process runner (m5-chat §3) against real child processes: stdin in, lines out, tree killed on cancel.</summary>
public sealed class CliRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ot-tests", "runner-" + Guid.NewGuid().ToString("N"));

    public CliRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }

    [Fact]
    public async Task Cmd_GetsThePromptOnStdin_AndStreamsLines()
    {
        var cli = Script("echo.cmd", "findstr /r \".*\"\r\necho done %~1 1>&2\r\nexit /b 3");
        var lines = new List<string>();
        var exit = await new CliRunner().RunAsync(new CliLaunch(cli, ["--tools=", "-"], "héllo & \"quoted\" %PATH%\nsecond", _dir), lines.Add, default);
        Assert.True(exit.Started);
        Assert.Equal(3, exit.ExitCode);
        Assert.Equal(["héllo & \"quoted\" %PATH%", "second"], lines);
        Assert.Contains("done --tools=", exit.StandardError);
    }

    [Fact]
    public async Task UnsafeOrMissingCommands_DoNotStart()
    {
        var cli = Script("x.cmd", "echo hi");
        Assert.False((await new CliRunner().RunAsync(new CliLaunch(cli, [""], "", _dir), _ => { }, default)).Started);
        Assert.False((await new CliRunner().RunAsync(new CliLaunch(cli, ["a\"b"], "", _dir), _ => { }, default)).Started);
        Assert.False((await new CliRunner().RunAsync(new CliLaunch(Path.Combine(_dir, "missing.exe"), [], "", _dir), _ => { }, default)).Started);
    }

    [Fact]
    public async Task ALineHandlerThatThrows_KillsTheProcessTree()
    {
        // The script keeps appending to a file until it is killed.
        var tick = Path.Combine(_dir, "tick.txt");
        var cli = Script("loop.cmd", $"echo first\r\n:loop\r\necho x>>\"{tick}\"\r\nping -n 2 127.0.0.1 >nul\r\ngoto loop");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CliRunner().RunAsync(new CliLaunch(cli, [], "", _dir), _ => throw new InvalidOperationException("parser"), default));
        await Task.Delay(1500); // a tick already on its way may still land
        var before = File.Exists(tick) ? new FileInfo(tick).Length : 0;
        await Task.Delay(3000);
        var after = File.Exists(tick) ? new FileInfo(tick).Length : 0;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task PipesHeldOpenByAGrandchild_DoNotHoldTheRunPastExit()
    {
        // A background child inherits the pipes and keeps them open after the CLI itself exits.
        var cli = Script("linger.cmd", "start /b ping -n 30 127.0.0.1 >nul\r\necho answer\r\nexit /b 0");
        var lines = new List<string>();
        var clock = Stopwatch.StartNew();
        var exit = await new CliRunner(pipeDrain: TimeSpan.FromSeconds(1)).RunAsync(new CliLaunch(cli, [], "", _dir), lines.Add, default).WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.Equal(0, exit.ExitCode);
        Assert.Contains("answer", lines);
    }

    [Fact]
    public async Task Cancel_KillsTheProcessTree()
    {
        var cli = Script("slow.cmd", "ping -n 60 127.0.0.1 >nul");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CliRunner().RunAsync(new CliLaunch(cli, [], "", _dir), _ => { }, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
    }
}

using System.Diagnostics;
using System.Text;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport;

namespace OptimizationTool.Host;

/// <summary>
/// Production <see cref="ICliRunner"/> for headless Claude Code / Codex runs (contracts/m5-chat.md §3). The command
/// comes from the shared safe builder (<see cref="ProcessCommand.ForCli"/>: .exe → argument list, npm shim → its exe or
/// node.exe + entry with an argument list, other .cmd → validated cmd.exe line), with no window, the environment unchanged except PATH refreshed from the registry, stdin written
/// (UTF-8, no BOM) and closed, stdout read line by line as UTF-8. Cancelling, or any other abnormal end of the read
/// loop, kills the whole process tree; after exit, stdout and stderr are drained for a bounded time only.
/// Output is parsed by the engine and never logged.
/// </summary>
public sealed class CliRunner(string? systemRoot = null, Func<string?>? childPath = null, TimeSpan? pipeDrain = null, NpmShimEnvironment? npm = null) : ICliRunner
{
    private const int MaxStderrChars = 16 * 1024;

    /// <summary>How long stdout and stderr are still read after the CLI exits (a lingering grandchild may keep them open).</summary>
    private readonly TimeSpan _pipeDrain = pipeDrain ?? TimeSpan.FromSeconds(3);
    private readonly string _systemRoot = systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly Func<string?> _childPath = childPath ?? SystemPath.ForChildren;

    /// <summary>The start info for a launch, or null when a token cannot be passed safely.</summary>
    internal ProcessStartInfo? StartInfo(CliLaunch launch)
    {
        if (ProcessCommand.ForCli(launch.CliPath, launch.Arguments, _systemRoot, npm) is not { } command) return null;
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = launch.WorkingDirectory,
        };
        if (command.CmdArguments is not null) psi.Arguments = command.CmdArguments;
        else foreach (var a in command.ArgumentList) psi.ArgumentList.Add(a);
        if (_childPath() is { Length: > 0 } path) psi.Environment["PATH"] = path;
        return psi;
    }

    public async Task<CliExit> RunAsync(CliLaunch launch, Action<string> onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StartInfo(launch) is not { } psi) return new CliExit(-1, "", Started: false, LaunchError: "unsupported_path");
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new CliExit(-1, "", Started: false, LaunchError: LaunchFailure.Failed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CliExit(-1, "", Started: false, LaunchError: LaunchFailure.Classify(ex));
        }

        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            try
            {
                while ((read = await process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
                    lock (stderr) if (stderr.Length < MaxStderrChars) stderr.Append(buffer, 0, Math.Min(read, MaxStderrChars - stderr.Length));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } // abandoned or closed
        }, CancellationToken.None);

        await using var kill = ct.Register(() => Kill(process));
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(launch.StandardInput.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) { } // the CLI exited before reading its input; its exit says why
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }

            // A grandchild that inherited the pipes can hold them open after the CLI exits, so after exit stdout and
            // stderr are drained for a bounded time only; lines arriving after that are dropped.
            var abandoned = 0;
            var readTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (Volatile.Read(ref abandoned) != 0) return;
                    onLine(line);
                }
            }, CancellationToken.None);
            var exitTask = process.WaitForExitAsync(ct);
            if (await Task.WhenAny(readTask, exitTask).ConfigureAwait(false) == readTask || readTask.IsCompleted)
                await readTask.ConfigureAwait(false); // a handler that threw, or cancellation, surfaces here
            else
            {
                await exitTask.ConfigureAwait(false);
                try { await readTask.WaitAsync(_pipeDrain, ct).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Volatile.Write(ref abandoned, 1);
                    _ = readTask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
            }
            await exitTask.ConfigureAwait(false);
            try { await errTask.WaitAsync(_pipeDrain, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        catch
        {
            // Any abnormal end of the read loop (cancel, a line handler that threw, a broken pipe) takes the tree down.
            Kill(process);
            throw;
        }
        ct.ThrowIfCancellationRequested();
        lock (stderr) return new CliExit(process.ExitCode, stderr.ToString());
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

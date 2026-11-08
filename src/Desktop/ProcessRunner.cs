using System.Diagnostics;
using System.Text;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop;

/// <summary>
/// Production <see cref="IProcessRunner"/>: no shell execute, no window, environment unchanged except PATH, which is
/// refreshed from the registry (so an npm .cmd shim finds node after a late install), output captured (capped) for
/// parsing only, killed with its tree on timeout. Working directory is the user profile so a project-scoped
/// configuration in the app's directory cannot shadow the user-scope entry.
/// </summary>
internal sealed class ProcessRunner(Func<string?>? childPath = null) : IProcessRunner
{
    private const int MaxOutputChars = 64 * 1024;
    private readonly Func<string?> _childPath = childPath ?? DefaultChildPath;

    /// <summary>Fresh registry PATH followed by any process PATH entries it lacks.</summary>
    public static string? DefaultChildPath() =>
        SystemPath.ForChildren(SystemPath.FromRegistry(), Environment.GetEnvironmentVariable("PATH"));

    internal static ProcessStartInfo BuildStartInfo(ProcessCommand command, string? path)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (command.CmdArguments is not null) psi.Arguments = command.CmdArguments;
        else foreach (var a in command.ArgumentList) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(path)) psi.Environment["PATH"] = path;
        return psi;
    }

    public async Task<ProcessResult> RunAsync(ProcessCommand command, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BuildStartInfo(command, _childPath()) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        try
        {
            if (!process.Start()) return new ProcessResult(-1, "", "", false, LaunchFailure.Failed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, "", "", false, LaunchFailure.Classify(ex));
        }
        process.StandardInput.Close(); // Never wait on an interactive prompt.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await process.WaitForExitAsync(CancellationToken.None); // flush redirected output
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(-1, "", "", true);
        }

        lock (stdout) lock (stderr)
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb)
        {
            if (sb.Length + line.Length + 1 > MaxOutputChars) return;
            sb.Append(line).Append('\n');
        }
    }
}

/// <summary>
/// Production <see cref="IInstallLauncher"/>: starts the fixed PowerShell installer command in its own visible console
/// window (UseShellExecute=false, CreateNoWindow=false, nothing redirected), not elevated, working directory the user
/// profile, PATH refreshed like <see cref="ProcessRunner"/>. Never waits on it; tracks it so only one runs at a time.
/// </summary>
internal sealed class ConsoleInstallLauncher : IInstallLauncher
{
    private readonly Lock _lock = new();
    private Process? _running;

    internal static ProcessStartInfo BuildStartInfo(InstallCommand command, string? path)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = false, // the GUI parent has no console: Windows gives the child a new, visible one
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in command.ArgumentList) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(path)) psi.Environment["PATH"] = path;
        return psi;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                if (_running is null) return false;
                try { if (!_running.HasExited) return true; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; }
                _running.Dispose();
                _running = null;
                return false;
            }
        }
    }

    public bool TryStart(InstallCommand command)
    {
        if (!Path.IsPathFullyQualified(command.FileName) || !File.Exists(command.FileName)) return false;
        lock (_lock)
        {
            try
            {
                var p = Process.Start(BuildStartInfo(command, ProcessRunner.DefaultChildPath()));
                if (p is null) return false;
                _running?.Dispose();
                _running = p;
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }
}

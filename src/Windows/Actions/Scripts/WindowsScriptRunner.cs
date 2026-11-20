using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OptimizationTool.Engine.Actions.Scripts;

namespace OptimizationTool.Windows.Actions.Scripts;

/// <summary>
/// A PowerShell process for one approved script or one parse (contracts/m9-ai-powers.md §5.4): created suspended with no
/// window, the given environment only (built from scratch), the run folder as its working directory, stdin closed, stdout
/// and stderr on pipes (only those three handles are inherited), put in a job (kill on close, no breakaway) before any of
/// its code runs, then resumed. Ending the job ends every process the script started inside it.
/// </summary>
internal sealed class ScriptProcess : IDisposable
{
    private readonly SafeJobHandle _job;
    private readonly IntPtr _process;
    private int _disposed;

    public Stream Stdout { get; }
    public Stream Stderr { get; }

    private ScriptProcess(SafeJobHandle job, IntPtr process, Stream stdout, Stream stderr)
    {
        _job = job;
        _process = process;
        Stdout = stdout;
        Stderr = stderr;
    }

    public static ScriptProcess Start(string exe, string commandLine, IReadOnlyDictionary<string, string> environment, string workingDirectory)
    {
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        SafeJobHandle? job = null;
        var attributes = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var env = IntPtr.Zero;
        var pi = default(JobNative.PROCESS_INFORMATION);
        var created = false;
        try
        {
            job = JobNative.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Kill on close, no breakaway: nothing the script starts in its job can leave it.
            var limits = new JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JobNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JobNative.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                },
            };
            if (!JobNative.SetInformationJobObject(job, JobNative.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var handles = new[]
            {
                stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle(),
            };
            handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, handleList, handles.Length);
            var size = IntPtr.Zero;
            JobNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!JobNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!JobNative.UpdateProcThreadAttribute(attributes, 0, (IntPtr)JobNative.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList,
                    (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            env = Marshal.StringToHGlobalUni(Block(environment));
            var si = new JobNative.STARTUPINFOEX
            {
                StartupInfo = new JobNative.STARTUPINFO
                {
                    cb = Marshal.SizeOf<JobNative.STARTUPINFOEX>(), dwFlags = JobNative.STARTF_USESTDHANDLES,
                    hStdInput = handles[0], hStdOutput = handles[1], hStdError = handles[2],
                },
                lpAttributeList = attributes,
            };
            if (!JobNative.CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                    JobNative.CREATE_SUSPENDED | JobNative.CREATE_NO_WINDOW | JobNative.EXTENDED_STARTUPINFO_PRESENT | ScriptNative.CREATE_UNICODE_ENVIRONMENT,
                    env, workingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            created = true;
            if (!JobNative.AssignProcessToJobObject(job, pi.hProcess)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (JobNative.ResumeThread(pi.hThread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());

            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            stdin.Dispose(); // no stdin: the child reads end-of-file
            JobNative.CloseHandle(pi.hThread);
            return new ScriptProcess(job, pi.hProcess, stdout, stderr);
        }
        catch
        {
            if (created && pi.hProcess != IntPtr.Zero) JobNative.TerminateProcess(pi.hProcess, 1);
            if (pi.hThread != IntPtr.Zero) JobNative.CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) JobNative.CloseHandle(pi.hProcess);
            job?.Dispose();
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                JobNative.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
        }
    }

    /// <summary>A Unicode environment block: NAME=value pairs sorted by name, each NUL-ended, a final NUL.</summary>
    internal static string Block(IReadOnlyDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (name.Length > 0 && !name.Contains('=') && !name.Contains('\0') && !value.Contains('\0'))
                sb.Append(name).Append('=').Append(value).Append('\0');
        return sb.Append('\0').ToString();
    }

    /// <summary>Completes when the process exits.</summary>
    public Task WaitForExitAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_process, ownsHandle: false) };
        var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => done.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        var registration = ct.Register(() => done.TrySetCanceled(ct));
        _ = done.Task.ContinueWith(_ =>
        {
            wait.Unregister(null);
            registration.Dispose();
            handle.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return done.Task;
    }

    public int ExitCode => ScriptNative.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

    /// <summary>Ends every process in the job (Stop, the time limit, shutdown, or what the script left behind).</summary>
    public void KillJob()
    {
        try { JobNative.TerminateJobObject(_job, 1); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        KillJob();
        _job.Dispose(); // kill on close: anything left in the job ends here
        JobNative.CloseHandle(_process);
        Stdout.Dispose();
        Stderr.Dispose();
    }

    /// <summary>The console encoding a child writes to a pipe (the OEM code page), else UTF-8.</summary>
    internal static Encoding OutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)ScriptNative.GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return new UTF8Encoding(false); }
    }

    /// <summary>
    /// Drains one stream continuously into lines (CR dropped, at most <paramref name="maxLine"/> characters each; a longer
    /// line is split), so a chatty or hostile script can neither block on a full pipe nor grow one line without bound.
    /// </summary>
    internal static async Task DrainAsync(Stream stream, Encoding encoding, Action<string> onLine, int maxLine = 4096)
    {
        var decoder = encoding.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes).ConfigureAwait(false)) > 0)
            {
                var n = decoder.GetChars(bytes, 0, read, chars, 0);
                for (var i = 0; i < n; i++)
                {
                    var c = chars[i];
                    if (c == '\n') { onLine(line.ToString()); line.Clear(); continue; }
                    if (c == '\r') continue;
                    line.Append(c);
                    if (line.Length >= maxLine) { onLine(line.ToString()); line.Clear(); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        if (line.Length > 0) onLine(line.ToString());
    }
}

/// <summary>
/// The production <see cref="IScriptRunner"/> (§5.4). Never run by the test suite. Windows PowerShell 5.1 from the system
/// directory with its Microsoft signature verified; the approved bytes staged in a new user-only run folder and re-hashed
/// from the held file; structured arguments -NoProfile -NonInteractive -InputFormat None [-ExecutionPolicy Bypass] -File;
/// the environment built from scratch; suspended → job → resumed; both streams drained continuously; the time limit, Stop
/// and shutdown end every process in the job. AMSI, Constrained Language mode, AppLocker and App Control are left alone: a
/// block is a failed run, never retried another way.
/// </summary>
public sealed class WindowsScriptRunner : IScriptRunner
{
    private readonly Func<(string? Path, string? Problem)> _locate;
    private readonly Func<string, IDisposable>? _trustedRoot;

    public WindowsScriptRunner() : this(locate: null) { }

    /// <param name="trustedRoot">The elevated broker's trusted-folder provisioning for the runs root (m9 §5.4, §13 R1); see ScriptStage.Prepare.</param>
    public WindowsScriptRunner(Func<string, IDisposable>? trustedRoot) : this(null, trustedRoot) { }

    internal WindowsScriptRunner(Func<(string? Path, string? Problem)>? locate, Func<string, IDisposable>? trustedRoot = null)
    {
        _locate = locate ?? PowerShellInterpreter.Locate;
        _trustedRoot = trustedRoot;
    }

    public async Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunObserver observer, CancellationToken stop)
    {
        var (exe, _) = _locate();
        if (exe is null) return ScriptRunResult.NotStarted("untrusted_interpreter");
        var (staged, problem) = ScriptStage.Prepare(request.RunsRoot, request.Bytes, request.Sha256, trustedRoot: _trustedRoot);
        if (staged is null) return ScriptRunResult.NotStarted(problem ?? "folder");
        using (staged)
        {
            ScriptProcess process;
            try
            {
                process = ScriptProcess.Start(exe, PowerShellInterpreter.CommandLine(exe, PowerShellInterpreter.Arguments(staged.ScriptPath, request.BypassPolicy)),
                    PowerShellInterpreter.Environment(staged.Folder), staged.Folder);
            }
            catch (Win32Exception) { return ScriptRunResult.NotStarted("launch_failed", staged.Folder); }
            using (process)
            {
                var clock = Stopwatch.StartNew();
                observer.Started(DateTimeOffset.UtcNow, staged.Folder);
                var encoding = ScriptProcess.OutputEncoding();
                var outTask = Task.Run(() => ScriptProcess.DrainAsync(process.Stdout, encoding, l => observer.Line(false, l)), CancellationToken.None);
                var errTask = Task.Run(() => ScriptProcess.DrainAsync(process.Stderr, encoding, l => observer.Line(true, l)), CancellationToken.None);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(request.Timeout + TimeSpan.FromSeconds(5)); // a backstop behind the engine's own limit
                var killed = false;
                try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    process.KillJob();
                    killed = true;
                    try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                var exit = process.ExitCode;
                process.KillJob(); // what the script left running in its job ends with it
                try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                return killed ? ScriptRunResult.KilledRun(staged.Folder, clock.Elapsed) : ScriptRunResult.Exit(exit, staged.Folder, clock.Elapsed);
            }
        }
    }
}

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Host.Bench;

/// <summary>
/// Starts OptimizationTool.Bench.exe beside the host (contracts/m6-benchmark.md §4): created suspended with no window
/// and stdin/stdout/stderr on anonymous pipes (only those three handles are inherited), assigned to a job object with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE before its thread is resumed, so it can never outlive the host.
/// </summary>
public sealed class WindowsBenchLauncher(string? executablePath = null) : IBenchWorkerLauncher
{
    public const string Executable = "OptimizationTool.Bench.exe";

    public string ExecutablePath { get; } = executablePath ?? Path.Combine(AppContext.BaseDirectory, Executable);

    public IBenchWorker Launch()
    {
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("The benchmark worker is not installed.", Executable);
        return WindowsBenchWorker.Start(ExecutablePath);
    }
}

internal sealed class WindowsBenchWorker : IBenchWorker
{
    private readonly SafeJobHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly AnonymousPipeServerStream _stdin, _stdout, _stderr;
    private readonly BoundedLineReader _reader;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _killed, _disposed;

    private WindowsBenchWorker(SafeJobHandle job, SafeProcessHandle process, AnonymousPipeServerStream stdin, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr)
    {
        _job = job;
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _reader = new BoundedLineReader(stdout);
        _ = Task.Run(DrainStderrAsync);
    }

    public static WindowsBenchWorker Start(string exePath)
    {
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        SafeJobHandle? job = null;
        var attributes = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var pi = default(Native.PROCESS_INFORMATION);
        var created = false;
        try
        {
            job = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Native.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                },
            };
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Only the three pipe ends are inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST).
            var handles = new[]
            {
                stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle(),
            };
            handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, handleList, handles.Length);
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList,
                    (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var si = new Native.STARTUPINFOEX
            {
                StartupInfo = new Native.STARTUPINFO
                {
                    cb = Marshal.SizeOf<Native.STARTUPINFOEX>(), dwFlags = Native.STARTF_USESTDHANDLES,
                    hStdInput = handles[0], hStdOutput = handles[1], hStdError = handles[2],
                },
                lpAttributeList = attributes,
            };
            var commandLine = new StringBuilder("\"" + exePath + "\"");
            if (!Native.CreateProcessW(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    Native.CREATE_SUSPENDED | Native.CREATE_NO_WINDOW | Native.EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, Path.GetDirectoryName(exePath), ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            created = true;

            // Into the kill-on-close job before any of its code runs.
            if (!Native.AssignProcessToJobObject(job, pi.hProcess)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Native.ResumeThread(pi.hThread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());

            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            Native.CloseHandle(pi.hThread);
            pi.hThread = IntPtr.Zero;
            return new WindowsBenchWorker(job, new SafeProcessHandle(pi.hProcess, ownsHandle: true), stdin, stdout, stderr);
        }
        catch
        {
            if (created && pi.hProcess != IntPtr.Zero) Native.TerminateProcess(pi.hProcess, 1);
            if (pi.hThread != IntPtr.Zero) Native.CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) Native.CloseHandle(pi.hProcess);
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
                Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
        }
    }

    /// <summary>stderr is drained and dropped (the worker writes at most 64 KiB of codes; nothing is logged verbatim).</summary>
    private async Task DrainStderrAsync()
    {
        var buffer = new byte[4096];
        try { while (await _stderr.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async Task SendAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try { return await _reader.ReadLineAsync(ct).ConfigureAwait(false); }
        catch (IOException) { return null; } // broken pipe: the worker is gone
        catch (ObjectDisposedException) { return null; }
    }

    public void Kill()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        try { Native.TerminateJobObject(_job, 1); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        _job.Dispose(); // KILL_ON_JOB_CLOSE: anything left in the job ends here
        _process.Dispose();
        _stdin.Dispose();
        _stdout.Dispose();
        _stderr.Dispose();
        _writeGate.Dispose();
    }
}

internal sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

internal static class Native
{
    public const uint CREATE_SUSPENDED = 0x00000004, CREATE_NO_WINDOW = 0x08000000, EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000, JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    public const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(SafeJobHandle hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
}

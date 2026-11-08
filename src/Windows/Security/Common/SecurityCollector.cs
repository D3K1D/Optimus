using System.Runtime.InteropServices;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Security.Common;

/// <summary>
/// Base for the security collectors: a <see cref="Read"/> step (Windows calls → raw DTO) that runs on a dedicated
/// background STA thread the coordinator's timeout can abandon, and a pure <see cref="Normalize"/> step
/// (raw DTO → facts + coverage) that unit tests drive with synthetic DTOs.
/// </summary>
internal abstract class SecurityCollector<TRaw>(CollectorOptions options, TimeSpan? timeout = null) : ICollector
{
    public abstract string Id { get; }
    public string Version => "1.0.0";
    public virtual string Category => "security";
    public TimeSpan Timeout { get; } = timeout ?? options.DefaultTimeout;
    public abstract string GapTitle { get; }
    public abstract string GapSource { get; }
    public abstract string GapDisplay { get; }

    /// <summary>Windows calls only. Must not write anything, prompt, elevate or touch the network.</summary>
    internal abstract TRaw Read(CancellationToken ct);

    /// <summary>Pure: raw DTO → sorted facts and an honest coverage receipt.</summary>
    internal abstract CollectorResult Normalize(TRaw raw);

    public async Task<CollectorResult> CollectAsync(CancellationToken ct)
    {
        TRaw raw;
        try
        {
            raw = await StaWorker.Run(() => Read(ct), Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return Denied("Windows needs administrator rights for this check."); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005)) { return Denied("Windows needs administrator rights for this check."); }
        catch (Exception ex) { return new CollectorResult([], new CoverageReceipt("failed", 0, ex.GetType().Name)); }
        ct.ThrowIfCancellationRequested();
        return Normalize(raw);
    }

    private static CollectorResult Denied(string reason) => new([], new CoverageReceipt("denied", 0, reason));
}

/// <summary>Runs blocking COM/WMI/Win32 work on its own background STA thread.</summary>
internal static class StaWorker
{
    public static Task<T> Run<T>(Func<T> work, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "collector " + name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // If the deadline passes the thread is abandoned (it is a background thread and only reads).
        return tcs.Task.WaitAsync(ct);
    }
}

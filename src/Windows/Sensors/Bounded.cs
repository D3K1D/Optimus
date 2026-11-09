using System.Globalization;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Sensors;

/// <summary>Runs a blocking call (WMI, driver queries) on its own background thread that a timeout abandons.</summary>
internal static class Bounded
{
    /// <summary>
    /// The result, or <c>timedOut = true</c> when <paramref name="timeout"/> passed first. An abandoned result that
    /// arrives later is disposed if it is <see cref="IDisposable"/>. Exceptions from <paramref name="work"/> propagate.
    /// Ownership of the result changes hands in one compare-exchange, so exactly one side ever disposes it: the
    /// caller when the work published first (even at the moment of the timeout), the worker thread when it was
    /// abandoned first. An abandoned call's late exception is dropped on its own thread (no unobserved task fault).
    /// </summary>
    public static T? Run<T>(Func<T> work, TimeSpan timeout, CancellationToken ct, out bool timedOut) where T : class?
    {
        const int Running = 0, Published = 1, Abandoned = 2;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = Running;
        var thread = new Thread(() =>
        {
            T r;
            try { r = work(); }
            catch (Exception ex)
            {
                if (Interlocked.CompareExchange(ref state, Published, Running) == Running) tcs.SetException(ex);
                return; // abandoned: nobody waits for this failure
            }
            if (Interlocked.CompareExchange(ref state, Published, Running) == Running) tcs.SetResult(r);
            else
                try { (r as IDisposable)?.Dispose(); } catch (Exception) { /* abandoned result: best effort */ }
        })
        { IsBackground = true, Name = "Optimus sensors probe" };
        thread.Start();
        var cancelled = false;
        try
        {
            if (tcs.Task.Wait(timeout, ct)) { timedOut = false; return tcs.Task.Result; }
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner) { throw inner; }
        catch (OperationCanceledException) { cancelled = true; }

        if (Interlocked.CompareExchange(ref state, Abandoned, Running) == Running)
        {
            if (cancelled) throw new OperationCanceledException(ct);
            timedOut = true;
            return null;
        }
        // The work published in the same instant: its result (or failure) belongs to this caller, set right after the exchange.
        T late;
        try { late = tcs.Task.GetAwaiter().GetResult(); }
        catch (Exception) when (cancelled) { throw new OperationCanceledException(ct); }
        if (cancelled)
        {
            (late as IDisposable)?.Dispose();
            throw new OperationCanceledException(ct);
        }
        timedOut = false;
        return late;
    }
}

/// <summary>Late-bound WbemScripting (no extra package), read-only queries with row caps.</summary>
internal sealed class Wmi : IDisposable
{
    private object? _service;

    private Wmi(object service) => _service = service;

    public static Wmi Connect(string ns)
    {
        var locator = Automation.Create("WbemScripting.SWbemLocator");
        try { return new Wmi(Automation.Call(locator, "ConnectServer", ".", ns)!); }
        finally { Automation.Release(locator); }
    }

    /// <summary>Rows as property bags (only the named properties are read).</summary>
    public List<Dictionary<string, object?>> Query(string wql, IReadOnlyList<string> props, int cap)
    {
        // wbemFlagReturnImmediately (0x10); not forward-only, which fails through late binding.
        var set = Automation.Call(_service!, "ExecQuery", wql, "WQL", 0x10)!;
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var row in Automation.Items(set, cap))
            {
                try
                {
                    var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in props) bag[p] = Prop(row, p);
                    rows.Add(bag);
                }
                finally { Automation.Release(row); }
            }
        }
        finally { Automation.Release(set); }
        return rows;
    }

    private static object? Prop(object row, string name)
    {
        try
        {
            var props = Automation.Get(row, "Properties_")!;
            try
            {
                var p = Automation.Call(props, "Item", name)!;
                try { return Automation.Get(p, "Value"); }
                finally { Automation.Release(p); }
            }
            finally { Automation.Release(props); }
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or System.Runtime.InteropServices.COMException or MissingMethodException)
        {
            return null;
        }
    }

    public static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    public static long? Long(object? v) => v switch
    {
        null => null,
        string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
        IConvertible c => TryConvert(c),
        _ => null,
    };

    private static long? TryConvert(IConvertible c)
    {
        try { return Convert.ToInt64(c, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    /// <summary>WBEM_E_ACCESS_DENIED / E_ACCESSDENIED.</summary>
    public static bool IsAccessDenied(Exception ex)
    {
        var hr = Automation.HResult(ex);
        return hr is unchecked((int)0x80041003) or unchecked((int)0x80070005);
    }

    public void Dispose()
    {
        Automation.Release(_service);
        _service = null;
    }
}

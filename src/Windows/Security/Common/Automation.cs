using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OptimizationTool.Windows.Security.Common;

/// <summary>Late-bound IDispatch helpers for read-only COM automation objects (getters and query methods only).</summary>
internal static class Automation
{
    public static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    public static object? Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args, CultureInfo.InvariantCulture);

    public static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    /// <summary>Enumerates a COM collection via its _NewEnum (IEnumVARIANT), capped.</summary>
    public static IEnumerable<object> Items(object collection, int cap)
    {
        var n = 0;
        foreach (var item in (System.Collections.IEnumerable)collection)
        {
            if (n++ >= cap) yield break;
            if (item is not null) yield return item;
        }
    }

    public static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    /// <summary>The HRESULT of a COM failure surfaced through reflection, or the exception's own.</summary>
    public static int HResult(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner.HResult : ex.HResult;

    public static string Hex(int hr) => "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Reflection;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Broker.Reads;

/// <summary>Why a WMI read failed, as the adapters tell "not on this PC" from "could not read".</summary>
public enum ReadWmiError { NoNamespace, NoClass, AccessDenied, Other }

public sealed class ReadWmiException(ReadWmiError kind, string hresult) : Exception($"WMI read failed ({kind}, {hresult}).")
{
    public ReadWmiError Kind { get; } = kind;
    public string HResultText { get; } = hresult;
}

/// <summary>
/// The read seam of the administrator checks (m9 §17.2): a WQL query that returns only the named properties, capped,
/// and one read-only method on one instance. Fakes in tests; <see cref="WindowsReadWmi"/> in the helper only.
/// </summary>
public interface IReadWmi
{
    /// <summary>Rows of <paramref name="wql"/> with only <paramref name="props"/>, at most <paramref name="cap"/>. Throws <see cref="ReadWmiException"/>.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql, IReadOnlyList<string> props, int cap);

    /// <summary>
    /// One method on the instance of <paramref name="className"/> whose key <paramref name="keyProperty"/> is <paramref name="keyValue"/>;
    /// returns the named out parameters (and ReturnValue). Only the methods in <see cref="WindowsReadWmi.AllowedMethods"/> exist.
    /// </summary>
    IReadOnlyDictionary<string, object?> Call(string ns, string className, string keyProperty, string keyValue, string method,
        IReadOnlyDictionary<string, object?> args, IReadOnlyList<string> outProps);
}

/// <summary>
/// Late-bound WbemScripting (the WMI client, the only COM object used), read-only. REVIEW NOTE: no Put_, Delete_ or
/// any method outside <see cref="AllowedMethods"/> (all three are BitLocker getters that change nothing); connections
/// are per namespace and released on dispose. Written and reviewed; never run by tests and not run on the development PC.
/// </summary>
public sealed class WindowsReadWmi : IReadWmi, IDisposable
{
    /// <summary>The read-only methods the helper may call, by class.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedMethods = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Win32_EncryptableVolume"] = ["GetConversionStatus", "GetKeyProtectors", "GetKeyProtectorType"],
    };

    private const int WBEM_E_INVALID_NAMESPACE = unchecked((int)0x8004100E), WBEM_E_INVALID_CLASS = unchecked((int)0x80041010),
        WBEM_E_NOT_FOUND = unchecked((int)0x80041002), WBEM_E_ACCESS_DENIED = unchecked((int)0x80041003), E_ACCESSDENIED = unchecked((int)0x80070005);

    private readonly Dictionary<string, object> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql, IReadOnlyList<string> props, int cap) => Guard(() =>
    {
        var set = Automation.Call(Service(ns), "ExecQuery", wql, "WQL", 0x10)!; // wbemFlagReturnImmediately
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            foreach (var row in Automation.Items(set, cap))
                try
                {
                    var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in props) bag[p] = Prop(row, p);
                    rows.Add(bag);
                }
                finally { Automation.Release(row); }
        }
        finally { Automation.Release(set); }
        return rows;
    });

    public IReadOnlyDictionary<string, object?> Call(string ns, string className, string keyProperty, string keyValue, string method,
        IReadOnlyDictionary<string, object?> args, IReadOnlyList<string> outProps)
    {
        if (!AllowedMethods.TryGetValue(className, out var allowed) || !allowed.Contains(method))
            throw new InvalidOperationException("Method not allowed.");
        return Guard(() =>
        {
            var svc = Service(ns);
            var path = $"{className}.{keyProperty}=\"{keyValue.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
            object? cls = null, methods = null, def = null, inDef = null, inst = null, result = null;
            try
            {
                cls = Automation.Call(svc, "Get", className);
                methods = Automation.Get(cls!, "Methods_");
                def = Automation.Call(methods!, "Item", method);
                inDef = Automation.Get(def!, "InParameters");
                if (inDef is not null)
                {
                    inst = Automation.Call(inDef, "SpawnInstance_");
                    foreach (var (k, v) in args) SetProp(inst!, k, v);
                }
                result = inst is null ? Automation.Call(svc, "ExecMethod", path, method) : Automation.Call(svc, "ExecMethod", path, method, inst);
                var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in outProps.Append("ReturnValue")) bag[p] = Prop(result!, p);
                return (IReadOnlyDictionary<string, object?>)bag;
            }
            finally
            {
                Automation.Release(result);
                Automation.Release(inst);
                Automation.Release(inDef);
                Automation.Release(def);
                Automation.Release(methods);
                Automation.Release(cls);
            }
        });
    }

    private object Service(string ns)
    {
        lock (_gate)
        {
            if (_services.TryGetValue(ns, out var s)) return s;
            var locator = Automation.Create("WbemScripting.SWbemLocator");
            try { return _services[ns] = Automation.Call(locator, "ConnectServer", ".", ns)!; }
            finally { Automation.Release(locator); }
        }
    }

    private static T Guard<T>(Func<T> work)
    {
        try { return work(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidOperationException))
        {
            var hr = Automation.HResult(ex);
            var kind = hr switch
            {
                WBEM_E_INVALID_NAMESPACE => ReadWmiError.NoNamespace,
                WBEM_E_INVALID_CLASS or WBEM_E_NOT_FOUND => ReadWmiError.NoClass,
                WBEM_E_ACCESS_DENIED or E_ACCESSDENIED => ReadWmiError.AccessDenied,
                _ => ReadWmiError.Other,
            };
            throw new ReadWmiException(kind, Automation.Hex(hr));
        }
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
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException or MissingMethodException)
        {
            return null;
        }
    }

    private static void SetProp(object inst, string name, object? value)
    {
        var props = Automation.Get(inst, "Properties_")!;
        try
        {
            var p = Automation.Call(props, "Item", name)!;
            try { p.GetType().InvokeMember("Value", BindingFlags.SetProperty, null, p, [value], CultureInfo.InvariantCulture); }
            finally { Automation.Release(p); }
        }
        finally { Automation.Release(props); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _services.Values) Automation.Release(s);
            _services.Clear();
        }
    }
}

/// <summary>Value conversions shared by the adapters (WMI hands back strings, boxed integers or arrays).</summary>
internal static class ReadValues
{
    public static long? Long(object? v) => Sensors.Wmi.Long(v);
    public static string? Str(object? v) => Sensors.Wmi.Str(v);
    public static bool? Bool(object? v) => v switch { bool b => b, string s when bool.TryParse(s, out var p) => p, _ => null };

    /// <summary>Plain text: control and format characters become spaces, runs collapsed, trimmed, clipped; null when empty.</summary>
    public static string? Clean(string? s, int max)
    {
        if (s is null) return null;
        var chars = s.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray();
        var t = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (t.Length > max) t = t[..max].TrimEnd();
        return t.Length == 0 ? null : t;
    }

    /// <summary>The number of elements of a WMI array value (0 when not an array); the elements are never kept.</summary>
    public static int Count(object? v) => v is Array a ? a.Length : 0;

    public static IEnumerable<string> Strings(object? v) => v is Array a ? a.Cast<object?>().Select(Str).OfType<string>() : [];
}

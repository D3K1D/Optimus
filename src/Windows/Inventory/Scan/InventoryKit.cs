using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Inventory;

/// <summary>A row from a read-only Windows source (a WMI instance or a registry key), as name → value.</summary>
internal sealed class Bag : Dictionary<string, object?>
{
    public Bag() : base(StringComparer.OrdinalIgnoreCase) { }
    public Bag(IDictionary<string, object?> values) : base(values, StringComparer.OrdinalIgnoreCase) { }

    public string? Str(string key) => TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    public long? Long(string key) => TryGetValue(key, out var v) ? Sensors.Wmi.Long(v) : null;
}

/// <summary>
/// m9 §6.10 shared pieces of the inventory collectors: cleaning (nothing that identifies this PC is stored), the bounded
/// item list (merged duplicates with a count, a cap with the count left out), per-source status and the one evidence item.
/// </summary>
internal static class InventoryKit
{
    private static readonly Regex Mac = new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(?:\1[0-9A-Fa-f]{2}){4}(?![0-9A-Fa-f])|(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Sid = new(@"[-_ ]*\bS-1-\d+(?:-\d+){1,14}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>This PC's user, PC and domain names, which no stored field may name (<see cref="IdentityFilter"/>).</summary>
    public static IReadOnlyList<LocalIdentity> LocalIdentities() => IdentityFilter.Local();

    /// <summary>
    /// Display-safe text, or null when empty or when it could identify this machine: a backslash (a path or a device
    /// instance path), a MAC or IP address, or the user, PC or domain name under <see cref="IdentityFilter"/>. Never cut
    /// down to make it pass.
    /// </summary>
    /// <remarks>
    /// m9 review R4: then the never-store list for the field (<see cref="NeverStore"/>): a name or publisher by
    /// <see cref="NeverStore.RejectsName"/>, a version by <see cref="NeverStore.RejectsVersion"/>, a hardware model field
    /// (<paramref name="model"/>: read from a dedicated model property, serial-shaped by nature; review R4b) by
    /// <see cref="NeverStore.RejectsModel"/>.
    /// </remarks>
    public static string? Clean(string? raw, IReadOnlyList<LocalIdentity> identities, int max = 100, bool version = false, bool model = false)
    {
        var s = Text.Clean(raw, max);
        // A SID (task names often end in the user's) is removed, not kept: it names an account.
        if (s.Contains("S-1-", StringComparison.OrdinalIgnoreCase))
            try { s = Sid.Replace(s, " ").Trim(' ', '-', '_', '.'); }
            catch (RegexMatchTimeoutException) { return null; }
        if (s.Length == 0 || s.Contains('\\') || s.Contains('/') && s.Contains(':')) return null;
        try { if (Mac.IsMatch(s) || !version && Ipv4.IsMatch(s)) return null; }
        catch (RegexMatchTimeoutException) { return null; }
        // m9 review R4: the never-store list, field by field (a dotted 4-part version is not an IPv4 address).
        if (version ? NeverStore.RejectsVersion(s) : model ? NeverStore.RejectsModel(s) : NeverStore.RejectsName(s)) return null;
        return IdentityFilter.Contains(s, identities) ? null : s;
    }

    /// <summary>A version string (≤ 40 plain characters, the first token of "10.0.1 (WinBuild…)"), or null.</summary>
    public static string? Version(string? raw, IReadOnlyList<LocalIdentity> identities)
    {
        var s = Text.Clean(raw, 80);
        var space = s.IndexOf(' ');
        if (space > 0 && char.IsAsciiDigit(s[0])) s = s[..space];
        s = s.Trim().TrimEnd(',', ';');
        if (s.Length is 0 or > 40 || !s.Any(char.IsAsciiDigit)) return null;
        return Clean(s, identities, 40, version: true);
    }

    /// <summary>An item: the given fields, nulls left out.</summary>
    public static JsonObject Item(params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields)
            if (v is not null && !(v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0)) o[k] = v;
        return o;
    }

    public static long? Positive(long? v) => v is > 0 ? v : null;

    public static string Iso(DateTimeOffset? d) => d is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    /// <summary>A status for one source: ok | denied | unavailable | timed_out | failed.</summary>
    public static string StatusOf(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => "denied",
        _ when Sensors.Wmi.IsAccessDenied(ex) => "denied",
        PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException => "unavailable",
        System.Runtime.InteropServices.COMException c when c.HResult is unchecked((int)0x8004100E) or unchecked((int)0x80041010) or unchecked((int)0x80040154) => "unavailable", // invalid namespace / class, class not registered
        System.Reflection.TargetInvocationException { InnerException: { } inner } => StatusOf(inner),
        _ => "failed",
    };

    /// <summary>
    /// Runs one read-only source on its own thread under <paramref name="limit"/>; records its status. Null when the
    /// source failed, was denied or took too long (the thread is abandoned; it only reads).
    /// </summary>
    public static T? Source<T>(string name, TimeSpan limit, Dictionary<string, string> status, CancellationToken ct, Func<T> read) where T : class
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = Sensors.Bounded.Run(read, limit, ct, out var timedOut);
            status[name] = timedOut ? "timed_out" : "ok";
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { status[name] = StatusOf(ex); return null; }
    }

    /// <summary>The single evidence item of an area (declared local: it never leaves through the AI's read tools).</summary>
    public static Fact Fact(string area, string source, ItemList items, IReadOnlyDictionary<string, string> sources, string display, JsonObject? extra = null)
    {
        var value = new JsonObject
        {
            ["area"] = area,
            ["items"] = new JsonArray(items.Items.Select(i => (JsonNode?)i).ToArray()),
            ["total"] = items.Total,
            ["omitted"] = items.Omitted,
            ["sources"] = new JsonObject(sources.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
        foreach (var (k, v) in extra ?? []) value[k] = v?.DeepClone();
        return new Fact(ScanInventory.Subject(area), source, "reported", value, display, null, Sensitivity: "local");
    }

    /// <summary>
    /// The coverage receipt: complete when every source answered and nothing was capped; a source that needs
    /// administrator rights, is missing on this PC or took too long is a gap (partial), never an error. Nothing read at
    /// all → denied (every source refused) or failed.
    /// </summary>
    public static CollectorResult Result(Fact? fact, IReadOnlyDictionary<string, string> sources, IReadOnlyDictionary<string, string> labels, ItemList items, IEnumerable<string>? more = null)
    {
        var reasons = new List<string>();
        foreach (var (name, status) in sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var label = labels.TryGetValue(name, out var l) ? l : name;
            switch (status)
            {
                case "ok": break;
                case "denied": reasons.Add($"{label} need administrator rights."); break;
                case "unavailable": reasons.Add($"{label} are not available on this PC."); break;
                case "timed_out": reasons.Add($"{label} took too long."); break;
                default: reasons.Add($"{label} could not be read."); break;
            }
        }
        reasons.AddRange(more ?? []);
        if (items.Omitted > 0) reasons.Add($"Capped at {items.Cap} items; {items.Omitted} left out.");
        var anyOk = sources.Values.Any(s => s == "ok");
        string status2;
        if (!anyOk && items.Total == 0)
            status2 = sources.Count > 0 && sources.Values.All(s => s == "denied") ? "denied" : sources.Values.All(s => s == "unavailable") ? "unsupported" : "failed";
        else status2 = reasons.Count == 0 ? "complete" : "partial";
        var facts = fact is null || status2 is "denied" or "failed" or "unsupported" ? [] : new List<Fact> { fact };
        return new CollectorResult(facts, new CoverageReceipt(status2, facts.Count == 0 ? 0 : items.Total, reasons.Count == 0 ? null : string.Join(" ", reasons)));
    }

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString(CultureInfo.InvariantCulture)} {many}";
}

/// <summary>
/// The items of one area, in the order added: duplicates (same kind, name, publisher, version and model, ignoring case; a
/// memory module's part number is its model, review R4c) merge into the first with a count; past <see cref="Cap"/> distinct items the rest are counted in <see cref="Omitted"/>.
/// </summary>
internal sealed class ItemList(int cap)
{
    private readonly List<JsonObject> _items = [];
    private readonly Dictionary<string, JsonObject> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _omitted = new(StringComparer.OrdinalIgnoreCase);

    public int Cap { get; } = cap;
    public IReadOnlyList<JsonObject> Items => _items;
    public int Omitted => _omitted.Count;
    /// <summary>Distinct items found (stored plus left out).</summary>
    public int Total => _items.Count + _omitted.Count;

    public void Add(JsonObject item)
    {
        if (item["name"] is null) return;
        var key = string.Join('\0', new[] { "kind", "source", "name", "publisher", "version", "model" }.Select(k => item[k]?.ToString() ?? ""));
        if (_byKey.TryGetValue(key, out var hit))
        {
            hit["count"] = (hit["count"]?.GetValue<int>() ?? 1) + (item["count"]?.GetValue<int>() ?? 1);
            return;
        }
        if (_items.Count >= Cap) { _omitted.Add(key); return; }
        _byKey[key] = item;
        _items.Add(item);
    }
}

/// <summary>What a file says about itself (its version resource): company, version, product. Read-only.</summary>
internal sealed record FileVersionFacts(string? Company, string? Version, string? Product);

internal static class FileVersions
{
    /// <summary>The version resource of a local file, or null when it has none or cannot be read.</summary>
    public static FileVersionFacts? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            var facts = new FileVersionFacts(N(info.CompanyName), N(info.ProductVersion) ?? N(info.FileVersion), N(info.ProductName) ?? N(info.FileDescription));
            return facts.Company is null && facts.Version is null && facts.Product is null ? null : facts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FileNotFoundException) { return null; }
    }
}

/// <summary>
/// Base for the inventory collectors (m9 §6.10): <c>read</c> is the seam (Windows calls → a raw DTO; tests pass a fake),
/// <see cref="Normalize"/> is pure. Category misc, so a full scan runs them and a targeted one does not. Registered last,
/// so they never hold a slot the audit's own checks wait for.
/// </summary>
internal abstract class InventoryCollector<TRaw>(Func<CancellationToken, TRaw> read, IReadOnlyList<LocalIdentity>? identities) : ICollector
{
    public abstract string Area { get; }
    public string Id => ScanInventory.CollectorId(Area);
    public string Version => "1.0.0";
    public string Category => "misc";
    public abstract TimeSpan Timeout { get; }
    public abstract string GapTitle { get; }
    public string Label => GapTitle;
    public string GapSource => "Windows inventory";
    public string GapDisplay => $"{GapTitle} could not be read.";

    /// <summary>COM sources (WMI, Task Scheduler, shortcuts) read on an STA thread; others on the pool.</summary>
    protected virtual bool Sta => false;

    protected IReadOnlyList<LocalIdentity> Identities { get; } = identities ?? InventoryKit.LocalIdentities();

    public async Task<CollectorResult> CollectAsync(CancellationToken ct)
    {
        TRaw raw;
        try
        {
            raw = Sta
                ? await StaWorker.Run(() => read(ct), Id, ct).ConfigureAwait(false)
                : await Task.Run(() => read(ct), ct).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (InventoryKit.StatusOf(ex) == "denied") { return new([], new CoverageReceipt("denied", 0, "Windows needs administrator rights for this list.")); }
        catch (Exception ex) { return new([], new CoverageReceipt("failed", 0, ex.GetType().Name)); }
        ct.ThrowIfCancellationRequested();
        return Normalize(raw);
    }

    internal abstract CollectorResult Normalize(TRaw raw);
}

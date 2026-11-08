using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

/// <summary>What the projection left out, for the gateway to report (e.g. review.submit "dropped").</summary>
public sealed class ProjectionStats
{
    private int _projected, _itemsDropped, _fieldsDropped;

    /// <summary>Items that were shared.</summary>
    public int ItemsProjected => Volatile.Read(ref _projected);

    /// <summary>Items never shared: local sensitivity, unknown collector or shape, or failed checks.</summary>
    public int ItemsDropped => Volatile.Read(ref _itemsDropped);

    /// <summary>Fields present in the evidence that failed a type, range, pattern or sensitivity check.</summary>
    public int FieldsDropped => Volatile.Read(ref _fieldsDropped);

    internal void Projected() => Interlocked.Increment(ref _projected);
    internal void DropItem() => Interlocked.Increment(ref _itemsDropped);
    internal void DropField() => Interlocked.Increment(ref _fieldsDropped);

    public JsonObject ToJson() => new() { ["projected"] = ItemsProjected, ["itemsDropped"] = ItemsDropped, ["fieldsDropped"] = FieldsDropped };
}

/// <summary>
/// The only path by which evidence leaves the PC (contracts/m3-agent.md §3.5). Dispatches by collector ID to an
/// allowlist projector; everything not explicitly allowlisted stays on the PC. Never throws on evidence content.
/// </summary>
public static class EvidenceProjection
{
    /// <summary>Bumped whenever what a projector may share changes (frozen into review previews).</summary>
    public const string RedactionVersion = "1";

    /// <summary>Fixture collectors (M1 simulated audit, website demo) share one generic projector.</summary>
    public const string FixturePrefix = "fixture.";

    private static readonly Projector[] All =
    [
        new ListenersProjector(), new StartupProjector(), new DefenderProjector(), new FirewallProjector(),
        new SecureBootProjector(), new UacProjector(), new WscProjector(),
        new OsProjector(), new HardwareProjector(), new VolumesProjector(), new PowerProjector(), new PerfProjector(),
        new AppsProjector(), new DevicesProjector(), new DisplayProjector(), new EventsProjector(),
        new DiagDataProjector(), new StartSuggestionsProjector(),
    ];

    private static readonly FixtureProjector Fixture = new();

    private static readonly IReadOnlyDictionary<string, Projector> ById =
        All.SelectMany(p => p.CollectorIds.Select(id => (id, p))).ToDictionary(x => x.id, x => x.p, StringComparer.Ordinal);

    private static readonly Regex IdRx = new(@"^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex SubjectRx = new(@"^[a-z0-9][a-z0-9._-]{0,159}$", RegexOptions.CultureInvariant);

    /// <summary>Every non-fixture collector ID with a projector.</summary>
    public static IReadOnlyCollection<string> CollectorIds { get; } = ById.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>The projector for a collector ID, or null (its evidence is never shared).</summary>
    public static IEvidenceProjector? For(string collectorId) =>
        ById.TryGetValue(collectorId, out var p) ? p : collectorId.StartsWith(FixturePrefix, StringComparison.Ordinal) ? Fixture : null;

    /// <summary>Projects an item whose quality and sensitivity are unknown (quality falls back to the projector's).</summary>
    public static ProjectedEvidence? Project(EvidenceItem item, IAliasMap aliases, ShareOptions share, ProjectionStats? stats = null) =>
        Project(item, aliases, share, quality: null, sensitivity: null, stats);

    /// <summary>
    /// Projects one evidence item, or returns null when it must not leave the PC: <paramref name="sensitivity"/>
    /// "local", an unknown collector or value shape, or an invalid ID/time. <paramref name="quality"/> is the stored
    /// evidence quality (schema enum); anything else falls back to the projector's default.
    /// </summary>
    public static ProjectedEvidence? Project(EvidenceItem item, IAliasMap aliases, ShareOptions share, string? quality, string? sensitivity, ProjectionStats? stats = null)
    {
        stats ??= new ProjectionStats();
        var result = TryProject(item, aliases, share, quality, sensitivity, stats);
        if (result is null) stats.DropItem(); else stats.Projected();
        return result;
    }

    /// <summary>Projects a stored evidence body (the evidence table / evidence.get shape), honouring its sensitivity and quality.</summary>
    public static ProjectedEvidence? ProjectStored(JsonObject body, IAliasMap aliases, ShareOptions share, ProjectionStats? stats = null)
    {
        static string S(JsonNode? n) => Read.Str(n) ?? "";
        var item = new EvidenceItem(S(body["id"]), S(body["subject"]), S(body["collectorId"]), S(body["category"]),
            body["value"]?.DeepClone() ?? new JsonObject(), "", S(body["observedAt"]));
        return Project(item, aliases, share, Read.Str(body["quality"]), Read.Str(body["sensitivity"]) ?? "local", stats);
    }

    /// <summary>
    /// Projects a stored evidence body using the collector-declared sensitivity (Fact.Sensitivity) instead of the
    /// body's stored value, which may be engine-inferred. Only a declared "local" withholds the item; null (not
    /// declared) or "public" goes through the projector allowlist, which never shares paths.
    /// </summary>
    public static ProjectedEvidence? ProjectStored(JsonObject body, IAliasMap aliases, ShareOptions share, string? declaredSensitivity, ProjectionStats? stats = null)
    {
        static string S(JsonNode? n) => Read.Str(n) ?? "";
        var item = new EvidenceItem(S(body["id"]), S(body["subject"]), S(body["collectorId"]), S(body["category"]),
            body["value"]?.DeepClone() ?? new JsonObject(), "", S(body["observedAt"]));
        return Project(item, aliases, share, Read.Str(body["quality"]), declaredSensitivity ?? "public", stats);
    }

    private static ProjectedEvidence? TryProject(EvidenceItem item, IAliasMap aliases, ShareOptions share, string? quality, string? sensitivity, ProjectionStats stats)
    {
        if (item is null || aliases is null || share is null) return null;
        if (sensitivity is not null && sensitivity != "public") return null;    // "local" (or anything unrecognized) stays on the PC
        if (item.Id is null || !IdRx.IsMatch(item.Id)) return null;
        var observedAt = Out.ParseTime(item.ObservedAt);
        if (observedAt is null) return null;
        if (item.CollectorId is null || For(item.CollectorId) is not Projector projector) return null;
        if (item.Value is not JsonObject value || item.Subject is null) return null;

        ProjectedEvidence? p;
        try
        {
            var o = new Out(aliases, share, stats);
            var mapped = projector.Map(item.CollectorId, item.Subject, value, o);
            if (mapped is null) return null;
            p = new ProjectedEvidence(item.Id, mapped.Value.Subject, projector.Source(item.CollectorId), observedAt,
                quality is "measured" or "reported" or "vendor_reported" or "estimated" or "unavailable" ? quality : mapped.Value.Quality,
                o.Finish(), mapped.Value.Display);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException
                                       or OverflowException or NullReferenceException or IndexOutOfRangeException or RegexMatchTimeoutException)
        {
            return null;
        }

        if (!SubjectRx.IsMatch(p.Subject)) return null;
        // Last line: no string leaf (or key) that looks like a path, address or credential survives, even if a
        // projector let one through. The display is regenerated text; if it trips the check it becomes generic.
        Scrub(p.Value, stats);
        var display = p.Display;
        if (display.Length > 280 || Sanitize.LooksSensitive(display)) { stats.DropField(); display = "Details are in the value fields."; }
        return p with { Display = display };
    }

    private static readonly Regex PlainVersion = new(@"^\d{1,9}(?:\.\d{1,9}){0,4}$", RegexOptions.CultureInvariant);

    private static void Scrub(JsonNode? node, ProjectionStats stats)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (Sanitize.LooksSensitive(key)) { o.Remove(key); stats.DropField(); continue; }
                    if (Bad(o[key])) { o.Remove(key); stats.DropField(); continue; }
                    Scrub(o[key], stats);
                }
                break;
            case JsonArray a:
                for (var i = a.Count - 1; i >= 0; i--)
                {
                    if (Bad(a[i])) { a.RemoveAt(i); stats.DropField(); continue; }
                    Scrub(a[i], stats);
                }
                break;
        }
    }

    private static bool Bad(JsonNode? n)
    {
        if (Read.Str(n) is not { } s) return false;
        if (s.Length > 160) return true;
        if (PlainVersion.IsMatch(s)) return Sanitize.LooksSensitiveVersion(s);
        if (Out.ParseTime(s) is not null) return false;
        return Sanitize.LooksSensitive(s);
    }
}

/// <summary>Result of mapping one evidence value: the safe subject key, regenerated display and default quality.</summary>
internal readonly record struct Mapped(string Subject, string Display, string Quality = "measured");

/// <summary>Base for the per-collector allowlist projectors.</summary>
internal abstract class Projector : IEvidenceProjector
{
    public abstract IReadOnlyCollection<string> CollectorIds { get; }

    /// <summary>Fixed source label (never the evidence's own source text).</summary>
    public abstract string Source(string collectorId);

    /// <summary>Maps the value through the allowlist into <paramref name="o"/>; null = never share this item.</summary>
    public abstract Mapped? Map(string collectorId, string subject, JsonObject v, Out o);

    public ProjectedEvidence? Project(EvidenceItem item, IAliasMap aliases, ShareOptions share)
    {
        if (item is null || !CollectorIds.Contains(item.CollectorId) && this is not FixtureProjector) return null;
        return EvidenceProjection.Project(item, aliases, share);
    }

    /// <summary>Last path segment of a normalized path (e.g. "%ProgramFiles%\Foo\foo.exe" → "foo.exe").</summary>
    protected static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var t = path.Trim();
        var i = t.LastIndexOfAny(['\\', '/']);
        var f = i >= 0 ? t[(i + 1)..] : t;
        return f.Length == 0 ? null : f;
    }

    protected static string Slug(string? alias) => alias is null ? "unknown" : AliasMap.Slug(alias);
}

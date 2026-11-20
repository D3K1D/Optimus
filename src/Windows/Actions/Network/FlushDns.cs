using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Network;

/// <summary>network.flush_dns's seam: one call to Windows' resolver cache. Tests use a fake; no test may call the real one.</summary>
internal interface IDnsCache
{
    /// <summary>True when Windows reports the cache cleared.</summary>
    bool Flush();
}

/// <summary>Real implementation: DnsFlushResolverCache (dnsapi.dll), what "ipconfig /flushdns" calls. Written, never run by the tests.</summary>
internal sealed partial class WindowsDnsCache : IDnsCache
{
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();

    public bool Flush() => DnsFlushResolverCache();
}

/// <summary>
/// network.flush_dns (contracts/m9-ai-powers.md §3.2): clears Windows' DNS resolver cache. There is nothing to read back
/// and nothing to restore, so it is modelled like the other one-shot operations (restore_point.create): kind "provider",
/// no undo, the operation ends "completed" with result "flushed" when Windows reports success. Not a fix step.
/// </summary>
internal sealed class FlushDnsHandler(IDnsCache dns) : IActionHandler
{
    public const string Id = "network.flush_dns";
    internal const string Resource = "dns:cache";

    public string ActionId => Id;
    public string Version => "1.1.0";
    public string Label => "Clear the DNS cache";
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public bool NothingToUndo => true;
    public string Kind => "provider";
    public string Group => "network";
    public string What => "Clears the list of website addresses Windows remembers, so it looks them up again; nothing else changes.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    /// <summary>Windows reports the cache cleared; there is nothing to read back, so it is completed, not verified.</summary>
    public bool ReportsOnly => true;

    internal static JsonObject Target() => new() { ["type"] = "dns_cache", ["id"] = "system", ["label"] = "DNS cache" };

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.FromResult(Resolve(request));

    /// <summary>Nothing to read: the cache's contents are not a state Optimus compares.</summary>
    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => Task.Run(() => Execute(), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;

    internal static ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");
        if (request.Target is { Count: > 0 } t && !JsonNode.DeepEquals(t, Target())) return ActionResolution.Ineligible("invalid_arguments", "This change takes no target.");
        if (request.Undo) return ActionResolution.Ineligible("not_supported", "Clearing the DNS cache can't be undone, and needs no undo.");
        return new ActionResolution(
            Eligible: true, Target: Target(), Before: new JsonObject(), After: new JsonObject { ["result"] = "flushed" },
            Rows: [new PlanRow("DNS cache", "Kept", "Cleared")],
            Context: [new("Applies to", "This PC")],
            Tradeoffs: ["The first visit to each site takes a moment longer while Windows looks it up again."],
            UndoConditions: null,
            Resources: [Resource]);
    }

    internal ActionOutcome Execute()
    {
        bool ok;
        try { ok = dns.Flush(); }
        catch (Exception) { ok = false; }
        return ok
            ? new ActionOutcome("completed", new JsonObject { ["result"] = "flushed" }, "Windows cleared its DNS cache. Nothing else was changed.")
            : new ActionOutcome("failed", new JsonObject { ["result"] = "failed" }, "Windows didn't clear its DNS cache. Nothing was changed.", "flush_failed");
    }
}

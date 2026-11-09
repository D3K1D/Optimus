using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// The one per-user lease shared by work that loads the PC or changes what it measures (contracts/m6-benchmark.md §2):
/// scans, benchmarks, sensors.snapshot and executing performance-affecting actions (power.select_scheme, defender.*).
/// Whoever comes second gets a busy result. Acquire and release are atomic under one lock, so the lease is symmetric:
/// a scan blocks a benchmark exactly as a benchmark blocks a scan.
/// </summary>
public sealed class WorkLease
{
    public const string Scan = "scan", Bench = "bench", Action = "action", Snapshot = "snapshot";

    /// <summary>Who holds the lease: <see cref="Kind"/> is scan|bench|action|snapshot, <see cref="Id"/> its record ID.</summary>
    public sealed record Holder(string Kind, string Id);

    private readonly object _gate = new();
    private Holder? _current;

    public Holder? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Takes the lease, or returns the current holder (the lease is not taken).</summary>
    public Holder? TryAcquire(string kind, string id)
    {
        lock (_gate)
        {
            if (_current is not null) return _current;
            _current = new Holder(kind, id);
            return null;
        }
    }

    /// <summary>Takes the lease or throws the busy error for its current holder.</summary>
    public void Acquire(string kind, string id)
    {
        if (TryAcquire(kind, id) is { } busy) throw BusyError(busy);
    }

    /// <summary>Releases the lease if this holder has it (a stale release is ignored).</summary>
    public void Release(string kind, string id)
    {
        lock (_gate)
            if (_current is { } c && c.Kind == kind && c.Id == id) _current = null;
    }

    /// <summary>Renames the holder's record ID (an action's lease moves from its plan to its operation).</summary>
    public void Rename(string kind, string oldId, string newId)
    {
        lock (_gate)
            if (_current is { } c && c.Kind == kind && c.Id == oldId) _current = c with { Id = newId };
    }

    /// <summary>
    /// The busy error for a holder (§2): -32004 scan_active {scanId}, -32013 {reason:"bench_active", runId},
    /// -32013 {reason:"action_active", operationId}, -32013 {reason:"snapshot_active"}.
    /// </summary>
    public static RpcError BusyError(Holder h) => h.Kind switch
    {
        Scan => new RpcError(ErrorCodes.ScanActive, "A scan is already running.", new JsonObject { ["scanId"] = h.Id }),
        Bench => new RpcError(ErrorCodes.InvalidState, "A benchmark is running.", new JsonObject { ["reason"] = "bench_active", ["runId"] = h.Id }),
        Action => new RpcError(ErrorCodes.InvalidState, "A change is being applied.", new JsonObject { ["reason"] = "action_active", ["operationId"] = h.Id }),
        _ => new RpcError(ErrorCodes.InvalidState, "A sensor reading is running.", new JsonObject { ["reason"] = "snapshot_active" }),
    };

    /// <summary>Actions whose execution takes the lease: power.select_scheme and the Defender scan and update actions.</summary>
    public static bool AffectsPerformance(string actionId) =>
        actionId == "power.select_scheme" || actionId.StartsWith("defender.", StringComparison.Ordinal);
}

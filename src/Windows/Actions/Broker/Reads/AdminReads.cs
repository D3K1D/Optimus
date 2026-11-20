using System.Text.Json.Nodes;

namespace OptimizationTool.Windows.Actions.Broker.Reads;

/// <summary>What one check is asked: its window in days (already resolved against the catalog) and the time "now".</summary>
public sealed record AdminReadInput(int? WindowDays, DateTimeOffset Now);

/// <summary>
/// One adapter's answer, before the helper shapes it. State ok | failed | unsupported. <see cref="Total"/>: how many
/// items matched (may be more than <see cref="Items"/> when the adapter keeps only the newest); <see cref="Complete"/>:
/// false when the adapter knows it could not read everything (a per-item method failed, a row cap was hit, another
/// antivirus is in charge, a disk without counters). "Nothing found" is ok with no items and Complete true.
/// </summary>
public sealed record AdminReadOutput(string State, string? Code, IReadOnlyList<JsonObject> Items, int Total, bool Complete)
{
    public static AdminReadOutput Ok(IReadOnlyList<JsonObject> items, int total, bool complete, string? code = null) => new("ok", code, items, total, complete);
    public static AdminReadOutput Failed(string code) => new("failed", code, [], 0, false);
    public static AdminReadOutput Unsupported(string code) => new("unsupported", code, [], 0, false);

    /// <summary>A WMI failure as a result: not on this PC → unsupported; denied → failed access_denied; else failed read_error.</summary>
    public static AdminReadOutput From(ReadWmiException ex, string unsupportedCode) => ex.Kind switch
    {
        ReadWmiError.NoNamespace or ReadWmiError.NoClass => Unsupported(unsupportedCode),
        ReadWmiError.AccessDenied => Failed("access_denied"),
        _ => Failed("read_error"),
    };
}

/// <summary>
/// One administrator check compiled into the helper (m9 §17.2): one fixed query, a bounded window where it has one, an
/// exact field list (Engine.Agent.AdminChecks) that the helper enforces after this returns. Each adapter carries its own
/// REVIEW NOTE. <see cref="Version"/> is the adapter's own constant; a test asserts it equals the catalog's.
/// </summary>
public interface IAdminReadAdapter
{
    string Id { get; }
    string Version { get; }
    AdminReadOutput Read(AdminReadInput input, CancellationToken ct);
}

/// <summary>The helper's read registry: every adapter compiled into it. Host ↔ helper parity is asserted on this list.</summary>
public static class BrokerReads
{
    public static IReadOnlyList<IAdminReadAdapter> Create(IReadWmi wmi) => [new DefenderHistoryRead(wmi), new EncryptionRead(wmi), new StorageHealthRead(wmi)];
}

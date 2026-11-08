using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

/// <summary>
/// Shared shape for this domain's collectors: <see cref="Read"/> touches Windows and returns a raw DTO on a worker
/// thread; <see cref="Normalize"/> is pure (raw → facts + coverage) and is what unit tests exercise.
/// </summary>
internal abstract class SystemCollector<TRaw>(CollectorOptions options) : ICollector
{
    protected CollectorOptions Options { get; } = options;

    public abstract string Id { get; }
    public string Version => "1.0.0";
    public abstract string Category { get; }
    public virtual string Label => Id;
    public virtual TimeSpan Timeout => Options.DefaultTimeout;
    public abstract string GapTitle { get; }
    public abstract string GapSource { get; }
    public virtual string GapDisplay => $"{GapTitle} was unavailable.";

    protected abstract TRaw Read(CancellationToken ct);

    public virtual async Task<CollectorResult> CollectAsync(CancellationToken ct)
    {
        try
        {
            var raw = await Task.Run(() => Read(ct), ct).ConfigureAwait(false);
            return Normalize(raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new([], new("denied", 0, "access denied")); }
        catch (Exception ex) { return new([], new("failed", 0, ex.GetType().Name)); }
    }

    public abstract CollectorResult Normalize(TRaw raw);
}

internal static class Sys
{
    /// <summary>First 8 hex of SHA-256 of the normalized (trimmed, lower-case) identity.</summary>
    public static string Hash8(string identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim().ToLowerInvariant())))[..8];

    public static CollectorResult Result(List<Fact> facts, string status, string? reason)
    {
        facts.Sort((a, b) => string.CompareOrdinal(a.Subject, b.Subject));
        return new(facts, new CoverageReceipt(status, facts.Count, reason));
    }

    public static string Gib(double bytes) => (bytes / (1L << 30)).ToString(bytes >= 10L * (1L << 30) ? "0" : "0.#", global::System.Globalization.CultureInfo.InvariantCulture);

    public static double Round(double v, int digits = 1) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    public static string? RegString(RegistryKey? key, string name) =>
        key?.GetValue(name) switch { string s when s.Trim().Length > 0 => s.Trim(), _ => null };

    public static long? RegNumber(RegistryKey? key, string name) => key?.GetValue(name) switch
    {
        int i => (uint)i,
        long l => l,
        byte[] b when b.Length == 8 => BitConverter.ToInt64(b),
        byte[] b when b.Length == 4 => BitConverter.ToUInt32(b),
        _ => null,
    };

    public static JsonArray Array<T>(IEnumerable<T> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
}

namespace OptimizationTool.Engine.Actions;

/// <summary>
/// Canonical resource keys (contracts/m9-ai-powers.md §3.1): "<namespace>:<rest>", e.g.
/// <c>reg:64:HKCU\SOFTWARE\…\NAME</c>, <c>service:NAME</c>, <c>task:\PATH</c>, <c>power:GUID:SETTING</c>, <c>fw:rule:ID</c>.
/// Keys are hierarchical: a key conflicts with itself, its parents and its children, where a parent is a prefix that
/// ends on a separator boundary (':', '\' or '/'). Compared without case (Windows names are case-insensitive).
/// </summary>
public static class ResourceKeys
{
    private const int MaxLength = 512;
    private static readonly char[] Separators = [':', '\\', '/'];

    /// <summary>A well-formed key: a lower-case namespace, ':', a non-empty rest; no control characters or whitespace edges.</summary>
    public static bool Valid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength || key.Trim() != key) return false;
        var colon = key.IndexOf(':');
        if (colon <= 0 || colon == key.Length - 1) return false;
        for (var i = 0; i < colon; i++)
            if (key[i] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return !key.Any(char.IsControl);
    }

    /// <summary>True when the two keys name the same object or one contains the other.</summary>
    public static bool Conflict(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
               && (Separators.Contains(longer[shorter.Length]) || Separators.Contains(shorter[^1]));
    }

    /// <summary>Distinct keys in a stable order (for the plan and its hash).</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> keys) =>
        keys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// The executor's lock table: an owner (a plan being approved, or its running operation) holds a set of resource keys.
/// A set is taken atomically and never waited on: if any key conflicts with a held key, nothing is taken.
/// </summary>
internal sealed class ResourceLocks
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _held = [];
    /// <summary>The owner holding everything (an approved script, m9 §5.1 rule 8), or null.</summary>
    private string? _exclusive;

    /// <summary>
    /// Takes the whole table for <paramref name="owner"/> (a script conflicts with every other action execution): only when
    /// nothing is held. Returns false and the first other owner when busy.
    /// </summary>
    public bool TryAcquireExclusive(string owner, out string? holder)
    {
        lock (_gate)
        {
            holder = _exclusive ?? _held.Keys.FirstOrDefault();
            if (holder is not null) return false;
            _exclusive = owner;
            _held[owner] = [];
            return true;
        }
    }

    /// <summary>Takes every key for <paramref name="owner"/>, or none and returns false.</summary>
    public bool TryAcquire(string owner, IReadOnlyList<string> keys)
    {
        lock (_gate)
        {
            if (_held.ContainsKey(owner) || _exclusive is not null) return false;
            foreach (var held in _held.Values)
                foreach (var h in held)
                    foreach (var k in keys)
                        if (ResourceKeys.Conflict(h, k)) return false;
            _held[owner] = keys;
            return true;
        }
    }

    public void Release(string owner)
    {
        lock (_gate)
        {
            _held.Remove(owner);
            if (_exclusive == owner) _exclusive = null;
        }
    }
}

namespace OptimizationTool.Windows.Sensors;

/// <summary>One instance's formatted value and its PDH CStatus.</summary>
internal readonly record struct PdhItem(string Instance, uint Status, double Value)
{
    public bool Valid => Status is Native.PdhCstatusValidData or Native.PdhCstatusNewData && double.IsFinite(Value);
}

/// <summary>A PDH query with wildcard counters added by English name. Not thread-safe; used from one runner thread.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private const int ItemCap = 4096;
    private nint _query;
    private readonly List<nint> _counters = [];

    public PdhQuery(IReadOnlyList<string> englishPaths)
    {
        var rc = Native.PdhOpenQueryW(null, 0, out _query);
        if (rc != 0) throw new PdhException(rc);
        foreach (var p in englishPaths)
        {
            rc = Native.PdhAddEnglishCounterW(_query, p, 0, out var c);
            if (rc != 0) { Dispose(); throw new PdhException(rc); }
            _counters.Add(c);
        }
    }

    public void Collect()
    {
        ObjectDisposedException.ThrowIf(_query == 0, this);
        var rc = Native.PdhCollectQueryData(_query);
        if (rc != 0) throw new PdhException(rc);
    }

    /// <summary>Formatted values for counter <paramref name="index"/> (double, no cap at 100).</summary>
    public List<PdhItem> Items(int index)
    {
        ObjectDisposedException.ThrowIf(_query == 0, this);
        var counter = _counters[index];
        const uint format = Native.PdhFmtDouble | Native.PdhFmtNoCap100;
        uint size = 0;
        var rc = Native.PdhGetFormattedCounterArrayW(counter, format, ref size, out _, null);
        if (rc != Native.PdhMoreData) throw new PdhException(rc);
        if (size == 0 || size > 4u << 20) throw new PdhException(rc);
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            rc = Native.PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, p);
            if (rc != 0) throw new PdhException(rc);
            var items = (Native.PdhFmtCounterValueItem*)p;
            var list = new List<PdhItem>((int)Math.Min(count, ItemCap));
            for (var i = 0; i < count && i < ItemCap; i++)
            {
                var name = items[i].Name == null ? "" : new string(items[i].Name);
                list.Add(new PdhItem(name, items[i].Value.CStatus, items[i].Value.DoubleValue));
            }
            return list;
        }
    }

    public void Dispose()
    {
        var q = Interlocked.Exchange(ref _query, 0); // one closer, whoever calls
        if (q != 0) Native.PdhCloseQuery(q);
    }
}

internal sealed class PdhException(uint status) : Exception($"pdh 0x{status:x8}")
{
    public uint Status { get; } = status;
}

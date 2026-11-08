using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability;

/// <summary>One event channel the domain reads. <see cref="InfoIds"/> null = all levels; otherwise critical/error/warning plus these IDs.</summary>
internal sealed record EventChannelSpec(string CollectorId, string Short, string Channel, string GapTitle, IReadOnlyList<int>? InfoIds)
{
    public static readonly EventChannelSpec System = new("win.events.system", "system", "System", "System events", [12, 13, 19, 1074, 6005, 6006]);
    public static readonly EventChannelSpec Application = new("win.events.application", "application", "Application", "App events", [1001]);
    public static readonly EventChannelSpec Update = new("win.events.update", "update", "Microsoft-Windows-WindowsUpdateClient/Operational", "Update history", null);
    public static readonly EventChannelSpec Defender = new("win.events.defender", "defender", "Microsoft-Windows-Windows Defender/Operational", "Defender events", null);

    public string XPath(TimeSpan lookback)
    {
        var time = $"TimeCreated[timediff(@SystemTime) <= {(long)lookback.TotalMilliseconds}]";
        if (InfoIds is null) return $"*[System[{time}]]";
        var levels = string.Join(" or ", new[] { "Level=1", "Level=2", "Level=3" }.Concat(InfoIds.Select(i => $"EventID={i}")));
        return $"*[System[({levels}) and {time}]]";
    }
}

/// <summary>Structured fields of one event (EventData only; never the rendered message).</summary>
internal sealed record RawEvent(string Provider, int EventId, int Level, DateTimeOffset Time, IReadOnlyList<KeyValuePair<string?, string>> Data)
{
    public string? Field(string name, int index)
    {
        foreach (var d in Data) if (string.Equals(d.Key, name, StringComparison.OrdinalIgnoreCase)) return d.Value;
        return index >= 0 && index < Data.Count && Data.All(d => d.Key is null) ? Data[index].Value : null;
    }
}

/// <summary>Status: ok | missing | disabled | denied | failed. <see cref="Unreadable"/> = rows that could not be parsed.</summary>
internal sealed record RawChannel(string Status, string? Reason, IReadOnlyList<RawEvent> Events, bool RowCapHit, bool Interrupted, int Unreadable);

internal sealed class EventsCollector(EventChannelSpec spec, CollectorOptions options) : ReliabilityCollector(options)
{
    public EventChannelSpec Spec => spec;
    public override string Id => spec.CollectorId;
    public override string Category => "diagnostics";
    public override string GapTitle => spec.GapTitle;
    public override string GapSource => "Event Log";

    protected override CollectorResult Collect(CancellationToken ct)
    {
        var raw = EventReader.Read(spec.Channel, spec.XPath(Options.EventLookback), Options.EventRowCap, ct);
        return EventNormalizer.Normalize(spec, raw, Options.EventLookback, Options.EventRowCap);
    }
}

internal static class EventReader
{
    private const string Ns = "{http://schemas.microsoft.com/win/2004/08/events/event}";

    public static RawChannel Read(string channel, string xpath, int cap, CancellationToken ct)
    {
        switch (ChannelEnabled(channel))
        {
            case (false, Native.ERROR_EVT_CHANNEL_NOT_FOUND or Native.ERROR_EVT_INVALID_CHANNEL_PATH): return Empty("missing", "Channel not found.");
            case (false, 0): return Empty("disabled", "Channel disabled.");
        }

        var query = Native.EvtQuery(IntPtr.Zero, channel, xpath, Native.EvtQueryChannelPath | Native.EvtQueryReverseDirection);
        if (query == IntPtr.Zero) return FromError(Marshal.GetLastWin32Error());

        var events = new List<RawEvent>();
        bool capHit = false, interrupted = false;
        int unreadable = 0;
        var batch = new IntPtr[64];
        try
        {
            while (!capHit)
            {
                ct.ThrowIfCancellationRequested();
                var want = Math.Min(batch.Length, cap + 1 - events.Count);
                if (!Native.EvtNext(query, want, batch, 5000, 0, out var n))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_NO_MORE_ITEMS) interrupted = true;
                    break;
                }
                for (var i = 0; i < n; i++)
                {
                    try
                    {
                        if (events.Count >= cap) { capHit = true; continue; }
                        if (Parse(Render(batch[i])) is { } e) events.Add(e); else unreadable++;
                    }
                    finally { Native.EvtClose(batch[i]); }
                }
            }
        }
        finally { Native.EvtClose(query); }
        return new RawChannel("ok", null, events, capHit, interrupted, unreadable);
    }

    private static RawChannel Empty(string status, string reason) => new(status, reason, [], false, false, 0);

    private static RawChannel FromError(int err) => err switch
    {
        Native.ERROR_EVT_CHANNEL_NOT_FOUND or Native.ERROR_EVT_INVALID_CHANNEL_PATH => Empty("missing", "Channel not found."),
        Native.ERROR_ACCESS_DENIED => Empty("denied", "Access denied."),
        _ => Empty("failed", $"Win32 error {err}."),
    };

    /// <summary>(true, 0) enabled; (false, 0) disabled; (false, err) config unreadable with that error; (true, err) unknown → try the query.</summary>
    private static (bool Enabled, int Error) ChannelEnabled(string channel)
    {
        var cfg = Native.EvtOpenChannelConfig(IntPtr.Zero, channel, 0);
        if (cfg == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return err is Native.ERROR_EVT_CHANNEL_NOT_FOUND or Native.ERROR_EVT_INVALID_CHANNEL_PATH ? (false, err) : (true, err);
        }
        var buf = IntPtr.Zero;
        try
        {
            Native.EvtGetChannelConfigProperty(cfg, Native.EvtChannelConfigEnabled, 0, 0, IntPtr.Zero, out var used);
            if (used <= 0) return (true, -1);
            buf = Marshal.AllocHGlobal(used);
            if (!Native.EvtGetChannelConfigProperty(cfg, Native.EvtChannelConfigEnabled, 0, used, buf, out _)) return (true, -1);
            const int EvtVarTypeBoolean = 13;
            var type = Marshal.ReadInt32(buf, 12) & 0x7f;
            if (type != EvtVarTypeBoolean) return (true, -1);
            return Marshal.ReadInt32(buf, 0) != 0 ? (true, 0) : (false, 0);
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            Native.EvtClose(cfg);
        }
    }

    private static string? Render(IntPtr evt)
    {
        Native.EvtRender(IntPtr.Zero, evt, Native.EvtRenderEventXml, 0, IntPtr.Zero, out var used, out _);
        if (used <= 0 || used > 1 << 20) return null;
        var buf = Marshal.AllocHGlobal(used);
        try
        {
            return Native.EvtRender(IntPtr.Zero, evt, Native.EvtRenderEventXml, used, buf, out _, out _) ? Marshal.PtrToStringUni(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    internal static RawEvent? Parse(string? xml)
    {
        if (xml is null) return null;
        try
        {
            var root = XElement.Parse(xml);
            var sys = root.Element(Ns + "System");
            if (sys is null) return null;
            var provider = sys.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "";
            if (!int.TryParse(sys.Element(Ns + "EventID")?.Value, out var id)) return null;
            int.TryParse(sys.Element(Ns + "Level")?.Value, out var level);
            if (!DateTimeOffset.TryParse(sys.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var time)) return null;
            var data = root.Element(Ns + "EventData")?.Elements(Ns + "Data")
                .Select(d => new KeyValuePair<string?, string>(d.Attribute("Name")?.Value, d.Value)).ToList() ?? [];
            return new RawEvent(provider, id, level, time, data);
        }
        catch (System.Xml.XmlException) { return null; }
    }
}

/// <summary>Raw rows → grouped facts. Keeps counts, first/last times and a few structured fields; never message text.</summary>
internal static class EventNormalizer
{
    public const int GroupCap = 400;
    public const int TimesCap = 20;

    private sealed class Group(string subject, RawEvent first)
    {
        public string Subject { get; } = subject;
        public string Provider { get; } = first.Provider;
        public int EventId { get; } = first.EventId;
        public int Level { get; set; } = first.Level;
        public int Count { get; set; }
        public DateTimeOffset First { get; set; } = first.Time;
        public DateTimeOffset Last { get; set; } = first.Time;
        public List<DateTimeOffset> Times { get; } = [];
        public JsonObject Extra { get; } = [];
        public Dictionary<string, int> Codes { get; } = [];
        public HashSet<string> Distinct { get; } = [];
        public int Flagged { get; set; }
    }

    private static readonly HashSet<string> TimedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft-Windows-Kernel-Power", "Microsoft-Windows-WER-SystemErrorReporting", "BugCheck", "Microsoft-Windows-WHEA-Logger", "EventLog",
    };

    public static CollectorResult Normalize(EventChannelSpec spec, RawChannel raw, TimeSpan lookback, int cap)
    {
        switch (raw.Status)
        {
            case "missing" or "disabled": return new([], new CoverageReceipt("unsupported", 0, raw.Reason));
            case "denied": return new([], new CoverageReceipt("denied", 0, raw.Reason));
            case "failed": return new([], new CoverageReceipt("failed", 0, raw.Reason));
        }

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var e in raw.Events)
        {
            var (subject, extra) = Classify(spec, e);
            if (!groups.TryGetValue(subject, out var g)) groups[subject] = g = new Group(subject, e);
            g.Count++;
            if (e.Time < g.First) g.First = e.Time;
            if (e.Time > g.Last) g.Last = e.Time;
            if (e.Level is >= 1 and <= 3 && (g.Level is 0 or > 3 || e.Level < g.Level)) g.Level = e.Level;
            if (TimedProviders.Contains(e.Provider)) g.Times.Add(e.Time);
            extra?.Invoke(g, e);
        }

        var kept = groups.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Subject, StringComparer.Ordinal).ToList();
        var groupCapHit = kept.Count > GroupCap;
        if (groupCapHit) kept = kept.Take(GroupCap).ToList();

        var lookbackSeconds = (long)lookback.TotalSeconds;
        var interval = R.Duration(lookback);
        var facts = new List<Fact>();
        foreach (var g in kept)
        {
            var v = new JsonObject
            {
                ["channel"] = spec.Channel,
                ["provider"] = g.Provider,
                ["eventId"] = g.EventId,
                ["level"] = LevelName(g.Level),
                ["count"] = g.Count,
                ["firstAt"] = R.Iso(g.First),
                ["lastAt"] = R.Iso(g.Last),
                ["lookbackSeconds"] = lookbackSeconds,
            };
            foreach (var (k, x) in g.Extra) v[k] = x?.DeepClone();
            if (g.Times.Count > 0)
                v["times"] = new JsonArray(g.Times.OrderByDescending(t => t).Take(TimesCap).Select(t => (JsonNode)R.Iso(t)).ToArray());
            Finish(g, v);
            facts.Add(new Fact(g.Subject, spec.Channel, "measured", v, Display(g, v, lookback), interval));
        }

        var reasons = new List<string>();
        if (raw.RowCapHit) reasons.Add($"row cap: newest {cap} events read");
        if (raw.Interrupted) reasons.Add("read interrupted");
        if (raw.Unreadable > 0) reasons.Add($"{raw.Unreadable} unreadable rows");
        if (groupCapHit) reasons.Add($"group cap: top {GroupCap} groups kept");

        facts.Add(new Fact($"events.{spec.Short}.coverage", spec.Channel, "measured", new JsonObject
        {
            ["channel"] = spec.Channel,
            ["scope"] = spec.InfoIds is null ? "all levels" : "critical, error, warning and selected events",
            ["rowsRead"] = raw.Events.Count,
            ["groups"] = groups.Count,
            ["lookbackSeconds"] = lookbackSeconds,
            ["rowCap"] = cap,
            ["rowCapHit"] = raw.RowCapHit,
            ["oldestAt"] = raw.Events.Count > 0 ? R.Iso(raw.Events.Min(e => e.Time)) : null,
            ["newestAt"] = raw.Events.Count > 0 ? R.Iso(raw.Events.Max(e => e.Time)) : null,
        }, $"Read {R.Plural(raw.Events.Count, "event", "events")} from the {ChannelLabel(spec)} log.", interval));

        var status = reasons.Count > 0 ? "partial" : "complete";
        return new CollectorResult(R.Sorted(facts), new CoverageReceipt(status, raw.Events.Count, reasons.Count > 0 ? string.Join("; ", reasons) : null));
    }

    private static string ChannelLabel(EventChannelSpec spec) => spec.Short switch
    {
        "system" => "System",
        "application" => "Application",
        "update" => "Windows Update",
        "defender" => "Defender",
        _ => spec.Short,
    };

    private static string ProviderShort(string provider)
    {
        var p = provider.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase) ? provider["Microsoft-Windows-".Length..] : provider;
        return p.Length == 0 ? "unknown" : p;
    }

    private static string BaseSubject(EventChannelSpec spec, RawEvent e) => $"events.{spec.Short}.{R.Slug(ProviderShort(e.Provider))}.{e.EventId}";

    private static string LevelName(int level) => level switch
    {
        1 => "critical",
        2 => "error",
        3 => "warning",
        4 => "information",
        5 => "verbose",
        _ => "information",
    };

    /// <summary>Group key (subject) and an optional accumulator for the group's structured fields.</summary>
    private static (string Subject, Action<Group, RawEvent>? Extra) Classify(EventChannelSpec spec, RawEvent e)
    {
        var baseSubject = BaseSubject(spec, e);
        var p = e.Provider;

        // Application Error 1000 / Application Hang 1002: one group per faulting app (file name only).
        if ((p.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && e.EventId == 1000) ||
            (p.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) && e.EventId == 1002))
        {
            var app = FileName(e.Field("AppName", 0) ?? e.Field("Program", 0));
            if (app.Length == 0) return (baseSubject + ".unknown", null);
            return ($"{baseSubject}.{R.Hash8(app.ToLowerInvariant())}", (g, ev) =>
            {
                g.Extra["appName"] = app;
                if (e.EventId == 1000)
                {
                    var module = FileName(ev.Field("ModuleName", 3));
                    if (module.Length > 0) Count(g, "module:" + module);
                    var code = Hex(ev.Field("ExceptionCode", 6));
                    if (code is not null) Count(g, "code:" + code);
                }
            });
        }

        // Windows Error Reporting 1001 (Application): group by the report's event name, e.g. APPCRASH, LiveKernelEvent.
        if (p.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) && e.EventId == 1001)
        {
            var name = e.Field("EventName", 2) is { } n && Regex.IsMatch(n, "^[A-Za-z0-9_]{1,60}$") ? n : "other";
            return ($"{baseSubject}.{R.Slug(name)}", (g, _) => g.Extra["reportType"] = name);
        }

        // Bugcheck 1001 (System): group by normalized stop code.
        if ((p.Equals("Microsoft-Windows-WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) || p.Equals("BugCheck", StringComparison.OrdinalIgnoreCase)) && e.EventId == 1001)
        {
            var code = Hex(e.Field("param1", 0)) ?? "unknown";
            return ($"{baseSubject}.{code}", (g, _) => g.Extra["bugcheckCode"] = code);
        }

        // Kernel-Power 41: one group; tally bugcheck codes and power-button holds.
        if (p.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.EventId == 41)
        {
            return (baseSubject, (g, ev) =>
            {
                if (int.TryParse(ev.Field("BugcheckCode", 0), out var bc) && bc != 0) Count(g, "code:0x" + bc.ToString("x", CultureInfo.InvariantCulture));
                var pb = ev.Field("PowerButtonTimestamp", 4);
                if (pb is not null && pb != "0" && pb.Length > 0) g.Flagged++;
            });
        }

        // Windows Update install failure 20: group by error code.
        if (p.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase) && e.EventId == 20)
        {
            var code = Hex(e.Field("errorCode", 0)) ?? "unknown";
            return ($"{baseSubject}.{code}", (g, ev) =>
            {
                g.Extra["errorCode"] = code;
                var id = ev.Field("updateGuid", 2);
                if (!string.IsNullOrEmpty(id)) g.Distinct.Add(id);
                var title = R.Clean(ev.Field("updateTitle", 1), 100);
                if (title.Length > 0 && g.Extra["sampleTitle"] is null) g.Extra["sampleTitle"] = title;
            });
        }

        // Windows Update install success 19: count distinct updates.
        if (p.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase) && e.EventId == 19)
        {
            return (baseSubject, (g, ev) =>
            {
                var id = ev.Field("updateGuid", 2);
                if (!string.IsNullOrEmpty(id)) g.Distinct.Add(id);
            });
        }

        return (baseSubject, null);
    }

    private static void Count(Group g, string key) => g.Codes[key] = g.Codes.GetValueOrDefault(key) + 1;

    private static void Finish(Group g, JsonObject v)
    {
        JsonObject? Tally(string prefix)
        {
            var items = g.Codes.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(10).ToList();
            if (items.Count == 0) return null;
            var o = new JsonObject();
            foreach (var kv in items) o[kv.Key[prefix.Length..]] = kv.Value;
            return o;
        }

        if (g.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && g.EventId == 41)
        {
            v["bugcheckCodes"] = Tally("code:") ?? new JsonObject();
            v["powerButtonCount"] = g.Flagged;
        }
        else
        {
            if (Tally("module:") is { } m) v["modules"] = m;
            if (Tally("code:") is { } c) v["exceptionCodes"] = c;
        }
        if (g.Distinct.Count > 0) v["distinctUpdates"] = g.Distinct.Count;
    }

    private static string Display(Group g, JsonObject v, TimeSpan lookback)
    {
        var window = RuleKit.Window(v["lookbackSeconds"]);
        var times = g.Count == 1 ? "once" : $"{g.Count} times";
        if (v["appName"] is { } app) return $"{(g.EventId == 1002 ? "Hang" : "Crash")} of {RuleKit.Str(app)} logged {times} {window}.";
        if (v["bugcheckCode"] is { } bc) return $"Stop code {RuleKit.Str(bc)} logged {times} {window}.";
        if (v["errorCode"] is { } ec) return $"Update install failure {RuleKit.Str(ec)} logged {times} {window}.";
        return $"{ProviderShort(g.Provider)} {g.EventId} logged {times} {window}.";
    }

    /// <summary>File name only (drops any directory part), display-safe.</summary>
    private static string FileName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        var i = t.LastIndexOfAny(['\\', '/']);
        return R.Clean(i >= 0 ? t[(i + 1)..] : t, 80);
    }

    /// <summary>Leading hex code normalized to "0x…" without leading zeros (e.g. "0x0000009f (..)" → "0x9f").</summary>
    internal static string? Hex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), "^(?:0x)?([0-9a-fA-F]{1,16})\\b");
        if (!m.Success) return null;
        var digits = m.Groups[1].Value.TrimStart('0').ToLowerInvariant();
        return "0x" + (digits.Length == 0 ? "0" : digits);
    }
}

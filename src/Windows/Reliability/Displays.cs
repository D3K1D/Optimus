using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability;

/// <summary>One active display. <see cref="CurrentHz"/> 0/1 = "hardware default" (unknown). Modes = refresh rates at the current resolution.</summary>
internal sealed record RawDisplay(string DeviceName, bool Primary, int Width, int Height, int CurrentHz, IReadOnlyList<int> ModesHz);

internal sealed class DisplayCollector(CollectorOptions options) : ReliabilityCollector(options)
{
    public override string Id => "win.custom.display";
    public override string Category => "customization";
    public override string GapTitle => "Displays";
    public override string GapSource => "Display settings";

    protected override CollectorResult Collect(CancellationToken ct) => DisplayNormalizer.Normalize(DisplayReader.Read(ct));
}

internal static class DisplayReader
{
    private const int ModeCap = 4000;

    public static List<RawDisplay> Read(CancellationToken ct)
    {
        var list = new List<RawDisplay>();
        for (var i = 0; i < 64; i++)
        {
            ct.ThrowIfCancellationRequested();
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 || (dd.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
            var cur = NewMode();
            if (!Native.EnumDisplaySettingsExW(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref cur, 0)) continue;
            var modes = new SortedSet<int>();
            for (var m = 0; m < ModeCap; m++)
            {
                var dm = NewMode();
                if (!Native.EnumDisplaySettingsExW(dd.DeviceName, m, ref dm, 0)) break;
                if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && (dm.dmDisplayFlags & Native.DM_INTERLACED) == 0 && dm.dmDisplayFrequency > 1)
                    modes.Add(dm.dmDisplayFrequency);
            }
            list.Add(new RawDisplay(dd.DeviceName, (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, modes.ToList()));
        }
        return list;
    }

    private static Native.DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>(), dmDeviceName = "", dmFormName = "" };
}

internal static class DisplayNormalizer
{
    public static CollectorResult Normalize(IReadOnlyList<RawDisplay> raw)
    {
        var facts = new List<Fact>();
        var ordered = raw.OrderByDescending(d => d.Primary).ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
        var n = 0;
        foreach (var d in ordered)
        {
            n++;
            int? current = d.CurrentHz > 1 ? d.CurrentHz : null;
            int? max = d.ModesHz.Count > 0 ? d.ModesHz.Max() : current;
            var label = ordered.Count == 1 ? "Your display" : d.Primary ? "Your main display" : $"Display {n}";
            var display = current is null ? $"{label} refresh rate could not be read."
                : max > current ? $"{label} runs at {current} Hz; it supports up to {max} Hz at this resolution."
                : $"{label} runs at {current} Hz, its highest rate at this resolution.";
            // Subject from the adapter output name (e.g. \\.\DISPLAY1): stable per output, not personal.
            facts.Add(new Fact($"display.{R.Slug(d.DeviceName)}", "Display settings", current is null ? "unavailable" : "measured", new JsonObject
            {
                ["index"] = n,
                ["label"] = label,
                ["primary"] = d.Primary,
                ["widthPx"] = d.Width,
                ["heightPx"] = d.Height,
                ["currentHz"] = current,
                ["maxHzAtResolution"] = max,
                ["ratesHz"] = new JsonArray(d.ModesHz.Select(h => (JsonNode)h).ToArray()),
                ["activeDisplayCount"] = ordered.Count,
            }, display, null));
        }
        return new CollectorResult(R.Sorted(facts), new CoverageReceipt("complete", facts.Count, null));
    }
}

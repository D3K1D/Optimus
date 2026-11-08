using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Inventory;

/// <summary>Pure raw-DTO to wave labels, facts, coverage and capabilities. No Windows calls.</summary>
internal static class InventoryNormalize
{
    // ---- helpers ----
    internal static bool IsSoftwareAdapter(uint flags, uint vendorId, uint deviceId) =>
        (flags & 2) != 0 || (vendorId == 0x1414 && deviceId == 0x8c);

    internal static int RamGb(ulong kilobytes) => Math.Max(1, (int)Math.Round(kilobytes / 1048576.0, MidpointRounding.AwayFromZero));

    internal static string? EditionClass(string? productName, int? build)
    {
        if (string.IsNullOrWhiteSpace(productName)) return null;
        var name = productName.Trim();
        // The registry still says "Windows 10 ..." on Windows 11 builds.
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)) name = "Windows 11" + name["Windows 10".Length..];
        return name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    internal static bool NameSaysTouchpad(string? name) =>
        name is not null && (name.Contains("touchpad", StringComparison.OrdinalIgnoreCase) || name.Contains("touch pad", StringComparison.OrdinalIgnoreCase));

    internal static PointerKind ClassifyPointer(string instanceId, string name, IReadOnlyList<string> ancestors)
    {
        if (NameSaysTouchpad(name)) return PointerKind.Touchpad;
        static bool Starts(string s, string p) => s.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        if (ancestors.Any(a => Starts(a, "USB\\") || Starts(a, "BTH") || Starts(a, "{"))) return PointerKind.Mouse;
        if (ancestors.Any(a => Starts(a, "ACPI\\") || Starts(a, "PCI\\"))) return PointerKind.Touchpad;
        return PointerKind.Other;
    }

    internal static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    static InventoryFact F(string kind, string label) => new(kind, label);
    static Fact M(string subject, string source, JsonNode value, string display) => new(subject, source, "measured", value, display, null);
    static IReadOnlyList<Fact> Sorted(IEnumerable<Fact> facts) => facts.OrderBy(f => f.Subject, StringComparer.Ordinal).ToList();

    static CoverageReceipt Coverage(int observations, IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return new("complete", observations, null);
        return observations == 0
            ? new("failed", 0, "Unreadable: " + string.Join(", ", missing))
            : new("partial", observations, "Unreadable: " + string.Join(", ", missing));
    }

    static InventoryStepResult Result(List<InventoryFact> wave, IEnumerable<Fact> facts, CoverageReceipt cov, JsonObject? caps = null) =>
        new(wave, Sorted(facts), cov, caps ?? new JsonObject());

    // ---- Specs ----
    public static InventoryStepResult Specs(SpecsRaw r)
    {
        var wave = new List<InventoryFact>(); var facts = new List<Fact>(); var missing = new List<string>(); int obs = 0;
        const string src = "Windows system information";

        int? cores = r.PhysicalCores ?? r.LogicalCores;
        if (cores is int c)
        {
            obs++; wave.Add(F("computer", $"{c}-core CPU"));
            facts.Add(M("inventory.specs.cpu", src, new JsonObject { ["logicalCores"] = r.LogicalCores, ["physicalCores"] = r.PhysicalCores },
                r.PhysicalCores is null ? $"{c} logical processors." : $"{c} cores, {r.LogicalCores} threads."));
        }
        else missing.Add("cpu");

        if (r.RamKilobytes is ulong kb)
        {
            int gb = RamGb(kb); obs++; wave.Add(F("computer", $"{gb} GB memory"));
            facts.Add(M("inventory.specs.memory", src, new JsonObject { ["installedGb"] = gb }, $"{gb} GB of memory installed."));
        }
        else missing.Add("memory");

        var edition = EditionClass(r.EditionName, r.Build);
        if (edition is not null)
        {
            obs++; wave.Add(F("computer", edition));
            facts.Add(M("inventory.specs.os", src, new JsonObject { ["edition"] = edition, ["build"] = r.Build }, $"{edition}."));
        }
        else missing.Add("edition");

        var caps = new JsonObject();
        if (r.HasBattery is bool b)
        {
            caps["hasBattery"] = b; obs++;
            facts.Add(M("inventory.specs.battery", src, new JsonObject { ["present"] = b }, b ? "A battery is present." : "No battery is present."));
        }
        else missing.Add("battery");
        return Result(wave, facts, Coverage(obs, missing), caps);
    }

    // ---- Graphics ----
    public static InventoryStepResult Graphics(GraphicsRaw r)
    {
        var wave = new List<InventoryFact>(); var facts = new List<Fact>(); var missing = new List<string>(); int obs = 0;
        const string src = "Windows graphics (DXGI, device drivers)";
        var caps = new JsonObject();

        if (r.GpuCount is int n)
        {
            obs++; caps["gpuCount"] = n;
            if (n > 0) wave.Add(F("display", n == 1 ? "1 GPU" : $"{n} GPUs"));
            facts.Add(M("inventory.graphics.gpu", src, new JsonObject { ["count"] = n }, n == 0 ? "No hardware graphics adapter found." : Plural(n, "graphics adapter.", "graphics adapters.")));
        }
        else missing.Add("gpu");

        if (r.DisplayDrivers is { } drivers)
        {
            obs++;
            var withDriver = drivers.Where(d => d.HasDriver).ToList();
            if (withDriver.Count > 0) wave.Add(F("driver", "Display driver found"));
            var versions = new JsonArray(withDriver.Select(d => d.Version).Where(v => v is not null).Distinct().Take(4).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            facts.Add(M("inventory.graphics.driver", src, new JsonObject { ["found"] = withDriver.Count > 0, ["count"] = withDriver.Count, ["versions"] = versions },
                withDriver.Count > 0 ? "A display driver is installed." : "No display driver was found."));
        }
        else missing.Add("display driver");
        return Result(wave, facts, Coverage(obs, missing), caps);
    }

    // ---- Displays ----
    public static InventoryStepResult Displays(DisplaysRaw r)
    {
        var wave = new List<InventoryFact>(); var facts = new List<Fact>(); var missing = new List<string>(); int obs = 0;
        const string src = "Windows display settings";
        var caps = new JsonObject();
        if (r.Count is int n)
        {
            obs++; caps["displayCount"] = n;
            if (n > 0) wave.Add(F("display", n == 1 ? "1 display" : $"{n} displays"));
            facts.Add(M("inventory.displays.count", src, new JsonObject { ["count"] = n }, Plural(n, "active display.", "active displays.")));
            if (r.MaxCurrentHz is int hz)
            {
                obs++;
                if (hz > 60) wave.Add(F("display", $"{hz} Hz panel"));
                facts.Add(M("inventory.displays.refresh", src, new JsonObject { ["maxCurrentHz"] = hz }, $"Highest current refresh rate is {hz} Hz."));
            }
            else if (n > 0) missing.Add("refresh rate");
        }
        else missing.Add("displays");
        return Result(wave, facts, Coverage(obs, missing), caps);
    }

    // ---- Devices ----
    public static InventoryStepResult Devices(DevicesRaw r)
    {
        var wave = new List<InventoryFact>(); var facts = new List<Fact>(); var missing = new List<string>(); int obs = 0;
        const string src = "Windows device manager (present devices)";

        if (r.Pointers is { } p)
        {
            obs++;
            int mice = p.Count(k => k == PointerKind.Mouse), pads = p.Count(k => k == PointerKind.Touchpad), other = p.Count(k => k == PointerKind.Other);
            if (mice > 0) wave.Add(F("input", "Mouse detected"));
            if (pads > 0) wave.Add(F("input", "Touchpad detected"));
            if (other > 0 && mice == 0 && pads == 0) wave.Add(F("input", "Pointing device detected"));
            facts.Add(M("inventory.devices.pointing", src, new JsonObject { ["mouse"] = mice, ["touchpad"] = pads, ["unclassified"] = other },
                $"{mice} mouse, {pads} touchpad, {other} other pointing devices."));
            if (other > 0) missing.Add("pointing device type");
        }
        else missing.Add("pointing devices");

        if (r.Keyboards is int k)
        {
            obs++;
            if (k > 0) wave.Add(F("input", "Keyboard detected"));
            facts.Add(M("inventory.devices.keyboard", src, new JsonObject { ["count"] = k }, Plural(k, "keyboard.", "keyboards.")));
        }
        else missing.Add("keyboards");

        if (r.Hubs is { } hubs)
        {
            obs++;
            var known = hubs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (hubs.Count > 0)
            {
                wave.Add(F("input", known.Count > 0 ? $"USB hub · {known.Max()} ports" : "USB hub"));
                if (known.Count < hubs.Count) missing.Add("USB hub port count");
            }
            facts.Add(M("inventory.devices.usb_hub", src,
                new JsonObject { ["externalHubs"] = hubs.Count, ["rootHubs"] = r.RootHubs, ["maxPorts"] = known.Count > 0 ? known.Max() : null },
                $"{hubs.Count} external USB hubs, {r.RootHubs} root hubs."));
        }
        else missing.Add("USB hubs");
        return Result(wave, facts, Coverage(obs, missing));
    }

    // ---- Audio ----
    public static InventoryStepResult Audio(AudioRaw r)
    {
        var wave = new List<InventoryFact>(); var facts = new List<Fact>(); var missing = new List<string>(); int obs = 0;
        const string src = "Windows audio endpoints (active)";
        if (r.Render is int o)
        {
            obs++; if (o > 0) wave.Add(F("audio", "Speakers detected"));
            facts.Add(M("inventory.audio.render", src, new JsonObject { ["activeCount"] = o }, Plural(o, "active playback device.", "active playback devices.")));
        }
        else missing.Add("playback devices");
        if (r.Capture is int c)
        {
            obs++; if (c > 0) wave.Add(F("audio", "Microphone detected"));
            facts.Add(M("inventory.audio.capture", src, new JsonObject { ["activeCount"] = c }, Plural(c, "active recording device.", "active recording devices.")));
        }
        else missing.Add("recording devices");
        return Result(wave, facts, Coverage(obs, missing));
    }

    // ---- Network ----
    public static InventoryStepResult Network(NetworkRaw r)
    {
        var wave = new List<InventoryFact>(); var facts = new List<Fact>(); int obs = 0;
        const string src = "Windows network adapters (classes only)";
        if (r.Adapters is not { } all) return Result(wave, facts, Coverage(0, ["network adapters"]), new JsonObject());

        // Virtual adapters (VPN, VM switches) are Ethernet-typed too; keep hardware ones, and unknowns only if nothing is known.
        var real = all.Where(a => a.IsHardware != false).ToList();
        int unknown = real.Count(a => a.IsHardware is null);
        var wifi = real.Where(a => a.IsWifi).ToList();
        var eth = real.Where(a => a.IsEthernet).ToList();

        if (wifi.Count > 0) wave.Add(F("network", "Wi-Fi adapter detected"));
        if (eth.Any(a => a.IsUp)) wave.Add(F("network", "Ethernet connected"));
        else if (eth.Count > 0) wave.Add(F("network", "Ethernet adapter detected"));

        obs = 2;
        facts.Add(M("inventory.network.ethernet", src, new JsonObject { ["present"] = eth.Count > 0, ["connected"] = eth.Any(a => a.IsUp), ["adapters"] = eth.Count },
            eth.Count == 0 ? "No Ethernet adapter found." : eth.Any(a => a.IsUp) ? "Ethernet is connected." : "An Ethernet adapter is present, not connected."));
        facts.Add(M("inventory.network.wifi", src, new JsonObject { ["present"] = wifi.Count > 0, ["connected"] = wifi.Any(a => a.IsUp), ["adapters"] = wifi.Count },
            wifi.Count == 0 ? "No Wi-Fi adapter found." : wifi.Any(a => a.IsUp) ? "A Wi-Fi adapter is present and up." : "A Wi-Fi adapter is present, not connected."));

        var cov = unknown > 0
            ? new CoverageReceipt("partial", obs, "Hardware status unreadable for some adapters")
            : new CoverageReceipt("complete", obs, null);
        return Result(wave, facts, cov, new JsonObject { ["hasWifi"] = wifi.Count > 0 });
    }

    // ---- Drivers ----
    public static InventoryStepResult Drivers(DriversRaw r, DateTime nowUtc)
    {
        const string src = "Windows installed device drivers";
        if (r.Devices is not { } devices) return Result([], [], Coverage(0, ["drivers"]));

        var cutoff = nowUtc.AddYears(-2);
        var withDriver = devices.Where(d => d.HasDriver).ToList();
        bool IsOld(DriverRec d) => d.Date is DateTime dt && dt < cutoff;
        // Drivers that ship inside Windows carry a fixed old date and update with Windows, so they are not "older".
        bool IsInbox(DriverRec d) => d.Provider?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
        int oldAll = withDriver.Count(IsOld), old = withDriver.Count(d => IsOld(d) && !IsInbox(d));

        var wave = new List<InventoryFact>
        {
            F("driver", withDriver.Count == 1 ? "1 driver" : $"{withDriver.Count} drivers"),
            F("driver", old > 0 ? $"{old} older than 2 yrs" : "None older than 2 yrs"),
        };
        var facts = new[]
        {
            M("inventory.drivers.count", src, new JsonObject { ["presentDevices"] = devices.Count, ["withDriver"] = withDriver.Count }, $"{withDriver.Count} present devices have a driver."),
            M("inventory.drivers.older_than_2_years", src, new JsonObject { ["nonMicrosoft"] = old, ["all"] = oldAll }, $"{old} non-Microsoft drivers are older than 2 years."),
        };
        return Result(wave, facts, Coverage(2, []));
    }
}

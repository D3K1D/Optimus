using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Inventory;
using OptimizationTool.Windows.Reliability;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Tests.Inventory;

/// <summary>
/// m9 §6.10 the inventory collectors on fakes behind their read seams (all values invented; nothing on this PC is read):
/// fields, bounds and left-out counts, a source needing administrator rights is a gap, and nothing identifying is stored
/// even when the fake source returns serials, instance IDs, MAC addresses, product keys and the PC and user names.
/// </summary>
public sealed class ScanInventoryTests
{
    private const string PcName = "DESKTOP-7Q2M4KD", User = "pat";
    private static readonly LocalIdentity[] Ids = [PcName, User];

    private static readonly string[] Hostile =
    [
        "SN-0042-99812", "WD-WCC4E1234567", "24A1B7C9", @"PCI\VEN_10DE", "3C-7C-3F-1A-2B-4D", "3C7C3F1A2B4D", PcName, "VK7JG-NPHTM", "00330-80000",
        "S-1-5-21", "Pat Owner", "{5A3F", "192.168.1.20",
    ];

    private static void AssertClean(CollectorResult r)
    {
        foreach (var f in r.Facts)
        {
            Assert.Equal("local", f.Sensitivity);
            var json = f.Value.ToJsonString() + f.Display;
            foreach (var bad in Hostile) Assert.DoesNotContain(bad, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"(?i)(?<![a-z0-9])pat(?![a-z0-9])", json);
        }
    }

    private static JsonArray Items(CollectorResult r) => (JsonArray)Assert.Single(r.Facts).Value["items"]!;
    private static JsonObject Named(CollectorResult r, string name) => Items(r).Single(i => i!["name"]!.GetValue<string>() == name)!.AsObject();

    private static Bag B(params (string K, object? V)[] values)
    {
        var b = new Bag();
        foreach (var (k, v) in values) b[k] = v;
        return b;
    }

    private static readonly Dictionary<string, string> AllOk = new()
    {
        ["cpu"] = "ok", ["gpu"] = "ok", ["firmware"] = "ok", ["memory"] = "ok", ["disks"] = "ok", ["monitors"] = "ok", ["network"] = "ok",
    };

    // ---------------------------------------------------------------- module

    [Fact]
    public void The_module_has_five_misc_collectors_no_rules_and_is_registered_last()
    {
        var m = new ScanInventoryModule();
        var collectors = m.Collectors(new CollectorOptions()).ToList();
        Assert.Equal(ScanInventory.Areas.Select(ScanInventory.CollectorId), collectors.Select(c => c.Id));
        Assert.All(collectors, c => Assert.Equal("misc", c.Category));
        Assert.All(collectors, c => Assert.InRange(c.Timeout, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(75)));
        Assert.Empty(m.Rules());
        Assert.IsType<ScanInventoryModule>(WindowsModules.All[^1]);
        // Nothing of it is projected to an AI's read tools.
        Assert.All(collectors, c => Assert.Null(EvidenceProjection.For(c.Id)));
    }

    // ---------------------------------------------------------------- hardware

    [Fact]
    public async Task Hardware_lists_models_makers_and_versions_and_drops_serials_ids_and_addresses()
    {
        var status = new Dictionary<string, string>(AllOk) { ["monitors"] = "denied" };
        var raw = new HardwareInventoryRaw(
            new CpuRaw("Northwind N7 8-core processor", "GenuineIntel", 3600, 1, 8, null, null, 16),
            [new GpuRaw("Contoso GX 4070", 0x10de, 0x2786, 8L << 30, 0, "31.0.15.5123", "2024-03-01", "NVIDIA")],
            B(("BIOSVendor", "Fabrikam Inc."), ("BIOSVersion", "1813"), ("BIOSReleaseDate", "03/14/2024"), ("SystemManufacturer", "To Be Filled By O.E.M."),
              ("SystemProductName", "System Product Name"), ("BaseBoardManufacturer", "Fabrikam"), ("BaseBoardProduct", "B650 GAMING"), ("BaseBoardVersion", "1.0"),
              ("SystemSerialNumber", "SN-0042-99812"), ("BaseBoardSerialNumber", "SN-0042-99812")),
            [
                B(("Manufacturer", "Litware"), ("PartNumber", "LW16G60C36"), ("Capacity", 17179869184L), ("ConfiguredClockSpeed", 6000), ("SMBIOSMemoryType", 34), ("SerialNumber", "24A1B7C9")),
                B(("Manufacturer", "Litware"), ("PartNumber", "LW16G60C36"), ("Capacity", 17179869184L), ("ConfiguredClockSpeed", 6000), ("SMBIOSMemoryType", 34), ("SerialNumber", "24A1B7D0")),
            ],
            [
                B(("Model", "Adatum Swift 1TB"), ("Manufacturer", null), ("FirmwareVersion", "5B2QGXA7"), ("MediaType", 4), ("BusType", 17), ("Size", 1000204886016L),
                  ("SerialNumber", "WD-WCC4E1234567"), ("UniqueId", "{5A3F00-0000}"), ("ObjectId", @"{1}\\" + PcName + @"\root/Microsoft/Windows/Storage")),
                B(("Model", PcName + " virtual disk"), ("FirmwareVersion", "1.0")),                                  // the PC name: dropped
            ],
            null,                                                                                                   // monitors: denied
            [
                B(("Name", "Wingtip Wi-Fi 7"), ("Manufacturer", "Wingtip Toys"), ("MACAddress", "3C-7C-3F-1A-2B-4D"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_1"), ("SystemName", PcName)),
                B(("Name", "Ethernet 3C7C3F1A2B4D"), ("Manufacturer", "Wingtip Toys")),                                // a MAC in the name: dropped
                B(("Name", "Wi-Fi at 192.168.1.20"), ("Manufacturer", "Wingtip Toys")),                                 // an address: dropped
            ],
            status);
        var r = await new HardwareInventoryCollector(_ => raw, Ids).CollectAsync(default);

        Assert.Equal("partial", r.Coverage.Status);
        Assert.Equal("Monitor details need administrator rights.", r.Coverage.Reason);
        AssertClean(r);
        var fact = Assert.Single(r.Facts);
        Assert.Equal(("inventory.hardware", "hardware"), (fact.Subject, fact.Value["area"]!.GetValue<string>()));
        Assert.Equal("Intel", Named(r, "Northwind N7 8-core processor")["publisher"]!.GetValue<string>());
        var gpu = Named(r, "Contoso GX 4070");
        Assert.Equal(("NVIDIA", "31.0.15.5123"), (gpu["publisher"]!.GetValue<string>(), gpu["driverVersion"]!.GetValue<string>()));
        Assert.DoesNotContain(Items(r), i => i!["kind"]!.GetValue<string>() == "system");   // placeholders are not a model
        var board = Named(r, "B650 GAMING");
        Assert.Equal(("Fabrikam", "1.0"), (board["publisher"]!.GetValue<string>(), board["version"]!.GetValue<string>()));
        var bios = Named(r, "B650 GAMING BIOS");
        Assert.Equal(("Fabrikam Inc.", "1813", "2024-03-14"), (bios["publisher"]!.GetValue<string>(), bios["version"]!.GetValue<string>(), bios["date"]!.GetValue<string>()));
        var ram = Named(r, "DDR5-6000 16 GB memory module");
        Assert.Equal((2, "Litware", "LW16G60C36"), (ram["count"]!.GetValue<int>(), ram["publisher"]!.GetValue<string>(), ram["model"]!.GetValue<string>()));
        var disk = Named(r, "Adatum Swift 1TB");
        Assert.Equal(("5B2QGXA7", "SSD", "NVMe"), (disk["version"]!.GetValue<string>(), disk["media"]!.GetValue<string>(), disk["bus"]!.GetValue<string>()));
        Assert.Equal("Wingtip Toys", Named(r, "Wingtip Wi-Fi 7")["publisher"]!.GetValue<string>());
        Assert.Equal(7, Items(r).Count);
        Assert.Equal("denied", fact.Value["sources"]!["monitors"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hardware_reads_monitor_names_from_edid_arrays_and_every_source_denied_is_a_denied_receipt()
    {
        static object[] Chars(string s) => [.. s.Select(c => (object)(ushort)c), (ushort)0, (ushort)0];
        var raw = new HardwareInventoryRaw(null, null, null, null, null,
            [B(("ManufacturerName", Chars("DEL")), ("UserFriendlyName", Chars("DELL U2723QE")), ("ProductCodeID", Chars("A1B2")), ("SerialNumberID", Chars("CN0ABC123")), ("YearOfManufacture", 2023))],
            null, new Dictionary<string, string>(AllOk));
        var r = await new HardwareInventoryCollector(_ => raw, Ids).CollectAsync(default);
        var monitor = Named(r, "DELL U2723QE");
        Assert.Equal(("Dell", "A1B2", 2023L), (monitor["publisher"]!.GetValue<string>(), monitor["model"]!.GetValue<string>(), monitor["year"]!.GetValue<long>()));
        Assert.DoesNotContain("CN0ABC123", r.Facts[0].Value.ToJsonString());

        var denied = AllOk.ToDictionary(kv => kv.Key, _ => "denied");
        var none = await new HardwareInventoryCollector(_ => new HardwareInventoryRaw(null, null, null, null, null, null, null, denied), Ids).CollectAsync(default);
        Assert.Equal(("denied", 0), (none.Coverage.Status, none.Facts.Count));

        // A source that throws "access denied" as a whole is a gap with the plain reason, never an error.
        var thrown = await new HardwareInventoryCollector(_ => throw new UnauthorizedAccessException(), Ids).CollectAsync(default);
        Assert.Equal("denied", thrown.Coverage.Status);
    }

    // ---------------------------------------------------------------- drivers

    [Fact]
    public async Task Drivers_list_every_device_driver_third_party_first_capped_with_a_count()
    {
        var devices = new List<RawDevice>
        {
            new(@"ROOT\BASICDISPLAY\0000", "Microsoft Basic Display Adapter", "Display", "Microsoft", "10.0.26100.1", new DateTimeOffset(2006, 6, 21, 0, 0, 0, TimeSpan.Zero), true, 0),
            new(@"PCI\VEN_10DE&DEV_2786&SUBSYS_40BF1458&REV_A1\4&2B8260C8&0&0008", "Contoso GX 4070", "Display", "Contoso", "31.0.15.5123", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), true, 0),
            new(@"USB\VID_1234&PID_5678\SN-0042-99812", "Wingtip headset", "AudioEndpoint", "Wingtip Toys", "2.1.0", null, true, 10),
            new(@"SWD\X\1", PcName + " virtual display", "Display", "Contoso", "1.0", null, true, 0),               // the PC name: not listed
            new(@"ACPI\PNP0C0C\2&daba3ff&1", "Power button", "System", null, null, null, true, 0),                     // no driver: counted
            new(@"HID\X\1", "HID mouse", "Mouse", "Microsoft", "10.0.26100.1", null, true, 0),
            new(@"HID\X\2", "HID mouse", "Mouse", "Microsoft", "10.0.26100.1", null, true, 0),                           // the same driver again: merged
        };
        var r = await new DriversInventoryCollector(_ => new RawDevices(devices, false, 0), Ids).CollectAsync(default);
        Assert.Equal("complete", r.Coverage.Status);
        AssertClean(r);
        Assert.DoesNotContain("SUBSYS", r.Facts[0].Value.ToJsonString());
        var names = Items(r).Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Wingtip headset", "Contoso GX 4070", "Microsoft Basic Display Adapter", "HID mouse"], names); // third-party first, by class
        var gpu = Named(r, "Contoso GX 4070");
        Assert.Equal(("Display", "Contoso", "31.0.15.5123", "2024-03-01"), (gpu["class"]!.GetValue<string>(), gpu["publisher"]!.GetValue<string>(), gpu["version"]!.GetValue<string>(), gpu["date"]!.GetValue<string>()));
        Assert.Equal(10, Named(r, "Wingtip headset")["problem"]!.GetValue<int>());
        Assert.Equal(2, Named(r, "HID mouse")["count"]!.GetValue<int>());
        Assert.Equal((1, 1), (r.Facts[0].Value["noDriver"]!.GetValue<int>(), r.Facts[0].Value["unnamed"]!.GetValue<int>()));

        var many = Enumerable.Range(0, DriversInventoryCollector.Cap + 37)
            .Select(i => new RawDevice($@"PCI\X\{i}", $"Device {i:D4}", "System", "Northwind", "1.0." + i, null, true, 0)).ToList();
        var capped = await new DriversInventoryCollector(_ => new RawDevices(many, true, 0), Ids).CollectAsync(default);
        Assert.Equal("partial", capped.Coverage.Status);
        Assert.Equal(DriversInventoryCollector.Cap, Items(capped).Count);
        Assert.Equal((37, DriversInventoryCollector.Cap + 37), (capped.Facts[0].Value["omitted"]!.GetValue<int>(), capped.Facts[0].Value["total"]!.GetValue<int>()));
        Assert.Contains("37 left out", capped.Coverage.Reason);
        Assert.Contains($"first {PnpCollector.DeviceCap} devices", capped.Coverage.Reason);
    }

    // ---------------------------------------------------------------- windows

    [Fact]
    public async Task Windows_names_the_edition_version_build_and_update_level_and_never_keeps_keys_or_owners()
    {
        var bag = B(("ProductName", "Windows 10 Pro"), ("EditionID", "Professional"), ("DisplayVersion", "24H2"), ("CurrentBuild", "26100"), ("UBR", 2605),
            ("InstallationType", "Client"), ("ProductId", "00330-80000-00000-AA123"), ("DigitalProductId", new byte[] { 1, 2 }), ("RegisteredOwner", "Pat Owner"),
            ("BackupProductKeyDefault", "VK7JG-NPHTM-C97JM-9MPGT-3V66T"));
        var r = await new WindowsInventoryCollector(_ => bag, Ids).CollectAsync(default);
        Assert.Equal("complete", r.Coverage.Status);
        AssertClean(r);
        var os = Assert.Single(Items(r))!.AsObject();
        Assert.Equal(("Windows 11 Pro", "Microsoft", "24H2 build 26100.2605"), (os["name"]!.GetValue<string>(), os["publisher"]!.GetValue<string>(), os["version"]!.GetValue<string>()));
        Assert.Equal((26100, 2605L), (os["build"]!.GetValue<int>(), os["ubr"]!.GetValue<long>()));

        var empty = await new WindowsInventoryCollector(_ => new Bag(), Ids).CollectAsync(default);
        Assert.Equal("failed", empty.Coverage.Status);
    }

    // ---------------------------------------------------------------- programs

    [Fact]
    public async Task Programs_list_installed_programs_and_store_apps_skipping_components_and_updates()
    {
        var entries = new List<RawUninstallEntry>
        {
            new("hklm64", "{AAAA}", "Adatum PDF Reader", "Adatum", "11.0.2", 1000, false, false, null),
            new("hkcu", "Litware", "Litware VPN", "Litware", "3.2", null, false, false, null),
            new("hklm32", "Litware", "Litware VPN", "Litware", "3.2", null, false, false, null),      // the same program in two places: merged
            new("hklm64", "{BBBB}", "Contoso Runtime component", "Contoso", "1.0", null, true, false, null),   // SystemComponent
            new("hklm64", "KB5034441", "Security Update for Contoso (KB5034441)", "Contoso", "1.0", null, false, false, null),
            new("hklm64", "{CCCC}.fix", "Contoso Fix Pack", "Contoso", "1.1", null, false, true, null),          // ParentKeyName
            new("hklm64", "{DDDD}", "Contoso Hotfix", "Contoso", "1.2", null, false, false, "Hotfix"),
            new("hkcu", "x", @"C:\Users\pat\AppData\Local\Programs\tool.exe", "Adatum", "1.0", null, false, false, null),
            new("hkcu", "y", "Notes for pat", "Adatum", "1.0", null, false, false, null),
            new("hkcu", "z", "Remote agent", PcName, "1.0", null, false, false, null),                          // the PC name as publisher: field dropped
        };
        var packages = new List<RawPackage>
        {
            new("Wingtip.PhotoStudio_8wekyb3d8bbwe", "Wingtip Photo Studio", "Wingtip Toys", "2.4.118.0"),
            new("Contoso.Notes_abcd", "ms-resource:AppName", "ms-resource:Publisher", "1.0.0.0"),
        };
        var status = new Dictionary<string, string> { ["hklm64"] = "ok", ["hklm32"] = "denied", ["hkcu"] = "ok", ["packages"] = "ok" };
        var r = await new ProgramsInventoryCollector(_ => new RawApps(entries, packages, status), Ids).CollectAsync(default);
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Equal("32-bit programs installed for everyone need administrator rights.", r.Coverage.Reason);
        AssertClean(r);
        var names = Items(r).Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Adatum PDF Reader", "Litware VPN", "Remote agent", "Contoso.Notes", "Wingtip Photo Studio"], names);
        Assert.Equal(2, Named(r, "Litware VPN")["count"]!.GetValue<int>());
        Assert.Null(Named(r, "Remote agent")["publisher"]);
        var store = Named(r, "Wingtip Photo Studio");
        Assert.Equal(("Wingtip Toys", "2.4.118.0", "store"), (store["publisher"]!.GetValue<string>(), store["version"]!.GetValue<string>(), store["source"]!.GetValue<string>()));
        Assert.Equal((1, 3), (r.Facts[0].Value["skippedSystemComponents"]!.GetValue<int>(), r.Facts[0].Value["skippedUpdates"]!.GetValue<int>()));
    }

    // ---------------------------------------------------------------- background

    [Fact]
    public async Task Background_lists_startup_services_tasks_and_processes_with_publisher_and_version()
    {
        var roots = Security.SecurityNormalizeTests.Roots;
        var valid = new SignatureInfo("valid", "Litware Inc.", false);
        var unsigned = new SignatureInfo("unsigned", null, false);
        var ms = new SignatureInfo("valid", "Microsoft Windows", true);
        StartupItemRaw[] startup =
        [
            new("run", "hkcu", "64", "Litware VPN", "REG_SZ", @"Q:\Apps\Litware\vpn.exe", true, null, valid, true, "logon", "Litware VPN", new("Litware Inc.", "3.2.0.1", "Litware VPN")),
            new("service", "services", null, "Tailspin Backup Service", "TailspinSvc", @"Q:\Apps\Tailspin\svc.exe", false, null, unsigned, true, "manual", "TailspinSvc", new("Tailspin Toys", "2.0", null)),
            new("task", @"\Vendor", null, "Updater S-1-5-21-1111111111-2222222222-3333333333-1001", "exec", @"Q:\Users\pat\AppData\Local\Up\up.exe", true, null, unsigned, true, "schedule", null, new("Adatum", "1.0 (build 7)", null)),
            new("task", @"\Vendor", null, "Sync for pat", "exec", @"Q:\Apps\Sync\sync.exe", false, null, unsigned, true, "logon"),               // the account: not listed
        ];
        ProcessImageRaw[] processes =
        [
            new("svchost", @"Q:\Win\System32\svchost.exe", ms, new("Microsoft Corporation", "10.0.26100.1", "Microsoft® Windows® Operating System"), 64),
            new("game", @"\\?\Q:\Games\Wingtip\game.exe", unsigned, new("Wingtip Toys", "8.1.4", "Wingtip Game"), 1),
            new("helper", @"Q:\Users\pat\Desktop\helper.exe", unsigned, null, 1),
        ];
        var raw = new BackgroundRaw(new StartupRaw(startup, [], 2, 0, 40, 120, false), null, processes, null, 5, 0);
        var r = await new BackgroundInventoryCollector(_ => raw, Ids, roots).CollectAsync(default);

        Assert.Equal("partial", r.Coverage.Status);
        Assert.Equal("2 task folders need administrator rights.", r.Coverage.Reason); // unreadable single processes are counted, not a gap
        AssertClean(r);
        var vpn = Named(r, "Litware VPN");
        Assert.Equal(("run", "Litware Inc.", true, "3.2.0.1"), (vpn["source"]!.GetValue<string>(), vpn["publisher"]!.GetValue<string>(), vpn["publisherVerified"]!.GetValue<bool>(), vpn["version"]!.GetValue<string>()));
        Assert.Equal(@"%ProgramFiles%\Litware\vpn.exe", vpn["image"]!.GetValue<string>());
        var svc = Named(r, "Tailspin Backup Service");
        Assert.Equal(("Tailspin Toys", false, "manual"), (svc["publisher"]!.GetValue<string>(), svc["publisherVerified"]!.GetValue<bool>(), svc["trigger"]!.GetValue<string>()));
        var task = Named(r, "Updater");                                                   // the SID removed from the task name
        Assert.Equal(("1.0", @"%LOCALAPPDATA%\Up\up.exe"), (task["version"]!.GetValue<string>(), task["image"]!.GetValue<string>()));
        Assert.DoesNotContain(Items(r), i => i!["name"]!.GetValue<string>().Contains("Sync", StringComparison.Ordinal));
        var host = Named(r, "Microsoft® Windows® Operating System");
        Assert.Equal(("process", "Microsoft Windows", 64, "svchost.exe"), (host["source"]!.GetValue<string>(), host["publisher"]!.GetValue<string>(), host["count"]!.GetValue<int>(), host["file"]!.GetValue<string>()));
        Assert.Equal("Wingtip Toys", Named(r, "Wingtip Game")["publisher"]!.GetValue<string>());
        Assert.False(Named(r, "Wingtip Game")["publisherVerified"]!.GetValue<bool>());
        Assert.Equal(@"%USERPROFILE%\Desktop\helper.exe", Named(r, "helper.exe")["image"]!.GetValue<string>());
        var value = r.Facts[0].Value;
        Assert.Equal((120, 40, 5), (value["microsoftServicesOmitted"]!.GetValue<int>(), value["microsoftTasksOmitted"]!.GetValue<int>(), value["processesUnreadable"]!.GetValue<int>()));

        // Both sources failing: failed, nothing stored.
        var none = await new BackgroundInventoryCollector(_ => new BackgroundRaw(null, "failed", null, "failed", 0, 0), Ids, roots).CollectAsync(default);
        Assert.Equal(("failed", 0), (none.Coverage.Status, none.Facts.Count));
    }

    [Fact]
    public void The_startup_evidence_gains_the_launched_files_company_and_version()
    {
        StartupItemRaw[] items = [new("run", "hkcu", "64", "Litware VPN", "REG_SZ", @"Q:\Apps\Litware\vpn.exe", false, null, new("valid", "Litware Inc.", false), true, "logon", "Litware VPN", new("Litware Inc.", "3.2.0.1", null))];
        var r = new StartupCollector(new CollectorOptions(), Security.SecurityNormalizeTests.Roots) { UserName = User }.Normalize(new StartupRaw(items, [], 0, 0, 0, 0, false));
        var fact = r.Facts.Single(f => f.Subject != "startup.summary");
        Assert.Equal(("Litware Inc.", "3.2.0.1"), (fact.Value["company"]!.GetValue<string>(), fact.Value["version"]!.GetValue<string>()));
        // The startup projector's allowlist does not release the new fields.
        var projected = EvidenceProjection.Project(new EvidenceItem("ev-0123456789abcdef", fact.Subject, StartupCollector.CollectorId, "security", fact.Value, fact.Display, "2026-11-12T10:00:00Z"),
            new AliasMap(), new ShareOptions(true));
        Assert.NotNull(projected);
        Assert.Null(projected!.Value["company"]);
        Assert.Null(projected.Value["version"]);
    }

    // ---------------------------------------------------------------- kit

    [Theory]
    [InlineData("Contoso GX 4070", "Contoso GX 4070")]
    [InlineData(@"PCI\VEN_10DE&DEV_2786", null)]
    [InlineData("Ethernet 3C:7C:3F:1A:2B:4D", null)]
    [InlineData("Viewer for 192.168.1.20", null)]
    [InlineData("DESKTOP-7Q2M4KD display", null)]
    [InlineData("pat's notes", null)]
    [InlineData("Patcher", null)]                       // R3: a 3+ character name matches by containment, not as a word
    [InlineData("Pathfinder", null)]
    [InlineData("Contoso Tools", "Contoso Tools")]
    [InlineData("Task S-1-5-21-1-2-3-1001", "Task")]
    public void Clean_drops_what_could_identify_this_pc(string raw, string? expected) => Assert.Equal(expected, InventoryKit.Clean(raw, Ids));

    [Theory]
    [InlineData("10.0.22621.1 (WinBuild.160101.0800)", "10.0.22621.1")]
    [InlineData("22.200.0.6", "22.200.0.6")]
    [InlineData("v", null)]
    [InlineData(@"C:\x\1.0", null)]
    public void Version_keeps_plain_versions(string raw, string? expected) => Assert.Equal(expected, InventoryKit.Version(raw, Ids));

    // m9 review R4: the never-store list is checked on every kept name, publisher and version, field by field.
    [Theory]
    [InlineData("Viewer fe80::1ff:fe23:4567:890a", null)]                          // IPv6, compressed
    [InlineData("Viewer fe80::1", null)]
    [InlineData("Viewer fe80::1%12", null)]                                       // with a zone
    [InlineData("Viewer 2001:0db8:85a3:0000:0000:8a2e:0370:7334", null)]          // full
    [InlineData("Viewer ::ffff:10.1.2.3", null)]                                  // IPv4-mapped
    [InlineData("Viewer fe80:0:1", null)]                                         // three colon-separated hex groups
    [InlineData("Office VK7JG-NPHTM-C97JM-9MPGT-3V66T", null)]                    // a 5x5 product key
    [InlineData("Office BCDFG-HJKMP-QRTVW-XYBCD-FGHJK", null)]                    // a 5x5 key without digits
    [InlineData("Licence ABCD-1234-EF56-7890", null)]                             // a 4-group key
    [InlineData("Disk 5A3F2C1D-0B9E-4A7C-8D6F-1E2B3C4D5E6F", null)]               // a GUID-like serial
    [InlineData("Disk WD-WCC4E1234567", null)]                                    // a long mixed serial run
    [InlineData("Disk E823_8FA6_BF53_0001", null)]
    [InlineData("Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810")]
    [InlineData("7-Zip 24.08 (x64)", "7-Zip 24.08 (x64)")]
    [InlineData("NVIDIA Graphics Driver 566.36", "NVIDIA Graphics Driver 566.36")]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", "Intel(R) Wi-Fi 6E AX211 160MHz")]
    [InlineData("Microsoft Edge WebView2 Runtime", "Microsoft Edge WebView2 Runtime")]
    [InlineData("Update for Windows 10 (KB5034441)", "Update for Windows 10 (KB5034441)")]
    public void Clean_drops_ipv6_key_and_serial_shapes_inside_kept_names(string raw, string? expected) => Assert.Equal(expected, InventoryKit.Clean(raw, Ids));

    [Theory]
    [InlineData("10.0.26100.2", "10.0.26100.2")]                                  // a dotted 4-part version is not IPv4
    [InlineData("1.2.3-beta+build.5", "1.2.3-beta+build.5")]
    [InlineData("24.08", "24.08")]
    [InlineData("2.1.0.1234", "2.1.0.1234")]
    [InlineData("1.0.0.abcd", "1.0.0.abcd")]                                      // hex-ish, not IPv6
    [InlineData("566.36", "566.36")]
    [InlineData("fe80::1", null)]
    [InlineData("fe80::1%12", null)]
    [InlineData("::ffff:1.2.3.4", null)]
    [InlineData("1:2:3", null)]
    [InlineData("2001:db8:0:0:0:0:2:1", null)]
    [InlineData("3C:7C:3F:1A:2B:4D", null)]                                       // MAC
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T", null)]                           // product key
    [InlineData("1.0-WCC4E1234567", null)]                                        // a serial run glued to a version
    public void Version_keeps_version_syntax_and_drops_ipv6_mac_and_key_shapes(string raw, string? expected) => Assert.Equal(expected, InventoryKit.Version(raw, Ids));

    [Fact]
    public async Task Stored_program_items_drop_forbidden_content_inside_name_publisher_and_version()
    {
        static RawUninstallEntry App(string name, string? publisher, string? version) => new("hklm64", name, name, publisher, version, null, false, false, null);
        var status = new Dictionary<string, string> { ["hklm64"] = "ok", ["hklm32"] = "ok", ["hkcu"] = "ok", ["packages"] = "ok" };
        var r = await new ProgramsInventoryCollector(_ => new RawApps(
        [
            App("Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Corporation", "14.40.33810.0"),
            App("Contoso VPN fe80::1%12", "Contoso", "1.0"),
            App("Litware Office", "Litware VK7JG-NPHTM-C97JM-9MPGT-3V66T", "16.0.1"),
            App("Fabrikam Sync", "Fabrikam", "::ffff:10.0.0.5"),
            App("Adatum Tool", "Adatum", "2.0-WCC4E1234567"),
        ], [], status), Ids).CollectAsync(default);
        var rows = ((JsonArray)Assert.Single(r.Facts).Value["items"]!).Select(i => $"{i!["name"]}|{i["publisher"]}|{i["version"]}").OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(["Adatum Tool|Adatum|", "Fabrikam Sync|Fabrikam|", "Litware Office||16.0.1", "Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810|Microsoft Corporation|14.40.33810.0"], rows);
    }

    [Theory]
    [InlineData("Litware fe80::1", "3.2.0.1", null, "3.2.0.1")]
    [InlineData("Litware VK7JG-NPHTM-C97JM-9MPGT-3V66T", "3.2.0.1", null, "3.2.0.1")]
    [InlineData("pat's tools", "3.2.0.1", null, "3.2.0.1")]                                  // the user name
    [InlineData("DESKTOP-7Q2M4KD Labs", "3.2.0.1", null, "3.2.0.1")]                         // the PC name
    [InlineData("Litware Inc.", "fe80::1%12", "Litware Inc.", null)]
    [InlineData("Litware Inc.", "::ffff:1.2.3.4", "Litware Inc.", null)]
    [InlineData("Litware Inc.", "1.0 WCC4E1234567-pat", "Litware Inc.", "1.0")]             // the first token of the version only
    [InlineData("Litware Inc.", "3.0-pat", "Litware Inc.", null)]
    [InlineData("Litware Inc.", @"C:\x\1.0", "Litware Inc.", null)]
    [InlineData("Litware Inc.", "10.0.26100.2 (WinBuild.160101.0800)", "Litware Inc.", "10.0.26100.2")]
    public void The_startup_company_and_version_go_through_the_never_store_check(string company, string version, string? keptCompany, string? keptVersion)
    {
        StartupItemRaw[] items = [new("run", "hkcu", "64", "Litware VPN", "REG_SZ", @"Q:\Apps\Litware\vpn.exe", false, null, new("valid", "Litware Inc.", false), true, "logon", "Litware VPN", new(company, version, null))];
        var r = new StartupCollector(new CollectorOptions(), Security.SecurityNormalizeTests.Roots) { UserName = User, Identities = Ids }.Normalize(new StartupRaw(items, [], 0, 0, 0, 0, false));
        var fact = r.Facts.Single(f => f.Subject != "startup.summary");
        Assert.Equal((keptCompany, keptVersion), (fact.Value["company"]?.GetValue<string>(), fact.Value["version"]?.GetValue<string>()));
    }
}

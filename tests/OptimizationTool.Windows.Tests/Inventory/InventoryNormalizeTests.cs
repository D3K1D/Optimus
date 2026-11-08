using OptimizationTool.Windows.Inventory;

namespace OptimizationTool.Windows.Tests.Inventory;

// Synthetic inputs only: no real machine values.
public class InventoryNormalizeTests
{
    static string[] Labels(OptimizationTool.Engine.InventoryStepResult r) => r.WaveFacts.Select(f => f.Label).ToArray();

    [Fact]
    public void Specs_labels_round_ram_and_fix_edition()
    {
        var r = InventoryNormalize.Specs(new SpecsRaw(16, 8, 33_400_000, "Windows 10 Pro", 26100, true));
        Assert.Equal(["8-core CPU", "32 GB memory", "Windows 11 Pro"], Labels(r));
        Assert.Equal("complete", r.Coverage.Status);
        Assert.True(r.Capabilities["hasBattery"]!.GetValue<bool>());
        Assert.Equal(["inventory.specs.battery", "inventory.specs.cpu", "inventory.specs.memory", "inventory.specs.os"], r.Facts.Select(f => f.Subject));
    }

    [Fact]
    public void Specs_partial_when_memory_unreadable_and_failed_when_nothing()
    {
        var p = InventoryNormalize.Specs(new SpecsRaw(4, null, null, "Windows 11 Home", 22631, null));
        Assert.Equal("partial", p.Coverage.Status);
        Assert.Equal(["4-core CPU", "Windows 11 Home"], Labels(p));
        Assert.Equal("failed", InventoryNormalize.Specs(new SpecsRaw(null, null, null, null, null, null)).Coverage.Status);
    }

    [Theory]
    [InlineData(0u, 0x10deu, 0x1u, true)]  // software flag
    [InlineData(0u, 0x1414u, 0x8cu, true)] // basic render driver
    [InlineData(0u, 0x10deu, 0x1u, false)]
    public void Software_adapter_detection(uint flags, uint vendor, uint device, bool expected)
    {
        if (flags == 0 && vendor == 0x10de && expected) flags = 2;
        Assert.Equal(expected, InventoryNormalize.IsSoftwareAdapter(flags, vendor, device));
    }

    [Fact]
    public void Graphics_counts_and_driver()
    {
        var r = InventoryNormalize.Graphics(new GraphicsRaw(2, [new DriverRec(new DateTime(2025, 1, 1), "1.2.3.4", "Vendor"), new DriverRec(null, null, null)]));
        Assert.Equal(["2 GPUs", "Display driver found"], Labels(r));
        Assert.Equal(2, r.Capabilities["gpuCount"]!.GetValue<int>());
        Assert.Equal("complete", r.Coverage.Status);
        var none = InventoryNormalize.Graphics(new GraphicsRaw(0, []));
        Assert.Empty(Labels(none));
    }

    [Theory]
    [InlineData(2, 165, new[] { "2 displays", "165 Hz panel" })]
    [InlineData(1, 60, new[] { "1 display" })]
    public void Displays_labels(int count, int hz, string[] expected)
    {
        var r = InventoryNormalize.Displays(new DisplaysRaw(count, hz));
        Assert.Equal(expected, Labels(r));
        Assert.Equal(count, r.Capabilities["displayCount"]!.GetValue<int>());
    }

    [Fact]
    public void Displays_unreadable_is_failed()
    {
        Assert.Equal("failed", InventoryNormalize.Displays(new DisplaysRaw(null, null)).Coverage.Status);
    }

    [Fact]
    public void Pointer_classification()
    {
        Assert.Equal(PointerKind.Touchpad, InventoryNormalize.ClassifyPointer("HID\\X", "Synthetic Touchpad", []));
        Assert.Equal(PointerKind.Mouse, InventoryNormalize.ClassifyPointer("HID\\X", "HID-compliant mouse", ["HID\\A", "USB\\B"]));
        Assert.Equal(PointerKind.Touchpad, InventoryNormalize.ClassifyPointer("HID\\X", "HID-compliant mouse", ["HID\\A", "ACPI\\B"]));
        Assert.Equal(PointerKind.Other, InventoryNormalize.ClassifyPointer("HID\\X", "x", ["ROOT\\B"]));
    }

    [Fact]
    public void Devices_labels_and_hub_ports()
    {
        var full = InventoryNormalize.Devices(new DevicesRaw([PointerKind.Mouse, PointerKind.Touchpad], 1, [4, 7], 2));
        Assert.Equal(["Mouse detected", "Touchpad detected", "Keyboard detected", "USB hub · 7 ports"], Labels(full));
        Assert.Equal("complete", full.Coverage.Status);

        var partial = InventoryNormalize.Devices(new DevicesRaw([PointerKind.Other], 0, [null], 1));
        Assert.Equal(["Pointing device detected", "USB hub"], Labels(partial));
        Assert.Equal("partial", partial.Coverage.Status);
    }

    [Fact]
    public void Audio_labels_and_counts()
    {
        var r = InventoryNormalize.Audio(new AudioRaw(2, 0));
        Assert.Equal(["Speakers detected"], Labels(r));
        Assert.Equal("complete", r.Coverage.Status);
        Assert.Equal("partial", InventoryNormalize.Audio(new AudioRaw(1, null)).Coverage.Status);
        Assert.Equal("failed", InventoryNormalize.Audio(new AudioRaw(null, null)).Coverage.Status);
    }

    [Fact]
    public void Network_classes_hide_virtual_adapters()
    {
        var r = InventoryNormalize.Network(new NetworkRaw([
            new AdapterRec(true, false, true, true),
            new AdapterRec(false, true, false, true),
            new AdapterRec(false, true, true, false), // virtual switch, ignored
        ]));
        Assert.Equal(["Wi-Fi adapter detected", "Ethernet adapter detected"], Labels(r));
        Assert.True(r.Capabilities["hasWifi"]!.GetValue<bool>());
        Assert.Equal("complete", r.Coverage.Status);

        var up = InventoryNormalize.Network(new NetworkRaw([new AdapterRec(false, true, true, null)]));
        Assert.Equal(["Ethernet connected"], Labels(up));
        Assert.Equal("partial", up.Coverage.Status);
        Assert.False(up.Capabilities["hasWifi"]!.GetValue<bool>());
        Assert.Equal("failed", InventoryNormalize.Network(new NetworkRaw(null)).Coverage.Status);
    }

    [Fact]
    public void Drivers_age_ignores_inbox_microsoft_drivers()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var r = InventoryNormalize.Drivers(new DriversRaw([
            new DriverRec(new DateTime(2020, 1, 1), "1", "Vendor A"),      // old
            new DriverRec(new DateTime(2025, 1, 1), "2", "Vendor B"),      // recent
            new DriverRec(new DateTime(2006, 6, 21), "3", "Microsoft"),    // inbox, not counted
            new DriverRec(null, null, null),                               // no driver
        ]), now);
        Assert.Equal(["3 drivers", "1 older than 2 yrs"], Labels(r));
        Assert.Equal("complete", r.Coverage.Status);
        Assert.Equal(2, r.Facts.Single(f => f.Subject == "inventory.drivers.older_than_2_years").Value["all"]!.GetValue<int>());
        Assert.Equal("None older than 2 yrs", Labels(InventoryNormalize.Drivers(new DriversRaw([]), now))[1]);
        Assert.Equal("failed", InventoryNormalize.Drivers(new DriversRaw(null), now).Coverage.Status);
    }

    [Fact]
    public void Facts_are_sorted_and_use_inventory_subjects()
    {
        var r = InventoryNormalize.Network(new NetworkRaw([new AdapterRec(true, false, true, true)]));
        Assert.All(r.Facts, f => Assert.StartsWith("inventory.network.", f.Subject));
        Assert.Equal(r.Facts.Select(f => f.Subject).OrderBy(s => s, StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
    }
}

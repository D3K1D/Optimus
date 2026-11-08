using System.Text.Json.Nodes;

namespace OptimizationTool.Tests;

/// <summary>Additive protocol fields from the UI integration: waveWords, gap source, inventory fact group, targeted Misc. card.</summary>
public class ContractUpdateTests
{
    [Fact]
    public async Task Scan_WaveWords_AreAllForFull_AndFilteredForTargeted()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var (full, _) = await c.ScanAsync("audit.full");
        Assert.Equal(9, full["waveWords"]!.AsArray().Count);

        var (storage, _) = await c.ScanAsync("audit.storage");
        Assert.Equal(["C: 11 GiB free"], storage["waveWords"]!.AsArray().Select(w => (string)w!));

        var (custom, _) = await c.ScanAsync("audit.customization");
        Assert.Equal(["Display 2: 60 Hz / 165 Hz", "Start suggestions: on"], custom["waveWords"]!.AsArray().Select(w => (string)w!));

        var (reliability, _) = await c.ScanAsync("audit.reliability");
        Assert.Equal(["Kernel-Power 41 ×3"], reliability["waveWords"]!.AsArray().Select(w => (string)w!));
    }

    [Fact]
    public async Task Gap_CarriesItsSource()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var (scan, _) = await c.ScanAsync("audit.full");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal("Sensors", Assert.Single(report["gaps"]!.AsArray()).Str("source"));
    }

    [Fact]
    public async Task TargetedCustomization_IncludesTheMiscCard()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var (scan, _) = await c.ScanAsync("audit.customization", "Which Windows settings can I personalize?");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal(["display.below_max_refresh", "start.suggestions_on"], report["findings"]!.AsArray().Select(f => f.Str("ruleId")));
        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal(2, (int)followup["total"]!);
        Assert.Equal("customization", followup["card"].Str("category"));
    }

    [Fact]
    public async Task InventoryFactEvents_CarryTheirStepGroup()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        await Rpc.OnboardLocal(c, consent: "granted");
        var inv = await c.Call("inventory.start", new JsonObject { ["idempotencyKey"] = "g" });
        await c.WaitForEvent(e => e.Str("type") == "inventory.finished");
        var facts = c.Events.Where(e => e.Str("type") == "inventory.fact").ToList();
        Assert.Equal("Specs", facts[0]["payload"].Str("group"));
        Assert.Equal("Drivers", facts[^1]["payload"].Str("group"));
        Assert.Equal(["Specs", "Graphics", "Displays", "Devices", "Audio", "Network", "Drivers"],
            facts.Select(f => f["payload"].Str("group")).Distinct());
        Assert.NotNull(inv.Str("inventoryId"));
    }

    [Fact]
    public void EventsRead_IsOnTheDesktopAllowlist() =>
        Assert.Contains("events.read", OptimizationTool.Host.HostServer.Allowlists["desktop"]);
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

public class OnboardingTests
{
    [Fact]
    public async Task Save_WithStaleRevision_ReturnsCurrent()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var saved = await Rpc.OnboardLocal(c);
        Assert.Equal(2, (int)saved["revision"]!);
        var error = await c.Fails("onboarding.save", new JsonObject { ["expectedRevision"] = 1, ["patch"] = new JsonObject { ["step"] = "use" } });
        Assert.Equal(ErrorCodes.StaleRevision, error.Code);
        Assert.Equal(saved.ToJsonString(), error.ErrorData!["current"]!.ToJsonString());
    }

    [Fact]
    public async Task Save_RejectsUnknownFieldsAndLongText()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var unknown = await c.Fails("onboarding.save", new JsonObject { ["expectedRevision"] = 1, ["patch"] = new JsonObject { ["admin"] = true } });
        Assert.Equal("patch.admin", unknown.ErrorData.Str("field"));
        var longText = await c.Fails("onboarding.save", new JsonObject { ["expectedRevision"] = 1, ["patch"] = new JsonObject { ["customUse"] = new string('x', 201) } });
        Assert.Equal(ErrorCodes.InvalidParams, longText.Code);
    }

    [Fact]
    public async Task Starters_AreAtLeastTen_AndFollowProfileAndInventory()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var general = (await c.Call("starter.list", new JsonObject { ["profileRevision"] = 1 }))["starters"]!.AsArray();
        Assert.Equal(10, general.Count);
        Assert.All(general, s => Assert.Equal("general", s.Str("basis")));
        Assert.Equal("s02", general[0].Str("id"));
        Assert.DoesNotContain(general, s => s.Str("text").Contains("Begin scan", StringComparison.OrdinalIgnoreCase));

        await Rpc.OnboardLocal(c, uses: ["Gaming", "Editing"], consent: "granted");
        var inv = await c.Call("inventory.start", new JsonObject { ["idempotencyKey"] = "inv-key-1" });
        var inventoryId = inv.Str("inventoryId");
        await c.WaitForEvent(e => e.Str("stream") == "inventory:" + inventoryId && e.Str("type") == "inventory.finished");
        var inventory = await c.Call("inventory.get", new JsonObject { ["inventoryId"] = inventoryId });
        Assert.Equal("partial", inventory.Str("status"));
        Assert.Equal(16, inventory["facts"]!.AsArray().Count);
        Assert.Equal(16, c.Events.Count(e => e.Str("type") == "inventory.fact"));

        var starters = (await c.Call("starter.list"))["starters"]!.AsArray();
        Assert.Equal(13, starters.Count);
        Assert.Equal(["s02", "s11", "g01", "s12", "s03", "p01", "s04"], starters.Take(7).Select(s => s.Str("id")));
        Assert.Equal("inventory", starters.Single(s => s.Str("id") == "s12").Str("basis"));
        Assert.Equal(starters.Count, starters.Select(s => s.Str("text")).Distinct().Count());
    }

    [Fact]
    public async Task Inventory_RequiresConsent()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var error = await c.Fails("inventory.start", new JsonObject { ["idempotencyKey"] = "k" });
        Assert.Equal(ErrorCodes.InvalidState, error.Code);
        Assert.Equal("inventory_consent_required", error.ErrorData.Str("reason"));
    }

    [Fact]
    public async Task Inventory_CanBeCancelled()
    {
        await using var host = TestHost.Start(speed: 1);
        var c = await host.ConnectAsync();
        await Rpc.OnboardLocal(c, consent: "granted");
        var inv = await c.Call("inventory.start", new JsonObject { ["idempotencyKey"] = "k" });
        var cancelled = await c.Call("inventory.cancel", new JsonObject { ["inventoryId"] = inv.Str("inventoryId") });
        Assert.Equal("cancelled", cancelled.Str("status"));
        Assert.Equal(inv.Str("inventoryId"), (await c.Call("onboarding.get"))["resume"].Str("inventoryId"));
    }
}

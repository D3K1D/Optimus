using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Settings;

namespace OptimizationTool.Tests;

/// <summary>
/// m9 §3.1 / §4 in the engine: resource locks across handlers, plan metadata (flags, what, group, resources), the
/// hash over it, and the live recheck of privilege, flags and resources. Fakes only; nothing touches this PC.
/// </summary>
public class ResourceLockTests
{
    private const string Adv = @"reg:64:HKCU\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\EXPLORER\ADVANCED";

    /// <summary>A registry in memory for the real setting.set handler.</summary>
    private sealed class MemoryRegistry : IRegistryStore
    {
        public readonly Dictionary<string, RegData> Values = new(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        private static string K(RegLocation at) => $"{at.Hive}:{at.View}:{at.Key}\\{at.Name}";
        public RegData Read(RegLocation at) => Values.TryGetValue(K(at), out var d) ? d : RegData.Absent;
        public RegData Write(RegLocation at, string type, JsonNode data)
        {
            Interlocked.Increment(ref Writes);
            Values[K(at)] = new RegData(true, type, data.DeepClone());
            return Read(at);
        }
        public RegData Delete(RegLocation at)
        {
            Interlocked.Increment(ref Writes);
            Values.Remove(K(at));
            return Read(at);
        }
        public bool Exists(RegLocation at) => false;
        public IReadOnlyList<string> ValueNames(RegLocation key) => [];
        public void Set(SettingRow row, RegData d) => Values[K(row.Location)] = d;
    }

    private sealed class NoNotify : ISettingNotifier
    {
        public void Notify(string kind) { }
    }

    private static JsonObject Keys(params string[] keys) => new() { ["keys"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()) };

    private static Task<JsonObject> Preview(Client c, string actionId, JsonObject parameters) =>
        c.Call("action.preview", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = actionId, ["parameters"] = parameters });

    [Theory]
    [InlineData(@"reg:64:HKCU\A\B", @"reg:64:HKCU\A\B", true)]
    [InlineData(@"reg:64:HKCU\A", @"reg:64:HKCU\A\B", true)]          // parent / child
    [InlineData(@"reg:64:hkcu\a\b", @"reg:64:HKCU\A\B", true)]        // case-insensitive
    [InlineData(@"reg:64:HKCU\A\B", @"reg:64:HKCU\A\BC", false)]      // shared prefix, not a separator boundary
    [InlineData(@"reg:64:HKCU\A\B", @"reg:32:HKCU\A\B", false)]       // another view
    [InlineData("service:Foo", "service:Foo", true)]
    [InlineData("power:ABC", "power:ABC:DISPLAY", true)]
    [InlineData("fw:rule:1", "fw:rule:10", false)]
    [InlineData("defender:quick_scan", "defender:full_scan", false)]
    public void Keys_conflict_with_themselves_parents_and_children(string a, string b, bool conflict)
    {
        Assert.Equal(conflict, ResourceKeys.Conflict(a, b));
        Assert.Equal(conflict, ResourceKeys.Conflict(b, a));
    }

    [Fact]
    public void Keys_are_validated()
    {
        Assert.True(ResourceKeys.Valid(Adv));
        Assert.True(ResourceKeys.Valid("service:Spooler"));
        foreach (var bad in new[] { "", "nonamespace", ":x", "reg:", " reg:x", "REG:x", "reg:a\nb" }) Assert.False(ResourceKeys.Valid(bad));
    }

    [Fact]
    public void Locks_are_taken_atomically_or_not_at_all()
    {
        var locks = new ResourceLocks();
        Assert.True(locks.TryAcquire("p1", ["a:1", "b:1"]));
        Assert.False(locks.TryAcquire("p2", ["c:1", "b:1:x"])); // one overlap refuses the whole set
        Assert.True(locks.TryAcquire("p3", ["c:1"]));            // and nothing of it was taken
        Assert.False(locks.TryAcquire("p1", ["z:1"]));           // an owner holds one set
        locks.Release("p1");
        Assert.True(locks.TryAcquire("p2", ["b:1:x"]));
    }

    [Theory]
    [InlineData(Adv + @"\HIDEFILEEXT", Adv + @"\HIDEFILEEXT", true)]   // the same value
    [InlineData(Adv, Adv + @"\HIDEFILEEXT", true)]                     // parent held, child asked
    [InlineData(Adv + @"\HIDEFILEEXT", Adv, true)]                     // child held, parent asked
    [InlineData(Adv + @"\HIDDEN", Adv + @"\HIDEFILEEXT", false)]       // siblings
    public async Task Two_handlers_on_overlapping_resources_exclude_each_other(string held, string asked, bool busy)
    {
        var a = new FakeHoldHandler("test.hold_a");
        var b = new FakeHoldHandler("test.hold_b");
        await using var host = TestHost.StartActions([new FakeActionModule(a, b)]);
        var c = await host.ConnectAsync(minor: 3);

        var first = await Preview(c, "test.hold_a", Keys(held));
        Assert.Equal([held], first["resources"]!.AsArray().Select(k => k!.GetValue<string>()));
        await Act.Approve(c, first);
        await a.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await Preview(c, "test.hold_b", Keys(asked));
        if (busy)
        {
            var refused = await c.Fails("action.approve", Act.ApproveParams(second, await Act.Nonce(c, second.Str("planId"))));
            Assert.Equal(ErrorCodes.InvalidState, refused.Code);
            Assert.Equal("action_active", refused.ErrorData.Str("reason"));
            Assert.Equal(0, b.Runs);
            Assert.Equal("awaiting_user", (await c.Call("action.get", new JsonObject { ["planId"] = second.Str("planId") })).Str("status"));
        }
        else
        {
            await Act.Approve(c, second);
            await b.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, b.Runs);
        }
        a.Gate.SetResult();
        b.Gate.TrySetResult();
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("state") == "verified");

        // Released when the run ends: the refused plan can now run.
        if (busy)
        {
            // The run releases its locks right after it journals the result; retry briefly.
            for (var i = 0; ; i++)
            {
                try { await Act.Approve(c, second); break; }
                catch (OptimizationTool.Transport.JsonRpcException) when (i < 50) { await Task.Delay(50); }
            }
            await b.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, b.Runs);
        }
    }

    [Fact]
    public async Task SettingSet_and_another_handler_on_the_same_registry_value_exclude_each_other()
    {
        var registry = new MemoryRegistry();
        var row = SettingsCatalog.Find("explorer.file_extensions")!;
        registry.Set(row, RegData.Dword(1));
        var hold = new FakeHoldHandler("test.hold_a");
        var setting = new SetSettingHandler(registry, new NoNotify(), () => 26200);
        await using var host = TestHost.StartActions([new FakeActionModule(hold, setting)]);
        var c = await host.ConnectAsync(minor: 3);

        await Act.Approve(c, await Preview(c, "test.hold_a", Keys(Adv)));
        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var plan = await Preview(c, "setting.set", new JsonObject { ["settingId"] = row.SettingId, ["value"] = "shown" });
        Assert.Equal([row.Resource], plan["resources"]!.AsArray().Select(k => k!.GetValue<string>()));
        var refused = await c.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(c, plan.Str("planId"))));
        Assert.Equal("action_active", refused.ErrorData.Str("reason"));
        Assert.Equal(0, registry.Writes);
        hold.Gate.SetResult();
    }

    [Fact]
    public async Task SettingSet_end_to_end_with_undo_and_drift()
    {
        var registry = new MemoryRegistry();
        var row = SettingsCatalog.Find("explorer.hidden_files")!;
        var setting = new SetSettingHandler(registry, new NoNotify(), () => 26200);
        await using var host = TestHost.StartActions([new FakeActionModule(setting)]);
        var c = await host.ConnectAsync(minor: 3);

        var catalogRow = (await c.Call("action.catalog"))["actions"]!.AsArray().Single(a => a.Str("actionId") == "setting.set")!;
        Assert.Equal("settings", catalogRow.Str("group"));
        Assert.False(string.IsNullOrEmpty(catalogRow.Str("what")));
        Assert.Empty(catalogRow["flags"]!.AsArray());

        // Absent → shown, then undo restores absence.
        var plan = await Preview(c, "setting.set", new JsonObject { ["settingId"] = row.SettingId, ["value"] = "shown" });
        Assert.Equal("awaiting_user", plan.Str("status"));
        Assert.Equal("Not set", plan["rows"]![0].Str("before"));
        Assert.Equal("Shown", plan["rows"]![0].Str("after"));
        var op = await Act.Approve(c, plan);
        Assert.Equal("verified", op.Str("state"));
        Assert.Equal(1L, registry.Read(row.Location).Data!.GetValue<long>());

        var undo = await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u1", ["operationId"] = op.Str("operationId") });
        Assert.Equal("awaiting_user", undo.Str("status"));
        Assert.Equal("Not set", undo["rows"]![0].Str("after"));
        Assert.Equal("verified", (await Act.Approve(c, undo)).Str("state"));
        Assert.False(registry.Read(row.Location).Present);

        // A verified change that someone else changed since: undo is refused.
        var op2 = await Act.Approve(c, await Preview(c, "setting.set", new JsonObject { ["settingId"] = row.SettingId, ["value"] = "hidden" }));
        Assert.Equal("verified", op2.Str("state"));
        registry.Set(row, RegData.Dword(1));
        var drift = await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u2", ["operationId"] = op2.Str("operationId") });
        Assert.Equal("ineligible", drift.Str("status"));
        Assert.Equal("changed_since", drift["ineligible"].Str("code"));

        // An agent-style request cannot smuggle a preimage.
        var smuggled = await Preview(c, "setting.set", new JsonObject
        {
            ["settingId"] = row.SettingId, ["restore"] = new JsonObject { ["present"] = true, ["type"] = "dword", ["data"] = 7 },
        });
        Assert.Equal("invalid_arguments", smuggled["ineligible"].Str("code"));
        Assert.Equal(3, registry.Writes);
    }

    [Fact]
    public async Task Plans_carry_flags_what_group_and_resources_and_the_hash_covers_them()
    {
        var pc = new FakePc { SchemeFlags = [ActionFlags.Restart, ActionFlags.Irreversible] };
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c);
        Assert.Equal(["irreversible", "restart"], plan["flags"]!.AsArray().Select(f => f!.GetValue<string>())); // canonical order
        Assert.Equal(["power:ACTIVE"], plan["resources"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal("power", plan.Str("group"));
        Assert.False(string.IsNullOrEmpty(plan.Str("what")));
        SchemaTests.AssertValid("action-plan", plan);

        Assert.Equal(plan.Str("planHash"), ActionService.Hash(plan));
        foreach (var (field, value) in new (string, JsonNode)[] { ("flags", new JsonArray("restart")), ("resources", new JsonArray("power:OTHER")), ("privilege", "admin") })
        {
            var changed = (JsonObject)plan.DeepClone();
            changed[field] = value;
            Assert.NotEqual(plan.Str("planHash"), ActionService.Hash(changed));
        }
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("privilege")]
    [InlineData("resources")]
    public async Task Flag_privilege_or_resource_drift_makes_the_plan_stale(string what)
    {
        var pc = new FakePc();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c);
        var nonce = await Act.Nonce(c, plan.Str("planId"));
        switch (what)
        {
            case "flags": pc.SchemeFlags = [ActionFlags.DataLoss]; break;
            case "privilege": pc.SchemePrivilege = "admin"; break;
            default: pc.SchemeResources = ["power:ACTIVE", "power:OTHER"]; break;
        }
        var stale = await c.Fails("action.approve", Act.ApproveParams(plan, nonce));
        Assert.Equal(ErrorCodes.StaleRevision, stale.Code);
        Assert.Equal("stale_plan", stale.ErrorData.Str("reason"));
        Assert.Equal(0, pc.Writes);
    }

    [Fact]
    public async Task A_resolution_with_an_unknown_flag_is_never_approvable()
    {
        var pc = new FakePc { SchemeFlags = ["reboot_now"] };
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c);
        Assert.Equal("ineligible", plan.Str("status"));
        Assert.Equal("unavailable", plan["ineligible"].Str("code"));
    }
}

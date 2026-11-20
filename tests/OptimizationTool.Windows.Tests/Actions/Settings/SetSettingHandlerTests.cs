using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Settings;

namespace OptimizationTool.Windows.Tests.Actions.Settings;

/// <summary>An in-memory registry. Never touches Windows.</summary>
internal sealed class FakeRegistryStore : IRegistryStore
{
    public readonly Dictionary<string, RegData> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Policies = new(StringComparer.OrdinalIgnoreCase);
    public int Writes, Deletes;
    /// <summary>Replaces what a write leaves behind (default: the written data).</summary>
    public Func<RegData, RegData>? OnWrite;

    public static string K(RegLocation at) => $"{at.Hive}:{at.View}:{at.Key}\\{at.Name}";
    public void Set(RegLocation at, RegData d) { if (d.Present) Values[K(at)] = d; else Values.Remove(K(at)); }
    public RegData Read(RegLocation at) => Values.TryGetValue(K(at), out var d) ? d : RegData.Absent;

    public RegData Write(RegLocation at, string type, JsonNode data)
    {
        Writes++;
        var written = new RegData(true, type, data.DeepClone());
        Set(at, OnWrite?.Invoke(written) ?? written);
        return Read(at);
    }

    public RegData Delete(RegLocation at)
    {
        Deletes++;
        Set(at, OnWrite?.Invoke(RegData.Absent) ?? RegData.Absent);
        return Read(at);
    }

    public bool Exists(RegLocation at) => Policies.Contains(K(at));

    public IReadOnlyList<string> ValueNames(RegLocation key)
    {
        var prefix = $"{key.Hive}:{key.View}:{key.Key}\\";
        return Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0)
            .Select(k => k[prefix.Length..]).ToList();
    }
}

internal sealed class FakeNotifier : ISettingNotifier
{
    public readonly List<string> Calls = [];
    public void Notify(string kind) => Calls.Add(kind);
}

public class SetSettingHandlerTests
{
    private const int Build = 26200;

    public static TheoryData<string> RowIds()
    {
        var data = new TheoryData<string>();
        foreach (var r in SettingsCatalog.Rows) data.Add(r.SettingId);
        return data;
    }

    private static (SetSettingHandler H, FakeRegistryStore Store, FakeNotifier Notifier) New(int build = Build)
    {
        var store = new FakeRegistryStore();
        var notifier = new FakeNotifier();
        return (new SetSettingHandler(store, notifier, () => build), store, notifier);
    }

    private static ActionRequest Req(string settingId, string? value) =>
        new(SetSettingHandler.Id, null, new JsonObject { ["settingId"] = settingId, ["value"] = value }, null);

    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", r.Target, r.Before, r.After, null);

    private static RegData DataOf(SettingRow row, SettingValue v) => v.Data is null ? RegData.Absent : new RegData(true, row.Type, v.Data.DeepClone());

    /// <summary>Plans and execution go through JSON text, as the engine stores them.</summary>
    private static ActionExecution Stored(ActionResolution r)
    {
        JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
        return new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);
    }

    [Fact]
    public void Catalog_is_user_scope_with_unique_ids_sources_and_valid_resources()
    {
        Assert.NotEmpty(SettingsCatalog.Rows);
        Assert.Equal(SettingsCatalog.Rows.Count, SettingsCatalog.Rows.Select(r => r.SettingId).Distinct().Count());
        Assert.Equal(SettingsCatalog.Rows.Count, SettingsCatalog.Rows.Select(r => r.Resource).Distinct().Count());
        foreach (var r in SettingsCatalog.Rows)
        {
            Assert.Contains((r.Scope, r.Hive, r.View), new[] { ("user", "HKCU", 64), ("machine", "HKLM", 64) });
            Assert.Contains(r.Type, new[] { "dword", "string" });
            Assert.Contains(r.Notify, new[] { "none", "setting_change", "explorer" });
            Assert.True(r.Values.Count >= 2);
            Assert.Equal(r.Values.Count, r.Values.Select(v => v.Value).Distinct().Count());
            Assert.True(r.AbsentMeans is null || r.Values.Any(v => v.Value == r.AbsentMeans));
            Assert.NotEmpty(r.Source);
            Assert.NotNull(ActionFlags.Union(r.Flags));
            Assert.True(ResourceKeys.Valid(r.Resource));
            Assert.StartsWith(r.Scope == "machine" ? @"reg:64:HKLM\" : @"reg:64:HKCU\SOFTWARE\", r.Resource);
            Assert.Equal(r.Resource[4..].ToUpperInvariant(), r.Resource[4..]);
            Assert.True(r.MinBuild >= SettingsCatalog.Windows11);
            Assert.All(r.PolicyKeys, p => Assert.Contains(@"\Policies\", p.Key, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory, MemberData(nameof(RowIds))]
    public async Task Every_listed_value_round_trips(string id)
    {
        var row = SettingsCatalog.Find(id)!;
        foreach (var value in row.Values)
        {
            var (h, store, notifier) = New();
            // Start from another listed value, so the target is a change.
            var other = row.Values.First(v => v != value);
            store.Set(row.Location, DataOf(row, other));

            var r = h.Resolve(Req(id, value.Value));
            Assert.True(r.Eligible, $"{id}={value.Value}: {r.IneligibleCode} {r.IneligibleMessage}");
            Assert.Equal(value.Value, r.After["value"]!.GetValue<string>());
            Assert.Equal(new PlanRow(row.Label, other.Label, value.Label), Assert.Single(r.Rows));
            Assert.Equal([row.Resource], r.Resources);

            var outcome = h.Execute(Stored(r));
            Assert.Equal("verified", outcome.State);
            Assert.Equal(DataOf(row, value).Present, store.Read(row.Location).Present);
            Assert.True(JsonNode.DeepEquals(r.After, await h.ReadTargetAsync(r.Target, CancellationToken.None)));
            Assert.Equal(row.Notify == "none" ? Array.Empty<string>() : new[] { row.Notify }, notifier.Calls);

            // The same value again: already set, nothing written.
            var writes = store.Writes + store.Deletes;
            Assert.Equal("already_set", h.Resolve(Req(id, value.Value)).IneligibleCode);
            Assert.Equal(writes, store.Writes + store.Deletes);
        }
    }

    [Theory, MemberData(nameof(RowIds))]
    public void Absent_to_value_and_back_to_absent_through_undo(string id)
    {
        var row = SettingsCatalog.Find(id)!;
        var (h, store, _) = New();
        var value = row.Values.First(v => v.Data is not null);

        var r = h.Resolve(Req(id, value.Value));
        Assert.True(r.Eligible);
        Assert.False(r.Before["present"]!.GetValue<bool>());
        Assert.Equal(row.Values.FirstOrDefault(v => v.Data is null)?.Label ?? "Not set", r.Rows[0].Before);
        Assert.Equal("verified", h.Execute(Stored(r)).State);
        Assert.True(store.Read(row.Location).Present);

        var undo = h.UndoRequest(r.Target, r.Before)!;
        Assert.True(undo.Undo);
        var u = h.Resolve(undo);
        Assert.True(u.Eligible, u.IneligibleMessage);
        Assert.Equal(r.After.ToJsonString(), u.Before.ToJsonString());
        Assert.Equal("verified", h.Execute(Stored(u)).State);
        Assert.False(store.Read(row.Location).Present);
    }

    [Theory, MemberData(nameof(RowIds))]
    public void Undo_restores_unlisted_previous_data_exactly(string id)
    {
        var row = SettingsCatalog.Find(id)!;
        var (h, store, _) = New();
        var odd = row.Type == "dword" ? RegData.Dword(77) : RegData.String("odd");
        store.Set(row.Location, odd);
        var r = h.Resolve(Req(id, row.Values.First(v => v.Data is not null).Value));
        Assert.True(r.Eligible);
        Assert.Equal(row.Type == "dword" ? "Value 77" : "Another value", r.Rows[0].Before);
        h.Execute(Stored(r));
        var u = h.Resolve(h.UndoRequest(r.Target, r.Before)!);
        Assert.Equal("verified", h.Execute(Stored(u)).State);
        Assert.Equal(odd.Data!.ToJsonString(), store.Read(row.Location).Data!.ToJsonString());
    }

    [Theory, MemberData(nameof(RowIds))]
    public void Policy_override_is_managed(string id)
    {
        var row = SettingsCatalog.Find(id)!;
        foreach (var policy in row.PolicyKeys)
        {
            var (h, store, _) = New();
            store.Policies.Add(FakeRegistryStore.K(policy));
            var r = h.Resolve(Req(id, row.Values[0].Value));
            Assert.Equal("managed", r.IneligibleCode);
            Assert.Equal(0, store.Writes);
        }
    }

    [Theory, MemberData(nameof(RowIds))]
    public void Build_below_minimum_is_unsupported(string id)
    {
        var row = SettingsCatalog.Find(id)!;
        var (h, _, _) = New(build: row.MinBuild - 1);
        Assert.Equal("unsupported", h.Resolve(Req(id, row.Values[0].Value)).IneligibleCode);
        var (ok, _, _) = New(build: row.MinBuild);
        Assert.True(ok.Resolve(Req(id, row.Values.First(v => v.Data is not null).Value)).Eligible);
    }

    [Theory, MemberData(nameof(RowIds))]
    public void Value_not_in_the_list_is_rejected(string id)
    {
        var (h, store, _) = New();
        foreach (var bad in new[] { "maybe", "", "1", "0" })
            Assert.Equal("invalid_arguments", h.Resolve(Req(id, bad)).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(Req(id, null)).IneligibleCode);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Unknown_ids_extra_arguments_and_caller_restores_are_rejected()
    {
        var (h, store, _) = New();
        Assert.Equal("invalid_arguments", h.Resolve(Req("privacy.not_a_setting", "on")).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(SetSettingHandler.Id, null, null, null)).IneligibleCode);
        var extra = Req("appearance.transparency", "off");
        extra.Parameters!["key"] = @"HKCU\Software\Evil";
        Assert.Equal("invalid_arguments", h.Resolve(extra).IneligibleCode);

        // A caller (an agent) cannot pass a preimage: only the engine's undo request may restore one.
        var restore = new JsonObject { ["settingId"] = "appearance.transparency", ["restore"] = new JsonObject { ["present"] = true, ["type"] = "dword", ["data"] = 12345 } };
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(SetSettingHandler.Id, null, restore, null)).IneligibleCode);
        // Even an engine undo cannot restore data of another type.
        var wrongType = new JsonObject { ["settingId"] = "appearance.transparency", ["restore"] = new JsonObject { ["present"] = true, ["type"] = "string", ["data"] = "x" } };
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(SetSettingHandler.Id, null, wrongType, null) { Undo = true }).IneligibleCode);
        Assert.Equal("wrong_action", h.Resolve(new ActionRequest("power.select_scheme", null, Req("appearance.transparency", "off").Parameters, null)).IneligibleCode);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void A_value_of_another_type_is_not_touched()
    {
        var (h, store, _) = New();
        var row = SettingsCatalog.Find("appearance.transparency")!;
        store.Set(row.Location, RegData.String("1"));
        Assert.Equal("unexpected_value", h.Resolve(Req(row.SettingId, "off")).IneligibleCode);
    }

    [Fact]
    public void Execute_reports_failed_and_indeterminate_without_notifying()
    {
        var row = SettingsCatalog.Find("explorer.file_extensions")!;
        var (h, store, notifier) = New();
        store.Set(row.Location, RegData.Dword(1));
        var r = h.Resolve(Req(row.SettingId, "shown"));

        store.OnWrite = _ => RegData.Dword(1); // Windows kept the old value
        var failed = h.Execute(Stored(r));
        Assert.Equal(("failed", "not_applied"), (failed.State, failed.Code));

        store.OnWrite = _ => RegData.Dword(9); // a third value
        Assert.Equal("indeterminate", h.Execute(Stored(r)).State);
        Assert.Empty(notifier.Calls);

        store.OnWrite = null;
        var ok = h.Execute(Stored(r));
        Assert.Equal("verified", ok.State);
        Assert.Equal("File name extensions is Shown, read back from Windows.", ok.ReceiptText);
        Assert.Equal(["explorer"], notifier.Calls);
    }

    [Fact]
    public void Metadata_matches_the_seam()
    {
        var (h, _, _) = New();
        Assert.Equal(("setting.set", "none", "setting", "settings", true, true), (h.ActionId, h.Privilege, h.Kind, h.Group, h.SupportsUndo, h.TakesLease));
        Assert.Empty(h.Flags);
        var ids = h.ParameterSchema!["properties"]!["settingId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(SettingsCatalog.Rows.Select(r => r.SettingId), ids);
        Assert.Equal(@"reg:64:HKCU\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\EXPLORER\ADVANCED\HIDEFILEEXT", SettingsCatalog.Find("explorer.file_extensions")!.Resource);
    }

    [Fact]
    public void Existing_handlers_declare_lease_and_resources()
    {
        var context = new ActionContext(new NoElevation(), EnableFirewallHandler: false);
        var all = OptimizationTool.Windows.WindowsModules.Actions.SelectMany(m => m.Handlers(context)).ToDictionary(h => h.ActionId);
        Assert.True(all["power.select_scheme"].TakesLease);
        Assert.True(all["defender.quick_scan"].TakesLease);
        Assert.True(all["setting.set"].TakesLease);
        Assert.False(all["firewall.enable_public"].TakesLease);
        Assert.Contains(ActionFlags.Long, all["defender.full_scan"].Flags);
        foreach (var handler in all.Values)
        {
            Assert.NotNull(ActionFlags.Union(handler.Flags));
            Assert.False(string.IsNullOrWhiteSpace(handler.What));
            Assert.False(string.IsNullOrWhiteSpace(handler.Group));
        }
    }

    private sealed class NoElevation : IElevation
    {
        public Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct) =>
            throw new InvalidOperationException("Live tests must never execute.");
        public ElevatedJournal? Journal(string planId) => null;
    }
}

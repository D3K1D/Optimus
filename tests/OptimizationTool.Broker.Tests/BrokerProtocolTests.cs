using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Windows;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

/// <summary>Protocol v2 shapes and limits (m9 §3.5), and the administrator handler set: identical in host and broker.</summary>
public sealed class BrokerProtocolTests
{
    [Fact]
    public void Administrator_handlers_are_the_same_set_and_versions_in_host_and_broker()
    {
        var host = WindowsModules.Actions.SelectMany(m => m.Handlers(new ActionContext(null!, false))).Where(h => h.CanElevate).ToList();
        var broker = BrokerRegistry.Create();
        Assert.Equal(host.Select(h => h.ActionId).Order(), broker.Select(h => h.ActionId).Order());
        Assert.Equal(BrokerCatalog.ActionIds.Order(), broker.Select(h => h.ActionId).Order());
        Assert.Equal(BrokerElevation.AllowedActions.Order(), broker.Select(h => h.ActionId).Order());
        foreach (var b in broker)
        {
            var h = host.Single(x => x.ActionId == b.ActionId);
            Assert.Equal(h.Version, b.Version);
            Assert.Equal(h.Label, b.Label);
            Assert.Equal(ActionFlags.Union(h.Flags), ActionFlags.Union(b.Flags));
        }
        Assert.Equal(broker.Count, broker.Select(h => h.ActionId).Distinct().Count());
    }

    [Fact]
    public void Request_round_trips_with_every_field()
    {
        var steps = new[]
        {
            Kit.Firewall(1),
            Kit.Step(3, "setting.set", "1.0.0", new JsonObject { ["type"] = "setting", ["id"] = "x" }, new JsonObject { ["present"] = false },
                new JsonObject { ["present"] = true }, new JsonObject { ["settingId"] = "x", ["restore"] = new JsonObject { ["present"] = false } }, new ElevatedUndoOf("rcpt-00000000000000aa", 2)),
        };
        var req = new BrokerRequest(Kit.Nonce(), "plan-1", "hash-1", steps);
        var json = req.ToJson();
        Assert.Equal(2, json["version"]!.GetValue<int>());
        var back = BrokerRequest.Parse(JsonNode.Parse(json.ToJsonString()))!;
        Assert.Equal(json.ToJsonString(), back.ToJson().ToJsonString());
        Assert.Equal(new ElevatedUndoOf("rcpt-00000000000000aa", 2), back.Steps[1].UndoOf);
        Assert.Null(back.Steps[0].UndoOf);
    }

    [Fact]
    public void Response_round_trips_and_clips()
    {
        var res = new BrokerResponse(Kit.Nonce(), [new BrokerStepOutcome(1, "verified", new JsonObject { ["v"] = 1 }, "ok"),
            new BrokerStepOutcome(2, "skipped", [], "Not started."), new BrokerStepOutcome(3, "declined", [], "No.", "broker_cancelled", new string('m', 500))], "rcpt-0000000000000001");
        var back = BrokerResponse.Parse(JsonNode.Parse(res.ToJson().ToJsonString()))!;
        Assert.Equal(["verified", "skipped", "declined"], back.Steps.Select(s => s.State));
        Assert.Equal(300, back.Steps[2].Message!.Length);
        Assert.Equal("rcpt-0000000000000001", back.ReceiptId);
        var bad = res.ToJson();
        bad["steps"]![0]!["state"] = "done";
        Assert.Null(BrokerResponse.Parse(bad));
    }

    public static TheoryData<string> BadRequests => new()
    {
        "seven-steps", "no-steps", "n-not-ascending", "n-zero", "unknown-top-key", "unknown-step-key", "v1", "bad-hash", "bad-undo", "parameters-array", "long-action",
    };

    [Theory, MemberData(nameof(BadRequests))]
    public void Out_of_shape_requests_are_refused(string what)
    {
        var json = Kit.Request(null, Kit.Firewall(1), Kit.Firewall(2)).ToJson();
        var steps = json["steps"]!.AsArray();
        switch (what)
        {
            case "seven-steps": json = Kit.Request(null, [.. Enumerable.Range(1, 6).Select(n => Kit.Firewall(n))]).ToJson(); json["steps"]!.AsArray().Add(Kit.Firewall(7).ToJson()); break;
            case "no-steps": steps.Clear(); break;
            case "n-not-ascending": steps[1]!["n"] = 1; break;
            case "n-zero": steps[0]!["n"] = 0; break;
            case "unknown-top-key": json["shell"] = "cmd"; break;
            case "unknown-step-key": steps[0]!["command"] = "x"; break;
            case "v1": json["version"] = 1; break;
            case "bad-hash": steps[0]!["contentHash"] = "abc"; break;
            case "bad-undo": steps[0]!["undoOf"] = new JsonObject { ["receiptId"] = "../../x", ["n"] = 1 }; break;
            case "parameters-array": steps[0]!["parameters"] = new JsonArray(1); break;
            case "long-action": steps[0]!["actionId"] = new string('a', 65); break;
        }
        Assert.Null(BrokerRequest.Parse(JsonNode.Parse(json.ToJsonString())));
    }

    [Fact]
    public async Task Frames_over_64_KB_or_deeper_than_16_are_refused()
    {
        var big = new JsonObject { ["type"] = "request", ["pad"] = new string('x', BrokerProtocol.MaxFrameBytes) };
        await Assert.ThrowsAsync<InvalidDataException>(() => BrokerProtocol.WriteAsync(new MemoryStream(), big, CancellationToken.None));

        var deep = new StringBuilder();
        for (var i = 0; i < 20; i++) deep.Append("{\"a\":");
        deep.Append('1').Append('}', 20);
        var payload = Encoding.UTF8.GetBytes(deep.ToString());
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((uint)payload.Length));
        ms.Write(payload);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => BrokerProtocol.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public void Stop_frame_needs_the_sessions_nonce()
    {
        Assert.True(BrokerStop.Is(BrokerStop.ToJson(Kit.Nonce()), Kit.Nonce()));
        Assert.False(BrokerStop.Is(BrokerStop.ToJson(Kit.Nonce('b')), Kit.Nonce()));
        Assert.True(BrokerProgress.Is(BrokerProgress.ToJson(Kit.Nonce(), 1, "Making a restore point")));
    }

    [Fact]
    public void Content_hash_covers_parameters_and_version_but_not_labels()
    {
        var a = BrokerContent.Hash(FirewallPlan.ActionId, "1.0.0", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null);
        Assert.Equal(a, BrokerContent.Hash(FirewallPlan.ActionId, "1.0.0", new JsonObject { ["id"] = "public", ["label"] = "Other" }, FirewallPlan.State(false, false), FirewallPlan.State(true, true), null));
        Assert.NotEqual(a, BrokerContent.Hash(FirewallPlan.ActionId, "1.0.1", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null));
        Assert.NotEqual(a, BrokerContent.Hash(FirewallPlan.ActionId, "1.0.0", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), new JsonObject { ["mode"] = "enable" }));
    }

    [Fact]
    public void Journal_files_round_trip_for_recovery_and_undo()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(new ElevatedJournal(null, []).Steps, BrokerJournalFiles.Read(root, "plan-1")!.Steps);
            const string id = "rcpt-00000000000000c1";
            var key = new byte[32];
            key[0] = 7;
            void Write(string name, JsonObject record) => File.WriteAllBytes(Path.Combine(root, name), BrokerJournalFiles.Seal(name, record, key));
            Write(BrokerJournalFiles.SessionFile(id), new JsonObject
            {
                ["receiptId"] = id, ["planId"] = "plan-1", ["at"] = "2026-11-12T00:00:00.000Z",
                ["steps"] = new JsonArray(new JsonObject { ["n"] = 2, ["actionId"] = FirewallPlan.ActionId }),
            });
            Write(BrokerJournalFiles.PreparedFile(id, 2), new JsonObject
            {
                ["receiptId"] = id, ["n"] = 2, ["actionId"] = FirewallPlan.ActionId, ["target"] = FirewallPlan.Target(), ["before"] = FirewallPlan.State(false, false),
                ["after"] = FirewallPlan.State(true, true),
            });
            Write(BrokerJournalFiles.OutcomeFile(id, 2), new JsonObject { ["receiptId"] = id, ["n"] = 2, ["state"] = "verified", ["observed"] = FirewallPlan.State(true, true) });
            var j = BrokerJournalFiles.Read(root, "plan-1")!;
            Assert.Equal(id, j.ReceiptId);
            var s = Assert.Single(j.Steps);
            Assert.Equal((2, true, "verified"), (s.N, s.Prepared, s.State));
            var rec = BrokerJournalFiles.Find(root, id, 2, key)!;
            Assert.Equal(FirewallPlan.ActionId, rec.ActionId);
            Assert.Equal("verified", rec.State);
            Assert.Null(BrokerJournalFiles.Find(root, id, 1, key));
            Assert.Null(BrokerJournalFiles.Find(root, "../" + id, 2, key));
            Assert.Null(BrokerJournalFiles.Find(root, id, 2, new byte[32])); // another key: nothing is authentic
            Assert.Empty(BrokerJournalFiles.Read(root, "plan-2")!.Steps);
        }
        finally { Directory.Delete(root, true); }
    }
}

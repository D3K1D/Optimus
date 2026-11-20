using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Broker.Reads;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// Administrator checks inside the helper (m9 §17.2) with fake adapters, window and journal: nothing is read from this PC,
/// no window is shown, nothing is elevated.
/// </summary>
public sealed class ReadSessionTests
{
    internal sealed class FakeAdapter(string id, Func<AdminReadInput, CancellationToken, AdminReadOutput> read, string? version = null) : IAdminReadAdapter
    {
        public readonly List<AdminReadInput> Calls = [];
        public string Id => id;
        public string Version => version ?? AdminChecks.Find(id)?.Version ?? "1";
        public AdminReadOutput Read(AdminReadInput input, CancellationToken ct)
        {
            lock (Calls) Calls.Add(input);
            return read(input, ct);
        }
    }

    internal sealed class ReadWindow : IBrokerReadConfirm
    {
        public bool Read = true;
        /// <summary>When set, the window stays open until this completes or the helper closes it (a stop).</summary>
        public TaskCompletionSource? Hold;
        public readonly List<BrokerReadView> Shown = [];
        public async Task<bool> AskAsync(BrokerReadView view, CancellationToken ct)
        {
            Shown.Add(view);
            if (Hold is { } h) await h.Task.WaitAsync(ct);
            return Read;
        }
    }

    internal sealed class ReadJournal : IBrokerReadJournal
    {
        public bool Fail;
        public readonly List<BrokerReadRequest> Begins = [];
        public readonly List<(string Id, string State, IReadOnlyList<(int N, string State, string? Code)> Results)> Ends = [];
        public string BeginRead(BrokerReadRequest request, DateTimeOffset at)
        {
            if (Fail) throw new IOException("disk full");
            Begins.Add(request);
            return "rcpt-00000000000000b1";
        }
        public void EndRead(string receiptId, string state, IReadOnlyList<(int N, string State, string? Code)> results, DateTimeOffset at) => Ends.Add((receiptId, state, results));
    }

    internal static JsonObject Threat(string name = "Trojan:Win32/Test", string severity = "high") => new()
    {
        ["threatName"] = name, ["category"] = "trojan", ["severity"] = severity, ["detectedAt"] = "2026-11-18T10:05Z", ["action"] = "quarantine",
        ["succeeded"] = true, ["foundBy"] = "real_time", ["affectedItems"] = 2,
    };

    internal static JsonObject Disk(int n = 0) => new()
    {
        ["disk"] = n, ["model"] = "Fast NVMe 1TB", ["counters"] = true, ["temperatureC"] = 41, ["wearPercent"] = 3, ["readErrors"] = 0, ["writeErrors"] = 0, ["powerOnHours"] = 1200,
    };

    private sealed class Setup
    {
        public readonly Rig Rig = new();
        public readonly ReadWindow Window = new();
        public readonly ReadJournal Journal = new();
        public FakeAdapter Defender = new(AdminChecks.DefenderHistory, (_, _) => AdminReadOutput.Ok([Threat(), Threat("Virus:DOS/Test", "severe")], 2, true));
        public FakeAdapter Storage = new(AdminChecks.StorageHealth, (_, _) => AdminReadOutput.Ok([Disk()], 1, true));
        public FakeAdapter Encryption = new(AdminChecks.Encryption, (_, _) => AdminReadOutput.Unsupported("bitlocker_unavailable"));
        public TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
        public bool Reads = true;

        public BrokerSession Session(string? nonce = null) =>
            new(new BrokerSessionOptions(nonce ?? Kit.Nonce(), Kit.HostImage, Kit.Self, Rig.Handlers, Rig.Journal, Rig.Ledger, Rig.Confirm)
            {
                Reads = Reads ? new BrokerReadOptions([Defender, Storage, Encryption], Window, Journal) { CheckTimeout = CheckTimeout } : null,
            });

        public async Task<(BrokerResult Result, List<BrokerReadFrames.Frame> Frames)> Run(JsonObject request, Func<Duplex, Task>? during = null, PeerInfo? server = null)
        {
            var pipe = await Duplex.WithFrames(request);
            var running = Session().RunAsync(pipe, server ?? Kit.Host, CancellationToken.None);
            if (during is not null) await during(pipe);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
            return (result, await Frames(pipe));
        }

        public int Reads_ => Defender.Calls.Count + Storage.Calls.Count + Encryption.Calls.Count;
    }

    internal static async Task<List<BrokerReadFrames.Frame>> Frames(Duplex pipe, string? nonce = null)
    {
        var list = new List<BrokerReadFrames.Frame>();
        pipe.Output.Position = 0;
        while (pipe.Output.Position < pipe.Output.Length)
            list.Add(BrokerReadFrames.Parse(await BrokerProtocol.ReadAsync(pipe.Output, CancellationToken.None), nonce ?? Kit.Nonce()) ?? throw new InvalidDataException("bad frame"));
        return list;
    }

    internal static JsonObject Request(string? nonce = null, params (string Id, int? Window)[] checks)
    {
        if (checks.Length == 0) checks = [(AdminChecks.DefenderHistory, 30), (AdminChecks.StorageHealth, null)];
        return new BrokerReadRequest(nonce ?? Kit.Nonce(), "req-0001",
            [.. checks.Select((c, i) => new BrokerReadCheck(i + 1, c.Id, AdminChecks.Find(c.Id)?.Version ?? "1", c.Window))]).ToJson();
    }

    private static string Kinds(List<BrokerReadFrames.Frame> f) =>
        string.Join(" ", f.Select(x => x.Kind switch { "result" => $"r{x.N}:{x.State}{(x.Code is { } c ? "/" + c : "")}", "end" => $"end:{x.State}{(x.Code is { } c ? "/" + c : "")}", _ => x.Kind }));

    private static void Sequenced(List<BrokerReadFrames.Frame> frames) =>
        Assert.Equal(Enumerable.Range(1, frames.Count), frames.Select(f => f.Seq));

    // ---------------------------------------------------------------- the session

    [Fact]
    public async Task Reads_each_check_in_order_with_one_result_each_and_read_end_last()
    {
        var s = new Setup();
        var (result, frames) = await s.Run(Request());
        Assert.Equal("started r1:ok r2:ok end:complete", Kinds(frames));
        Sequenced(frames);
        Assert.All(frames, f => Assert.Equal("req-0001", f.RequestId));
        Assert.Equal("complete", result.Code);
        Assert.False(result.Wrote);
        Assert.Equal(2, frames[1].Items.Count);
        Assert.Equal(new BrokerReadCoverage(true, 2, 2), frames[1].Coverage);
        Assert.True(AdminChecks.Conforms(AdminChecks.Find(AdminChecks.StorageHealth)!, frames[2].Items[0]));
        // The window from the helper's own catalog only; the adapter got the window it was asked for.
        var view = Assert.Single(s.Window.Shown);
        Assert.Equal(["Defender detection history", "Disk health"], view.Checks.Select(c => c.Label));
        Assert.Equal(30, Assert.Single(s.Defender.Calls).WindowDays);
        Assert.Null(Assert.Single(s.Storage.Calls).WindowDays);
        Assert.Empty(s.Encryption.Calls); // only what is on the window is read
        // One journal line before reading and its outcome after.
        Assert.Equal("req-0001", Assert.Single(s.Journal.Begins).RequestId);
        var end = Assert.Single(s.Journal.Ends);
        Assert.Equal("complete", end.State);
        Assert.Equal([(1, "ok", (string?)null), (2, "ok", null)], end.Results);
        Assert.Contains(Kit.Nonce(), s.Rig.Ledger.Used);
    }

    [Fact]
    public async Task A_null_window_takes_the_default_and_unsupported_is_its_own_state()
    {
        var s = new Setup();
        var (_, frames) = await s.Run(Request(null, (AdminChecks.DefenderHistory, null), (AdminChecks.Encryption, null)));
        Assert.Equal("started r1:ok r2:unsupported/bitlocker_unavailable end:complete", Kinds(frames));
        Assert.Equal(30, s.Defender.Calls[0].WindowDays);
        Assert.Equal(new BrokerReadCoverage(false, 0, 0), frames[2].Coverage);
    }

    [Fact]
    public async Task A_replayed_nonce_reads_nothing()
    {
        var s = new Setup();
        s.Rig.Ledger.Used.Add(Kit.Nonce());
        var (result, frames) = await s.Run(Request());
        Assert.Equal("r1:failed/replayed r2:failed/replayed end:refused/replayed", Kinds(frames));
        Assert.Equal("replayed", result.Code);
        Assert.Equal(0, s.Reads_);
        Assert.Empty(s.Window.Shown);
        Assert.Empty(s.Journal.Begins);
    }

    [Fact]
    public async Task A_ledger_that_fails_and_a_journal_that_fails_read_nothing()
    {
        var s = new Setup();
        s.Rig.Ledger.Fail = true;
        Assert.Equal("r1:failed/journal_failed r2:failed/journal_failed end:refused/journal_failed", Kinds((await s.Run(Request())).Frames));
        var t = new Setup();
        t.Journal.Fail = true;
        Assert.Equal("r1:failed/journal_failed r2:failed/journal_failed end:refused/journal_failed", Kinds((await t.Run(Request())).Frames));
        Assert.Equal(0, s.Reads_ + t.Reads_);
    }

    public static TheoryData<string, string> Malformed() => new()
    {
        { "version", "end:refused/bad_request" },
        { "extra_key", "end:refused/bad_request" },
        { "n_order", "end:refused/bad_request" },
        { "no_window_key", "end:refused/bad_request" },
        { "five_checks", "end:refused/bad_request" },
        { "window_out_of_range", "r1:failed/bad_request end:refused/bad_request" },
        { "window_on_a_check_without_one", "r1:failed/bad_request end:refused/bad_request" },
        { "unknown_check", "r1:failed/check_not_allowed end:refused/check_not_allowed" },
        { "version_mismatch", "r1:failed/version_mismatch end:refused/version_mismatch" },
        { "duplicate", "r1:failed/bad_request r2:failed/bad_request end:refused/bad_request" },
        { "wrong_nonce", "r1:failed/bad_nonce end:refused/bad_nonce" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task A_malformed_or_foreign_request_is_refused_before_the_window_and_reads_nothing(string kind, string expected)
    {
        var s = new Setup();
        var req = Request(null, (AdminChecks.DefenderHistory, 30));
        var check = (JsonObject)req["checks"]![0]!;
        switch (kind)
        {
            case "version": req["version"] = 2; break;
            case "extra_key": req["query"] = "SELECT * FROM Win32_Process"; break;
            case "n_order": check["n"] = 2; break;
            case "no_window_key": check.Remove("window"); break;
            case "five_checks":
                req["checks"] = new JsonArray([.. Enumerable.Range(1, 5).Select(i => (JsonNode?)new BrokerReadCheck(i, AdminChecks.StorageHealth, "1", null).ToJson())]);
                break;
            case "window_out_of_range": check["window"] = 91; break;
            case "window_on_a_check_without_one": req = Request(null, (AdminChecks.StorageHealth, 7)); break;
            case "unknown_check": req = Request(null, ("security.tpm", null)); break;
            case "version_mismatch": check["handlerVersion"] = "2"; break;
            case "duplicate": req = Request(null, (AdminChecks.StorageHealth, null), (AdminChecks.StorageHealth, null)); break;
            case "wrong_nonce": req["nonce"] = Kit.Nonce('b'); break;
        }
        var (_, frames) = await s.Run(req);
        Assert.Equal(expected, Kinds(frames));
        Sequenced(frames);
        Assert.Equal(0, s.Reads_);
        Assert.Empty(s.Window.Shown);
    }

    [Fact]
    public async Task Cancel_in_the_helpers_window_reads_nothing_and_ends_declined()
    {
        var s = new Setup();
        s.Window.Read = false;
        var (result, frames) = await s.Run(Request());
        Assert.Equal("r1:cancelled/broker_cancelled r2:cancelled/broker_cancelled end:declined/broker_cancelled", Kinds(frames));
        Assert.Equal("broker_cancelled", result.Code);
        Assert.Equal(0, s.Reads_);
        Assert.Empty(s.Journal.Begins);
    }

    [Fact]
    public async Task The_watchdog_fails_a_stuck_check_skips_the_rest_and_ends_with_timeout()
    {
        var s = new Setup { CheckTimeout = TimeSpan.FromMilliseconds(300) };
        using var never = new ManualResetEventSlim();
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => { never.Wait(TimeSpan.FromSeconds(30)); return AdminReadOutput.Ok([], 0, true); });
        var (result, frames) = await s.Run(Request());
        Assert.Equal("started r1:failed/timeout r2:skipped/timeout end:timeout/timeout", Kinds(frames));
        Assert.Equal("timeout", result.Code); // Program exits at once on this code
        Assert.Empty(s.Storage.Calls); // no further check starts
        Assert.Equal("timeout", Assert.Single(s.Journal.Ends).State);
        never.Set();
    }

    [Fact]
    public async Task Stop_cancels_the_checks_that_have_not_started_and_the_one_in_flight_finishes()
    {
        var s = new Setup();
        using var gate = new ManualResetEventSlim();
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => { gate.Wait(TimeSpan.FromSeconds(10)); return AdminReadOutput.Ok([Threat()], 1, true); });
        var (result, frames) = await s.Run(Request(), async pipe =>
        {
            while (s.Defender.Calls.Count == 0) await Task.Delay(10);
            await pipe.Push(BrokerStop.ToJson(Kit.Nonce()));
            await Task.Delay(200);
            gate.Set();
        });
        Assert.Equal("started r1:ok r2:cancelled/stopped end:stopped", Kinds(frames));
        Assert.Equal("stopped", result.Code);
        Assert.Empty(s.Storage.Calls);
    }

    [Fact]
    public async Task Stop_while_the_window_is_open_closes_it_and_reads_nothing()
    {
        var s = new Setup();
        s.Window.Hold = new TaskCompletionSource();
        var (_, frames) = await s.Run(Request(), async pipe =>
        {
            while (s.Window.Shown.Count == 0) await Task.Delay(10);
            await pipe.Push(BrokerStop.ToJson(Kit.Nonce()));
        });
        Assert.Equal("r1:cancelled/stopped r2:cancelled/stopped end:stopped/stopped", Kinds(frames));
        Assert.Equal(0, s.Reads_);
    }

    public static TheoryData<string> ForeignFrames() => ["request", "script", "read", "stop_other_nonce", "garbage"];

    [Theory]
    [MemberData(nameof(ForeignFrames))]
    public async Task A_write_script_or_any_other_frame_on_a_read_connection_is_refused(string kind)
    {
        var s = new Setup();
        using var gate = new ManualResetEventSlim();
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => { gate.Wait(TimeSpan.FromSeconds(10)); return AdminReadOutput.Ok([Threat()], 1, true); });
        var (result, frames) = await s.Run(Request(), async pipe =>
        {
            while (s.Defender.Calls.Count == 0) await Task.Delay(10);
            switch (kind)
            {
                case "request": await pipe.Push(Kit.Request().ToJson()); break;
                case "script": await pipe.Push(ScriptSessionTests.Request()); break;
                case "read": await pipe.Push(Request()); break;
                case "stop_other_nonce": await pipe.Push(BrokerStop.ToJson(Kit.Nonce('b'))); break;
                default: pipe.PushRaw([5, 0, 0, 0, (byte)'n', (byte)'o', (byte)'p', (byte)'e', (byte)'!']); break;
            }
            await Task.Delay(200);
            gate.Set();
        });
        Assert.Equal("started r1:ok r2:cancelled/bad_frame end:refused/bad_frame", Kinds(frames));
        Assert.Equal("bad_frame", result.Code);
        Assert.False(result.Wrote);
        Assert.Equal(0, s.Rig.Fw.Writes); // the write request in it was never run
        Assert.Empty(s.Storage.Calls);
    }

    [Fact]
    public async Task A_frame_while_the_window_is_open_is_refused_and_nothing_is_read()
    {
        var s = new Setup();
        s.Window.Hold = new TaskCompletionSource();
        var (_, frames) = await s.Run(Request(), async pipe =>
        {
            while (s.Window.Shown.Count == 0) await Task.Delay(10);
            await pipe.Push(Kit.Request().ToJson());
        });
        Assert.Equal("r1:cancelled/bad_frame r2:cancelled/bad_frame end:refused/bad_frame", Kinds(frames));
        Assert.Equal(0, s.Reads_);
    }

    [Fact]
    public async Task A_different_administrator_is_refused_for_reads_as_for_writes()
    {
        var s = new Setup();
        var (result, frames) = await s.Run(Request(), server: Kit.Host with { User = Kit.OtherAdmin });
        Assert.Equal("different_admin", result.Code);
        Assert.Equal("r1:failed/different_admin r2:failed/different_admin end:refused/different_admin", Kinds(frames));
        Sequenced(frames);
        Assert.Equal(0, s.Reads_);
        Assert.Empty(s.Window.Shown);
        Assert.Empty(s.Rig.Ledger.Used); // refused before the nonce is consumed, as for writes
    }

    [Fact]
    public async Task A_helper_without_read_adapters_refuses_read_requests()
    {
        var s = new Setup { Reads = false };
        var (result, frames) = await s.Run(Request());
        Assert.Equal("check_not_allowed", result.Code);
        Assert.Equal("r1:failed/check_not_allowed r2:failed/check_not_allowed end:refused/check_not_allowed", Kinds(frames));
    }

    [Fact]
    public async Task An_adapter_that_throws_is_a_failed_check_and_the_rest_still_run()
    {
        var s = new Setup();
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => throw new InvalidOperationException("boom"));
        var (_, frames) = await s.Run(Request());
        Assert.Equal("started r1:failed/read_error r2:ok end:complete", Kinds(frames));
    }

    // ---------------------------------------------------------------- fields and caps, inside the helper

    [Fact]
    public async Task Fields_not_on_the_list_are_dropped_and_a_bad_item_is_rejected_before_sending()
    {
        var s = new Setup();
        var leaky = Threat();
        leaky["resourcePath"] = @"C:\Users\someone\Downloads\evil.exe";
        leaky["processName"] = "evil.exe";
        var bad = Threat(severity: "catastrophic");
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => AdminReadOutput.Ok([leaky, bad, Threat("PUA:Win32/Test")], 3, true));
        var (_, frames) = await s.Run(Request());
        var r1 = frames[1];
        Assert.Equal(2, r1.Items.Count);
        Assert.All(r1.Items, i => Assert.False(i.ContainsKey("resourcePath") || i.ContainsKey("processName")));
        Assert.All(r1.Items, i => Assert.True(AdminChecks.Conforms(AdminChecks.Find(AdminChecks.DefenderHistory)!, i)));
        Assert.Equal(new BrokerReadCoverage(false, 3, 2), r1.Coverage); // the rejected item shows as incomplete
    }

    [Fact]
    public async Task Items_over_the_checks_cap_are_cut_whole_and_coverage_keeps_the_total()
    {
        var s = new Setup();
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => AdminReadOutput.Ok([.. Enumerable.Range(0, 80).Select(i => Threat($"Trojan:Win32/T{i}"))], 80, true));
        var (_, frames) = await s.Run(Request());
        Assert.Equal(50, frames[1].Items.Count);
        Assert.Equal(new BrokerReadCoverage(false, 80, 50), frames[1].Coverage);
        Assert.Equal("Trojan:Win32/T49", frames[1].Items[^1]["threatName"]!.GetValue<string>()); // whole items, in order
    }

    [Fact]
    public void A_result_over_64_KB_is_cut_by_whole_items_and_says_so()
    {
        var def = new AdminCheckDef("test.big", "1", "Big", "Big", null, 200, [new AdminField("text", AdminFieldType.Text, false, MaxLength: 2000)]);
        var items = Enumerable.Range(0, 200).Select(i => new JsonObject { ["text"] = new string((char)('a' + i % 26), 1000) }).ToList();
        var (state, _, coverage, sent) = BrokerReadSession.Pack(def, AdminReadOutput.Ok(items, 200, true), Kit.Nonce(), "req-0001", 3, 1);
        Assert.Equal("ok", state);
        Assert.InRange(sent.Count, 1, 64);
        Assert.Equal(new BrokerReadCoverage(false, 200, sent.Count), coverage);
        Assert.All(sent, i => Assert.Equal(1000, i["text"]!.GetValue<string>().Length)); // whole items only
        var frame = BrokerReadFrames.Result(Kit.Nonce(), "req-0001", 3, 1, "ok", null, coverage, sent);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(frame.ToJsonString()) <= AdminChecks.MaxResultBytes);
        Assert.NotNull(BrokerReadFrames.Parse(JsonNode.Parse(frame.ToJsonString()), Kit.Nonce()));
    }

    [Fact]
    public void Nothing_found_is_complete_and_could_not_read_is_not()
    {
        var def = AdminChecks.Find(AdminChecks.DefenderHistory)!;
        var none = BrokerReadSession.Pack(def, AdminReadOutput.Ok([], 0, true), Kit.Nonce(), "r", 2, 1);
        Assert.Equal(("ok", new BrokerReadCoverage(true, 0, 0)), (none.State, none.Coverage));
        var failed = BrokerReadSession.Pack(def, AdminReadOutput.Failed("access_denied"), Kit.Nonce(), "r", 2, 1);
        Assert.Equal(("failed", "access_denied", new BrokerReadCoverage(false, 0, 0)), (failed.State, failed.Code, failed.Coverage));
    }

    // ---------------------------------------------------------------- the catalog, parity, the window's words

    [Fact]
    public void Host_and_helper_list_the_same_checks_and_versions()
    {
        var helper = BrokerReads.Create(new NoWmi()).ToDictionary(a => a.Id, a => a.Version);
        Assert.Equal(AdminChecks.Versions.OrderBy(k => k.Key), helper.OrderBy(k => k.Key));
        Assert.Equal([AdminChecks.DefenderHistory, AdminChecks.Encryption, AdminChecks.StorageHealth], AdminChecks.All.Select(d => d.Id));
        Assert.DoesNotContain(AdminChecks.All, d => d.Id == "security.tpm"); // dropped this round (§17.6)
        Assert.All(AdminChecks.All, d => Assert.InRange(d.MaxItems, 1, AdminChecks.MaxItems));
    }

    private sealed class NoWmi : IReadWmi
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql, IReadOnlyList<string> props, int cap) => throw new InvalidOperationException();
        public IReadOnlyDictionary<string, object?> Call(string ns, string className, string keyProperty, string keyValue, string method, IReadOnlyDictionary<string, object?> args, IReadOnlyList<string> outProps) => throw new InvalidOperationException();
    }

    [Fact]
    public void The_catalogs_field_specs_are_exact()
    {
        string Spec(string id) => string.Join(",", AdminChecks.Find(id)!.Fields.Select(f => f.Name + ":" + f.Type + (f.Nullable ? "?" : "")));
        Assert.Equal("threatName:Text?,category:Text,severity:Text,detectedAt:Minute?,action:Text,succeeded:Boolean?,foundBy:Text,affectedItems:Integer",
            Spec(AdminChecks.DefenderHistory));
        Assert.Equal("drive:Text,protection:Text,conversion:Text,percent:Integer?,method:Text,protectorTypes:TextList", Spec(AdminChecks.Encryption));
        Assert.Equal("disk:Integer,model:Text?,counters:Boolean,temperatureC:Integer?,wearPercent:Integer?,readErrors:Integer?,writeErrors:Integer?,powerOnHours:Integer?",
            Spec(AdminChecks.StorageHealth));
        Assert.Equal(new AdminCheckWindow(1, 90, 30), AdminChecks.Find(AdminChecks.DefenderHistory)!.Window);
        Assert.Equal(120, AdminChecks.Find(AdminChecks.DefenderHistory)!.Fields[0].MaxLength);
        Assert.Null(AdminChecks.Find(AdminChecks.Encryption)!.Window);
        Assert.Equal((50, 26, 16), (AdminChecks.Find(AdminChecks.DefenderHistory)!.MaxItems, AdminChecks.Find(AdminChecks.Encryption)!.MaxItems, AdminChecks.Find(AdminChecks.StorageHealth)!.MaxItems));
    }

    [Fact]
    public void Shape_drops_unlisted_fields_and_conforms_is_strict()
    {
        var def = AdminChecks.Find(AdminChecks.DefenderHistory)!;
        var t = Threat();
        Assert.True(AdminChecks.Conforms(def, t));
        var extra = Threat();
        extra["path"] = @"C:\x";
        Assert.False(AdminChecks.Conforms(def, extra));
        Assert.True(AdminChecks.Conforms(def, AdminChecks.Shape(def, extra)));
        // A name that doesn't fit (too long, a format character) is nulled, the detection kept.
        Assert.Null(AdminChecks.Shape(def, Threat("Trojan:Win32/" + new string('x', 110)))!["threatName"]);
        Assert.Null(AdminChecks.Shape(def, Threat("Trojan:Win32/bad\u202Ename"))!["threatName"]);
        var wrongType = Threat();
        wrongType["threatName"] = 5;
        Assert.Null(AdminChecks.Shape(def, wrongType)); // a wrong type still rejects the item
        var time = Threat();
        time["detectedAt"] = "2026-11-18T10:05:33Z"; // seconds: not to the minute
        Assert.Null(AdminChecks.Shape(def, time));
        var enc = AdminChecks.Find(AdminChecks.Encryption)!;
        var vol = new JsonObject { ["drive"] = "C:", ["protection"] = "on", ["conversion"] = "fully_encrypted", ["percent"] = 100, ["method"] = "xts_aes_128", ["protectorTypes"] = new JsonArray("tpm", "recovery_password") };
        Assert.True(AdminChecks.Conforms(enc, vol));
        vol["protectorTypes"] = new JsonArray("tpm", "{ABCD-1234}");
        Assert.Null(AdminChecks.Shape(enc, vol)); // never an id
    }

    [Fact]
    public void The_read_window_uses_the_designs_words_and_never_names_the_AI()
    {
        Assert.Equal("Optimus · Administrator checks", BrokerReadView.TitleBar);
        Assert.Equal("Cancel", BrokerReadView.CancelText);
        Assert.Equal("Read", BrokerReadView.ReadText);
        var one = BrokerReadView.Build([AdminChecks.Find(AdminChecks.DefenderHistory)!]).Lines();
        Assert.Equal(
        [
            (BrokerLineKind.Heading, "Read this as administrator?"),
            (BrokerLineKind.Explanation, "You allowed this in Optimus. Windows asked for permission because these need administrator rights."),
            (BrokerLineKind.StepName, "Defender detection history"),
            (BrokerLineKind.Row, "Threats Microsoft Defender found in the last 30 days and what it did about them."),
            (BrokerLineKind.Notice, "Nothing on your PC is changed. The results go to Optimus and may be shown to the AI you use."),
        ], one.Select(l => (l.Kind, l.Text)));
        var three = BrokerReadView.Build(AdminChecks.All).Lines();
        Assert.Equal("Read these as administrator?", three[0].Text);
        Assert.Equal(
        [
            "Defender detection history", "Threats Microsoft Defender found in the last 30 days and what it did about them.",
            "Drive encryption status", "Whether BitLocker protects each drive.",
            "Disk health", "Your drives’ own health reports and warnings.",
        ], three.Skip(2).Take(6).Select(l => l.Text));
        Assert.All(three, l => Assert.DoesNotContain("Claude", l.Text, StringComparison.Ordinal));
        Assert.True(BrokerConfirmWindow.Cancels(Keys.Enter) && BrokerConfirmWindow.Cancels(Keys.Escape));
    }

    // ---------------------------------------------------------------- review item 1: private values in listed free-text fields

    /// <summary>Private values for both free-text fields; the bool says whether the model field is tested too (a serial run
    /// is checked for names only: real disk models are serial-shaped by nature, NeverStore.RejectsModel).</summary>
    public static TheoryData<string, bool> PrivateValues() => new()
    {
        { @"C:\Users\someone\evil.exe", true }, { "C:/Users/someone/evil.exe", true }, { "someone@example.com", true }, { "192.168.1.20", true },
        { "fe80::1c2b:3d4e", true }, { "2001:db8:0:0:0:0:2:1", true }, { "00-11-22-33-44-55", true }, { "0011.2233.4455", true },
        { "VK7JG-NPHTM-C97JM-9MPGT-3V66T", true }, { "S-1-5-21-1000-2000-3000-1001", true }, { "https://evil.example/x", true },
        { "token=abcdef123456", true }, { "%USERPROFILE%", true }, { "Ünïcode", true }, { "a@b", true }, { "name; drop", true },
        { "Disk of " + Environment.UserName, true }, { "WD-WCC4E1234567", false },
        // Paths and names that a character allowlist alone would let through as a threat name (re-check A).
        { "//fileserver/share/report.csv", true }, { "C:/x/y", true }, { "a/b/c", true }, { "host:8080/path", true },
        { "Trojan:Win32/Some Name", false }, { "Trojan:Win32//x", true }, { "Trojan:Win32/x@example", true },
        { "Trojan:Win32/Test:Extra", true }, { "Trojan:Win32/Test/More", true },
    };

    [Theory]
    [MemberData(nameof(PrivateValues))]
    public async Task A_private_value_in_a_listed_free_text_field_never_leaves_the_helper(string value, bool model)
    {
        var s = new Setup();
        var disk = Disk();
        disk["model"] = model ? value : "Fast NVMe 1TB";
        var threat = Threat();
        threat["threatName"] = value;
        s.Storage = new FakeAdapter(AdminChecks.StorageHealth, (_, _) => AdminReadOutput.Ok([disk, Disk(1)], 2, true));
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => AdminReadOutput.Ok([threat, Threat()], 2, true));
        var (_, frames) = await s.Run(Request());
        var wire = string.Join("\n", frames.SelectMany(f => f.Items).Select(i => i.ToJsonString()));
        Assert.DoesNotContain(System.Text.Json.JsonSerializer.Serialize(value).Trim('"'), wire, StringComparison.Ordinal);
        // Both fields are nullable: the value is nulled, the item kept, and coverage says something was left out.
        Assert.Equal((2, false), (frames[1].Items.Count, frames[1].Coverage!.Complete));
        Assert.Null(frames[1].Items[0]["threatName"]);
        Assert.Equal("high", frames[1].Items[0]["severity"]!.GetValue<string>());
        Assert.Equal("Trojan:Win32/Test", frames[1].Items[1]["threatName"]!.GetValue<string>());
        Assert.False(AdminChecks.Conforms(AdminChecks.Find(AdminChecks.DefenderHistory)!, threat)); // the host's strict check too
        if (!model) return;
        Assert.Equal(2, frames[2].Items.Count);
        Assert.Null(frames[2].Items[0]["model"]);
        Assert.False(frames[2].Coverage!.Complete);
        Assert.False(AdminChecks.Conforms(AdminChecks.Find(AdminChecks.StorageHealth)!, disk));
    }

    [Theory]
    [InlineData("Trojan:Win32/Wacatac.B!ml")]
    [InlineData("Virus:DOS/EICAR_Test_File")]
    [InlineData("Worm:Win32/Mydoom.O@mm")]
    [InlineData("Worm:Win32/Bagle@m")]
    [InlineData("PUA:Win32/Bundler")]
    [InlineData("HackTool:Win64/Mimikatz!pz")]
    [InlineData("Exploit:O97M/CVE-2017-11882.RV!MTB")]
    public void Real_threat_names_pass(string name)
    {
        var t = Threat(name);
        Assert.True(AdminChecks.Conforms(AdminChecks.Find(AdminChecks.DefenderHistory)!, t, []));
    }

    [Theory]
    [InlineData("Samsung SSD 980 PRO 1TB")]
    [InlineData("WDC WD10EZEX-08WN4A0")]
    [InlineData("ST1000DM010-2EP102")]
    [InlineData("KXG60ZNV512G NVMe TOSHIBA 512GB")]
    public void Real_disk_models_pass(string model)
    {
        var d = Disk();
        d["model"] = model;
        Assert.True(AdminChecks.Conforms(AdminChecks.Find(AdminChecks.StorageHealth)!, d, []));
    }

    [Fact]
    public void The_identity_filter_uses_the_names_given()
    {
        var d = Disk();
        d["model"] = "Contoso Disk";
        var def = AdminChecks.Find(AdminChecks.StorageHealth)!;
        Assert.True(AdminChecks.Conforms(def, d, []));
        Assert.False(AdminChecks.Conforms(def, d, IdentityFilter.Local("contoso", "pc1", null)));
        Assert.Null(AdminChecks.Shape(def, d, out var redacted, IdentityFilter.Local("contoso", "pc1", null))!["model"]);
        Assert.True(redacted);
    }

    // ---------------------------------------------------------------- review item 8: drive has an exact pattern

    [Theory]
    [InlineData("C:", true)]
    [InlineData("Z:", true)]
    [InlineData("c:", false)]
    [InlineData("1:", false)]
    [InlineData("C\\", false)]
    [InlineData("CD", false)]
    public void The_drive_field_is_one_upper_case_letter_and_a_colon(string drive, bool ok)
    {
        var vol = new JsonObject { ["drive"] = drive, ["protection"] = "on", ["conversion"] = "fully_encrypted", ["percent"] = 100, ["method"] = "xts_aes_128", ["protectorTypes"] = new JsonArray("tpm") };
        Assert.Equal(ok, AdminChecks.Shape(AdminChecks.Find(AdminChecks.Encryption)!, vol) is not null);
    }

    // ---------------------------------------------------------------- review item 9: the window's span (design v79)

    [Theory]
    [InlineData(1, "Threats Microsoft Defender found in the last day and what it did about them.", "Defender detection history · last day")]
    [InlineData(30, "Threats Microsoft Defender found in the last 30 days and what it did about them.", "Defender detection history · last 30 days")]
    [InlineData(90, "Threats Microsoft Defender found in the last 90 days and what it did about them.", "Defender detection history · last 90 days")]
    [InlineData(null, "Threats Microsoft Defender found in the last 30 days and what it did about them.", "Defender detection history · last 30 days")]
    public async Task The_window_line_and_the_host_label_name_the_resolved_span(int? asked, string line, string label)
    {
        var def = AdminChecks.Find(AdminChecks.DefenderHistory)!;
        var (ok, days) = AdminChecks.ResolveWindow(def, asked);
        Assert.True(ok);
        Assert.Equal(line, AdminChecks.WindowLine(def.Id, days));
        Assert.Equal(label, AdminChecks.HostLabel(def.Id, days));
        // The helper's window shows the line for the window it will read.
        var s = new Setup();
        await s.Run(Request(null, (AdminChecks.DefenderHistory, asked)));
        Assert.Equal(line, Assert.Single(Assert.Single(s.Window.Shown).Checks).Reads);
        Assert.Equal(days, Assert.Single(s.Defender.Calls).WindowDays);
        // The others carry no span.
        Assert.Equal("Disk health", AdminChecks.HostLabel(AdminChecks.StorageHealth, null));
        Assert.Equal("Your drives’ own health reports and warnings.", AdminChecks.WindowLine(AdminChecks.StorageHealth, null));
        Assert.Equal("Whether BitLocker protects each drive.", AdminChecks.WindowLine(AdminChecks.Encryption, null));
    }

    [Theory]
    [InlineData(0, "end:refused/bad_request")]
    [InlineData(91, "r1:failed/bad_request end:refused/bad_request")]
    [InlineData(3650, "r1:failed/bad_request end:refused/bad_request")]
    public async Task A_window_outside_1_to_90_is_refused_before_the_window_never_clamped(int days, string expected)
    {
        var s = new Setup();
        var req = Request(null, (AdminChecks.DefenderHistory, 30));
        req["checks"]![0]!["window"] = days;
        var (_, frames) = await s.Run(req);
        Assert.Equal(expected, Kinds(frames));
        Assert.Empty(s.Window.Shown);
        Assert.Equal(0, s.Reads_);
        Assert.False(OptimizationTool.Host.Elevation.BrokerElevation.ValidRead(new("req-1", [new(1, AdminChecks.DefenderHistory, "1", days)])));
    }

    // ---------------------------------------------------------------- review item 2: writes are bounded

    /// <summary>Reads like a pipe (frames pushed by the test); the first <see cref="OkWrites"/> writes go through, the rest never complete, whatever the token says.</summary>
    private sealed class StuckStream(Duplex reads, int okWrites) : Stream
    {
        private int _writes;
        public int OkWrites => okWrites;
        public readonly MemoryStream Written = new();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => reads.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => reads.ReadAsync(buffer, offset, count, ct);
        public override int Read(byte[] buffer, int offset, int count) => reads.Read(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) > okWrites) return new ValueTask(new TaskCompletionSource().Task); // never completes, ignores ct
            lock (Written) Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_host_that_stops_draining_cannot_hold_the_watchdog_the_journal_line_or_the_timeout_exit()
    {
        var s = new Setup { CheckTimeout = TimeSpan.FromMilliseconds(300) };
        using var never = new ManualResetEventSlim();
        s.Defender = new FakeAdapter(AdminChecks.DefenderHistory, (_, _) => { never.Wait(TimeSpan.FromSeconds(30)); return AdminReadOutput.Ok([], 0, true); });
        var reads = await Duplex.WithFrames(Request());
        var pipe = new StuckStream(reads, okWrites: 1); // read_started goes through; the timeout result's write never completes
        var session = new BrokerSession(new BrokerSessionOptions(Kit.Nonce(), Kit.HostImage, Kit.Self, s.Rig.Handlers, s.Rig.Journal, s.Rig.Ledger, s.Rig.Confirm)
        {
            Reads = new BrokerReadOptions([s.Defender, s.Storage, s.Encryption], s.Window, s.Journal) { CheckTimeout = s.CheckTimeout, WriteTimeout = TimeSpan.FromMilliseconds(200) },
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await session.RunAsync(pipe, Kit.Host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("timeout", result.Code); // Program exits at once on this code
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var end = Assert.Single(s.Journal.Ends);
        Assert.Equal("timeout", end.State);
        Assert.Equal([(1, "failed", (string?)"timeout"), (2, "skipped", "timeout")], end.Results);
        Assert.Empty(s.Storage.Calls);
        never.Set();
    }

    [Fact]
    public async Task A_host_that_never_drains_from_the_start_ends_the_session_without_reading()
    {
        var s = new Setup();
        var reads = await Duplex.WithFrames(Request());
        var pipe = new StuckStream(reads, okWrites: 0);
        var session = new BrokerSession(new BrokerSessionOptions(Kit.Nonce(), Kit.HostImage, Kit.Self, s.Rig.Handlers, s.Rig.Journal, s.Rig.Ledger, s.Rig.Confirm)
        {
            Reads = new BrokerReadOptions([s.Defender, s.Storage, s.Encryption], s.Window, s.Journal) { WriteTimeout = TimeSpan.FromMilliseconds(200) },
        });
        var result = await session.RunAsync(pipe, Kit.Host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pipe_lost", result.Code);
        Assert.Equal(0, s.Reads_);
        Assert.Equal("stopped", Assert.Single(s.Journal.Ends).State);
    }

    [Fact]
    public async Task A_bounded_write_gives_up_on_a_stream_that_ignores_cancellation()
    {
        var pipe = new StuckStream(new Duplex(), okWrites: 0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await BrokerReadSession.WriteBoundedAsync(pipe, BrokerStop.ToJson(Kit.Nonce()), TimeSpan.FromMilliseconds(200)));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(10), BrokerReadSession.DefaultWriteTimeout);
    }

    // ---------------------------------------------------------------- the protected journal's read records

    [Fact]
    public void The_protected_journal_writes_a_read_record_beside_and_apart_from_session_records()
    {
        var @base = Path.Combine(Path.GetTempPath(), "ot-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(@base);
        var fs = new FakeTrustedFs();
        fs.Set(@base, FakeTrustedFs.SystemFolder);
        var root = Path.Combine(@base, "OptimizationTool", "journal");
        try
        {
            using var j = new ProtectedJournal(@base, root, Kit.User, fs);
            var req = BrokerReadRequest.Parse(Request())!;
            var id = j.BeginRead(req, DateTimeOffset.UnixEpoch);
            j.EndRead(id, "complete", [(1, "ok", null), (2, "failed", "access_denied")], DateTimeOffset.UnixEpoch);
            Assert.True(File.Exists(Path.Combine(root, id + ".read.json")));
            Assert.True(File.Exists(Path.Combine(root, id + ".read-end.json")));
            var begin = BrokerJournalFiles.Authentic(root, id + ".read.json", j.Key(), _ => { })!;
            Assert.Equal("req-0001", begin["requestId"]!.GetValue<string>());
            Assert.Equal(AdminChecks.DefenderHistory, begin["checks"]![0]!["id"]!.GetValue<string>());
            var end = BrokerJournalFiles.Authentic(root, id + ".read-end.json", j.Key(), _ => { })!;
            Assert.Equal("access_denied", end["checks"]![1]!["code"]!.GetValue<string>());
            Assert.Empty(Directory.EnumerateFiles(root, "rcpt-*.session.json")); // the host's session enumeration is untouched
        }
        finally { try { Directory.Delete(@base, true); } catch (IOException) { } }
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// Provenance of the broker's protected journal (m9 §3.5 "as built"): only records the broker can prove it wrote authorize
/// an undo. The real <see cref="ProtectedJournal"/> on a temp folder; its Windows security is a fake (no ACL or owner is set
/// on this PC, see <see cref="FakeTrustedFs"/>); <c>_base</c> plays %ProgramData%.
/// </summary>
public sealed class JournalProvenanceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ot-prov-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTrustedFs _folder = new();
    private readonly Rig _rig = new("test.a");
    private readonly List<ProtectedJournal> _journals = [];
    private string Root => Path.Combine(_base, "OptimizationTool", "journal");

    public JournalProvenanceTests()
    {
        Directory.CreateDirectory(_base);
        _folder.Set(_base, FakeTrustedFs.SystemFolder);
    }

    public void Dispose()
    {
        foreach (var j in _journals) j.Dispose();
        _locked.Dispose();
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    private ProtectedJournal Journal()
    {
        var j = new ProtectedJournal(_base, Root, Kit.User, _folder);
        _journals.Add(j);
        return j;
    }

    /// <summary>A folder the broker made earlier (trusted facts), as a later launch finds it.</summary>
    private void TrustedRoot()
    {
        Directory.CreateDirectory(Root);
        _folder.Set(Path.GetDirectoryName(Root)!, FakeTrustedFs.TrustedFolder);
        _folder.Set(Root, FakeTrustedFs.TrustedFolder);
    }

    private async Task<(BrokerResult Result, BrokerResponse? Sent)> Run(ProtectedJournal journal, BrokerRequest request, string nonce)
    {
        var pipe = await Duplex.WithFrames(request.ToJson());
        var session = new BrokerSession(new BrokerSessionOptions(nonce, Kit.HostImage, Kit.Self, _rig.Handlers, journal, _rig.Ledger, _rig.Confirm));
        var result = await session.RunAsync(pipe, Kit.Host, CancellationToken.None);
        return (result, await pipe.Response());
    }

    private static BrokerStepRequest UndoOf5(string receiptId) =>
        Kit.Step(1, "test.a", "1.0.0", Kit.TestTarget(), new JsonObject { ["value"] = 5 }, new JsonObject { ["value"] = 0 }, new JsonObject { ["restore"] = 0 },
            new ElevatedUndoOf(receiptId, 1));

    /// <summary>A plausible, well-formed record set for "test.a: 0 → 5, verified", written by someone other than the broker.</summary>
    private static void Plant(string root, string id, Func<string, JsonObject, byte[]>? seal = null)
    {
        Directory.CreateDirectory(root);
        void Write(string name, JsonObject o) => File.WriteAllBytes(Path.Combine(root, name), seal?.Invoke(name, o) ?? System.Text.Encoding.UTF8.GetBytes(o.ToJsonString()));
        Write(BrokerJournalFiles.SessionFile(id), new JsonObject
        {
            ["receiptId"] = id, ["planId"] = "plan-0000000000000001", ["planHash"] = "planhash-1", ["at"] = "2026-11-12T00:00:00.000Z",
            ["steps"] = new JsonArray(new JsonObject { ["n"] = 1, ["actionId"] = "test.a" }),
        });
        Write(BrokerJournalFiles.PreparedFile(id, 1), new JsonObject
        {
            ["receiptId"] = id, ["n"] = 1, ["actionId"] = "test.a", ["target"] = Kit.TestTarget(), ["before"] = new JsonObject { ["value"] = 0 },
            ["after"] = new JsonObject { ["value"] = 5 }, ["at"] = "2026-11-12T00:00:00.000Z",
        });
        Write(BrokerJournalFiles.OutcomeFile(id, 1), new JsonObject
        {
            ["receiptId"] = id, ["n"] = 1, ["state"] = "verified", ["observed"] = new JsonObject { ["value"] = 5 }, ["at"] = "2026-11-12T00:00:01.000Z",
        });
    }

    [Fact]
    public async Task Records_in_a_trusted_folder_without_the_brokers_key_are_quarantined_and_cannot_authorize_an_undo()
    {
        const string id = "rcpt-00000000000000aa";
        TrustedRoot();
        Plant(Root, id);
        File.WriteAllBytes(Path.Combine(Root, BrokerJournalFiles.KeyFile), new byte[32]); // a "key" the broker didn't make
        File.WriteAllText(Path.Combine(Root, "nonces.log"), "");
        _rig.Test("test.a").Value = 5; // the planted record claims Optimus set 5

        var (r, _) = await Run(Journal(), Kit.Request(null, UndoOf5(id)), Kit.Nonce());

        Assert.Equal("no_record", r.Code);
        Assert.Equal(0, _rig.Test("test.a").Writes);
        Assert.Equal(5, _rig.Test("test.a").Value);
        var q = Assert.Single(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
        Assert.True(File.Exists(Path.Combine(q, BrokerJournalFiles.PreparedFile(id, 1))));
        Assert.True(File.Exists(Path.Combine(q, BrokerJournalFiles.KeyFile)));
        Assert.False(File.Exists(Path.Combine(Root, BrokerJournalFiles.SessionFile(id))));
        Assert.True(_folder.IsSecret(Path.Combine(Root, BrokerJournalFiles.KeyFile))); // a new key of its own
    }

    [Fact]
    public async Task Records_planted_after_provisioning_are_ignored_and_counted()
    {
        var journal = Journal();
        journal.EnsureDirectory(); // the broker's folder and key exist
        const string id = "rcpt-00000000000000ab";
        Plant(Root, id); // no MAC
        const string id2 = "rcpt-00000000000000ac";
        var otherKey = new byte[32];
        otherKey[3] = 9;
        Plant(Root, id2, (name, o) => BrokerJournalFiles.Seal(name, o, otherKey)); // a MAC with a key that isn't the broker's
        _rig.Test("test.a").Value = 5;

        var (r, _) = await Run(journal, Kit.Request(null, UndoOf5(id)), Kit.Nonce());
        var (r2, _) = await Run(Journal(), Kit.Request(Kit.Nonce('b'), UndoOf5(id2)), Kit.Nonce('b'));

        Assert.Equal(("no_record", "no_record"), (r.Code, r2.Code));
        Assert.Equal(0, _rig.Test("test.a").Writes);
        Assert.True(journal.Rejected >= 2);
        Assert.Empty(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*")); // the broker's own key: nothing quarantined
    }

    [Fact]
    public async Task The_brokers_own_records_still_authorize_an_undo_in_a_later_launch()
    {
        var (_, enabled) = await Run(Journal(), Kit.Request(null, Kit.Test(1, "test.a", 0, 5)), Kit.Nonce());
        Assert.Equal("verified", enabled!.Steps[0].State);
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, BrokerJournalFiles.PreparedFile(enabled.ReceiptId!, 1))))!.AsObject();
        Assert.Matches("^[0-9a-f]{64}$", record[BrokerJournalFiles.MacField]!.GetValue<string>());

        var (r, undone) = await Run(Journal(), Kit.Request(Kit.Nonce('c'), UndoOf5(enabled.ReceiptId!)), Kit.Nonce('c')); // a new broker process
        Assert.Equal("verified", undone!.Steps[0].State);
        Assert.Equal(0, _rig.Test("test.a").Value);
        Assert.True(r.Wrote);
        Assert.Empty(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
    }

    [Fact]
    public async Task A_tampered_record_is_ignored()
    {
        var journal = Journal();
        var (_, enabled) = await Run(journal, Kit.Request(null, Kit.Test(1, "test.a", 0, 5)), Kit.Nonce());
        var path = Path.Combine(Root, BrokerJournalFiles.PreparedFile(enabled!.ReceiptId!, 1));
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        record["before"] = new JsonObject { ["value"] = 3 }; // "undo" would now write 3: a value Optimus never saw
        File.WriteAllText(path, record.ToJsonString());
        var undo = Kit.Step(1, "test.a", "1.0.0", Kit.TestTarget(), new JsonObject { ["value"] = 5 }, new JsonObject { ["value"] = 3 }, new JsonObject { ["restore"] = 3 },
            new ElevatedUndoOf(enabled.ReceiptId!, 1));

        var (r, _) = await Run(journal, Kit.Request(Kit.Nonce('d'), undo), Kit.Nonce('d'));

        Assert.Equal("no_record", r.Code);
        Assert.Equal(5, _rig.Test("test.a").Value);
        Assert.True(journal.Rejected >= 1);
    }

    [Fact]
    public async Task A_genuine_record_moved_to_another_session_or_step_is_ignored()
    {
        var journal = Journal();
        var (_, a) = await Run(journal, Kit.Request(null, Kit.Test(1, "test.a", 0, 5)), Kit.Nonce());
        _rig.Test("test.a").Value = 0;
        var (_, b) = await Run(journal, Kit.Request(Kit.Nonce('b'), Kit.Test(1, "test.a", 0, 5)), Kit.Nonce('b'));
        // Session b's genuine records replaced by session a's (same shape, valid MACs for their own names).
        foreach (var name in new[] { BrokerJournalFiles.PreparedFile(b!.ReceiptId!, 1), BrokerJournalFiles.OutcomeFile(b.ReceiptId!, 1) })
            File.Delete(Path.Combine(Root, name));
        File.Copy(Path.Combine(Root, BrokerJournalFiles.PreparedFile(a!.ReceiptId!, 1)), Path.Combine(Root, BrokerJournalFiles.PreparedFile(b.ReceiptId!, 1)));
        File.Copy(Path.Combine(Root, BrokerJournalFiles.OutcomeFile(a.ReceiptId!, 1)), Path.Combine(Root, BrokerJournalFiles.OutcomeFile(b.ReceiptId!, 1)));

        Assert.Null(journal.Find(b.ReceiptId!, 1));
        Assert.NotNull(journal.Find(a.ReceiptId!, 1));
        Assert.True(journal.Rejected >= 1);
    }

    [Fact]
    public void A_record_with_the_right_mac_but_another_steps_identity_is_ignored()
    {
        var journal = Journal();
        journal.EnsureDirectory();
        var key = File.ReadAllBytes(Path.Combine(Root, BrokerJournalFiles.KeyFile)); // what only elevated code can read
        const string id = "rcpt-00000000000000ad";
        // Properly sealed, but the prepared record says it is another session's step 2 and the session lists no such step.
        Plant(Root, id, (name, o) =>
        {
            if (name.EndsWith(".prepared.json", StringComparison.Ordinal)) { o["receiptId"] = "rcpt-00000000000000ff"; o["n"] = 2; }
            return BrokerJournalFiles.Seal(name, o, key);
        });
        Assert.Null(journal.Find(id, 1));
        Assert.Equal(1, journal.Rejected);
    }

    [Fact]
    public void The_nonce_ledger_provisions_the_folder_before_reading_it()
    {
        TrustedRoot();
        var nonce = Kit.Nonce('e');
        File.WriteAllText(LedgerPath, NonceHash(nonce) + Environment.NewLine); // planted: would block this approval
        var ledger = new FileNonceLedger(Journal());
        Assert.Equal(NonceConsume.Consumed, ledger.TryConsume(nonce));
        Assert.Equal(NonceConsume.Replayed, ledger.TryConsume(nonce));
        Assert.Single(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
    }

    // ---- m9 §13 R1: the trust root is created with its security, never adopted; key and ledger read through one handle.

    private string LedgerPath => Path.Combine(Root, FileNonceLedger.FileName);
    private string KeyPath => Path.Combine(Root, BrokerJournalFiles.KeyFile);
    private static string NonceHash(string nonce) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(nonce)));
    private string[] Retired(string dir, string name) => Directory.GetFileSystemEntries(dir, name + ".untrusted-*");

    [Fact]
    public async Task A_folder_a_user_made_first_is_retired_and_a_fresh_one_created_never_adopted()
    {
        const string id = "rcpt-00000000000000a1";
        Plant(Root, id); // the user made OptimizationTool\journal first (owner: the user) and filled it
        _rig.Test("test.a").Value = 5;

        var (r, _) = await Run(Journal(), Kit.Request(null, UndoOf5(id)), Kit.Nonce());

        Assert.Equal("no_record", r.Code);
        Assert.Equal(0, _rig.Test("test.a").Writes);
        var parent = Path.GetDirectoryName(Root)!;
        var old = Assert.Single(Retired(_base, "OptimizationTool")); // the parent was the user's too: the whole chain retired
        Assert.True(File.Exists(Path.Combine(old, "journal", BrokerJournalFiles.PreparedFile(id, 1))));
        Assert.Contains(Path.GetFullPath(parent), _folder.Created);
        Assert.Contains(Path.GetFullPath(Root), _folder.Created);
        Assert.True(TrustPolicy.IsTrustedFolder(_folder.FactsOf(Root)!));
        Assert.False(File.Exists(Path.Combine(Root, BrokerJournalFiles.SessionFile(id))));
        Assert.True(_folder.IsSecret(KeyPath));
    }

    [Theory]
    [InlineData(0x00040000u)] // WRITE_DAC
    [InlineData(0x00080000u)] // WRITE_OWNER
    [InlineData(0x00000002u)] // add file
    [InlineData(0x40000000u)] // GENERIC_WRITE
    public void An_admin_owned_folder_with_a_user_entry_beyond_read_is_retired(uint userRight)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "planted.txt"), "x");
        var parent = Path.GetDirectoryName(Root)!;
        _folder.Set(parent, FakeTrustedFs.TrustedFolder);
        var t = FakeTrustedFs.TrustedFolder;
        _folder.Set(Root, t with { Aces = [.. t.Aces, new AceFact(Kit.User, 0x001200A9 | userRight, AceKind.Allow)] });

        Journal().EnsureDirectory();

        var old = Assert.Single(Retired(parent, "journal"));
        Assert.True(File.Exists(Path.Combine(old, "planted.txt")));
        Assert.Contains(Path.GetFullPath(Root), _folder.Created);
        Assert.DoesNotContain(Path.GetFullPath(parent), _folder.Created); // the trusted parent is kept
        Assert.False(File.Exists(Path.Combine(Root, "planted.txt")));
    }

    [Fact]
    public void An_unprotected_or_reparse_folder_is_retired_and_the_chain_is_held_while_the_journal_lives()
    {
        Directory.CreateDirectory(Root);
        var parent = Path.GetDirectoryName(Root)!;
        _folder.Set(parent, FakeTrustedFs.TrustedFolder with { DaclProtected = false });
        _folder.Set(Root, FakeTrustedFs.TrustedFolder with { IsReparsePoint = true });
        var journal = Journal();

        journal.EnsureDirectory();

        Assert.Single(Retired(_base, "OptimizationTool"));
        Assert.True(_folder.IsHeld(_base) && _folder.IsHeld(parent) && _folder.IsHeld(Root));
        journal.Dispose();
        Assert.False(_folder.IsHeld(_base) || _folder.IsHeld(parent) || _folder.IsHeld(Root));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_retired_fails_closed_and_nothing_is_written()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "planted.txt"), "x");
        _folder.Set(Path.GetDirectoryName(Root)!, FakeTrustedFs.TrustedFolder);
        _folder.BlockRetire.Add(Path.GetFullPath(Root)); // the user holds a handle that blocks the rename

        var (r, sent) = await Run(Journal(), Kit.Request(null, Kit.Test(1, "test.a", 0, 5)), Kit.Nonce());

        Assert.Equal("journal_failed", r.Code);
        Assert.False(r.Wrote);
        Assert.Equal(0, _rig.Test("test.a").Writes);
        Assert.Null(sent!.ReceiptId);
        Assert.Equal(new[] { "planted.txt" }, Directory.GetFileSystemEntries(Root).Select(e => Path.GetFileName(e)));
        Assert.Empty(_folder.Created);
    }

    [Fact]
    public void A_trust_root_that_is_not_a_system_folder_or_is_a_reparse_point_is_refused()
    {
        _folder.Set(_base, FakeTrustedFs.UserFolder);
        Assert.Throws<IOException>(() => Journal().EnsureDirectory());
        _folder.Set(_base, FakeTrustedFs.SystemFolder with { IsReparsePoint = true });
        Assert.Throws<IOException>(() => Journal().EnsureDirectory());
        Assert.Empty(_folder.Created);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Root)));
    }

    [Fact]
    public async Task The_key_is_the_bytes_read_through_the_validated_handle_not_a_later_swap()
    {
        var first = Journal();
        first.EnsureDirectory();
        first.Dispose();
        var k1 = File.ReadAllBytes(KeyPath);
        var k2 = new byte[32];
        k2[0] = 7;
        _folder.AfterSecretOpen = p => { if (p.EndsWith(BrokerJournalFiles.KeyFile, StringComparison.Ordinal)) File.WriteAllBytes(p, k2); };

        var (_, sent) = await Run(Journal(), Kit.Request(null, Kit.Test(1, "test.a", 0, 5)), Kit.Nonce());

        var name = BrokerJournalFiles.PreparedFile(sent!.ReceiptId!, 1);
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, name)))!.AsObject();
        Assert.True(BrokerJournalFiles.Verify(name, record, k1));
        Assert.False(BrokerJournalFiles.Verify(name, record, k2));
    }

    public static TheoryData<string> BadKeys => ["user-owned", "unprotected", "user-entry", "reparse", "short", "long"];

    [Theory]
    [MemberData(nameof(BadKeys))]
    public void A_key_that_fails_validation_on_its_handle_is_never_used(string kind)
    {
        var first = Journal();
        first.EnsureDirectory();
        first.Dispose();
        var k1 = File.ReadAllBytes(KeyPath);
        var s = FakeTrustedFs.SecretFile;
        switch (kind)
        {
            case "user-owned": _folder.Set(KeyPath, s with { Owner = Kit.User }); break;
            case "unprotected": _folder.Set(KeyPath, s with { DaclProtected = false }); break;
            case "user-entry": _folder.Set(KeyPath, s with { Aces = [.. s.Aces, new AceFact(Kit.User, 0x00120089, AceKind.Allow)] }); break;
            case "reparse": _folder.Set(KeyPath, s with { IsReparsePoint = true }); break;
            case "short": File.WriteAllBytes(KeyPath, k1[..31]); break;
            case "long": File.WriteAllBytes(KeyPath, [.. k1, 0]); break;
        }

        var journal = Journal();
        journal.EnsureDirectory();

        var q = Assert.Single(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
        Assert.True(File.Exists(Path.Combine(q, BrokerJournalFiles.KeyFile)));
        Assert.True(_folder.IsSecret(KeyPath));
        Assert.NotEqual(k1, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void The_ledger_is_created_secret_and_signed_and_detects_a_replay()
    {
        var journal = Journal();
        var ledger = new FileNonceLedger(journal);
        Assert.Equal(NonceConsume.Consumed, ledger.TryConsume(Kit.Nonce('a'))); // missing ledger: empty

        Assert.True(_folder.IsSecret(LedgerPath));
        Assert.True(_folder.IsSecret(LockPath)); // the lock file too, created with the secret's security
        Assert.False(_folder.IsLocked(LockPath)); // and released
        var lines = File.ReadAllLines(LedgerPath);
        Assert.Equal(NonceHash(Kit.Nonce('a')), lines[0]);
        Assert.StartsWith("mac:", lines[^1], StringComparison.Ordinal);
        Assert.Equal(NonceConsume.Replayed, new FileNonceLedger(Journal()).TryConsume(Kit.Nonce('a'))); // a replay, in a later launch
        Assert.Equal(NonceConsume.Consumed, new FileNonceLedger(Journal()).TryConsume(Kit.Nonce('b')));
        Assert.Empty(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
        Assert.Empty(Directory.GetFiles(Root, FileNonceLedger.FileName + ".tmp-*"));
    }

    public static TheoryData<string> BadLedgers => ["unsigned", "tampered", "wrong-key", "not-secret", "reparse"];

    [Theory]
    [MemberData(nameof(BadLedgers))]
    public void A_bad_ledger_refuses_the_request_is_quarantined_and_a_fresh_signed_one_is_started(string kind)
    {
        var journal = Journal();
        var ledger = new FileNonceLedger(journal);
        Assert.Equal(NonceConsume.Consumed, ledger.TryConsume(Kit.Nonce('a')));
        var signed = File.ReadAllLines(LedgerPath);
        switch (kind)
        {
            case "unsigned":
                File.WriteAllText(LedgerPath, signed[0] + "\n"); // the hashes alone, no MAC
                break;
            case "tampered":
                File.WriteAllText(LedgerPath, string.Join('\n', [NonceHash(Kit.Nonce('c')), .. signed]) + "\n"); // a line added, MAC kept
                break;
            case "wrong-key":
                var other = new byte[32];
                var mac = System.Security.Cryptography.HMACSHA256.HashData(other, System.Text.Encoding.UTF8.GetBytes(FileNonceLedger.FileName + "\n" + signed[0]));
                File.WriteAllText(LedgerPath, signed[0] + "\nmac:" + Convert.ToHexStringLower(mac) + "\n");
                break;
            case "not-secret":
                _folder.Set(LedgerPath, FakeTrustedFs.UserFile); // correctly signed, but someone else can write it
                break;
            case "reparse":
                _folder.Set(LedgerPath, FakeTrustedFs.SecretFile with { IsReparsePoint = true });
                break;
        }

        var readUnlocked = false;
        _folder.AfterSecretOpen = p => readUnlocked |= p.EndsWith(FileNonceLedger.FileName, StringComparison.OrdinalIgnoreCase) && !_folder.IsLocked(LockPath);
        Assert.Equal(NonceConsume.Failed, ledger.TryConsume(Kit.Nonce('d'))); // refused, though 'd' was never used
        _folder.AfterSecretOpen = null;

        Assert.False(readUnlocked); // the ledger is only ever read under the lock
        Assert.False(_folder.IsLocked(LockPath)); // released after the reset
        Assert.True(File.Exists(LockPath)); // the lock file is not quarantined with the bad ledger
        var q = Assert.Single(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
        Assert.True(File.Exists(Path.Combine(q, FileNonceLedger.FileName)));
        Assert.True(_folder.IsSecret(LedgerPath));
        Assert.StartsWith("mac:", Assert.Single(File.ReadAllLines(LedgerPath)), StringComparison.Ordinal); // fresh, signed, empty
        Assert.Equal(NonceConsume.Consumed, ledger.TryConsume(Kit.Nonce('d')));
        Assert.True(_folder.IsSecret(KeyPath)); // the journal's key was not touched
    }

    // ---- m9 review S3: one atomic consume, under a lock across broker processes (two ledgers on one fake disk).

    private string LockPath => Path.Combine(Root, FileNonceLedger.LockName);
    private readonly ManualResetEventSlim _locked = new();

    /// <summary>
    /// Ledger A reads the ledger; at that moment ledger B (another "process", its own journal over the same disk) starts
    /// consuming on another thread. A goes on once B is either blocked on the lock or has read the ledger itself.
    /// </summary>
    private (NonceConsume A, NonceConsume B) Race(string nonceA, string nonceB)
    {
        var a = new FileNonceLedger(Journal());
        var b = new FileNonceLedger(Journal());
        a.TryConsume(Kit.Nonce('0')); // both journals provisioned, the ledger exists
        b.TryConsume(Kit.Nonce('1'));
        using var bRead = new ManualResetEventSlim();
        Task<NonceConsume>? second = null;
        var started = 0;
        _folder.LockBusy = _ => _locked.Set();
        _folder.AfterSecretOpen = p =>
        {
            if (!p.EndsWith(FileNonceLedger.FileName, StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref started, 1) == 0)
            {
                second = Task.Run(() => b.TryConsume(nonceB));
                WaitHandle.WaitAny([_locked.WaitHandle, bRead.WaitHandle], TimeSpan.FromSeconds(5));
            }
            else bRead.Set();
        };
        var resultA = a.TryConsume(nonceA);
        Assert.True(second!.Wait(TimeSpan.FromSeconds(10)));
        _folder.AfterSecretOpen = null;
        _folder.LockBusy = null;
        return (resultA, second.Result);
    }

    [Fact]
    public void Two_brokers_consuming_the_same_nonce_at_once_run_it_only_once()
    {
        var (a, b) = Race(Kit.Nonce('a'), Kit.Nonce('a'));
        Assert.Equal(NonceConsume.Consumed, a);
        Assert.Equal(NonceConsume.Replayed, b);
        Assert.True(_locked.IsSet); // B really waited on A's lock
    }

    [Fact]
    public void Two_brokers_consuming_different_nonces_at_once_both_stay_recorded()
    {
        var (a, b) = Race(Kit.Nonce('a'), Kit.Nonce('b'));
        Assert.Equal(NonceConsume.Consumed, a);
        Assert.Equal(NonceConsume.Consumed, b);
        var later = new FileNonceLedger(Journal());
        Assert.Equal(NonceConsume.Replayed, later.TryConsume(Kit.Nonce('a'))); // no lost update
        Assert.Equal(NonceConsume.Replayed, later.TryConsume(Kit.Nonce('b')));
        Assert.Empty(Directory.GetFiles(Root, FileNonceLedger.FileName + ".tmp-*"));
    }

    [Fact]
    public void A_lock_held_by_another_broker_past_the_timeout_fails_closed_and_writes_nothing()
    {
        var journal = Journal();
        new FileNonceLedger(journal).TryConsume(Kit.Nonce('0'));
        var before = File.ReadAllBytes(LedgerPath);
        var clock = new ManualClock();
        var slept = 0;
        var ledger = new FileNonceLedger(journal, TimeSpan.FromSeconds(3), clock, d => { slept++; clock.Advance(d); });

        using (var other = _folder.TryHoldLock(LockPath)) // another broker, holding it throughout
        {
            Assert.NotNull(other);
            Assert.Equal(NonceConsume.Failed, ledger.TryConsume(Kit.Nonce('a')));
        }

        Assert.True(slept > 1); // it retried, with short waits, before giving up
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(3) && clock.Elapsed < TimeSpan.FromSeconds(4));
        Assert.Equal(before, File.ReadAllBytes(LedgerPath)); // nothing written
        Assert.Empty(Directory.GetFiles(Root, FileNonceLedger.FileName + ".tmp-*"));
        Assert.Equal(NonceConsume.Consumed, ledger.TryConsume(Kit.Nonce('a'))); // once released, 'a' was never recorded
    }

    [Fact]
    public void A_lock_that_frees_up_within_the_timeout_is_taken()
    {
        var journal = Journal();
        var clock = new ManualClock();
        IHeldLock? other = null;
        var ledger = new FileNonceLedger(journal, TimeSpan.FromSeconds(3), clock, d => { clock.Advance(d); if (clock.Elapsed > TimeSpan.FromSeconds(1)) other?.Dispose(); });
        journal.EnsureDirectory();
        other = _folder.TryHoldLock(LockPath);
        Assert.Equal(NonceConsume.Consumed, ledger.TryConsume(Kit.Nonce('a')));
    }

    [Fact]
    public void A_lock_file_that_is_not_the_brokers_secret_refuses_and_writes_nothing()
    {
        var journal = Journal();
        new FileNonceLedger(journal).TryConsume(Kit.Nonce('0'));
        var before = File.ReadAllBytes(LedgerPath);
        _folder.Set(LockPath, FakeTrustedFs.UserFile);
        Assert.Equal(NonceConsume.Failed, new FileNonceLedger(journal).TryConsume(Kit.Nonce('a')));
        Assert.Equal(before, File.ReadAllBytes(LedgerPath));
        _folder.Set(LockPath, FakeTrustedFs.SecretFile with { IsReparsePoint = true });
        Assert.Equal(NonceConsume.Failed, new FileNonceLedger(journal).TryConsume(Kit.Nonce('a')));
        Assert.Equal(before, File.ReadAllBytes(LedgerPath));
        Assert.False(_folder.IsLocked(LockPath)); // released either way
    }

    // ---- m9 review S3b: first-time provisioning (quarantine + key) under the same journal lock.

    [Fact]
    public async Task Two_brokers_provisioning_an_empty_folder_at_once_end_with_one_key_both_accept()
    {
        TrustedRoot(); // the folder exists and is empty: both brokers find no key
        var a = Journal();
        var b = Journal();
        Task? second = null;
        var started = 0;
        using var bDone = new ManualResetEventSlim();
        _folder.LockBusy = _ => _locked.Set();
        _folder.BeforeCreateSecret = p =>
        {
            // A is about to create its key: B starts provisioning now, and A goes on once B is blocked or done.
            if (!p.EndsWith(BrokerJournalFiles.KeyFile, StringComparison.OrdinalIgnoreCase) || Interlocked.Exchange(ref started, 1) != 0) return;
            second = Task.Run(() => { try { b.EnsureDirectory(); } finally { bDone.Set(); } });
            WaitHandle.WaitAny([_locked.WaitHandle, bDone.WaitHandle], TimeSpan.FromSeconds(5));
        };
        a.EnsureDirectory();
        await second!.WaitAsync(TimeSpan.FromSeconds(10));
        _folder.LockBusy = null;
        _folder.BeforeCreateSecret = null;

        Assert.True(_locked.IsSet); // B waited on A's lock
        Assert.Equal(a.Key(), b.Key()); // B adopted A's fresh key
        Assert.Equal(a.Key(), File.ReadAllBytes(KeyPath));
        Assert.Empty(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*")); // A's key was never quarantined
        var (_, enabled) = await Run(a, Kit.Request(null, Kit.Test(1, "test.a", 0, 5)), Kit.Nonce());
        var (_, undone) = await Run(b, Kit.Request(Kit.Nonce('c'), UndoOf5(enabled!.ReceiptId!)), Kit.Nonce('c'));
        Assert.Equal("verified", undone!.Steps[0].State); // A's records verify for B
        Assert.Equal(0, _rig.Test("test.a").Value);
    }

    [Fact]
    public void Provisioning_while_another_broker_holds_the_lock_fails_closed_past_the_bound_and_moves_nothing()
    {
        const string id = "rcpt-00000000000000ad";
        TrustedRoot();
        Plant(Root, id); // what a provisioning would quarantine
        var clock = new ManualClock();
        var slept = 0;
        var journal = new ProtectedJournal(_base, Root, Kit.User, _folder, TimeSpan.FromSeconds(3), clock, d => { slept++; clock.Advance(d); });
        _journals.Add(journal);

        using (var other = _folder.TryHoldLock(LockPath)) // another broker, holding it throughout
        {
            Assert.NotNull(other);
            Assert.Throws<IOException>(journal.EnsureDirectory);
            Assert.True(slept > 1 && clock.Elapsed >= TimeSpan.FromSeconds(3) && clock.Elapsed < TimeSpan.FromSeconds(4)); // waited, then gave up
            Assert.Equal(NonceConsume.Failed, new FileNonceLedger(journal).TryConsume(Kit.Nonce('a'))); // the request: journal_failed
        }

        Assert.False(File.Exists(KeyPath)); // nothing written
        Assert.False(File.Exists(LedgerPath));
        Assert.Empty(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*")); // nothing moved
        Assert.True(File.Exists(Path.Combine(Root, BrokerJournalFiles.SessionFile(id))));
        journal.EnsureDirectory(); // once released
        Assert.True(_folder.IsSecret(KeyPath));
        Assert.Single(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
    }

    [Fact]
    public void Provisioning_waits_for_a_lock_that_frees_up_within_the_bound()
    {
        TrustedRoot();
        var clock = new ManualClock();
        IHeldLock? other = null;
        var slept = 0;
        var journal = new ProtectedJournal(_base, Root, Kit.User, _folder, TimeSpan.FromSeconds(3), clock,
            d => { slept++; clock.Advance(d); if (clock.Elapsed > TimeSpan.FromSeconds(1)) other?.Dispose(); });
        _journals.Add(journal);
        other = _folder.TryHoldLock(LockPath);

        journal.EnsureDirectory();

        Assert.True(slept > 1);
        Assert.True(_folder.IsSecret(KeyPath));
        Assert.False(_folder.IsLocked(LockPath)); // released before returning, so the ledger can take it next
        Assert.Equal(NonceConsume.Consumed, new FileNonceLedger(journal).TryConsume(Kit.Nonce('a')));
    }

    [Fact]
    public void The_lock_file_is_never_quarantined_with_the_folder()
    {
        const string id = "rcpt-00000000000000ae";
        TrustedRoot();
        Plant(Root, id);
        File.WriteAllBytes(KeyPath, new byte[32]); // a "key" the broker didn't make
        _folder.TryHoldLock(LockPath)!.Dispose(); // the lock file an earlier broker left (a secret)

        Journal().EnsureDirectory();

        var q = Assert.Single(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
        Assert.True(File.Exists(Path.Combine(q, BrokerJournalFiles.SessionFile(id))));
        Assert.True(File.Exists(Path.Combine(q, BrokerJournalFiles.KeyFile)));
        Assert.False(File.Exists(Path.Combine(q, FileNonceLedger.LockName)));
        Assert.True(_folder.IsSecret(LockPath));
        Assert.False(_folder.IsLocked(LockPath));
        Assert.True(_folder.IsSecret(KeyPath));
    }

    [Theory]
    [InlineData("user-owned")]
    [InlineData("reparse")]
    public void A_journal_lock_that_is_not_the_brokers_secret_refuses_provisioning_and_moves_nothing(string kind)
    {
        const string id = "rcpt-00000000000000af";
        TrustedRoot();
        Plant(Root, id);
        _folder.TryHoldLock(LockPath)!.Dispose();
        _folder.Set(LockPath, kind == "reparse" ? FakeTrustedFs.SecretFile with { IsReparsePoint = true } : FakeTrustedFs.UserFile);

        Assert.Throws<IOException>(Journal().EnsureDirectory);

        Assert.False(File.Exists(KeyPath));
        Assert.Empty(Directory.GetDirectories(Root, BrokerJournalFiles.QuarantinePrefix + "*"));
        Assert.True(File.Exists(Path.Combine(Root, BrokerJournalFiles.SessionFile(id))));
        Assert.False(_folder.IsLocked(LockPath));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    }

    [Fact]
    public void The_script_runs_root_is_a_trusted_chain_too()
    {
        var runs = Path.Combine(_base, "OptimizationTool", "script-runs", "parse");
        Directory.CreateDirectory(runs); // made by the user first
        File.WriteAllText(Path.Combine(runs, "planted.ps1"), "x");

        var hold = BrokerRunFolders.Trusted(_folder, _base)(runs);

        Assert.Single(Retired(_base, "OptimizationTool"));
        Assert.True(TrustPolicy.IsTrustedFolder(_folder.FactsOf(runs)!));
        Assert.Empty(Directory.GetFileSystemEntries(runs));
        Assert.True(_folder.IsHeld(runs));
        hold.Dispose();
        Assert.False(_folder.IsHeld(runs));
        Assert.Throws<IOException>(() => BrokerRunFolders.Trusted(_folder, _base)(Path.Combine(Path.GetTempPath(), "elsewhere")));
    }
}

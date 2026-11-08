using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

public class StoreTests
{
    private static SqliteConnection Open(string dir)
    {
        var conn = new SqliteConnection($"Data Source={Path.Combine(dir, Store.FileName)};Pooling=False");
        conn.Open();
        return conn;
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void FreshDatabase_IsCreatedAtTheLatestVersion()
    {
        var dir = TestHost.NewDataDir();
        using (var store = Store.Open(dir)) { }
        using var conn = Open(dir);
        Assert.Equal((long)Store.SchemaVersion, Scalar(conn, "PRAGMA user_version"));
        Assert.Equal(6, Store.SchemaVersion);
    }

    [Fact]
    public async Task OlderSchema_IsBackedUpAndMigrated_KeepingRows()
    {
        var dir = TestHost.NewDataDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Store.FileName);
        Migrations.CreateAt(path, 1);
        using (var conn = Open(dir))
        {
            Scalar(conn, """
                INSERT INTO scans (scan_id, intent, text, status, started_at, finished_at, report_id)
                VALUES ('scan-00000000000000v1', 'audit.storage', '', 'complete', '2026-11-01T10:00:00.000Z', '2026-11-01T10:00:01.000Z', NULL);
                """);
            Assert.Equal(1L, Scalar(conn, "PRAGMA user_version"));
        }

        await using (var host = TestHost.Start(dataDir: dir))
        {
            var c = await host.ConnectAsync();
            var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = "scan-00000000000000v1" });
            Assert.Equal("complete", scan.Str("status"));
        }

        using (var conn = Open(dir))
        {
            Assert.Equal((long)Store.SchemaVersion, Scalar(conn, "PRAGMA user_version"));
            Assert.Equal(0L, Scalar(conn, "SELECT profile_revision FROM scans WHERE scan_id = 'scan-00000000000000v1'"));
        }
        Assert.True(File.Exists(path + ".v1.bak"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void NewerSchema_IsRefused()
    {
        var dir = TestHost.NewDataDir();
        using (var store = Store.Open(dir)) { }
        using (var conn = Open(dir)) Scalar(conn, "PRAGMA user_version = 99");
        var ex = Assert.Throws<InvalidOperationException>(() => Store.Open(dir));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public async Task CorruptReportRecord_IsReportedWithoutBreakingTheHost()
    {
        var dir = TestHost.NewDataDir();
        string reportId;
        await using (var host = TestHost.Start(dataDir: dir))
        {
            var c = await host.ConnectAsync();
            var (scan, _) = await c.ScanAsync("audit.storage");
            reportId = scan.Str("reportId");
        }
        using (var conn = Open(dir))
        {
            Scalar(conn, "UPDATE reports SET body = '{\"reportId\": tru' ");
            Scalar(conn, "UPDATE followups SET state = '[]'");
        }

        await using (var host = TestHost.Start(dataDir: dir))
        {
            var c = await host.ConnectAsync();
            var error = await c.Fails("report.get", new JsonObject { ["reportId"] = reportId });
            Assert.Equal(ErrorCodes.InvalidState, error.Code);
            Assert.Equal("corrupt_record", error.ErrorData.Str("reason"));
            var followup = await c.Fails("followup.get", new JsonObject { ["reportId"] = reportId });
            Assert.Equal("corrupt_record", followup.ErrorData.Str("reason"));
            Assert.Equal(reportId, (await c.Call("onboarding.get"))["resume"].Str("reportId"));
        }
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task UnreadableDatabaseFile_IsMovedAside_AndAFreshOneStarts()
    {
        var dir = TestHost.NewDataDir();
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, Store.FileName), "this is not a SQLite database, just some bytes that are long enough to be read as a header");
        await using (var host = TestHost.Start(dataDir: dir))
        {
            var c = await host.ConnectAsync();
            Assert.Equal(1, (int)(await c.Call("onboarding.get"))["revision"]!);
        }
        Assert.Single(Directory.GetFiles(dir, Store.FileName + ".corrupt-*"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Close_FoldsTheLogIntoTheFile_SoTheFileStandsOnItsOwn()
    {
        var dir = TestHost.NewDataDir();
        var path = Path.Combine(dir, Store.FileName);
        using (var store = Store.Open(dir))
            store.Write(db => db.Exec("INSERT INTO onboarding (id, revision, state, updated_at) VALUES (1, 7, '{}', 'now')"));
        Assert.True(!File.Exists(path + "-wal") || new FileInfo(path + "-wal").Length == 0);
        // The main file alone (no log next to it) has the row.
        var lone = Path.Combine(TestHost.NewDataDir(), Store.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(lone)!);
        File.Copy(path, lone);
        using var conn = Open(Path.GetDirectoryName(lone)!);
        Assert.Equal(7L, Scalar(conn, "SELECT revision FROM onboarding WHERE id = 1"));
    }

    [Fact]
    public void StaleWriteAheadLog_IsSetAside_AndTheIntactFileIsKept()
    {
        // The 2026-11-05 incident: an older log of the same database replayed over a newer, larger main file.
        var dir = TestHost.NewDataDir();
        var path = Path.Combine(dir, Store.FileName);
        var staleWal = Path.Combine(TestHost.NewDataDir() + "-wal-copy");
        using (var store = Store.Open(dir)) { }
        using (var conn = Open(dir))
        {
            Scalar(conn, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            Scalar(conn, "INSERT INTO onboarding (id, revision, state, updated_at) VALUES (1, 1, '{}', 'now')");
            for (var i = 0; i < 40; i++)
                Scalar(conn, $"INSERT INTO events (stream, seq, type, at, payload) VALUES ('s', {i}, 't', 'now', '{new string('x', 900)}')");
            Scalar(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
            Scalar(conn, "UPDATE onboarding SET revision = 2");
            File.Copy(path + "-wal", staleWal);                       // a log holding one small change, at the early size
            for (var i = 40; i < 400; i++)
                Scalar(conn, $"INSERT INTO events (stream, seq, type, at, payload) VALUES ('s', {i}, 't', 'now', '{new string('y', 900)}')");
            Scalar(conn, "UPDATE onboarding SET revision = 42");
            Scalar(conn, "PRAGMA wal_checkpoint(TRUNCATE)");          // the newer state lands in the main file
        }
        File.Copy(staleWal, path + "-wal", overwrite: true);
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");

        using (var store = Store.Open(dir))
        {
            Assert.Null(store.MovedAsidePath);
            Assert.NotNull(store.WalAsidePath);
            Assert.Equal(42L, store.Read(db => db.Scalar("SELECT revision FROM onboarding WHERE id = 1")));
        }
        Assert.Single(Directory.GetFiles(dir, Store.FileName + "-wal.damaged-*"));
    }

    [Fact]
    public async Task ConcurrentWriters_PublishEventsInCommitOrder()
    {
        using var store = Store.Open(TestHost.NewDataDir());
        var seen = new List<long>();
        store.EventCommitted += e =>
        {
            if (e.Stream != "order") return;
            if (e.Seq % 7 == 0) Thread.Sleep(1); // a slow subscriber widens the window between commit and publish
            lock (seen) seen.Add(e.Seq);
        };
        const int writers = 8, each = 40;
        var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < each; i++) store.Write(db => { db.AppendEvent("order", "t", []); });
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(Enumerable.Range(1, writers * each).Select(i => (long)i), seen);
    }

    [Fact]
    public async Task HandlerThatWrites_IsPublishedAfterTheCurrentEvent_WithoutDeadlock()
    {
        using var store = Store.Open(TestHost.NewDataDir());
        var seen = new List<string>();
        store.EventCommitted += e =>
        {
            if (e.Stream == "s") lock (seen) seen.Add(e.Type + e.Seq);
            if (e.Type == "a") store.Write(db => { db.AppendEvent("s", "b", []); });
        };
        store.EventCommitted += e => { if (e.Stream == "s") lock (seen) seen.Add("second:" + e.Type + e.Seq); };
        var other = Task.Run(() => { for (var i = 0; i < 20; i++) store.Write(db => { db.AppendEvent("x", "c", []); }); });
        store.Write(db => { db.AppendEvent("s", "a", []); });
        await other.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["a1", "second:a1", "b2", "second:b2"], seen);
    }

    [Fact]
    public void DataDirectory_AllowsOnlyTheUserAndSystem_WithoutInheritance()
    {
        var dir = TestHost.NewDataDir();
        using (var store = Store.Open(dir)) { }
        var security = new DirectoryInfo(dir).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToHashSet();
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(new HashSet<SecurityIdentifier> { me.User!, new(WellKnownSidType.LocalSystemSid, null) }, sids);

        var file = new FileInfo(Path.Combine(dir, Store.FileName)).GetAccessControl();
        Assert.True(file.AreAccessRulesProtected);
        Directory.Delete(dir, true);
    }
}

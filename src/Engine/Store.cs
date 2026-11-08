using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace OptimizationTool.Engine;

/// <summary>A committed event row: one per state change, sequenced per stream (protocol §5).</summary>
public sealed record EventRecord(string Stream, long Seq, string Type, string At, JsonNode Payload)
{
    public JsonObject ToJson() => new()
    {
        ["stream"] = Stream, ["seq"] = Seq, ["type"] = Type, ["at"] = At, ["payload"] = Payload.DeepClone(),
    };
}

/// <summary>
/// The per-user SQLite store. One connection, one writer: every read and write runs under a lock.
/// <see cref="Write{T}"/> runs a transaction; events appended inside it get their stream seq in the same
/// transaction and are published to <see cref="EventCommitted"/> only after commit, in commit order: committed batches
/// are queued under the store lock and one publisher at a time drains the queue outside it, so handlers may call back
/// into the store (a handler's own writes are published by the drain already running, after the current event).
/// </summary>
public sealed class Store : IDisposable
{
    public const string FileName = "state.db";

    private readonly SqliteConnection _conn;
    private readonly Func<string> _now;
    private readonly object _gate = new();

    public event Action<EventRecord>? EventCommitted;

    public string Path { get; }

    /// <summary>Set when the database file could not be read and was moved aside: where it was moved to.</summary>
    public string? MovedAsidePath { get; private init; }

    public static int SchemaVersion => Migrations.Latest;

    private Store(SqliteConnection conn, string path, Func<string> now)
    {
        _conn = conn;
        Path = path;
        _now = now;
    }

    /// <summary>Opens (creating and migrating as needed) the database in <paramref name="dataDir"/>.</summary>
    public static Store Open(string dataDir, Func<string>? now = null)
    {
        now ??= () => Time.Format(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(dataDir);
        Acl.RestrictDirectory(dataDir);
        var path = System.IO.Path.Combine(dataDir, FileName);

        string? movedAside = null, walAside = null;
        // A write-ahead log that doesn't belong to the file (stale frames replayed over a newer main file, seen on
        // 2026-11-05) reads as corruption. The check runs read-only first: closing a read-write connection would
        // checkpoint those frames into the main file. If the file is sound without the log, the log is set aside.
        if (File.Exists(path) && !IsSoundReadOnly(path) && File.Exists(path + "-wal"))
        {
            walAside = path + "-wal.damaged-" + Stamp();
            File.Move(path + "-wal", walAside);
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
        }
        var conn = TryOpenSound(path);
        if (conn is null)
        {
            // An unreadable file is kept for inspection and a fresh database is started.
            movedAside = path + ".corrupt-" + Stamp();
            File.Move(path, movedAside);
            foreach (var side in new[] { "-wal", "-shm" })
                if (File.Exists(path + side)) File.Delete(path + side);
            conn = OpenAndMigrate(path);
        }
        return new Store(conn, path, now) { MovedAsidePath = movedAside, WalAsidePath = walAside };
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>quick_check over a read-only connection, which never writes the log back into the file.</summary>
    private static bool IsSoundReadOnly(string path)
    {
        try
        {
            using var ro = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            ro.Open();
            return QuickCheck(ro);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { return false; }
        catch (SqliteException) { return true; } // can't tell read-only (e.g. busy): let the normal open decide
    }

    /// <summary>Opens and migrates; null when the file (with its log) reads as corrupt or isn't a database.</summary>
    private static SqliteConnection? TryOpenSound(string path)
    {
        SqliteConnection conn;
        try { conn = OpenAndMigrate(path); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { return null; } // SQLITE_CORRUPT, SQLITE_NOTADB
        if (QuickCheck(conn)) return conn;
        conn.Dispose();
        return null;
    }

    /// <summary>PRAGMA quick_check: true when it reports "ok". A read that itself fails as corrupt counts as false.</summary>
    private static bool QuickCheck(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check(1)";
            return cmd.ExecuteScalar() is string s && s == "ok";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { return false; }
    }

    /// <summary>Copies the write-ahead log into the main file and empties it, so the file stands on its own.</summary>
    private static void Checkpoint(SqliteConnection conn)
    {
        try { Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);"); }
        catch (SqliteException) { /* busy or closing: the log stays valid and is replayed on the next open */ }
    }

    private static SqliteConnection OpenAndMigrate(string path)
    {
        var isNew = !File.Exists(path);
        var conn = Connect(path);
        try
        {
            Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            if (isNew) Acl.RestrictFile(path);
            var before = Migrations.CurrentVersion(conn);
            Migrations.Upgrade(conn, path, Migrations.Latest);
            if (before != Migrations.Latest) Checkpoint(conn); // a migrated file carries its new version itself
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    internal static SqliteConnection Connect(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    internal static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public T Read<T>(Func<Db, T> read)
    {
        T result;
        lock (_gate) result = read(new Db(_conn, null, _now));
        Publish(); // only does work when a write nested in a read left events queued
        return result;
    }

    public T Write<T>(Func<Db, T> write)
    {
        T result;
        List<EventRecord> events;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var db = new Db(_conn, tx, _now);
            result = write(db);
            tx.Commit();
            events = db.Appended;
            // Queued in commit order while the store lock is still held; published below, outside it.
            if (events.Count > 0) lock (_queueGate) foreach (var e in events) _unpublished.Enqueue(e);
            if (++_writesSinceCheckpoint >= CheckpointEvery) { _writesSinceCheckpoint = 0; Checkpoint(_conn); }
        }
        Publish();
        return result;
    }

    private readonly object _queueGate = new();   // guards _unpublished only; no other lock is taken while it is held
    private readonly object _publishGate = new(); // one publisher at a time; never taken while holding _gate
    private readonly Queue<EventRecord> _unpublished = new();

    /// <summary>
    /// Publishes queued events in commit order. A thread that still holds the store lock (a write nested in a read) or
    /// is already publishing (a handler that wrote) leaves them queued for the drain in progress or the next one.
    /// Otherwise, when this returns, every event committed before the call has been published.
    /// </summary>
    private void Publish()
    {
        if (Monitor.IsEntered(_gate) || Monitor.IsEntered(_publishGate)) return;
        lock (_queueGate) if (_unpublished.Count == 0) return;
        lock (_publishGate)
        {
            while (true)
            {
                EventRecord? e;
                lock (_queueGate) if (!_unpublished.TryDequeue(out e)) return;
                EventCommitted?.Invoke(e);
            }
        }
    }

    public void Write(Action<Db> write) => Write(db => { write(db); return 0; });

    /// <summary>Set when a damaged write-ahead log was set aside and the main file kept: where the log was moved to.</summary>
    public string? WalAsidePath { get; private init; }

    /// <summary>Folds the log into the main file (and empties it) every few hundred writes and on close.</summary>
    private int _writesSinceCheckpoint;
    private const int CheckpointEvery = 200;

    public void Dispose()
    {
        lock (_gate)
        {
            Checkpoint(_conn);
            _conn.Dispose();
        }
    }
}

/// <summary>Thin command helper bound to the store connection (and transaction, for writes).</summary>
public sealed class Db(SqliteConnection conn, SqliteTransaction? tx, Func<string> now)
{
    internal List<EventRecord> Appended { get; } = [];

    public string Now() => now();

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    /// <summary>Appends an event with the stream's next seq inside the current transaction.</summary>
    public EventRecord AppendEvent(string stream, string type, JsonObject payload)
    {
        if (tx is null) throw new InvalidOperationException("Events can only be appended in a write transaction.");
        var seq = LastSeq(stream) + 1;
        var at = Now();
        Exec("INSERT INTO events (stream, seq, type, at, payload) VALUES ($s, $q, $t, $a, $p)",
            ("$s", stream), ("$q", seq), ("$t", type), ("$a", at), ("$p", Json.Write(payload)));
        var record = new EventRecord(stream, seq, type, at, payload);
        Appended.Add(record);
        return record;
    }

    public long LastSeq(string stream) =>
        (long)Scalar("SELECT COALESCE(MAX(seq), 0) FROM events WHERE stream = $s", ("$s", stream))!;
}

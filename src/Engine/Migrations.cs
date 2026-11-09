using Microsoft.Data.Sqlite;

namespace OptimizationTool.Engine;

/// <summary>
/// The data folder's database was migrated by a newer build: this build must not open it (the host exits with
/// <c>HostExitCodes.NeedsUpdate</c> and the shell asks the user to update).
/// </summary>
public sealed class DataTooNewException(int found, int supported)
    : InvalidOperationException($"Database schema v{found} is newer than this build supports (v{supported}).")
{
    public int Found { get; } = found;
    public int Supported { get; } = supported;
}

/// <summary>
/// Versioned schema migrations tracked in PRAGMA user_version. Version 0 is an empty file. Before upgrading an
/// existing database the file is backed up next to itself as state.db.v{old}.bak. A database newer than this
/// build is refused rather than guessed at.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // v1: initial M1 schema.
        """
        CREATE TABLE onboarding (id INTEGER PRIMARY KEY CHECK (id = 1), revision INTEGER NOT NULL, state TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE scans (scan_id TEXT PRIMARY KEY, intent TEXT NOT NULL, text TEXT NOT NULL, status TEXT NOT NULL,
            started_at TEXT NOT NULL, finished_at TEXT, report_id TEXT);
        CREATE TABLE scan_collectors (scan_id TEXT NOT NULL, collector_id TEXT NOT NULL, ord INTEGER NOT NULL, category TEXT NOT NULL,
            version TEXT NOT NULL, status TEXT NOT NULL, observations INTEGER NOT NULL DEFAULT 0, reason TEXT,
            PRIMARY KEY (scan_id, collector_id));
        CREATE TABLE evidence (evidence_id TEXT PRIMARY KEY, scan_id TEXT NOT NULL, collector_id TEXT NOT NULL, ord INTEGER NOT NULL,
            subject TEXT NOT NULL, body TEXT NOT NULL);
        CREATE TABLE reports (report_id TEXT PRIMARY KEY, scan_id TEXT NOT NULL UNIQUE, generated_at TEXT NOT NULL, status TEXT NOT NULL,
            intent TEXT NOT NULL, body TEXT NOT NULL);
        CREATE TABLE followups (report_id TEXT PRIMARY KEY, revision INTEGER NOT NULL, state TEXT NOT NULL);
        CREATE TABLE inventories (inventory_id TEXT PRIMARY KEY, status TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
            coverage TEXT NOT NULL, capabilities TEXT);
        CREATE TABLE inventory_facts (inventory_id TEXT NOT NULL, seq INTEGER NOT NULL, kind TEXT NOT NULL, label TEXT NOT NULL,
            PRIMARY KEY (inventory_id, seq));
        CREATE TABLE events (stream TEXT NOT NULL, seq INTEGER NOT NULL, type TEXT NOT NULL, at TEXT NOT NULL, payload TEXT NOT NULL,
            PRIMARY KEY (stream, seq));
        CREATE TABLE idempotency (method TEXT NOT NULL, key TEXT NOT NULL, digest TEXT NOT NULL, result TEXT NOT NULL, created_at TEXT NOT NULL,
            PRIMARY KEY (method, key));
        """,
        // v2: record the onboarding profile revision a scan was planned with; index active-scan lookups.
        """
        ALTER TABLE scans ADD COLUMN profile_revision INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX scans_status ON scans (status);
        """,
        // v3 (M2): rescan base, inventory groups and structured inventory facts, cross-scan dispositions.
        """
        ALTER TABLE scans ADD COLUMN base_report_id TEXT;
        ALTER TABLE inventory_facts ADD COLUMN grp TEXT;
        CREATE TABLE inventory_items (inventory_id TEXT NOT NULL, ord INTEGER NOT NULL, grp TEXT NOT NULL, subject TEXT NOT NULL,
            body TEXT NOT NULL, PRIMARY KEY (inventory_id, ord));
        CREATE TABLE dispositions (scope TEXT NOT NULL, key TEXT NOT NULL, rule_id TEXT NOT NULL, status TEXT NOT NULL,
            expires_at TEXT, severity TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY (scope, key));
        """,
        // v4 (M3): agent tasks and grants, task evidence and what was released, aliases, operations, pairings, reviews.
        // connection_id on a task is its binding ("pid:<bridge pid>" for native clients, null in-app).
        """
        CREATE TABLE agent_tasks (task_id TEXT PRIMARY KEY, recipient_kind TEXT NOT NULL, body TEXT NOT NULL, status TEXT NOT NULL,
            connection_id TEXT, created_at TEXT NOT NULL, last_activity_at TEXT NOT NULL);
        CREATE INDEX agent_tasks_status ON agent_tasks (status, recipient_kind);
        CREATE TABLE agent_evidence (evidence_id TEXT PRIMARY KEY, task_id TEXT NOT NULL, collector_id TEXT NOT NULL, subject TEXT NOT NULL, body TEXT NOT NULL);
        CREATE INDEX agent_evidence_task ON agent_evidence (task_id);
        CREATE TABLE agent_released (task_id TEXT NOT NULL, evidence_id TEXT NOT NULL, PRIMARY KEY (task_id, evidence_id));
        CREATE TABLE agent_aliases (scope_id TEXT NOT NULL, kind TEXT NOT NULL, name_hash TEXT NOT NULL, alias TEXT NOT NULL,
            PRIMARY KEY (scope_id, kind, name_hash));
        CREATE TABLE agent_operations (operation_id TEXT PRIMARY KEY, task_id TEXT NOT NULL, status TEXT NOT NULL, result TEXT, created_at TEXT NOT NULL);
        CREATE TABLE agent_pairings (pairing_id TEXT PRIMARY KEY, client TEXT NOT NULL, client_version TEXT, companion_pid INTEGER NOT NULL,
            code TEXT NOT NULL, status TEXT NOT NULL, connection_id TEXT NOT NULL, created_at TEXT NOT NULL, expires_at TEXT NOT NULL);
        CREATE TABLE reviews (review_id TEXT PRIMARY KEY, kind TEXT NOT NULL, recipient_kind TEXT, report_id TEXT NOT NULL, revision INTEGER NOT NULL,
            evidence_digest TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL, expires_at TEXT NOT NULL, consumed_at TEXT);
        """,
        // v5 (M4): action plans, one operation per approved plan with its journal steps, approval nonces, manual tasks.
        """
        CREATE TABLE action_plans (plan_id TEXT PRIMARY KEY, body TEXT NOT NULL, status TEXT NOT NULL, task_id TEXT, target_key TEXT NOT NULL,
            created_at TEXT NOT NULL, expires_at TEXT NOT NULL);
        CREATE INDEX action_plans_task ON action_plans (task_id, status);
        CREATE TABLE operations (operation_id TEXT PRIMARY KEY, plan_id TEXT NOT NULL UNIQUE, body TEXT NOT NULL, state TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE operation_steps (operation_id TEXT NOT NULL, seq INTEGER NOT NULL, state TEXT NOT NULL, at TEXT NOT NULL, detail TEXT,
            PRIMARY KEY (operation_id, seq));
        CREATE TABLE action_nonces (nonce TEXT PRIMARY KEY, plan_id TEXT NOT NULL, user_sid TEXT NOT NULL, session INTEGER NOT NULL,
            expires_at TEXT NOT NULL, used_at TEXT);
        CREATE TABLE manual_tasks (finding_id TEXT PRIMARY KEY, report_id TEXT NOT NULL, status TEXT NOT NULL, updated_at TEXT NOT NULL);
        """,
        // v6 (M5): persisted in-app transcripts per task (last 40 messages, tool results cut to 4 KB each).
        """
        CREATE TABLE agent_transcripts (task_id TEXT PRIMARY KEY, body TEXT NOT NULL, updated_at TEXT NOT NULL);
        """,
        // v7 (M6a): benchmark runs (body = the BenchRun as stored; parts = successful parts, sorted, for comparability)
        // and their evidence (ev- + H(runId, part, metric)).
        """
        CREATE TABLE bench_runs (run_id TEXT PRIMARY KEY, state TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
            bench_version TEXT NOT NULL, config_id TEXT, score REAL, partial INTEGER NOT NULL DEFAULT 0, parts TEXT NOT NULL DEFAULT '',
            body TEXT NOT NULL);
        CREATE INDEX bench_runs_state ON bench_runs (state, config_id);
        CREATE TABLE bench_evidence (evidence_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, part TEXT NOT NULL, metric TEXT NOT NULL, body TEXT NOT NULL);
        CREATE INDEX bench_evidence_run ON bench_evidence (run_id);
        """,
    ];

    public static int Latest => Steps.Length;

    public static int CurrentVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Upgrade(SqliteConnection conn, string path, int target)
    {
        var current = CurrentVersion(conn);
        if (current > Latest)
            throw new DataTooNewException(current, Latest);
        if (current >= target) return;

        if (current > 0)
        {
            using var backup = Store.Connect($"{path}.v{current}.bak");
            conn.BackupDatabase(backup);
        }

        for (var v = current; v < target; v++)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Steps[v] + $"\nPRAGMA user_version = {v + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>Test hook: creates a database at an older schema version.</summary>
    public static void CreateAt(string path, int version)
    {
        using var conn = Store.Connect(path);
        Upgrade(conn, path, version);
    }
}

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
        // v8 (M9, contracts/m9-ai-powers.md §8): per-user settings (key ai.permissions, §1.1). Later M9 phases add their
        // tables to this step (research_*, script_runs) before v8 ships. operation_step_states (§4): one row per fix step;
        // operation_steps stays the append-only transition history.
        """
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE operation_step_states (operation_id TEXT NOT NULL, n INTEGER NOT NULL, state TEXT NOT NULL, preimage TEXT, dispatched_at TEXT,
            observed TEXT, receipt TEXT, undo TEXT, error TEXT, updated_at TEXT NOT NULL, PRIMARY KEY (operation_id, n));
        """ + ResearchTables + ScriptTables,
        // v9 (M9 §17.3 "Fix for me"): a plan's card (one pending AI plan per card, across tasks) and the fix attempts (one
        // current attempt per card; card.fix is derived on read, never stored). §17.2 administrator checks: the requests (one
        // per asking turn, 2 per task; revision and frozen membership), each check as an agent operation of its request (n, its
        // window), and the one continuation per request a turn asked for.
        """
        ALTER TABLE action_plans ADD COLUMN card_id TEXT;
        CREATE INDEX action_plans_card ON action_plans (card_id, status);
        CREATE TABLE fix_attempts (attempt_id TEXT PRIMARY KEY, task_id TEXT NOT NULL, report_id TEXT NOT NULL, card_id TEXT NOT NULL,
            state TEXT NOT NULL, turn_id TEXT, outcome TEXT, opinion TEXT, by_label TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX fix_attempts_card ON fix_attempts (card_id, state);
        CREATE INDEX fix_attempts_turn ON fix_attempts (turn_id);
        CREATE TABLE check_requests (request_id TEXT PRIMARY KEY, task_id TEXT NOT NULL, turn_id TEXT, attempt_id TEXT, card_id TEXT,
            revision INTEGER NOT NULL, state TEXT NOT NULL, code TEXT, frozen INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL, ended_at TEXT);
        CREATE INDEX check_requests_task ON check_requests (task_id, state);
        CREATE INDEX check_requests_attempt ON check_requests (attempt_id, state);
        ALTER TABLE agent_operations ADD COLUMN request_id TEXT;
        ALTER TABLE agent_operations ADD COLUMN check_id TEXT;
        ALTER TABLE agent_operations ADD COLUMN n INTEGER;
        ALTER TABLE agent_operations ADD COLUMN check_window INTEGER;
        CREATE INDEX agent_operations_request ON agent_operations (request_id);
        CREATE TABLE continuations (request_id TEXT PRIMARY KEY, task_id TEXT NOT NULL, attempt_id TEXT, card_id TEXT, state TEXT NOT NULL,
            turn_id TEXT, created_at TEXT NOT NULL);
        CREATE INDEX continuations_task ON continuations (task_id, state);
        CREATE INDEX continuations_attempt ON continuations (attempt_id, state);
        """,
    ];

    /// <summary>
    /// M9 §5 approved scripts (part of v8, created if missing on every open like <see cref="ResearchTables"/>): one row per
    /// approved script (the durable dispatch record: dispatched_at is committed before launch), and the dialog's options and
    /// plan hash each script nonce is bound to.
    /// </summary>
    internal const string ScriptTables = """
        CREATE TABLE IF NOT EXISTS script_runs (operation_id TEXT PRIMARY KEY, plan_id TEXT NOT NULL UNIQUE, task_id TEXT, sha256 TEXT NOT NULL, text TEXT NOT NULL,
            options TEXT NOT NULL, approved_at TEXT NOT NULL, state TEXT NOT NULL, exit_code INTEGER, guard_version TEXT NOT NULL, warnings TEXT NOT NULL,
            restore_point TEXT, transcript_path TEXT, dispatched_at TEXT, ended_at TEXT);
        CREATE TABLE IF NOT EXISTS action_nonce_bindings (nonce TEXT PRIMARY KEY, plan_hash TEXT NOT NULL, options TEXT NOT NULL);
        """;

    /// <summary>
    /// M9 §6 research stores (part of v8). Idempotent: <see cref="Store"/> also runs it on open, so a development database
    /// migrated to v8 before these tables were appended gets them too.
    /// </summary>
    internal const string ResearchTables = """
        CREATE TABLE IF NOT EXISTS research_runs (run_id TEXT PRIMARY KEY, report_id TEXT NOT NULL, status TEXT NOT NULL, done INTEGER NOT NULL, total INTEGER NOT NULL,
            trial INTEGER NOT NULL, provider_kind TEXT NOT NULL, account TEXT NOT NULL, model TEXT, permissions_revision INTEGER NOT NULL, usage TEXT NOT NULL,
            started_at TEXT NOT NULL, finished_at TEXT);
        CREATE INDEX IF NOT EXISTS research_runs_report ON research_runs (report_id);
        CREATE TABLE IF NOT EXISTS research_products (product_key TEXT NOT NULL, policy_version TEXT NOT NULL, body TEXT NOT NULL, researched_at TEXT NOT NULL,
            expires_at TEXT NOT NULL, PRIMARY KEY (product_key, policy_version));
        CREATE TABLE IF NOT EXISTS research_labels (report_id TEXT NOT NULL, finding_id TEXT NOT NULL, run_id TEXT NOT NULL, product_key TEXT NOT NULL,
            severity TEXT NOT NULL, item TEXT NOT NULL, researched_at TEXT NOT NULL, PRIMARY KEY (report_id, finding_id));
        CREATE TABLE IF NOT EXISTS research_sources (source_id TEXT PRIMARY KEY, url TEXT NOT NULL, title TEXT NOT NULL, domain TEXT NOT NULL, run_id TEXT NOT NULL,
            product_key TEXT NOT NULL, created_at TEXT NOT NULL);
        """;

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

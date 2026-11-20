using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Engine.Agent;

// Administrator checks on the host (contracts/m9-ai-powers.md §17.2, §17.6). run_diagnostic with an admin-tier id does not
// run: the waiting calls of one turn form one request {requestId, revision, taskId, attemptId, checks[1–4]} (a paired window's
// calls join the task's open request until it is approved). The user runs it with checks.approve (stale revision -32002,
// membership frozen from there) or declines it with checks.decline; agent.checks on stream user carries every change. The
// host rechecks the task, the grant (checks and admin), the flag and the budget before the helper is started and again before
// the results are released; a narrowing, a task end or a change of the chosen AI cancels the request (a running session gets
// stop) and nothing is stored. After any decline or cancel by the user (in the app, at UAC or in the helper's window) no read
// is dispatched for 60 s (retryAt). Valid results become the task's evidence (source admin, coverage kept). When a request
// that a turn asked for ends, ONE continuation is recorded and started through the normal ask admission.
public sealed partial class AgentService
{
    public const int MaxCheckRequestsPerTask = 2;
    public static readonly TimeSpan CheckRequestWait = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ReadCooldown = TimeSpan.FromSeconds(60);

    /// <summary>A request's and an administrator check operation's states (§17.6: complete, not done).</summary>
    public static readonly string[] CheckStates = ["awaiting_user", "running", "complete", "declined", "expired", "failed", "cancelled"];

    public const string ChecksEvent = "agent.checks";
    public const string AdminUnavailableMessage = "Administrator checks aren't available in this version.";
    public const string DeclinedBeforeMessage = "The user declined this check earlier in this conversation. Go on without it.";
    public const string CheckRequestsUsed = "This conversation has used its two requests for administrator checks.";
    public const string OtherWindowMessage = "That check is already waiting with another time span.";

    private long _cooldownEnd; // the cooldown's end as a monotonic timestamp (TimeProvider.GetTimestamp), 0: none
    private DateTimeOffset _cooldownRetryAt; // its wall-clock end as last shown (retryAt)
    private readonly Dictionary<string, CancellationTokenSource> _checkStops = [];
    private readonly SemaphoreSlim _readOne = new(1, 1); // one helper session at a time, held through its intake

    /// <summary>Administrator checks can run in this host: --enable-admin-actions and an elevation path for reads.</summary>
    internal bool AdminChecksAvailable => Actions?.AdminChecksEnabled == true && _options.ElevatedReads is not null;

    private sealed record CheckRequest(string RequestId, string TaskId, string? TurnId, string? AttemptId, string? CardId, int Revision, string State,
        string? Code, bool Frozen, string CreatedAt, string ExpiresAt);

    private sealed record CheckOp(string OperationId, string CheckId, int N, int? Window, string Status, JsonObject? Result);

    private const string RequestColumns = "request_id, task_id, turn_id, attempt_id, card_id, revision, state, code, frozen, created_at, expires_at";

    private static CheckRequest RequestRow(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.GetInt32(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8) != 0, r.GetString(9), r.GetString(10));

    private static CheckRequest? LoadRequest(Db db, string requestId) =>
        db.Query($"SELECT {RequestColumns} FROM check_requests WHERE request_id = $r", RequestRow, ("$r", requestId)).FirstOrDefault();

    private static List<CheckRequest> TaskRequests(Db db, string taskId, params string[] states) =>
        db.Query($"SELECT {RequestColumns} FROM check_requests WHERE task_id = $t ORDER BY rowid", RequestRow, ("$t", taskId))
            .Where(q => states.Length == 0 || states.Contains(q.State)).ToList();

    private static List<CheckOp> Ops(Db db, string requestId) =>
        db.Query("SELECT operation_id, check_id, n, check_window, status, result FROM agent_operations WHERE request_id = $r ORDER BY n",
            r => new CheckOp(r.GetString(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : Json.Parse(r.GetString(5)) as JsonObject), ("$r", requestId));

    /// <summary>The time left in the cooldown, on the monotonic clock (a wall-clock change neither shortens nor extends it), or null.</summary>
    private TimeSpan? CooldownLeft()
    {
        lock (_gate)
        {
            if (_cooldownEnd == 0) return null;
            var left = _time.GetElapsedTime(_time.GetTimestamp(), _cooldownEnd);
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>
    /// When the cooldown ends, as a wall-clock time (retryAt): now plus the monotonic time left. Kept stable between calls
    /// (the same retryAt in agent.checks and a refused checks.approve) and recomputed only when the wall clock moved.
    /// </summary>
    private DateTimeOffset? CooldownUntil()
    {
        if (CooldownLeft() is not { } left) return null;
        var expected = Now() + left;
        lock (_gate)
        {
            if ((_cooldownRetryAt - expected).Duration() > TimeSpan.FromSeconds(1)) _cooldownRetryAt = expected;
            return _cooldownRetryAt;
        }
    }

    private string? RetryAt() => CooldownUntil() is { } until ? Time.Format(until) : null;

    /// <summary>The settings row that keeps the cooldown's end across a restart.</summary>
    internal const string CooldownSetting = "checks.cooldownUntil";

    /// <summary>Starts (or, after a restart, resumes) the in-process cooldown with <paramref name="left"/> to go and stores its wall-clock end.</summary>
    private void SetCooldown(Db db, TimeSpan left)
    {
        var until = Now() + left;
        Settings.Set(db, CooldownSetting, Time.Format(until));
        lock (_gate)
        {
            // GetElapsedTime(now, end) == left: the end as a timestamp of this process's monotonic clock.
            _cooldownEnd = _time.GetTimestamp() + (long)(left.TotalSeconds * _time.TimestampFrequency);
            _cooldownRetryAt = until;
        }
    }

    /// <summary>Once the cooldown has passed, its stored end is cleared (it can't come back after a clock rollback).</summary>
    private void ClearPassedCooldown(Db db)
    {
        lock (_gate)
        {
            if (_cooldownEnd == 0 || _time.GetElapsedTime(_time.GetTimestamp(), _cooldownEnd) > TimeSpan.Zero) return;
            _cooldownEnd = 0;
        }
        db.Exec("DELETE FROM settings WHERE key = $k", ("$k", CooldownSetting));
    }

    /// <summary>
    /// A decline or cancel by the user (in the app, at UAC, in the helper's window, Stop): no read request is dispatched for
    /// 60 s, app-wide, also after a restart (the end is stored); waiting requests show retryAt.
    /// </summary>
    private void StartCooldown(Db db)
    {
        SetCooldown(db, ReadCooldown);
        foreach (var waiting in db.Query($"SELECT {RequestColumns} FROM check_requests WHERE state = 'awaiting_user'", RequestRow))
            EmitChecks(db, waiting);
    }

    /// <summary>The request as the app shows it (checks.approve / checks.decline results and the agent.checks payload).</summary>
    private JsonObject RequestView(Db db, CheckRequest q) => new()
    {
        ["requestId"] = q.RequestId, ["revision"] = q.Revision, ["taskId"] = q.TaskId, ["cardId"] = q.CardId, ["state"] = q.State, ["code"] = q.Code,
        ["retryAt"] = q.State == "awaiting_user" ? RetryAt() : null, ["expiresAt"] = q.ExpiresAt,
        ["checks"] = new JsonArray([.. Ops(db, q.RequestId).Select(o => (JsonNode?)new JsonObject
        {
            ["operationId"] = o.OperationId, ["id"] = o.CheckId, ["label"] = AdminChecks.HostLabel(o.CheckId, o.Window) ?? o.CheckId, ["state"] = o.Status,
        })]),
    };

    private void EmitChecks(Db db, CheckRequest q)
    {
        db.AppendEvent("user", ChecksEvent, RequestView(db, q));
        if (q.AttemptId is { } attemptId && FixAttempts.Get(db, attemptId) is { } attempt) FixAttempts.Emit(db, attempt.CardId, attempt.ReportId);
    }

    private static bool Due(CheckRequest q, DateTimeOffset now) => q.State == "awaiting_user" && ParseTime(q.ExpiresAt) <= now;

    /// <summary>Requests that waited 10 minutes end expired (lazily, and from the status sweep).</summary>
    private void ExpireDueChecks(Db db)
    {
        var now = Now();
        foreach (var q in db.Query($"SELECT {RequestColumns} FROM check_requests WHERE state = 'awaiting_user'", RequestRow).Where(q => Due(q, now)))
            EndRequest(db, q, "expired", null, _ => ("expired", null));
    }

    /// <summary>The status sweep's tick (every 2 s): expiry, and a cooldown that has passed is cleared.</summary>
    private void CheckRequestsTick() => _store.Write(db =>
    {
        ExpireDueChecks(db);
        ClearPassedCooldown(db);
    });

    /// <summary>
    /// A request reaches an end state: its operations still open take <paramref name="opState"/>, agent.checks (and card.fix)
    /// follow, and the turn that asked gets its one continuation (recorded dropped when <paramref name="stale"/>).
    /// </summary>
    private void EndRequest(Db db, CheckRequest q, string state, string? code, Func<CheckOp, (string Status, string? Code)> opState, bool stale = false)
    {
        foreach (var op in Ops(db, q.RequestId).Where(o => o.Status is "awaiting_user" or "running"))
        {
            var (status, opCode) = opState(op);
            db.Exec("UPDATE agent_operations SET status = $s, result = $r WHERE operation_id = $o",
                ("$s", status), ("$r", opCode is null ? null : Json.Write(new JsonObject { ["diagnosticId"] = op.CheckId, ["code"] = opCode })), ("$o", op.OperationId));
        }
        db.Exec("UPDATE check_requests SET state = $s, code = $c, ended_at = $n WHERE request_id = $r", ("$s", state), ("$c", code), ("$n", db.Now()), ("$r", q.RequestId));
        var ended = q with { State = state, Code = code };
        if (RecordContinuation(db, ended, stale)) ScheduleContinuation(q.TaskId); // runs once this transaction has committed
        EmitChecks(db, ended);
    }

    /// <summary>A failed recheck cancels the request, unless the recheck's own expiry of the task already did.</summary>
    private void CancelIfOpen(Db db, string requestId, string code)
    {
        if (LoadRequest(db, requestId) is { State: "awaiting_user" or "running" } still)
            EndRequest(db, still, "cancelled", code, _ => ("cancelled", code), stale: code is "task_ended" or "ai");
    }

    /// <summary>
    /// Why a request may not start or be released now, or null (§17.2, in one transaction): the task is active, the grant still
    /// has every check and administrator rights, the flag is on, the chosen AI is still the task's (in-app and headless), and
    /// the sharing budget holds. Codes: task_ended | admin_unavailable | permissions | ai | exhausted.
    /// </summary>
    private string? CheckRecheck(Db db, CheckRequest q)
    {
        var task = LoadLive(db, q.TaskId);
        if (task.Text("status") != "active") return "task_ended";
        if (!AdminChecksAvailable) return "admin_unavailable";
        var grant = GrantOf(task);
        if (!grant.Admin || Ops(db, q.RequestId).Any(o => !grant.Diagnostics.Contains(o.CheckId))) return "permissions";
        if (task["pairing"] is not JsonObject && !ChosenKinds(db).Contains(task["recipient"].Text("kind"))) return "ai";
        if (UsedValue(task, "bytesShared") >= grant.Limits.TotalBytes) return "exhausted";
        return null;
    }

    // ---------------------------------------------------------------- run_diagnostic with an admin-tier id

    /// <summary>
    /// Phase 1 of run_diagnostic for an administrator check, in its transaction: refusals (flag off wins over permission off;
    /// declined_before), the diagnostic budget, then the call joins this turn's open request (a paired window: the task's open
    /// request) or starts one (2 per task). The same check already waiting or running comes back uncharged.
    /// </summary>
    private JsonObject AdminCheckCall(Db db, JsonObject task, Grant grant, string invocationId, string? turnId, JsonObject args, string origin)
    {
        var taskId = task.Text("taskId");
        var id = args.Text("diagnosticId");
        int? window = args["window"] is JsonValue wv && wv.TryGetValue<int>(out var w) ? w : null;
        JsonObject Refuse(string code, string message)
        {
            ToolEvent(db, taskId, invocationId, turnId, Diagnostics.RunDiagnostic, args, "denied", 0, [], origin);
            return ToolError(code, message);
        }
        if (!AdminChecksAvailable) return Refuse(ActionService.AdminUnavailable, AdminUnavailableMessage);
        if (!grant.Admin) return Refuse(ActionService.AdminNotAllowed, ActionService.AdminNotAllowedMessage);
        ExpireDueChecks(db);
        if ((long)db.Scalar("SELECT COUNT(*) FROM agent_operations WHERE task_id = $t AND check_id = $c AND status = 'declined'", ("$t", taskId), ("$c", id))! > 0)
            return Refuse("declined_before", DeclinedBeforeMessage);

        JsonObject Waiting(string operationId, string status) => Release(db, task, grant, invocationId, turnId, Diagnostics.RunDiagnostic, args, origin, new JsonObject
        {
            ["operationId"] = operationId, ["status"] = status,
            ["next"] = turnId is null
                ? "The user approves administrator checks in Optimus, then Windows asks. Poll get_operation with this operationId."
                : "The user approves administrator checks in Optimus, then Windows asks. End this answer: Optimus sends you the results when the checks end.",
        }, "");

        var open = TaskRequests(db, taskId, "awaiting_user", "running");
        // One entry per check id in a request (the helper takes nothing else): the same check and window again is that
        // operation, uncharged; the same check with another window while its request waits is refused.
        foreach (var q in open)
            if (Ops(db, q.RequestId).FirstOrDefault(o => o.CheckId == id) is { } same)
            {
                if (same.Window == window) return Waiting(same.OperationId, same.Status);
                if (q.State == "awaiting_user") return Refuse("not_allowed", OtherWindowMessage);
            }

        var join = open.LastOrDefault(q => q.State == "awaiting_user" && !q.Frozen && (turnId is null || q.TurnId == turnId)
            && Ops(db, q.RequestId).Count < AdminChecks.MaxChecksPerRequest);
        if (join is null && TaskRequests(db, taskId).Count >= MaxCheckRequestsPerTask) return Refuse("not_allowed", CheckRequestsUsed);

        if (UsedValue(task, "diagnosticCalls") >= grant.Limits.DiagnosticCalls)
        {
            SetStatus(db, task, "exhausted");
            ToolEvent(db, taskId, invocationId, turnId, Diagnostics.RunDiagnostic, args, "error", 0, [], origin);
            return ToolError("exhausted", "The diagnostic budget for this task is used up.");
        }
        AddUsed(task, "diagnosticCalls", 1);

        CheckRequest request;
        if (join is not null)
        {
            db.Exec("UPDATE check_requests SET revision = revision + 1 WHERE request_id = $r", ("$r", join.RequestId));
            request = join with { Revision = join.Revision + 1 };
        }
        else
        {
            var attempt = FixAttempts.OfTurn(db, turnId) is { } a && a.TaskId == taskId ? a : null;
            request = new CheckRequest("chk-" + Ids.RandomHex(), taskId, turnId, attempt?.Id, attempt?.CardId, 1, "awaiting_user", null, false,
                db.Now(), Time.Format(Now() + CheckRequestWait));
            db.Exec($"INSERT INTO check_requests ({RequestColumns}) VALUES ($r, $t, $turn, $a, $c, 1, 'awaiting_user', NULL, 0, $n, $e)",
                ("$r", request.RequestId), ("$t", taskId), ("$turn", turnId), ("$a", request.AttemptId), ("$c", request.CardId), ("$n", request.CreatedAt), ("$e", request.ExpiresAt));
        }
        var operationId = "op-" + Ids.RandomHex();
        var n = Ops(db, request.RequestId).Count + 1;
        db.Exec("""
            INSERT INTO agent_operations (operation_id, task_id, status, created_at, request_id, check_id, n, check_window)
            VALUES ($o, $t, 'awaiting_user', $c, $r, $id, $n, $w)
            """, ("$o", operationId), ("$t", taskId), ("$c", db.Now()), ("$r", request.RequestId), ("$id", id), ("$n", n), ("$w", window));
        EmitChecks(db, request);
        return Waiting(operationId, "awaiting_user");
    }

    // ---------------------------------------------------------------- checks.approve / checks.decline (desktop only)

    /// <summary>
    /// checks.approve {idempotencyKey, requestId, revision} → the request. Only an awaiting request (else -32013 with its state);
    /// a stale revision → -32002 {current}; during the cooldown → -32013 {reason: cooldown, retryAt}. The recheck failing
    /// cancels the request (its code) instead of starting the helper. Membership is frozen from here.
    /// </summary>
    public JsonNode ChecksApprove(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var requestId = P.Req(p, "requestId", 64);
        var revision = P.Int(p, "revision") ?? throw RpcError.Params("revision", "revision is required.");
        _store.Write(ExpireDueChecks); // lazy expiry commits even when the approval below is refused
        CheckRequest? dispatch = null;
        CancellationTokenSource? stop = null;
        JsonNode result;
        try
        {
            (result, _) = _store.Write(db => Idempotency.Run(db, "checks.approve", key, p, () =>
            {
                var q = LoadRequest(db, requestId) ?? throw RpcError.NotFound("request", requestId);
                if (q.State != "awaiting_user") throw RpcError.State(q.State, $"The checks are {q.State.Replace('_', ' ')}.");
                if (revision != q.Revision)
                    throw new RpcError(ErrorCodes.StaleRevision, "The checks changed.", new JsonObject { ["reason"] = "stale_revision", ["current"] = RequestView(db, q) });
                if (CooldownUntil() is { } until)
                    throw new RpcError(ErrorCodes.InvalidState, "Wait a moment before running administrator checks.", new JsonObject { ["reason"] = "cooldown", ["retryAt"] = Time.Format(until) });
                if (CheckRecheck(db, q) is { } code)
                {
                    CancelIfOpen(db, requestId, code);
                    return (JsonNode)RequestView(db, LoadRequest(db, requestId)!);
                }
                db.Exec("UPDATE check_requests SET state = 'running', frozen = 1 WHERE request_id = $r", ("$r", requestId));
                db.Exec("UPDATE agent_operations SET status = 'running' WHERE request_id = $r AND status = 'awaiting_user'", ("$r", requestId));
                // The session's stop is registered in the approval's transaction: a cancel committed after it always finds it.
                stop = new CancellationTokenSource();
                lock (_gate) _checkStops[requestId] = stop;
                dispatch = LoadRequest(db, requestId)!;
                EmitChecks(db, dispatch);
                return (JsonNode)RequestView(db, dispatch);
            }));
        }
        catch
        {
            if (stop is not null) lock (_gate) if (_checkStops.TryGetValue(requestId, out var s) && s == stop) _checkStops.Remove(requestId);
            throw;
        }
        if (dispatch is not null && stop is not null) DispatchRead(dispatch, stop);
        return result;
    }

    /// <summary>
    /// checks.list {} (desktop only) → {requests: [request view]}: every request that has not ended (awaiting_user, also one
    /// waiting out the cooldown with retryAt, and running), oldest first, so the app can show them again after a reload.
    /// </summary>
    public JsonNode ChecksList(JsonObject p) => _store.Write(db =>
    {
        ExpireDueChecks(db); // lazily, as every read of a request
        return (JsonNode)new JsonObject
        {
            ["requests"] = new JsonArray([.. db.Query($"SELECT {RequestColumns} FROM check_requests WHERE state IN ('awaiting_user','running') ORDER BY rowid", RequestRow)
                .Select(q => (JsonNode?)RequestView(db, q))]),
        };
    });

    /// <summary>checks.decline {requestId} → the request, declined; the cooldown starts. Only an awaiting request (else -32013 with its state).</summary>
    public JsonNode ChecksDecline(JsonObject p)
    {
        var requestId = P.Req(p, "requestId", 64);
        _store.Write(ExpireDueChecks);
        return _store.Write(db =>
        {
            var q = LoadRequest(db, requestId) ?? throw RpcError.NotFound("request", requestId);
            if (q.State != "awaiting_user") throw RpcError.State(q.State, $"The checks are {q.State.Replace('_', ' ')}.");
            StartCooldown(db);
            EndRequest(db, q, "declined", "user_declined", _ => ("declined", "user_declined"));
            return (JsonNode)RequestView(db, LoadRequest(db, requestId)!);
        });
    }

    /// <summary>Tests only: runs after the approval committed and before the launch-time recheck (the gap a cancel can fall into).</summary>
    internal Func<string, Task>? BeforeReadLaunch { get; set; }

    /// <summary>Tests only: runs after the session's outer shutdown check, before intake waits for the store.</summary>
    internal Action<string>? BeforeIntakeStore { get; set; }

    /// <summary>Shutdown has begun (see StopAllAsync).</summary>
    internal bool ShuttingDown { get { lock (_gate) return _shuttingDown; } }

    /// <summary>
    /// One helper session for an approved request, in the background; the result goes through <see cref="IntakeRead"/>.
    /// Sessions run one at a time, each held through its intake. Right before the launch (after waiting for the helper) the
    /// live request, the recheck and the cooldown are read again: a request cancelled since the approval (its stop already
    /// signalled) or failing the recheck never starts the helper; one that meets the cooldown goes back to awaiting_user.
    /// </summary>
    private void DispatchRead(CheckRequest q, CancellationTokenSource stop)
    {
        var reads = _options.ElevatedReads;
        Track(Task.Run(async () =>
        {
            ElevatedReadResult? result = null;
            var held = false;
            try
            {
                try
                {
                    if (BeforeReadLaunch is { } hook) await hook(q.RequestId).ConfigureAwait(false);
                    // One session at a time, through its intake: a decline there starts the cooldown before the next launch.
                    await _readOne.WaitAsync(stop.Token).ConfigureAwait(false);
                    held = true;
                    if (LaunchChecks(q.RequestId, stop) is { } checks)
                        result = reads is null ? new ElevatedReadResult("not_started", "admin_unavailable", [], false)
                            : await reads.RunReadAsync(new ElevatedReadRequest(q.RequestId, checks)
                            {
                                // Again at the helper's own exclusion (a write or script may have held it): live request, recheck,
                                // cooldown and shutdown, right before the launch; a cooldown sends it back to awaiting_user (retryAt).
                                Admit = () => LaunchChecks(q.RequestId, stop) is not null,
                            }, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!held && stop.IsCancellationRequested) { } // cancelled while waiting: already ended
                catch (Exception ex)
                {
                    _log($"checks {q.RequestId}: {ex.GetType().Name}");
                    result = new ElevatedReadResult("not_started", "internal", [], false);
                }
                finally
                {
                    lock (_gate) if (_checkStops.TryGetValue(q.RequestId, out var s) && s == stop) _checkStops.Remove(q.RequestId);
                }
                if (result is null) return; // not launched: nothing to take in
                lock (_gate) if (_shuttingDown) return; // stopped by the shutdown: startup fails the request interrupted
                try { IntakeRead(q.RequestId, result); }
                catch (Exception ex) { _log($"checks {q.RequestId} intake: {ex.GetType().Name}"); }
            }
            finally
            {
                if (held) _readOne.Release();
            }
        }));
    }

    /// <summary>At the launch boundary, in one transaction: the checks to send, or null when the request may not start now.</summary>
    private List<ElevatedReadCheck>? LaunchChecks(string requestId, CancellationTokenSource stop) => _store.Write(db =>
    {
        lock (_gate) if (_shuttingDown) return null; // the host is stopping: never a new session (startup fails the request interrupted)
        if (stop.IsCancellationRequested || LoadRequest(db, requestId) is not { State: "running" } live) return null;
        if (CheckRecheck(db, live) is { } code)
        {
            CancelIfOpen(db, requestId, code);
            return null;
        }
        if (CooldownUntil() is not null)
        {
            // A decline while this request waited for the helper: it goes back to waiting for the user (retryAt), not failed.
            db.Exec("UPDATE check_requests SET state = 'awaiting_user' WHERE request_id = $r", ("$r", requestId));
            db.Exec("UPDATE agent_operations SET status = 'awaiting_user' WHERE request_id = $r AND status = 'running'", ("$r", requestId));
            EmitChecks(db, LoadRequest(db, requestId)!);
            return null;
        }
        return Ops(db, requestId).Select(o => new ElevatedReadCheck(o.N, o.CheckId, AdminChecks.Find(o.CheckId)?.Version ?? "", o.Window)).ToList();
    });

    /// <summary>
    /// A narrowing, a task end or a change of the chosen AI (in the transaction that causes it): the task's open requests are
    /// cancelled with <paramref name="code"/> (permissions | ai | task_ended); a running session gets stop and its results
    /// are discarded when they arrive. Their continuations are dropped.
    /// </summary>
    private void EndTaskChecks(Db db, string taskId, string code)
    {
        foreach (var q in TaskRequests(db, taskId, "awaiting_user", "running"))
        {
            EndRequest(db, q, "cancelled", code, _ => ("cancelled", code), stale: true);
            CancellationTokenSource? stop;
            lock (_gate) _checkStops.TryGetValue(q.RequestId, out stop);
            if (stop is not null) _ = Task.Run(() => { try { stop.Cancel(); } catch (ObjectDisposedException) { } });
        }
    }

    // ---------------------------------------------------------------- intake

    private static readonly string[] CheckResultStates = ["ok", "failed", "unsupported", "skipped", "cancelled"];

    private static string SafeCode(string? code, string fallback) => code is not null && Regex.IsMatch(code, "^[a-z_]{1,40}$") ? code : fallback;

    /// <summary>
    /// The helper session's result, in one transaction: a request no longer running (cancelled meanwhile) is discarded; the
    /// recheck runs again; the whole session is validated (one result per check, in order; every item against its check's
    /// exact fields, identity included; coverage consistent) before anything is stored: malformed stores nothing; interrupted
    /// keeps the checks that finished. Valid items become the task's evidence.
    /// </summary>
    private void IntakeRead(string requestId, ElevatedReadResult result)
    {
        BeforeIntakeStore?.Invoke(requestId);
        _store.Write(db =>
        {
            // Inside the transaction (store first, then the gate): a shutdown that began while this waited for the store
            // takes nothing in; startup fails the request interrupted.
            lock (_gate) if (_shuttingDown) return;
            var q = LoadRequest(db, requestId);
            if (q is null || q.State != "running") return; // cancelled during the session: the results are discarded
            if (CheckRecheck(db, q) is { } code)
            {
                CancelIfOpen(db, requestId, code); // the task's end may already have cancelled it
                return;
            }
            var ops = Ops(db, requestId);
            if (result.State == "malformed" || !SessionValid(ops, result))
            {
                EndRequest(db, q, "failed", "malformed", _ => ("failed", "malformed"));
                return;
            }
            switch (result.State)
            {
                case "not_started":
                    var why = SafeCode(result.Code, "not_started");
                    EndRequest(db, q, "failed", why, _ => ("failed", why));
                    return;
                case "declined": // UAC cancelled, or Cancel in the helper's window
                    StartCooldown(db);
                    var declined = SafeCode(result.Code, "declined");
                    EndRequest(db, q, "declined", declined, _ => ("declined", declined));
                    return;
                case "stopped": // Stop in the helper's window (a stop the host sent finds the request already cancelled above)
                    StartCooldown(db);
                    EndRequest(db, q, "cancelled", "stopped", _ => ("cancelled", "stopped"));
                    return;
            }
            var observedAt = db.Now();
            foreach (var op in ops)
            {
                var c = result.Checks.FirstOrDefault(x => x.N == op.N);
                if (c is null)
                {
                    SetOp(db, op, "failed", new JsonObject { ["diagnosticId"] = op.CheckId, ["code"] = "interrupted" });
                    continue;
                }
                if (c.State == "ok")
                {
                    var ids = StoreAdminEvidence(db, q, op, c, observedAt);
                    SetOp(db, op, "complete", new JsonObject
                    {
                        ["diagnosticId"] = op.CheckId, ["status"] = c.Coverage.Complete ? "complete" : "partial",
                        ["reason"] = c.Coverage.Complete ? null : "Could not read all of it.", ["windowDays"] = op.Window,
                        ["coverage"] = Coverage(c.Coverage), ["code"] = c.Code is null ? null : SafeCode(c.Code, "other"),
                        ["evidenceIds"] = Json.Array(ids),
                    });
                }
                else SetOp(db, op, c.State == "cancelled" ? "cancelled" : "failed", new JsonObject { ["diagnosticId"] = op.CheckId, ["code"] = SafeCode(c.Code, c.State) });
            }
            if (result.State == "complete") EndRequest(db, q, "complete", null, _ => ("failed", "interrupted"));
            else
            {
                var failed = SafeCode(result.State == "refused" ? result.Code ?? "refused" : result.State, "failed");
                EndRequest(db, q, "failed", failed, _ => ("failed", failed));
            }
        });
    }

    private static JsonObject Coverage(ElevatedReadCoverage c) => new() { ["complete"] = c.Complete, ["total"] = c.Total, ["returned"] = c.Returned };

    private static void SetOp(Db db, CheckOp op, string status, JsonObject result) =>
        db.Exec("UPDATE agent_operations SET status = $s, result = $r WHERE operation_id = $o", ("$s", status), ("$r", Json.Write(result)), ("$o", op.OperationId));

    /// <summary>The whole session before anything is stored (§17.2 intake).</summary>
    private bool SessionValid(List<CheckOp> ops, ElevatedReadResult result)
    {
        var ns = result.Checks.Select(c => c.N).ToList();
        if (ns.Distinct().Count() != ns.Count || !ns.SequenceEqual(ns.Order()) || ns.Any(n => ops.All(o => o.N != n))) return false;
        if (result.State is "complete" or "declined" or "timeout" or "refused" or "stopped" && ns.Count != ops.Count) return false;
        var identities = _options.LocalIdentities?.Invoke() ?? IdentityFilter.Local();
        foreach (var c in result.Checks)
        {
            if (!CheckResultStates.Contains(c.State)) return false;
            if (c.State != "ok") { if (c.Items.Count > 0) return false; continue; }
            var def = AdminChecks.Find(ops.First(o => o.N == c.N).CheckId);
            if (def is null || c.Items.Count > Math.Min(def.MaxItems, AdminChecks.MaxItems)) return false;
            if (c.Coverage.Returned != c.Items.Count || c.Coverage.Total < c.Coverage.Returned || (c.Coverage.Complete && c.Coverage.Total != c.Coverage.Returned)) return false;
            if (c.Items.Any(i => !AdminChecks.Conforms(def, i, identities))) return false;
        }
        return true;
    }

    /// <summary>
    /// A finished check's evidence: one summary (state, coverage, window) and one item per row, ev- + H(taskId, requestId,
    /// checkId, subject), source admin, collectorId admin.&lt;id&gt;, declared public (the projector's allowlist decides).
    /// </summary>
    private static List<string> StoreAdminEvidence(Db db, CheckRequest q, CheckOp op, ElevatedCheckResult c, string observedAt)
    {
        var def = AdminChecks.Find(op.CheckId)!;
        var collectorId = "admin." + op.CheckId;
        var category = op.CheckId.StartsWith("storage.", StringComparison.Ordinal) ? "storage" : "security";
        var rows = new List<(string Subject, JsonObject Value)>();
        var summary = Coverage(c.Coverage);
        summary["state"] = "ok";
        summary["windowDays"] = op.Window;
        summary["code"] = c.Code is null ? null : SafeCode(c.Code, "other");
        rows.Add((op.CheckId + ".summary", summary));
        for (var i = 0; i < c.Items.Count; i++)
        {
            var item = c.Items[i];
            var subject = op.CheckId switch
            {
                AdminChecks.StorageHealth => $"{op.CheckId}.disk.{item["disk"]}",
                AdminChecks.Encryption => $"{op.CheckId}.volume.{item.Text("drive").TrimEnd(':').ToLowerInvariant()}",
                _ => $"{op.CheckId}.detection.{(i + 1).ToString(CultureInfo.InvariantCulture)}",
            };
            rows.Add((subject, (JsonObject)item.DeepClone()));
        }
        var ids = new List<string>();
        foreach (var (subject, value) in rows)
        {
            var id = "ev-" + Ids.Hash16(q.TaskId, q.RequestId, op.CheckId, subject);
            var body = new JsonObject
            {
                ["id"] = id, ["subject"] = subject, ["category"] = category, ["source"] = "admin", ["observedAt"] = observedAt, ["interval"] = null,
                ["display"] = "", ["value"] = value, ["collectorId"] = collectorId, ["collectorVersion"] = def.Version, ["quality"] = "reported",
                ["sensitivity"] = "public", ["sensitivityBasis"] = "declared",
            };
            db.Exec("INSERT OR IGNORE INTO agent_evidence (evidence_id, task_id, collector_id, subject, body) VALUES ($i, $t, $c, $s, $b)",
                ("$i", id), ("$t", q.TaskId), ("$c", collectorId), ("$s", subject), ("$b", Json.Write(body)));
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>get_operation for an administrator check of this task: its state; complete → the projected result with coverage; else its code.</summary>
    private JsonObject AdminOperationView(Db db, string taskId, Grant grant, CheckOp op)
    {
        var view = new JsonObject { ["operationId"] = op.OperationId, ["status"] = op.Status, ["diagnosticId"] = op.CheckId };
        if (op.Status == "complete" && op.Result is { } meta)
        {
            var ids = Json.Strings(meta["evidenceIds"]);
            var bodies = db.Query("SELECT body FROM agent_evidence WHERE task_id = $t", r => (JsonObject)Json.Parse(r.GetString(0))!, ("$t", taskId))
                .Where(b => ids.Contains(b.Text("id"))).OrderBy(b => ids.IndexOf(b.Text("id"))).ToList();
            var result = DiagnosticResult(db, taskId, grant, op.CheckId, meta.Text("status"), meta["reason"]?.GetValue<string>(), null, bodies);
            if (result["coverage"] is JsonObject coverage && meta["coverage"] is JsonObject kept)
                foreach (var (k, v) in kept) coverage[k] = v?.DeepClone(); // complete / total / returned: nothing found ≠ could not read all
            if (meta["windowDays"] is JsonValue days) result["windowDays"] = days.DeepClone();
            view["result"] = result;
        }
        else if (op.Result?["code"] is JsonValue code) view["code"] = code.DeepClone();
        return view;
    }

    /// <summary>The administrator check operation <paramref name="operationId"/> of this task, or null.</summary>
    private static CheckOp? TaskCheckOp(Db db, string taskId, string operationId) =>
        db.Query("SELECT operation_id, check_id, n, check_window, status, result FROM agent_operations WHERE operation_id = $o AND task_id = $t AND request_id IS NOT NULL",
            r => new CheckOp(r.GetString(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : Json.Parse(r.GetString(5)) as JsonObject), ("$o", operationId), ("$t", taskId)).FirstOrDefault();

    // ---------------------------------------------------------------- continuation

    /// <summary>
    /// The one continuation of a request a turn asked for (a paired window's request has none: it polls get_operation).
    /// Pending, or dropped when the task has ended.
    /// </summary>
    private static bool RecordContinuation(Db db, CheckRequest q, bool stale)
    {
        if (q.TurnId is null) return false;
        var state = stale || LoadTask(db, q.TaskId).Text("status") != "active" ? "dropped" : "pending";
        db.Exec("INSERT OR IGNORE INTO continuations (request_id, task_id, attempt_id, card_id, state, turn_id, created_at) VALUES ($r, $t, $a, $c, $s, NULL, $n)",
            ("$r", q.RequestId), ("$t", q.TaskId), ("$a", q.AttemptId), ("$c", q.CardId), ("$s", state), ("$n", db.Now()));
        return state == "pending";
    }

    private void ScheduleContinuation(string taskId)
    {
        lock (_gate) if (_shuttingDown) return; // shutdown admits no new run: the pending continuation is dropped at startup
        Track(Task.Run(() =>
        {
            try { TryStartContinuation(taskId); }
            catch (Exception ex) { _log($"task {taskId} continuation: {ex.GetType().Name}"); }
        }));
    }

    /// <summary>
    /// Starts the task's oldest pending continuation through the ask admission: dropped if the task ended, the chosen AI
    /// changed or the fix attempt was replaced; left pending while a turn runs (the run's end starts it). The turn is the
    /// host's fixed message (states, operation and evidence ids, never result text), the same card and attempt (a fix
    /// attempt's continuation has the fix budget, 12).
    /// </summary>
    private void TryStartContinuation(string taskId)
    {
        string? turnId = null;
        AskTurn? ask = null;
        var headless = false;
        var more = false;
        AskRun? added = null;
        var token = CancellationToken.None;
        try
        {
            _store.Write(db =>
            {
                var pending = db.Query("SELECT request_id, attempt_id, card_id FROM continuations WHERE task_id = $t AND state = 'pending' ORDER BY rowid",
                    r => (Request: r.GetString(0), Attempt: r.IsDBNull(1) ? null : r.GetString(1), Card: r.IsDBNull(2) ? null : r.GetString(2)), ("$t", taskId));
                if (pending.Count == 0) return;
                var (requestId, attemptId, cardId) = pending[0];
                more = pending.Count > 1;
                var task = LoadLive(db, taskId);
                var kind = task["recipient"].Text("kind");
                var attempt = attemptId is null ? null : FixAttempts.Get(db, attemptId);
                // Stale only when the attempt was replaced or dismissed: the current attempt, still working because its own
                // turn runs, keeps the continuation pending until that turn ends (its end schedules this again).
                var drop = task.Text("status") != "active" || !(IsHeadless(task) || InAppKinds.Contains(kind)) || !ChosenKinds(db).Contains(kind)
                    || (attemptId is not null && (attempt is not { State: "working" or "ended" } || FixAttempts.Current(db, attempt.CardId)?.Id != attemptId));
                if (drop)
                {
                    db.Exec("UPDATE continuations SET state = 'dropped' WHERE request_id = $r", ("$r", requestId));
                    if (attempt is not null) FixAttempts.Emit(db, attempt.CardId, attempt.ReportId);
                    return;
                }
                if (attempt is { State: "working" }) { more = false; return; }
                lock (_gate)
                {
                    if (_shuttingDown || _asking.ContainsKey(taskId)) { more = false; return; } // a turn runs: it starts this when it ends; shutdown: left pending, startup drops it
                    added = new AskRun();
                    _asking.Add(taskId, added);
                    token = TaskToken(taskId);
                }
                turnId = "turn-" + Ids.RandomHex(6);
                lock (_gate) added.TurnId = turnId;
                headless = IsHeadless(task);
                if (attempt is not null) FixAttempts.Resume(db, attempt.Id, turnId);
                db.Exec("UPDATE continuations SET state = 'started', turn_id = $turn WHERE request_id = $r", ("$turn", turnId), ("$r", requestId));
                Touch(task);
                SaveTask(db, task);
                var question = new JsonObject { ["turnId"] = turnId, ["text"] = "", ["cardId"] = cardId, ["checks"] = requestId };
                if (attempt is not null)
                {
                    question["intent"] = "fix";
                    question["attemptId"] = attempt.Id;
                }
                db.AppendEvent(Stream(taskId), "agent.question", question);
                var q = LoadRequest(db, requestId)!;
                ask = new AskTurn("", cardId, attempt is null ? null : new FixAsk(attempt.CardId, attempt.Id, null), ContinuationMessage(q, Ops(db, requestId), cardId));
            });
        }
        catch
        {
            lock (_gate) if (added is not null && _asking.TryGetValue(taskId, out var r) && r == added) _asking.Remove(taskId);
            throw;
        }
        if (ask is not null && turnId is not null)
        {
            var (t, a) = (turnId, ask);
            Track(Task.Run(() => headless ? HeadlessAsync(taskId, t, a, token) : LoopAsync(taskId, t, a, token)));
        }
        else if (more) ScheduleContinuation(taskId);
    }

    /// <summary>The host-written continuation (§17.2): each check's state, operation and evidence ids and code; never result text.</summary>
    internal static string ContinuationMessage(string requestId, string state, IEnumerable<(string CheckId, string OperationId, string Status, IReadOnlyList<string> Evidence, string? Code)> checks, string? cardId)
    {
        var lines = new List<string> { $"The administrator checks you asked for ended ({state.Replace('_', ' ')}):" };
        foreach (var c in checks)
            lines.Add($"- {c.CheckId}, operation {c.OperationId}: {c.Status.Replace('_', ' ')}"
                + (c.Evidence.Count > 0 ? $"; evidence {string.Join(", ", c.Evidence)}" : "") + (c.Code is { } code ? $" ({code})" : "") + ".");
        lines.Add("Read the results with get_operation and each operationId, and cite evidence as [ev-…]. A declined check can't be asked for again in this conversation: go on without it.");
        if (cardId is not null)
            lines.Add($"Then finish the fix request on card {cardId}: end with exactly one plan, or note_card on it with an outcome (needs_user or nothing_to_fix).");
        return string.Join("\n", lines);
    }

    private static string ContinuationMessage(CheckRequest q, List<CheckOp> ops, string? cardId) =>
        ContinuationMessage(q.RequestId, q.State, ops.Select(o => (o.CheckId, o.OperationId, o.Status,
            (IReadOnlyList<string>)(o.Status == "complete" ? Json.Strings(o.Result?["evidenceIds"]) : []),
            o.Result?["code"]?.GetValue<string>() is { } code ? SafeCode(code, "other") : null)), cardId);

    /// <summary>
    /// Startup: the stored cooldown is restored; a session that was running died with the host: its request fails
    /// interrupted; its continuation is dropped.
    /// </summary>
    private void RecoverChecks() => _store.Write(db =>
    {
        // The stored end is a wall-clock time: what is left of it is clamped to 60 s (a clock that was ahead when it was set
        // can't block longer) and stored normalised; one already passed is cleared.
        if (Settings.Get(db, CooldownSetting) is { } stored)
        {
            var left = stored is JsonValue v && v.TryGetValue<string>(out var s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until)
                ? until - Now() : TimeSpan.Zero;
            if (left > TimeSpan.Zero) SetCooldown(db, left < ReadCooldown ? left : ReadCooldown);
            else db.Exec("DELETE FROM settings WHERE key = $k", ("$k", CooldownSetting));
        }
        foreach (var q in db.Query($"SELECT {RequestColumns} FROM check_requests WHERE state = 'running'", RequestRow))
            EndRequest(db, q, "failed", "interrupted", _ => ("failed", "interrupted"), stale: true);
        db.Exec("UPDATE continuations SET state = 'dropped' WHERE state = 'pending'");
    });
}

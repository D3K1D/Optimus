using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M5 (contracts/m5-chat.md §1–§3): Claude Code used in-app. The shell reports the CLI (provider.setClient);
// a signed-in CLI makes its kind "connected", agent.start without a pairing makes a headless task, and agent.ask on it
// runs the CLI in the background with a one-run token its MCP bridge presents instead of a pairing code.
public sealed partial class AgentService
{
    /// <summary>
    /// What the shell last reported about a CLI. Kept in memory for the host's lifetime (the shell sends it again when it
    /// reconnects). Compatible false: an outdated CLI. Blocked: a CLI exists but cannot be started. Auth: subscription |
    /// api_key | other (how the CLI bills; informational). ClaudeFlags (m9 §6.3): which of --safe-mode and
    /// --setting-sources the probed help lists (chat adds --setting-sources=; research needs both).
    /// </summary>
    private sealed record ClientReport(string? Path, bool? SignedIn, string? Version, bool Compatible = true, bool Blocked = false, string? Auth = null,
        IReadOnlyList<string>? ClaudeFlags = null)
    {
        public bool Usable => Path is not null && Compatible && !Blocked;
    }

    private static readonly string[] AuthKinds = ["subscription", "api_key", "other"];

    /// <summary>The fields provider.setClient accepts.</summary>
    private static readonly string[] SetClientFields = ["kind", "path", "signedIn", "version", "compatible", "blocked", "auth", "claudeFlags"];

    /// <summary>A live run: its task, kind and turn. Bound to the bridge process once the bridge presents the token.</summary>
    private sealed record RunBinding(string Token, string TaskId, string Kind, string TurnId);

    private readonly Dictionary<string, ClientReport> _clients = [];
    private readonly Dictionary<string, RunBinding> _runTokens = [];               // token → run
    private readonly Dictionary<int, RunBinding> _runBridges = [];                  // bridge pid → run
    private readonly Dictionary<string, (int Pid, RunBinding Run)> _runPairings = []; // pairingId → bridge, run

    public const int MaxRunSeconds = 600;

    /// <summary>The data directory (agent-runs live under it). Set by the engine.</summary>
    internal string? DataDir { get; init; }

    private static bool IsHeadless(JsonObject task) => task["recipient"]?["mode"]?.GetValue<string>() == "headless";

    /// <summary>The CLI of an MCP kind is installed and signed in: the kind is usable in-app.</summary>
    private bool ClientConnected(string kind)
    {
        lock (_gate) return _clients.TryGetValue(kind, out var c) && c.Usable && c.SignedIn == true;
    }

    /// <summary>
    /// connected / signed_out from the shell's last report; error/unavailable for an outdated or blocked CLI (the shell's
    /// clientStatus says which); null (not installed, or the check failed).
    /// </summary>
    private (string Status, string? Detail)? ClientStatus(string kind)
    {
        ClientReport? c;
        lock (_gate) _clients.TryGetValue(kind, out c);
        if (c is null) return null;
        if (c.Blocked || (c.Path is not null && !c.Compatible)) return ("error", "unavailable");
        if (c.Path is null) return null;
        return c.SignedIn switch { true => ("connected", null), false => ("error", "signed_out"), _ => null };
    }

    /// <summary>
    /// provider.setClient (desktop shell only, never forwarded from the renderer): the CLI path that passed the shell's
    /// identity check (null = not installed) and whether it is signed in. The path must be fully qualified, exist and
    /// end in .exe or .cmd. Kept in memory; provider.status follows a change.
    /// </summary>
    public JsonNode SetClient(JsonObject p)
    {
        // A closed field set: a field this host no longer reads (M9 dropped the per-client feature list) is refused, not ignored.
        if (p.Select(x => x.Key).FirstOrDefault(key => !SetClientFields.Contains(key, StringComparer.Ordinal)) is { } unknown)
            throw RpcError.Params(unknown, $"{unknown} is not a provider.setClient field.");
        var kind = P.OneOf(p, "kind", McpKinds);
        var path = P.Str(p, "path", required: false, maxLength: 1024);
        if (p["signedIn"] is not (null or JsonValue)) throw RpcError.Params("signedIn", "signedIn must be true, false or null.");
        bool? signedIn = p["signedIn"] is JsonValue sv ? (sv.TryGetValue<bool>(out var b) ? b : throw RpcError.Params("signedIn", "signedIn must be true, false or null.")) : null;
        var version = P.Str(p, "version", required: false, maxLength: 64);
        var compatible = OptionalBool(p, "compatible") ?? true;
        var blocked = OptionalBool(p, "blocked") ?? false;
        var auth = p["auth"] is null ? null : P.OneOf(p, "auth", AuthKinds);
        var claudeFlags = ClaudeFlags(p, kind);
        if (blocked && path is not null) throw RpcError.Params("blocked", "A blocked client has no path.");
        if (path is not null)
        {
            var valid = path.All(ch => !char.IsControl(ch)) && Path.IsPathFullyQualified(path)
                && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                && File.Exists(path);
            if (!valid) throw RpcError.Params("path", "path must be the full path of an existing .exe or .cmd.");
        }
        lock (_gate)
        {
            // signedIn null (the check failed or timed out) is "unknown": the previous sign-in state of an installed CLI stays.
            if (path is not null && signedIn is null && _clients.TryGetValue(kind, out var previous) && previous.Path is not null) signedIn = previous.SignedIn;
            _clients[kind] = new ClientReport(path, path is null ? null : signedIn, version, compatible, blocked, path is null ? null : auth,
                path is null ? null : claudeFlags);
        }
        var state = blocked ? "blocked" : path is null ? "not installed" : !compatible ? "outdated"
            : signedIn switch { true => "signed in", false => "signed out", _ => "unknown" };
        _log($"provider {kind}: client {state}{(auth is null || path is null ? "" : " (" + auth + ")")}");
        return FreshEntry(kind);
    }

    /// <summary>The Claude Code flags the shell's help probe may report (m9 §6.3; --max-turns §17.6).</summary>
    public static readonly string[] ProbedClaudeFlags = ["--safe-mode", "--setting-sources", "--max-turns"];

    /// <summary>
    /// claudeFlags (m9 §6.3, Claude Code only, optional): which of <see cref="ProbedClaudeFlags"/> the probed help lists,
    /// each at most once; anything else → -32602. Absent: none (chat keeps its argv; research is unavailable).
    /// </summary>
    private static IReadOnlyList<string>? ClaudeFlags(JsonObject p, string kind)
    {
        if (p["claudeFlags"] is null) return null;
        const string message = "claudeFlags must be a list of distinct probed Claude Code flags.";
        if (kind != "claude_code_mcp" || p["claudeFlags"] is not JsonArray list) throw RpcError.Params("claudeFlags", message);
        List<string> flags = [];
        foreach (var item in list)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var name) || !ProbedClaudeFlags.Contains(name, StringComparer.Ordinal) || flags.Contains(name, StringComparer.Ordinal))
                throw RpcError.Params("claudeFlags", message);
            flags.Add(name);
        }
        return flags;
    }

    /// <summary>The CLI of an MCP kind as research sees it (m9 §6.3): usable path, version, sign-in and probed capabilities; else null.</summary>
    private ResearchClient? ResearchClientOf(string kind)
    {
        lock (_gate)
            return _clients.TryGetValue(kind, out var c) && c.Usable
                ? new ResearchClient(c.Path!, c.Version, c.SignedIn, c.ClaudeFlags ?? [])
                : null;
    }

    private static bool? OptionalBool(JsonObject p, string name) => p[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => throw RpcError.Params(name, name + " must be true or false."),
    };

    /// <summary>A run reported a sign-in problem: the kind shows signed_out until the shell reports again.</summary>
    private void MarkSignedOut(string kind)
    {
        lock (_gate)
            if (_clients.TryGetValue(kind, out var c) && c.Path is not null) _clients[kind] = c with { SignedIn = false };
        RefreshProviders(kind);
    }

    // ---------------------------------------------------------------- run tokens (§3)

    /// <summary>
    /// agent.pair with a runToken: a token of a live run is approved at once and bound to that run's task (no
    /// agent.pairing event, no card, no pending status). Unknown or expired → not_allowed.
    /// </summary>
    private JsonObject PairRun(string token, CallContext ctx)
    {
        lock (_gate)
        {
            if (!HeadlessCommand.TokenPattern().IsMatch(token) || !_runTokens.TryGetValue(token, out var run))
                throw RpcError.State("not_allowed", "That run is not active.");
            foreach (var stale in _runPairings.Where(x => x.Value.Pid == ctx.ProcessId).Select(x => x.Key).ToList()) _runPairings.Remove(stale);
            _runBridges[ctx.ProcessId] = run;
            var pairingId = "pair-" + Ids.RandomHex();
            _runPairings[pairingId] = (ctx.ProcessId, run);
            return new JsonObject { ["pairingId"] = pairingId, ["status"] = "approved", ["taskId"] = run.TaskId };
        }
    }

    /// <summary>The live run a bridge process presented a token for, if any.</summary>
    private RunBinding? RunOf(int pid)
    {
        lock (_gate) return _runBridges.TryGetValue(pid, out var run) && _runTokens.ContainsKey(run.Token) ? run : null;
    }

    private JsonObject? RunPairStatus(string pairingId, CallContext ctx)
    {
        lock (_gate)
        {
            if (!_runPairings.TryGetValue(pairingId, out var x)) return null;
            if (x.Pid != ctx.ProcessId) throw RpcError.NotFound("pairing", pairingId);
            var live = _runTokens.ContainsKey(x.Run.Token);
            return new JsonObject { ["pairingId"] = pairingId, ["status"] = live ? "approved" : "expired", ["taskId"] = live ? x.Run.TaskId : null };
        }
    }

    private RunBinding OpenRun(string taskId, string kind, string turnId)
    {
        var run = new RunBinding(Ids.RandomHex(16), taskId, kind, turnId);
        lock (_gate) _runTokens[run.Token] = run;
        return run;
    }

    /// <summary>A token dies when its run ends; its bridge bindings go with it.</summary>
    private void CloseRun(RunBinding run)
    {
        lock (_gate)
        {
            _runTokens.Remove(run.Token);
            foreach (var pid in _runBridges.Where(x => x.Value.Token == run.Token).Select(x => x.Key).ToList()) _runBridges.Remove(pid);
            foreach (var id in _runPairings.Where(x => x.Value.Run.Token == run.Token).Select(x => x.Key).ToList()) _runPairings.Remove(id);
        }
    }

    // ---------------------------------------------------------------- the run (§3)

    /// <summary>m9 §17.6: adds (or with a negative count releases) model round trips on the task's used.turns, in one transaction.</summary>
    private void ReserveTurns(string taskId, long count) => _store.Write(db =>
    {
        var t = LoadTask(db, taskId);
        AddUsed(t, "turns", count);
        SaveTask(db, t);
        UsageEvent(db, t);
    });

    private async Task HeadlessAsync(string taskId, string turnId, AskTurn ask, CancellationToken ct)
    {
        try
        {
            await RunHeadlessAsync(taskId, turnId, ask, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TurnFailed(taskId, turnId, 1, "cancelled");
        }
        catch (Exception ex)
        {
            _log($"task {taskId} headless run failed: {ex.GetType().Name}");
            TryEvent(taskId, "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = 1, ["status"] = "failed", ["code"] = "internal" });
        }
        finally
        {
            EndFixTurn(taskId, turnId); // m9 §17.3
            EndRun(taskId);
        }
    }

    private async Task RunHeadlessAsync(string taskId, string turnId, AskTurn ask, CancellationToken ct)
    {
        var task = _store.Write(db => LoadLive(db, taskId));
        if (InactiveCode(task) is { } inactive)
        {
            TurnFailed(taskId, turnId, 0, inactive);
            return;
        }
        var grant = GrantOf(task);
        // m9 §17.6: one launch may use up to min(the ask's budget, what is left of the per-task bound) model round trips.
        var allowance = (int)Math.Min(AskTurns(grant, ask.Fix is not null), TaskTurns - UsedValue(task, "turns"));
        if (allowance <= 0) // the per-task runaway bound
        {
            Exhaust(taskId, turnId, 0);
            return;
        }

        const int n = 1;
        TryEvent(taskId, "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = n, ["status"] = "started" });
        var kind = task["recipient"].Text("kind");
        string? cli;
        var settingSources = false; // m9 §6.3 chat hardening: --setting-sources= when the probed help lists it
        var maxTurnsListed = false; // m9 §17.6: --max-turns only when the probed help lists it
        lock (_gate)
        {
            var usable = _clients.TryGetValue(kind, out var c) && c.Usable && c.SignedIn != false;
            cli = usable ? c!.Path : null;
            if (usable)
            {
                settingSources = c!.ClaudeFlags?.Contains("--setting-sources", StringComparer.Ordinal) == true;
                maxTurnsListed = c.ClaudeFlags?.Contains("--max-turns", StringComparer.Ordinal) == true;
            }
        }
        var bridge = _options.McpExecutablePath;
        if (cli is null || bridge is null || _options.CliRunner is not { } runner || DataDir is null)
        {
            TurnFailed(taskId, turnId, n, cli is null && ClientStatus(kind)?.Detail == "signed_out" ? "auth" : "unavailable");
            return;
        }

        var runDir = Path.Combine(DataDir, "agent-runs", taskId);
        var session = task["clientSession"]?.GetValue<string>();
        var first = session is null;
        var prompt = _store.Write(db => Prompt(db, taskId, ask, first));
        var run = OpenRun(taskId, kind, turnId);
        var mcpConfig = Path.Combine(runDir, HeadlessCommand.McpConfigFile);
        try
        {
            Directory.CreateDirectory(runDir);
            try { Acl.RestrictDirectory(runDir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { _log($"task {taskId} run folder ACL: {ex.GetType().Name}"); }

            var id = first ? Guid.NewGuid() : Guid.TryParse(session, out var g) ? g : Guid.NewGuid();
            session = id.ToString("D");
            File.WriteAllText(mcpConfig, HeadlessCommand.ClaudeMcpConfig(bridge, run.Token));
            File.WriteAllText(Path.Combine(runDir, HeadlessCommand.SystemFile), SystemPrompt);
            // m9 §17.6: Claude Code's own cap on agentic turns is this launch's allowance (the ask's 8, a fix ask 12, never more
            // than the task has left), passed only when the probed help lists --max-turns; otherwise the host deadline below
            // and the reservation apply.
            var args = HeadlessCommand.Claude(runDir, id, resume: !first, settingSources, maxTurnsListed ? allowance : null);
            var stdin = prompt;

            // The allowance is reserved before the launch (it stays counted if the run is cut off or the host stops).
            ReserveTurns(taskId, allowance);
            var seconds = Math.Min(MaxRunSeconds, allowance * grant.Limits.TurnSeconds);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            // m6 §0 runtime guard: a tool the run must not have kills the process tree at once (cancelling the run).
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var stream = new HeadlessStream(() => { try { guard.Cancel(); } catch (ObjectDisposedException) { } });
            CliExit? exit = null;
            string? stopped = null; // cancelled | timeout | tool_blocked | internal: the run ended before the CLI did
            try
            {
                exit = await runner.RunAsync(new CliLaunch(cli, args, stdin, runDir), stream.Feed, guard.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stopped = ct.IsCancellationRequested ? "cancelled" : stream.Blocked is not null ? "tool_blocked" : "timeout";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log($"task {taskId} headless run: {ex.GetType().Name}");
                stopped = "internal";
            }
            if (stream.Blocked is { } blockedTool)
            {
                // Even if the CLI ended before the kill landed, the run is refused: no answer, code tool_blocked.
                _log($"task {taskId} headless run stopped: tool blocked ({blockedTool})");
                if (stopped is null or "timeout" or "internal") stopped = "tool_blocked";
            }
            if (exit is { Started: false })
            {
                _log($"task {taskId} headless run did not start ({exit.LaunchError ?? "launch_failed"})");
                ReserveTurns(taskId, -allowance); // never launched: nothing to count
                TurnFailed(taskId, turnId, n, "unavailable");
                return;
            }

            // m9 §17.6: a launched run counts the round trips it reports (num_turns) in place of the reservation; one that
            // reports none, or was cut off, keeps the reservation. Its tokens are added to used. The client's session is
            // kept for the next ask: one the CLI reported is kept even when the run was cancelled or timed out.
            var counted = stopped is null && stream.NumTurns is { } reported ? reported : allowance;
            var answer = stopped is null ? stream.Answer : null;
            var newSession = stream.Session ?? (answer is not null ? session : null);
            var usage = new JsonObject { ["inputTokens"] = stream.InputTokens, ["outputTokens"] = stream.OutputTokens };
            _store.Write(db =>
            {
                var t = LoadTask(db, taskId);
                AddUsed(t, "turns", counted - allowance);
                AddUsed(t, "inputTokens", stream.InputTokens);
                AddUsed(t, "outputTokens", stream.OutputTokens);
                if (newSession is not null) t["clientSession"] = newSession;
                Touch(t);
                SaveTask(db, t);
                UsageEvent(db, t);
            });
            if (answer is not null)
            {
                // exit ≠ 0 with an answer still answers (with none it is client_error); agent.cancel or the task's end may refuse it.
                Publish(taskId, turnId, n, answer, usage);
                return;
            }
            if (stopped is not null || exit is null)
            {
                TurnFailed(taskId, turnId, n, stopped ?? "internal");
                return;
            }
            if (stream.LooksLikeAuth(exit.StandardError))
            {
                MarkSignedOut(kind);
                TurnFailed(taskId, turnId, n, "auth");
                return;
            }
            TurnFailed(taskId, turnId, n, "client_error");
        }
        finally
        {
            CloseRun(run);
            try { if (File.Exists(mcpConfig)) File.Delete(mcpConfig); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log($"task {taskId} run cleanup: {ex.GetType().Name}"); }
        }
    }
}

using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M5 (contracts/m5-chat.md §1–§3): Claude Code and Codex used in-app. The shell reports each CLI (provider.setClient);
// a signed-in CLI makes its kind "connected", agent.start without a pairing makes a headless task, and agent.ask on it
// runs the CLI in the background with a one-run token its MCP bridge presents instead of a pairing code.
public sealed partial class AgentService
{
    /// <summary>
    /// What the shell last reported about a CLI. Kept in memory for the host's lifetime (the shell sends it again when it
    /// reconnects). Compatible false: an outdated CLI. Blocked: a CLI exists but cannot be started. Auth: subscription |
    /// api_key | other (how the CLI bills; informational).
    /// </summary>
    private sealed record ClientReport(string? Path, bool? SignedIn, string? Version, bool Compatible = true, bool Blocked = false, string? Auth = null)
    {
        public bool Usable => Path is not null && Compatible && !Blocked;
    }

    private static readonly string[] AuthKinds = ["subscription", "api_key", "other"];

    /// <summary>The user's Codex <c>cli_auth_credentials_store</c> (m5-chat §3), read per run. Tests replace it.</summary>
    internal Func<string?> CodexCredentialsStore { get; set; } = CodexConfig.ReadCredentialsStore;

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
        var kind = P.OneOf(p, "kind", McpKinds);
        var path = P.Str(p, "path", required: false, maxLength: 1024);
        if (p["signedIn"] is not (null or JsonValue)) throw RpcError.Params("signedIn", "signedIn must be true, false or null.");
        bool? signedIn = p["signedIn"] is JsonValue sv ? (sv.TryGetValue<bool>(out var b) ? b : throw RpcError.Params("signedIn", "signedIn must be true, false or null.")) : null;
        var version = P.Str(p, "version", required: false, maxLength: 64);
        var compatible = OptionalBool(p, "compatible") ?? true;
        var blocked = OptionalBool(p, "blocked") ?? false;
        var auth = p["auth"] is null ? null : P.OneOf(p, "auth", AuthKinds);
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
            _clients[kind] = new ClientReport(path, path is null ? null : signedIn, version, compatible, blocked, path is null ? null : auth);
        }
        var state = blocked ? "blocked" : path is null ? "not installed" : !compatible ? "outdated"
            : signedIn switch { true => "signed in", false => "signed out", _ => "unknown" };
        _log($"provider {kind}: client {state}{(auth is null || path is null ? "" : " (" + auth + ")")}");
        return FreshEntry(kind);
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

    private async Task HeadlessAsync(string taskId, string turnId, string text, string? cardId, CancellationToken ct)
    {
        try
        {
            await RunHeadlessAsync(taskId, turnId, text, cardId, ct).ConfigureAwait(false);
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
            EndRun(taskId);
        }
    }

    private async Task RunHeadlessAsync(string taskId, string turnId, string text, string? cardId, CancellationToken ct)
    {
        var task = _store.Write(db => LoadLive(db, taskId));
        if (InactiveCode(task) is { } inactive)
        {
            TurnFailed(taskId, turnId, 0, inactive);
            return;
        }
        var grant = GrantOf(task);
        if (UsedValue(task, "turns") >= grant.Limits.Turns)
        {
            Exhaust(taskId, turnId, 0);
            return;
        }

        const int n = 1;
        TryEvent(taskId, "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = n, ["status"] = "started" });
        var kind = task["recipient"].Text("kind");
        var codex = kind == "codex_mcp";
        string? cli;
        lock (_gate) cli = _clients.TryGetValue(kind, out var c) && c.Usable && c.SignedIn != false ? c.Path : null;
        var bridge = _options.McpExecutablePath;
        if (cli is null || bridge is null || _options.CliRunner is not { } runner || DataDir is null)
        {
            TurnFailed(taskId, turnId, n, cli is null && ClientStatus(kind)?.Detail == "signed_out" ? "auth" : "unavailable");
            return;
        }

        var runDir = Path.Combine(DataDir, "agent-runs", taskId);
        var session = task["clientSession"]?.GetValue<string>();
        var first = session is null;
        var prompt = _store.Write(db => Prompt(db, taskId, text, cardId, first));
        var run = OpenRun(taskId, kind, turnId);
        var mcpConfig = Path.Combine(runDir, HeadlessCommand.McpConfigFile);
        try
        {
            Directory.CreateDirectory(runDir);
            try { Acl.RestrictDirectory(runDir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { _log($"task {taskId} run folder ACL: {ex.GetType().Name}"); }

            IReadOnlyList<string>? args;
            string stdin;
            if (codex)
            {
                args = HeadlessCommand.Codex(runDir, bridge, run.Token, session, CodexCredentialsStore());
                stdin = first ? SystemPrompt + "\n\n" + prompt : prompt;
            }
            else
            {
                var id = first ? Guid.NewGuid() : Guid.TryParse(session, out var g) ? g : Guid.NewGuid();
                session = id.ToString("D");
                File.WriteAllText(mcpConfig, HeadlessCommand.ClaudeMcpConfig(bridge, run.Token));
                File.WriteAllText(Path.Combine(runDir, HeadlessCommand.SystemFile), SystemPrompt);
                args = HeadlessCommand.Claude(runDir, id, resume: !first);
                stdin = prompt;
            }
            if (args is null)
            {
                TurnFailed(taskId, turnId, n, "unavailable");
                return;
            }

            var seconds = Math.Min(MaxRunSeconds, grant.Limits.Turns * grant.Limits.TurnSeconds);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            var stream = new HeadlessStream(codex);
            CliExit? exit = null;
            string? stopped = null; // cancelled | timeout | internal: the run ended before the CLI did
            try
            {
                exit = await runner.RunAsync(new CliLaunch(cli, args, stdin, runDir), stream.Feed, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stopped = ct.IsCancellationRequested ? "cancelled" : "timeout";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log($"task {taskId} headless run: {ex.GetType().Name}");
                stopped = "internal";
            }
            if (exit is { Started: false })
            {
                _log($"task {taskId} headless run did not start ({exit.LaunchError ?? "launch_failed"})");
                TurnFailed(taskId, turnId, n, "unavailable"); // never launched: nothing to count
                return;
            }

            // Every launched run counts as a turn, however it ended; its tokens are added to used. The client's session or
            // thread is kept for the next ask: one the CLI reported is kept even when the run was cancelled or timed out.
            var answer = stopped is null ? stream.Answer : null;
            var newSession = codex ? stream.Session : stream.Session ?? (answer is not null ? session : null);
            var usage = new JsonObject { ["inputTokens"] = stream.InputTokens, ["outputTokens"] = stream.OutputTokens };
            _store.Write(db =>
            {
                var t = LoadTask(db, taskId);
                AddUsed(t, "turns", 1);
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

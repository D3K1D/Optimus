using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// provider.* and connection states (contracts/m3-agent.md §4, §7, §9). An API key is accepted only by
// provider.setKey, handed to the adapter's check and the credential store, and never returned, logged, evented,
// stored in SQLite or put in an exception message.
//
// Each kind's status is computed from one place (ComputeStatus) and published by RefreshProviders, which emits
// provider.status on the user stream exactly when (status, detail, code) changes. Every change point calls it.
public sealed partial class AgentService
{
    public static readonly string[] ProviderKinds = ["local", "anthropic_api", "openai_api", "chatgpt_plan", "claude_code_mcp", "codex_mcp"];
    public static readonly string[] McpKinds = ["claude_code_mcp", "codex_mcp"];
    public static readonly string[] ProviderKindsWithSignIn = ["chatgpt_plan"];
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private sealed class ApiState
    {
        public bool Checking;
        public string? Error; // auth | quota | network | unavailable | not_eligible | storage | callback_timeout | cancelled
        public bool? Tools;   // null = not tested (tools allowed); false = explain-only (report review)
        public IReadOnlyList<string> Models = [];
        public string? Model;  // the model this account uses, picked by the last check (null = not checked in this process)
        public bool NotSaved;  // the key works but could not be remembered on this PC (kept for this session only)
    }

    private sealed record Published(string Status, string? Detail, string? Code, string Since);

    private readonly Dictionary<string, ApiState> _api = new() { ["anthropic_api"] = new(), ["openai_api"] = new(), ["chatgpt_plan"] = new() };
    private readonly Dictionary<string, string> _mcpOutcome = [];   // kind → pairing_expired | denied | bridge_rejected
    private readonly Dictionary<string, Published> _published = [];

    private IProviderAdapter? Adapter(string kind) => _options.Providers.FirstOrDefault(a => a.Kind == kind);

    private IProviderSignIn? SignIn(string kind) => _options.SignIns.FirstOrDefault(s => s.Kind == kind);

    /// <summary>API key stored, or account signed in (chatgpt_plan).</summary>
    private bool HasCredential(string kind) => SignIn(kind) is { } signIn ? signIn.IsSignedIn : _credentials.Has(kind);

    /// <summary>The credential for one call: the stored key, or a fresh (refreshed) sign-in token. Never logged.</summary>
    private async Task<ProviderCredential?> CredentialAsync(string kind, CancellationToken ct)
    {
        if (SignIn(kind) is not { } signIn) return _credentials.Load(kind);
        try { return await signIn.CredentialAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log($"provider {kind}: credential unavailable ({ex.GetType().Name})"); return null; }
    }

    /// <summary>True when a tool-use test showed this account cannot call tools: tasks are explain-only.</summary>
    private bool ToolsDisabled(string kind)
    {
        lock (_gate) return _api.TryGetValue(kind, out var s) && s.Tools == false;
    }

    private static string ClientOf(string kind) => kind == "codex_mcp" ? "codex" : "claude_code";

    /// <summary>MCP kinds a pairing client counts for ("unknown" counts for both).</summary>
    private static string[] KindsOf(string client) => client switch
    {
        "codex" => ["codex_mcp"], "claude_code" => ["claude_code_mcp"], _ => McpKinds,
    };

    // ---------------------------------------------------------------- status (§9)

    private (string Status, string? Detail, string? Code) ComputeStatus(Db db, string kind)
    {
        if (kind == "local") return ("local", null, null);
        if (InAppKinds.Contains(kind))
        {
            if (Adapter(kind) is null) return ("error", "unavailable", null);
            ApiState s;
            lock (_gate) s = _api[kind];
            if (s.Checking) return ("checking", null, null);
            if (s.Error is not null) return ("error", s.Error, null);
            return HasCredential(kind) ? ("connected", s.NotSaved ? "not_saved" : null, null) : ("not_configured", null, null);
        }

        var client = ClientOf(kind);
        var now = Time.Format(Now());
        var pending = db.Query("SELECT code FROM agent_pairings WHERE status = 'pending' AND client IN ($c, 'unknown') AND expires_at > $n ORDER BY rowid DESC LIMIT 1",
            r => r.GetString(0), ("$c", client), ("$n", now)).FirstOrDefault();
        if (pending is not null) return ("pending", null, pending);

        // A user-window task (bound to its bridge). In-app headless tasks have no binding: they leave the kind "connected".
        // An idle-expired task is ended here first, so a long-gone session doesn't keep the kind "Closed" after a restart.
        var bound = db.Query("SELECT task_id, connection_id FROM agent_tasks WHERE status = 'active' AND recipient_kind = $k AND connection_id IS NOT NULL ORDER BY rowid DESC",
                r => (Task: r.GetString(0), Connection: r.GetString(1)), ("$k", kind)).ToList()
            .FirstOrDefault(t => LoadLive(db, t.Task).Text("status") == "active").Connection;
        if (bound is not null)
            return long.TryParse(bound.AsSpan(Math.Min(4, bound.Length)), out var pid) && ProcessAlive(pid) ? ("active", null, null) : ("client_closed", null, null);

        if (db.Scalar("SELECT 1 FROM agent_pairings WHERE status = 'approved' AND client IN ($c, 'unknown') LIMIT 1", ("$c", client)) is not null)
            return ("paired", null, null);

        string? outcome;
        lock (_gate) _mcpOutcome.TryGetValue(kind, out outcome);
        if (outcome == "bridge_rejected") return ("error", outcome, null); // stays as in m3

        // M5 §1: the shell's report on the CLI (installed and signed in → connected; installed, signed out → signed_out) wins over waiting.
        if (ClientStatus(kind) is { } reported) return (reported.Status, reported.Detail, null);

        if (outcome is not null) return ("error", outcome, null);

        var seen = db.Scalar("SELECT 1 FROM agent_pairings WHERE client IN ($c, 'unknown') LIMIT 1", ("$c", client)) is not null;
        return ("waiting", seen ? "idle" : "never_seen", null);
    }

    /// <summary>Recomputes the kinds and emits provider.status for each one whose (status, detail, code) changed.</summary>
    private void RefreshProviders(Db db, params string[] kinds)
    {
        // Pending pairings past their 5 minutes lapse here too, so the pill moves without anyone polling the pairing.
        foreach (var id in db.Query("SELECT pairing_id FROM agent_pairings WHERE status = 'pending' AND expires_at <= $n", r => r.GetString(0), ("$n", Time.Format(Now()))))
            LivePairing(db, id);

        foreach (var kind in kinds.Distinct())
        {
            var (status, detail, code) = ComputeStatus(db, kind);
            Published? previous;
            lock (_gate) _published.TryGetValue(kind, out previous);
            if (previous is not null && previous.Status == status && previous.Detail == detail && previous.Code == code) continue;
            var since = Time.Format(Now());
            lock (_gate) _published[kind] = new Published(status, detail, code, since);
            if (previous is null) continue; // the first computation is the starting state, not a change
            var payload = new JsonObject { ["kind"] = kind, ["status"] = status, ["detail"] = detail, ["since"] = since };
            if (code is not null) payload["code"] = code;
            db.AppendEvent("user", "provider.status", payload);
        }
    }

    private void RefreshProviders(params string[] kinds) => _store.Write(db => RefreshProviders(db, kinds));

    /// <summary>Starting states (no events) and a light sweep that notices lapsed pairings and exited bridges.</summary>
    private void StartStatusSweep()
    {
        RefreshProviders(ProviderKinds);
        var token = _sweep.Token;
        _sweepTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(SweepInterval, token).ConfigureAwait(false);
                    try { RefreshProviders(McpKinds); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log($"provider sweep: {ex.GetType().Name}"); }
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _sweep = new();
    private Task _sweepTask = Task.CompletedTask;

    /// <summary>Records an in-app turn or review outcome on the provider (auth/quota/network/unavailable; null = success). Keys are kept.</summary>
    private void RecordProviderOutcome(string kind, string? providerCode)
    {
        if (!InAppKinds.Contains(kind)) return;
        var error = providerCode switch
        {
            null => null,
            "auth" => "auth",
            "not_eligible" or "forbidden" => "not_eligible",
            "quota" => "quota",
            "rate_limited" or "overloaded" => "unavailable",
            "network" => "network",
            _ => "keep", // bad_request, incomplete, cancelled: says nothing about the connection
        };
        if (error == "keep") return;
        lock (_gate) _api[kind].Error = error;
        RefreshProviders(kind);
    }

    /// <summary>A bridge failed the image or session check (it gets no events; the app's pill shows the setup issue).</summary>
    public void BridgeRejected()
    {
        lock (_gate) foreach (var k in McpKinds) _mcpOutcome[k] = "bridge_rejected";
        RefreshProviders(McpKinds);
    }

    // ---------------------------------------------------------------- provider.list / entries

    public JsonNode ListProviders() => _store.Write(db =>
    {
        RefreshProviders(db, ProviderKinds);
        return (JsonNode)new JsonObject
        {
            ["providers"] = new JsonArray(ProviderKinds.Select(k => (JsonNode?)Entry(k, db)).ToArray()),
            ["diagnostics"] = AvailableDiagnostics(),
        };
    });

    /// <summary>The §3.3 diagnostics whose collectors exist in this build (none on the fixture source), for the grant sheet.</summary>
    private JsonArray AvailableDiagnostics()
    {
        var ids = _catalog.IsFixture ? [] : _catalog.Collectors.Select(c => c.Id).ToHashSet();
        var all = new Grant([.. Diagnostics.All.Select(d => d.Id)], false, new Limits(), 900);
        var runSchema = Diagnostics.Tools(all).First(t => t.Name == Diagnostics.RunDiagnostic).InputSchema;
        return new JsonArray(Diagnostics.All
            .Where(d => d.TakesTarget ? Diagnostics.EventTargets.Any(t => ids.Contains($"win.events.{t}")) : d.CollectorIds.Any(ids.Contains))
            .Select(d => (JsonNode?)new JsonObject
            {
                ["id"] = d.Id, ["description"] = d.Description, ["arguments"] = ArgumentSchema(d, all, runSchema),
            }).ToArray());
    }

    /// <summary>A provider entry from the published state (call after RefreshProviders). <paramref name="db"/>: the open
    /// transaction, when there is one (the pairing lookup for native clients reads through it).</summary>
    private JsonObject Entry(string kind, Db? db = null)
    {
        Published state;
        lock (_gate) state = _published[kind];
        var adapter = Adapter(kind);
        IReadOnlyList<string> models = [];
        if (InAppKinds.Contains(kind)) lock (_gate) models = _api[kind].Models;
        var entry = new JsonObject
        {
            ["kind"] = kind,
            ["label"] = kind switch { "local" => "Local only", "chatgpt_plan" => "ChatGPT", _ => adapter?.DisplayName ?? RecipientLabel(kind) },
            ["mode"] = kind == "local" ? "local" : ProviderKindsWithSignIn.Contains(kind) ? "browser_sign_in" : InAppKinds.Contains(kind) ? "api_key" : "native_mcp",
            ["status"] = state.Status, ["detail"] = state.Detail, ["since"] = state.Since,
            ["models"] = Json.Array(models), ["defaultModel"] = InAppKinds.Contains(kind) && adapter is not null ? ModelFor(kind, adapter) : adapter?.DefaultModel,
        };
        if (state.Code is not null) entry["code"] = state.Code;
        // Native clients: the pending pairing's ID (so the app can still approve it after a reload) or the approved one
        // (so a task can be started from any Start, not only straight after Approve).
        if (McpKinds.Contains(kind) && PairingStatusFor(state.Status) is { } want)
        {
            var id = db is null ? _store.Read(d => PairingIdOf(d, kind, want)) : PairingIdOf(db, kind, want);
            if (id is not null) entry["pairingId"] = id;
        }
        if (InAppKinds.Contains(kind)) entry["capabilities"] = new JsonObject { ["tools"] = !ToolsDisabled(kind) };
        return entry;
    }

    private static string? PairingStatusFor(string providerStatus) => providerStatus switch
    {
        "pending" => "pending", "paired" or "active" or "client_closed" => "approved", _ => null,
    };

    private string? PairingIdOf(Db db, string kind, string status) =>
        db.Query("SELECT pairing_id FROM agent_pairings WHERE status = $s AND client IN ($c, 'unknown') AND ($s <> 'pending' OR expires_at > $n) ORDER BY rowid DESC LIMIT 1",
            r => r.GetString(0), ("$s", status), ("$c", ClientOf(kind)), ("$n", Time.Format(Now()))).FirstOrDefault();

    private JsonObject FreshEntry(string kind)
    {
        RefreshProviders(kind);
        return Entry(kind);
    }

    // ---------------------------------------------------------------- setKey / connect / disconnect

    /// <summary>provider.setKey (desktop shell only): check, then store. Returns only a status.</summary>
    public async Task<JsonNode> SetKeyAsync(JsonObject p)
    {
        var kind = P.OneOf(p, "kind", ApiKeyKinds);
        if (p["apiKey"] is not JsonValue kv || !kv.TryGetValue<string>(out var apiKey) || apiKey.Length is 0 or > 1024)
            throw RpcError.Params("apiKey", "apiKey must be a non-empty string."); // never echoes the value
        var remember = p["remember"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
        var adapter = Adapter(kind) ?? throw RpcError.State("provider_unavailable", "That provider is not available in this build.");

        var (ok, error) = await CheckAsync(adapter, new ProviderCredential(kind, apiKey), save: () => SaveKey(kind, apiKey, remember)).ConfigureAwait(false);
        // Only the code (auth | quota | network | unavailable | not_eligible | storage): the shell relays code-shaped errors only.
        if (!ok) return new JsonObject { ["status"] = "error", ["detail"] = error, ["error"] = error };
        return new JsonObject { ["status"] = "connected" };
    }

    /// <summary>
    /// Stores a checked key. null: stored as asked. "not_saved": remembering failed on this PC (a locked or roaming
    /// profile, antivirus, DPAPI unavailable), so the key is kept for this session only. "storage": not even that.
    /// Only exception type names are logged.
    /// </summary>
    private string? SaveKey(string kind, string apiKey, bool remember)
    {
        try
        {
            _credentials.Save(kind, apiKey, remember);
            _log($"provider {kind}: key saved ({(remember ? "remembered" : "this session")})");
            return null;
        }
        catch (Exception ex) when (remember)
        {
            _log($"provider {kind}: key not remembered ({ex.GetType().Name}); keeping it for this session");
        }
        catch (Exception ex)
        {
            _log($"provider {kind}: key not saved ({ex.GetType().Name})");
            return "storage";
        }
        try
        {
            _credentials.Save(kind, apiKey, remember: false);
            return "not_saved";
        }
        catch (Exception ex)
        {
            _log($"provider {kind}: key not saved ({ex.GetType().Name})");
            return "storage";
        }
    }

    /// <summary>
    /// checking → connected | error, each published; Checking is always cleared. On success <paramref name="save"/>
    /// runs before the status leaves checking (so connected means a credential is held; its result is null, "not_saved"
    /// or "storage", see <see cref="SaveKey"/>), the account's model is picked from what it lists, then an adapter that
    /// can feature-test tool calling does so on that model (false → capabilities {tools:false}, explain-only). Failures
    /// keep the adapter's typed code, mapped to auth|quota|network|unavailable|not_eligible|storage; adapter error text
    /// is never forwarded (it could echo input).
    /// </summary>
    private async Task<(bool Ok, string? Error)> CheckAsync(IProviderAdapter adapter, ProviderCredential credential, Func<string?>? save = null, bool alreadyChecking = false)
    {
        var kind = adapter.Kind;
        if (!alreadyChecking)
        {
            lock (_gate) _api[kind].Checking = true;
            RefreshProviders(kind);
        }

        string? error = "unavailable";
        bool? tools = null;
        var notSaved = false;
        string? model = null;
        IReadOnlyList<string> models = [];
        try
        {
            try
            {
                using var cts = new CancellationTokenSource(CheckTimeout);
                var check = await adapter.CheckAsync(credential, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                error = check.Ok ? null : check.Code is null ? "auth" : ProviderError(check.Code) ?? "unavailable";
                models = check.Models.Take(50).Select(Clean).ToList();
                if (error is null)
                {
                    model = check.Model is { Length: > 0 and <= 100 } m ? Clean(m) : adapter.DefaultModel;
                    if (adapter is IToolCapability capability)
                        tools = await capability.SupportsToolsAsync(credential, model, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (ProviderException ex) { error = ProviderError(ex.Code) ?? "unavailable"; }
            catch (OperationCanceledException) { error = "network"; }
            catch (Exception ex)
            {
                _log($"provider {kind}: check failed ({ex.GetType().Name})");
                error = "unavailable";
            }

            if (error is null && save is not null)
            {
                var saved = save();
                if (saved == "storage") error = "storage";
                notSaved = saved == "not_saved";
            }
        }
        finally
        {
            lock (_gate)
            {
                var s = _api[kind];
                s.Checking = false;
                s.Error = error;
                if (error is null)
                {
                    s.Models = models;
                    s.Model = model;
                    s.Tools = tools;
                    s.NotSaved = notSaved || (save is null && s.NotSaved);
                }
            }
            RefreshProviders(kind);
        }
        if (error is null) _log($"provider {kind}: connected (model {model})");
        return (error is null, error);
    }

    /// <summary>The model a task or review on this in-app kind uses: the one the last check picked, else the adapter's default.</summary>
    private string ModelFor(string kind, IProviderAdapter adapter)
    {
        lock (_gate) return _api.TryGetValue(kind, out var s) && s.Model is { } m ? m : adapter.DefaultModel;
    }

    /// <summary>
    /// The model for one call. A model the user asked for is kept. Otherwise, when this process has not checked the
    /// account yet (the host restarted with a remembered key or sign-in), one quick model listing picks it, so a default
    /// the account cannot use is never sent blind. A failed listing falls back to the default (and is not retried this process).
    /// </summary>
    private async Task<string> ResolveModelAsync(string kind, IProviderAdapter adapter, ProviderCredential credential, string? requested, CancellationToken ct)
    {
        if (requested is not null && requested != adapter.DefaultModel) return requested;
        lock (_gate) if (_api.TryGetValue(kind, out var known) && known.Model is { } m) return m;
        var model = adapter.DefaultModel;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CheckTimeout);
            var check = await adapter.CheckAsync(credential, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            if (check.Ok && check.Model is { Length: > 0 and <= 100 } picked) model = Clean(picked);
            if (check.Ok) lock (_gate) _api[kind].Models = check.Models.Take(50).Select(Clean).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"provider {kind}: model lookup failed ({ex.GetType().Name})");
        }
        lock (_gate) _api[kind].Model ??= model;
        return model;
    }

    private static string? ProviderError(string code) => code switch
    {
        "auth" => "auth", "quota" => "quota", "rate_limited" or "overloaded" or "incomplete" => "unavailable", "network" or "timeout" => "network",
        "not_eligible" or "forbidden" => "not_eligible", _ => null,
    };

    private static string CheckMessage(string code) => code switch
    {
        "auth" => "The key was not accepted.",
        "quota" => "The account has no remaining credit.",
        "unavailable" or "rate_limited" or "overloaded" => "The provider is busy. Try again shortly.",
        "timeout" => "The provider did not answer in time.",
        "network" => "The provider could not be reached.",
        "not_eligible" => "This account or region cannot use it.",
        "storage" => "The key could not be saved on this PC.",
        _ => "The key could not be checked.",
    };

    /// <summary>
    /// provider.connect: API kinds re-run the check (Retry); chatgpt_plan starts the browser sign-in in the host and
    /// returns at once with status checking (provider.status events report the outcome); MCP kinds return the
    /// registration commands.
    /// </summary>
    public async Task<JsonNode> ConnectAsync(JsonObject p)
    {
        var kind = P.OneOf(p, "kind", ProviderKinds);
        P.Str(p, "model", required: false, maxLength: 100);
        if (SignIn(kind) is { } signIn)
        {
            var adapter = Adapter(kind) ?? throw RpcError.State("provider_unavailable", "That provider is not available in this build.");
            lock (_gate)
            {
                if (_api[kind].Checking) return Entry(kind); // a sign-in is already running
                _api[kind].Checking = true;
            }
            RefreshProviders(kind);
            Track(Task.Run(() => SignInAsync(kind, signIn, adapter)));
            return FreshEntry(kind);
        }
        if (InAppKinds.Contains(kind))
        {
            var adapter = Adapter(kind) ?? throw RpcError.State("provider_unavailable", "That provider is not available in this build.");
            var credential = _credentials.Load(kind) ?? throw RpcError.State("provider_not_configured", "Add a key first.");
            await CheckAsync(adapter, credential).ConfigureAwait(false);
            return FreshEntry(kind);
        }
        if (kind == "local") return FreshEntry(kind);

        var exe = _options.McpExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Mcp.exe");
        var entry = FreshEntry(kind);
        entry["registration"] = new JsonObject
        {
            ["claude"] = $"claude mcp add --scope user optimization-tool -- \"{exe}\"",
            ["codex"] = $"codex mcp add optimization-tool -- \"{exe}\"",
        };
        return entry;
    }

    /// <summary>
    /// Browser sign-in (at most SignInTimeout), then the same check as a key: connected, or error with a safe reason:
    /// callback_timeout (the browser never came back: blocked loopback, another browser profile, or the tab was left),
    /// cancelled (the user said no), not_eligible, network, storage (tokens not saved), unavailable (anything else).
    /// </summary>
    private async Task SignInAsync(string kind, IProviderSignIn signIn, IProviderAdapter adapter)
    {
        string? error = "unavailable";
        try
        {
            try
            {
                using var cts = new CancellationTokenSource(_options.SignInTimeout);
                var result = await signIn.SignInAsync(url => OpenAuthorizationUrl(signIn, url), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                error = result.Ok ? null : SignInError(result.Error);
            }
            catch (OperationCanceledException) { error = "callback_timeout"; }
            catch (Exception ex)
            {
                _log($"provider {kind}: sign-in failed ({ex.GetType().Name})");
                error = "unavailable";
            }

            if (error is null)
            {
                using var cts = new CancellationTokenSource(CheckTimeout);
                var credential = await CredentialAsync(kind, cts.Token).ConfigureAwait(false);
                if (credential is not null)
                {
                    var (ok, _) = await CheckAsync(adapter, credential, alreadyChecking: true).ConfigureAwait(false);
                    _log($"provider {kind}: signed in{(ok ? "" : ", check failed")}");
                    return; // CheckAsync published the outcome and cleared Checking
                }
                error = "auth";
            }
            _log($"provider {kind}: sign-in ended ({error})");
        }
        finally
        {
            bool stuck;
            lock (_gate)
            {
                stuck = _api[kind].Checking;
                if (stuck)
                {
                    _api[kind].Checking = false;
                    _api[kind].Error = error ?? "unavailable";
                }
            }
            if (stuck) RefreshProviders(kind);
        }
    }

    internal static string SignInError(string? code) => code switch
    {
        "not_eligible" => "not_eligible",
        "access_denied" => "cancelled",
        "timeout" => "callback_timeout",
        "network" => "network",
        "storage" => "storage",
        _ => "unavailable",
    };

    /// <summary>
    /// Opens only an https URL on the sign-in's issuer host (the exact authorization URL it built), in the system
    /// browser. Anything else is refused and the sign-in aborts.
    /// </summary>
    private bool OpenAuthorizationUrl(IProviderSignIn signIn, Uri url)
    {
        var sameHost = url.Scheme == Uri.UriSchemeHttps && string.Equals(url.Host, signIn.Issuer.Host, StringComparison.OrdinalIgnoreCase)
            && url.Port == signIn.Issuer.Port;
        if (!sameHost)
        {
            _log($"provider {signIn.Kind}: refused to open a URL outside the issuer");
            return false;
        }
        if (_options.OpenBrowser is { } open) return open(url);
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _log($"provider {signIn.Kind}: could not open the browser ({ex.GetType().Name})");
            return false;
        }
    }

    public JsonNode Disconnect(JsonObject p)
    {
        var kind = P.OneOf(p, "kind", ProviderKinds);
        if (InAppKinds.Contains(kind))
        {
            if (SignIn(kind) is { } signIn) signIn.SignOut(); // deletes the stored tokens
            else _credentials.Delete(kind);
            lock (_gate)
            {
                _api[kind].Error = null;
                _api[kind].Models = [];
                _api[kind].Model = null;
                _api[kind].NotSaved = false;
                _api[kind].Tools = null;
            }
        }
        lock (_gate) _mcpOutcome.Remove(kind);
        _store.Write(db =>
        {
            foreach (var id in db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active' AND recipient_kind = $k", r => r.GetString(0), ("$k", kind)))
                SetStatus(db, LoadTask(db, id), "stopped");
            if (McpKinds.Contains(kind))
                foreach (var id in db.Query("SELECT pairing_id FROM agent_pairings WHERE status IN ('pending','approved') AND client IN ($c, 'unknown')", r => r.GetString(0), ("$c", ClientOf(kind))))
                    SetPairing(db, PairingRow(db, id)!, "denied");
            RefreshProviders(db, kind);
        });
        return FreshEntry(kind);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>Why a ChatGPT sign-in or token refresh failed. Messages never contain tokens, codes or verifiers.</summary>
public sealed class ChatGptSignInException(string code, string message) : Exception(message)
{
    /// <summary>
    /// denied | timeout | state_mismatch | client_mismatch | invalid_token | discovery | exchange_failed |
    /// scope_missing | network | callback_failed.
    /// </summary>
    public string Code { get; } = code;
}

public sealed record ChatGptSignInOptions
{
    /// <summary>Issuer; discovery is read from {Issuer}/.well-known/openid-configuration.</summary>
    public Uri Issuer { get; init; } = new("https://auth.openai.com");
    /// <summary>Documented authorize endpoint for agent sign-in (docs/chatgpt-signin.md §3).</summary>
    public Uri AuthorizeEndpoint { get; init; } = new("https://auth.openai.com/api/accounts/authorize");
    /// <summary>Documented token endpoint; discovery's token_endpoint wins when present.</summary>
    public Uri TokenEndpoint { get; init; } = new("https://auth.openai.com/api/accounts/oauth/token");
    public string Resource { get; init; } = "https://api.openai.com/v1";
    public string Scope { get; init; } = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public string AgentNameHint { get; init; } = "Optimization Tool";
    /// <summary>
    /// How long to wait for the browser to come back to the loopback callback. 3 minutes: a real sign-in takes well
    /// under that, and a callback blocked by a firewall, proxy or a browser on another profile otherwise looks stuck.
    /// </summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Refresh this long before the access token expires.</summary>
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>Who signed in. No tokens.</summary>
public sealed record ChatGptAccount(string Subject, string? Email, string ClientId, IReadOnlyList<string> Scopes);

/// <summary>
/// "Continue with ChatGPT": OAuth 2.0 authorization code + PKCE (S256) public client with dynamic registration
/// (client_id=dynamic_agent_client on first sign-in, issued oaiapp_… afterwards), system browser, one-shot loopback
/// callback on http://127.0.0.1:&lt;ephemeral&gt;/auth/callback, OIDC ID-token validation against the issuer's discovered JWKS,
/// rotating refresh tokens. Stored through <see cref="ICredentialStore"/> (DPAPI, remember=true):
/// kind <see cref="HostKind"/> = {ext_agent_host_id, client_id} for the install, kind <see cref="TokenKind"/> = tokens.
/// Nothing here logs.
/// </summary>
public sealed class ChatGptSignIn : IProviderSignIn
{
    public const string TokenKind = "chatgpt_plan";
    public const string HostKind = "chatgpt_host";
    public const string CallbackPath = "/auth/callback";
    public const string RequiredScope = "chatgpt.tokens.use.direct";
    const string DynamicClientId = "dynamic_agent_client";

    readonly ICredentialStore _store;
    readonly HttpClient _http;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    JsonObject? _discovery;

    public ChatGptSignIn(ICredentialStore store, HttpClient http, ChatGptSignInOptions? options = null, TimeProvider? time = null)
    {
        _store = store;
        _http = http;
        Options = options ?? new ChatGptSignInOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Host constructor: DPAPI store under <paramref name="dataDir"/> and its own HttpClient.</summary>
    public ChatGptSignIn(string dataDir) : this(new DpapiCredentialStore(dataDir), ProviderHttp.CreateClient()) { }

    public ChatGptSignInOptions Options { get; }

    public string Kind => TokenKind;

    /// <summary>The authorization URL is always on this issuer's scheme, host and port.</summary>
    public Uri Issuer => Options.Issuer;

    public bool IsSignedIn => _store.Has(TokenKind);

    /// <summary>
    /// <see cref="IProviderSignIn"/> entry point. Errors: access_denied (user cancelled) | not_eligible (403 / plan scope
    /// not granted) | network | timeout (no callback within SignInTimeout) | browser (openBrowser refused) | storage
    /// (the DPAPI store could not be written) | invalid (anything else: state, nonce, signature,
    /// client mismatch, malformed responses). Caller cancellation propagates as OperationCanceledException.
    /// </summary>
    public async Task<SignInResult> SignInAsync(Func<Uri, bool> openBrowser, CancellationToken ct)
    {
        try
        {
            await SignInInteractiveAsync(openBrowser, ct).ConfigureAwait(false);
            return new SignInResult(true);
        }
        catch (ChatGptSignInException e)
        {
            return new SignInResult(false, ResultCode(e.Code));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new SignInResult(false, "storage"); // signed in, but the tokens could not be saved on this PC
        }
    }

    internal static string ResultCode(string code) => code switch
    {
        "denied" => "access_denied",
        "not_eligible" or "scope_missing" => "not_eligible",
        "network" => "network",
        "timeout" => "timeout",
        "browser" => "browser",
        _ => "invalid",
    };

    /// <summary><see cref="IProviderSignIn"/>: null when signed out; otherwise a fresh access token (refreshed if needed).</summary>
    public async Task<ProviderCredential?> CredentialAsync(CancellationToken ct) =>
        LoadTokens() is null ? null : await GetAccessTokenAsync(ct).ConfigureAwait(false);

    /// <summary>The signed-in account, or null. No network.</summary>
    public ChatGptAccount? Account => LoadTokens() is { } t ? new(t.Subject, t.Email, t.ClientId, t.Scopes) : null;

    /// <summary>Clears tokens (provider.disconnect). The install's host ID and issued client ID are kept for the next sign-in.</summary>
    public void SignOut() => _store.Delete(TokenKind);

    // ---- sign-in ------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one interactive sign-in. The loopback listener is bound before <paramref name="openBrowser"/> is called with
    /// the exact https authorize URL, and is closed when this returns (success, error, timeout or cancellation).
    /// </summary>
    public async Task<ChatGptAccount> SignInInteractiveAsync(Func<Uri, bool> openBrowser, CancellationToken ct)
    {
        var discovery = await DiscoverAsync(ct).ConfigureAwait(false);
        if (Options.AuthorizeEndpoint.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(Options.AuthorizeEndpoint.Host, Options.Issuer.Host, StringComparison.OrdinalIgnoreCase) ||
            Options.AuthorizeEndpoint.Port != Options.Issuer.Port)
            throw new ChatGptSignInException("discovery", "The sign-in page is not on the ChatGPT issuer.");
        var host = LoadOrCreateHost();

        var verifier = RandomToken(32);
        var state = RandomToken(32);
        var nonce = RandomToken(32);

        using var listener = LoopbackCallback.Start();
        var redirectUri = $"http://127.0.0.1:{listener.Port}{CallbackPath}";
        var url = BuildAuthorizeUrl(host, redirectUri, state, nonce, Challenge(verifier));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.SignInTimeout);
        var callbackTask = listener.WaitAsync(timeout.Token);
        if (!openBrowser(url)) throw new ChatGptSignInException("browser", "The browser could not be opened.");

        IReadOnlyDictionary<string, string> query;
        try
        {
            query = await callbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatGptSignInException("timeout", "Sign-in was not completed in time.");
        }

        if (!query.TryGetValue("state", out var gotState) || !FixedEquals(gotState, state))
            throw new ChatGptSignInException("state_mismatch", "The sign-in response did not match this attempt.");
        if (query.TryGetValue("error", out var error))
            throw new ChatGptSignInException(error == "access_denied" ? "denied" : error.Contains("not_eligible", StringComparison.Ordinal) || error.Contains("region", StringComparison.Ordinal) ? "not_eligible" : "callback_failed",
                error == "access_denied" ? "Sign-in was cancelled." : "ChatGPT refused the sign-in.");
        if (!query.TryGetValue("code", out var code) || code.Length == 0)
            throw new ChatGptSignInException("callback_failed", "The sign-in response had no authorization code.");

        var clientId = host.ClientId;
        query.TryGetValue("client_id", out var issued);
        if (clientId is null)
        {
            if (string.IsNullOrEmpty(issued))
                throw new ChatGptSignInException("callback_failed", "ChatGPT did not issue a client ID.");
            clientId = issued;
        }
        else if (!string.IsNullOrEmpty(issued) && issued != clientId)
        {
            throw new ChatGptSignInException("client_mismatch", "ChatGPT returned a different client ID than this install's.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = Options.Resource,
        };
        var response = await PostTokenAsync(discovery, form, "exchange_failed", ct).ConfigureAwait(false);

        var idToken = response["id_token"].AsString()
            ?? throw new ChatGptSignInException("invalid_token", "ChatGPT did not return an ID token.");
        var claims = await ValidateIdTokenAsync(idToken, clientId, nonce, ct).ConfigureAwait(false);

        var scopes = ReadScopes(response["scope"].AsString() ?? (query.TryGetValue("scope", out var s) ? s : null));
        if (!scopes.Contains(RequiredScope))
            throw new ChatGptSignInException("scope_missing", "This ChatGPT account did not grant plan usage to the app.");

        // Persist the issued client ID only after a validated exchange.
        if (host.ClientId is null) SaveHost(host with { ClientId = clientId });
        var tokens = TokensFrom(response, clientId, claims, scopes, previousRefresh: null)
            ?? throw new ChatGptSignInException("exchange_failed", "ChatGPT returned an incomplete token response.");
        SaveTokens(tokens);
        return new ChatGptAccount(tokens.Subject, tokens.Email, clientId, scopes);
    }

    internal Uri BuildAuthorizeUrl(HostRecord host, string redirectUri, string state, string nonce, string challenge)
    {
        var p = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", host.ClientId ?? DynamicClientId),
            new("redirect_uri", redirectUri),
            new("scope", Options.Scope),
            new("resource", Options.Resource),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge_method", "S256"),
            new("code_challenge", challenge),
            new("ext_agent_host_id", host.HostId),
        };
        if (host.ClientId is null) p.Add(new("agent_name_hint", Options.AgentNameHint));
        var query = string.Join("&", p.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        return new Uri(Options.AuthorizeEndpoint + "?" + query);
    }

    // ---- access tokens ------------------------------------------------------------------------------------

    /// <summary>A valid access token as a credential (kind chatgpt_plan), refreshing (and rotating the stored refresh token) when near expiry.</summary>
    public Task<ProviderCredential> GetAccessTokenAsync(CancellationToken ct = default) => GetAccessTokenCoreAsync(force: false, ct);

    /// <summary>Refreshes even if the current token looks valid (after a 401).</summary>
    public Task<ProviderCredential> RefreshAsync(CancellationToken ct = default) => GetAccessTokenCoreAsync(force: true, ct);

    async Task<ProviderCredential> GetAccessTokenCoreAsync(bool force, CancellationToken ct)
    {
        var before = LoadTokens() ?? throw NotSignedIn();
        if (!force && before.ExpiresAt - Options.RefreshSkew > _time.GetUtcNow())
            return new ProviderCredential(TokenKind, before.AccessToken);

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited; never spend a rotating refresh token twice.
            var current = LoadTokens() ?? throw NotSignedIn();
            if (current.RefreshToken != before.RefreshToken && current.ExpiresAt - Options.RefreshSkew > _time.GetUtcNow())
                return new ProviderCredential(TokenKind, current.AccessToken);
            if (!force && current.ExpiresAt - Options.RefreshSkew > _time.GetUtcNow())
                return new ProviderCredential(TokenKind, current.AccessToken);

            JsonObject discovery;
            try
            {
                discovery = await DiscoverAsync(ct).ConfigureAwait(false);
            }
            catch (ChatGptSignInException e)
            {
                throw new ProviderException(e.Code == "network" ? "network" : "auth", e.Message, retryable: false);
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = current.ClientId,
                ["refresh_token"] = current.RefreshToken,
                ["resource"] = Options.Resource,
            };
            JsonObject response;
            try
            {
                response = await PostTokenAsync(discovery, form, "refresh_failed", ct).ConfigureAwait(false);
            }
            catch (ChatGptSignInException e) when (e.Code == "terminal")
            {
                // invalid_grant, refresh_token_reused/expired/…: the refresh token is dead. Sign-in again (same client ID).
                _store.Delete(TokenKind);
                throw new ProviderException("auth", "ChatGPT sign-in expired. Sign in again.", retryable: false);
            }
            catch (ChatGptSignInException e)
            {
                // Network/server trouble or an unexpected rejection: keep the stored tokens.
                throw new ProviderException(e.Code is "network" or "not_eligible" ? e.Code : "auth", e.Message, retryable: false);
            }

            Dictionary<string, JsonNode?>? claims = null;
            if (response["id_token"].AsString() is { } idToken)
            {
                try
                {
                    claims = await ValidateIdTokenAsync(idToken, current.ClientId, expectedNonce: null, ct).ConfigureAwait(false);
                }
                catch (ChatGptSignInException)
                {
                    throw new ProviderException("auth", "ChatGPT returned an invalid ID token on refresh.", retryable: false);
                }
                if (claims["sub"].AsString() != current.Subject)
                    throw new ProviderException("auth", "ChatGPT refreshed a different account. Sign in again.", retryable: false);
            }
            var scopes = response["scope"].AsString() is { } sc ? ReadScopes(sc) : current.Scopes;
            var next = TokensFrom(response, current.ClientId, claims, scopes, previousRefresh: current.RefreshToken, previous: current)
                ?? throw new ProviderException("incomplete", "ChatGPT returned an incomplete token response.", retryable: false);
            SaveTokens(next); // access, expiry, scopes and refresh token replaced together
            if (!scopes.Contains(RequiredScope))
                throw new ProviderException("not_eligible", "This ChatGPT account no longer grants plan usage to the app.", retryable: false);
            return new ProviderCredential(TokenKind, next.AccessToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    static ProviderException NotSignedIn() => new("auth", "Not signed in to ChatGPT.", retryable: false);

    // ---- token endpoint -----------------------------------------------------------------------------------

    static readonly HashSet<string> TerminalRefreshErrors = new(StringComparer.Ordinal)
    {
        "invalid_grant", "refresh_token_reused", "refresh_token_expired", "refresh_token_invalidated", "invalid_client", "unauthorized_client",
    };

    async Task<JsonObject> PostTokenAsync(JsonObject discovery, Dictionary<string, string> form, string failCode, CancellationToken ct)
    {
        var endpoint = discovery["token_endpoint"].AsString() is { } te && Uri.TryCreate(te, UriKind.Absolute, out var teUri) && teUri.Scheme == "https"
            ? teUri : Options.TokenEndpoint;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var raw = await SendAsync(request, ct).ConfigureAwait(false);
        JsonObject? body = null;
        try { body = JsonNode.Parse(raw.Body) as JsonObject; } catch (JsonException) { }

        if (!raw.Status.IsSuccess())
        {
            var err = body?["error"].AsString() ?? body?["error"]?["code"].AsString();
            if (form["grant_type"] == "refresh_token" && (err is not null && TerminalRefreshErrors.Contains(err) || raw.Status is HttpStatusCode.Unauthorized))
                throw new ChatGptSignInException("terminal", "The ChatGPT sign-in is no longer valid.");
            if (raw.Status == HttpStatusCode.Forbidden)
                throw new ChatGptSignInException("not_eligible", "This ChatGPT account, workspace or region can't use plan access in this app.");
            if ((int)raw.Status >= 500)
                throw new ChatGptSignInException("network", $"ChatGPT sign-in service is unavailable ({(int)raw.Status}).");
            throw new ChatGptSignInException(failCode, $"ChatGPT rejected the token request ({(int)raw.Status}{(err is null ? "" : ", " + SafeCode(err))}).");
        }
        return body ?? throw new ChatGptSignInException(failCode, "ChatGPT returned an unreadable token response.");
    }

    /// <summary>An OAuth error code is a short identifier; anything else is dropped rather than echoed.</summary>
    static string SafeCode(string s) => s.Length <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? s : "error";

    async Task<ProviderHttp.RawResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await ProviderHttp.SendAsync(_http, request, Options.HttpTimeout, "ChatGPT sign-in", ct).ConfigureAwait(false);
        }
        catch (ProviderException e)
        {
            throw new ChatGptSignInException("network", e.Message);
        }
    }

    // ---- discovery and ID token ---------------------------------------------------------------------------

    internal async Task<JsonObject> DiscoverAsync(CancellationToken ct)
    {
        if (_discovery is not null) return _discovery;
        var url = new Uri(Options.Issuer.ToString().TrimEnd('/') + "/.well-known/openid-configuration");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var raw = await SendAsync(request, ct).ConfigureAwait(false);
        JsonObject? doc = null;
        if (raw.Status.IsSuccess())
            try { doc = JsonNode.Parse(raw.Body) as JsonObject; } catch (JsonException) { }
        if (doc is null)
            throw new ChatGptSignInException("discovery", $"Could not read ChatGPT's sign-in configuration ({(int)raw.Status}).");
        if (doc["issuer"].AsString() is not { } issuer || issuer.TrimEnd('/') != Options.Issuer.ToString().TrimEnd('/'))
            throw new ChatGptSignInException("discovery", "ChatGPT's sign-in configuration names a different issuer.");
        if (doc["jwks_uri"].AsString() is not { } jwks || !Uri.TryCreate(jwks, UriKind.Absolute, out var jwksUri) || jwksUri.Scheme != "https")
            throw new ChatGptSignInException("discovery", "ChatGPT's sign-in configuration has no signing-key location (jwks_uri).");
        if (doc["grant_types_supported"] is JsonArray grants && !grants.Any(g => g.AsString() == "refresh_token"))
            throw new ChatGptSignInException("discovery", "ChatGPT's sign-in configuration does not offer token refresh.");
        return _discovery = doc;
    }

    internal async Task<Dictionary<string, JsonNode?>> ValidateIdTokenAsync(string idToken, string clientId, string? expectedNonce, CancellationToken ct)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw Invalid("The ID token is malformed.");
        JsonObject header, payload;
        byte[] signature;
        try
        {
            header = JsonNode.Parse(Base64Url(parts[0])) as JsonObject ?? throw Invalid("The ID token header is malformed.");
            payload = JsonNode.Parse(Base64Url(parts[1])) as JsonObject ?? throw Invalid("The ID token payload is malformed.");
            signature = Base64Url(parts[2]);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw Invalid("The ID token is malformed.");
        }

        var alg = header["alg"].AsString();
        var kid = header["kid"].AsString();
        var key = await FindKeyAsync(kid, alg, refetch: false, ct).ConfigureAwait(false)
                  ?? await FindKeyAsync(kid, alg, refetch: true, ct).ConfigureAwait(false)
                  ?? throw Invalid("The ID token was signed with an unknown key.");
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!Verify(key, alg, signed, signature)) throw Invalid("The ID token signature is not valid.");

        var claims = payload.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (payload["iss"].AsString()?.TrimEnd('/') != Options.Issuer.ToString().TrimEnd('/')) throw Invalid("The ID token has the wrong issuer.");
        var audOk = payload["aud"] switch
        {
            JsonValue v => v.AsString() == clientId,
            JsonArray a => a.Any(x => x.AsString() == clientId),
            _ => false,
        };
        if (!audOk) throw Invalid("The ID token is for a different app.");
        if (payload["exp"] is not JsonValue expV || !expV.TryGetValue(out long exp) ||
            DateTimeOffset.FromUnixTimeSeconds(exp) + Options.ClockSkew <= _time.GetUtcNow())
            throw Invalid("The ID token has expired.");
        if (expectedNonce is not null && !FixedEquals(payload["nonce"].AsString() ?? "", expectedNonce))
            throw Invalid("The ID token does not match this sign-in attempt.");
        if (string.IsNullOrEmpty(payload["sub"].AsString())) throw Invalid("The ID token has no subject.");
        return claims;
    }

    static ChatGptSignInException Invalid(string message) => new("invalid_token", message);

    JsonArray? _jwks;

    async Task<JsonObject?> FindKeyAsync(string? kid, string? alg, bool refetch, CancellationToken ct)
    {
        if (_jwks is null || refetch)
        {
            var discovery = await DiscoverAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, discovery["jwks_uri"].AsString()!);
            var raw = await SendAsync(request, ct).ConfigureAwait(false);
            JsonObject? doc = null;
            if (raw.Status.IsSuccess())
                try { doc = JsonNode.Parse(raw.Body) as JsonObject; } catch (JsonException) { }
            _jwks = doc?["keys"] as JsonArray ?? throw new ChatGptSignInException("discovery", "Could not read ChatGPT's signing keys.");
        }
        var candidates = _jwks.OfType<JsonObject>()
            .Where(k => k["use"].AsString() is null or "sig")
            .Where(k => kid is null || k["kid"].AsString() == kid)
            .ToList();
        return candidates.Count == 1 || kid is not null ? candidates.FirstOrDefault() : null;
    }

    static bool Verify(JsonObject jwk, string? alg, byte[] data, byte[] signature)
    {
        try
        {
            switch (alg)
            {
                case "RS256" when jwk["kty"].AsString() == "RSA":
                    using (var rsa = RSA.Create())
                    {
                        rsa.ImportParameters(new RSAParameters { Modulus = Base64Url(jwk["n"].AsString()!), Exponent = Base64Url(jwk["e"].AsString()!) });
                        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }
                case "ES256" when jwk["kty"].AsString() == "EC" && jwk["crv"].AsString() == "P-256":
                    using (var ec = ECDsa.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint { X = Base64Url(jwk["x"].AsString()!), Y = Base64Url(jwk["y"].AsString()!) },
                    }))
                    {
                        return ec.VerifyData(data, signature, HashAlgorithmName.SHA256);
                    }
                default:
                    return false; // "none", HS*, unknown or key/alg mismatch
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or NullReferenceException)
        {
            return false;
        }
    }

    // ---- storage ------------------------------------------------------------------------------------------

    internal sealed record HostRecord(string HostId, string? ClientId);

    internal sealed record TokenRecord(
        string ClientId, string Subject, string? Email, string AccessToken, string RefreshToken,
        DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes, DateTimeOffset SavedAt)
    {
        public override string ToString() => $"ChatGPT tokens for {Subject}";
    }

    internal HostRecord LoadOrCreateHost()
    {
        if (_store.Load(HostKind) is { } cred)
        {
            try
            {
                if (JsonNode.Parse(cred.ApiKey) is JsonObject o && o["ext_agent_host_id"].AsString() is { Length: > 0 } id)
                    return new HostRecord(id, o["client_id"].AsString());
            }
            catch (JsonException) { }
        }
        var host = new HostRecord("urn:uuid:" + Guid.NewGuid().ToString("D"), null);
        SaveHost(host);
        return host;
    }

    void SaveHost(HostRecord host)
    {
        var o = new JsonObject { ["ext_agent_host_id"] = host.HostId };
        if (host.ClientId is not null) o["client_id"] = host.ClientId;
        _store.Save(HostKind, o.ToJsonString(), remember: true);
    }

    internal TokenRecord? LoadTokens()
    {
        if (_store.Load(TokenKind) is not { } cred) return null;
        try
        {
            if (JsonNode.Parse(cred.ApiKey) is not JsonObject o) return null;
            return new TokenRecord(
                o["client_id"].AsString()!, o["sub"].AsString()!, o["email"].AsString(),
                o["access_token"].AsString()!, o["refresh_token"].AsString()!,
                DateTimeOffset.Parse(o["expires_at"].AsString()!, System.Globalization.CultureInfo.InvariantCulture),
                (o["scopes"] as JsonArray ?? []).Select(s => s.AsString()).OfType<string>().ToList(),
                DateTimeOffset.Parse(o["saved_at"].AsString()!, System.Globalization.CultureInfo.InvariantCulture)) is { ClientId: not null, Subject: not null, AccessToken: not null, RefreshToken: not null } t ? t : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentNullException)
        {
            return null;
        }
    }

    void SaveTokens(TokenRecord t)
    {
        var o = new JsonObject
        {
            ["issuer"] = Options.Issuer.ToString().TrimEnd('/'),
            ["client_id"] = t.ClientId,
            ["sub"] = t.Subject,
            ["email"] = t.Email,
            ["access_token"] = t.AccessToken,
            ["refresh_token"] = t.RefreshToken,
            ["expires_at"] = t.ExpiresAt.ToString("O"),
            ["scopes"] = new JsonArray(t.Scopes.Select(s => (JsonNode)JsonValue.Create(s)).ToArray()),
            ["saved_at"] = t.SavedAt.ToString("O"),
        };
        _store.Save(TokenKind, o.ToJsonString(), remember: true);
    }

    TokenRecord? TokensFrom(JsonObject response, string clientId, Dictionary<string, JsonNode?>? claims, IReadOnlyList<string> scopes,
        string? previousRefresh, TokenRecord? previous = null)
    {
        var access = response["access_token"].AsString();
        var refresh = response["refresh_token"].AsString() ?? previousRefresh;
        var sub = claims?["sub"].AsString() ?? previous?.Subject;
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(sub)) return null;
        var expiresIn = response["expires_in"] is JsonValue v && v.TryGetValue(out long secs) ? secs : 3600;
        var now = _time.GetUtcNow();
        var email = claims is not null && claims.TryGetValue("email", out var e) ? e.AsString() : previous?.Email;
        return new TokenRecord(clientId, sub, email, access, refresh, now.AddSeconds(expiresIn), scopes, now);
    }

    static List<string> ReadScopes(string? scope) =>
        (scope ?? "").Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();

    // ---- crypto helpers -----------------------------------------------------------------------------------

    internal static string RandomToken(int bytes) => Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    internal static string Challenge(string verifier) => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string Base64UrlEncode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Base64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b + new string('=', (4 - b.Length % 4) % 4));
    }

    static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>
/// One-shot loopback HTTP listener on 127.0.0.1 with an OS-assigned port. Answers the first GET to
/// /auth/callback with a tiny static page and returns its query; other paths get 404. Bound only while in use.
/// (A raw TcpListener rather than HttpListener: http.sys URL ACLs would otherwise be needed for 127.0.0.1.)
/// </summary>
internal sealed class LoopbackCallback : IDisposable
{
    readonly TcpListener _listener;

    LoopbackCallback(TcpListener listener) => _listener = listener;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static LoopbackCallback Start()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Server.ExclusiveAddressUse = true;
        l.Start(backlog: 4);
        return new LoopbackCallback(l);
    }

    const string Page = "<!doctype html><meta charset=utf-8><title>Optimization Tool</title><p style=\"font:16px system-ui;margin:40px\">You can close this tab.</p>";

    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var stream = client.GetStream();
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(10));
            string? target;
            try
            {
                target = await ReadRequestTargetAsync(stream, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // a stalled connection; keep waiting for the real callback
            }
            catch (IOException)
            {
                continue;
            }

            if (target is null || !target.StartsWith(ChatGptSignIn.CallbackPath, StringComparison.Ordinal) ||
                (target.Length > ChatGptSignIn.CallbackPath.Length && target[ChatGptSignIn.CallbackPath.Length] != '?'))
            {
                await WriteAsync(stream, "404 Not Found", "", ct).ConfigureAwait(false);
                continue;
            }
            await WriteAsync(stream, "200 OK", Page, ct).ConfigureAwait(false);
            var q = target.IndexOf('?');
            return ParseQuery(q < 0 ? "" : target[(q + 1)..]);
        }
    }

    static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var first = (lineEnd < 0 ? text : text[..lineEnd]).Split(' ');
        return first.Length == 3 && first[0] == "GET" ? first[1] : null;
    }

    static async Task WriteAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(k, v);
        }
        return result;
    }

    public void Dispose() => _listener.Stop();
}

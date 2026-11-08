using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers.Tests;

/// <summary>ICredentialStore in memory, with a write log so tests can see what was persisted.</summary>
internal sealed class MemoryStore : ICredentialStore
{
    public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    public bool FailTokenSaves;
    public void Save(string kind, string apiKey, bool remember)
    {
        if (FailTokenSaves && kind == ChatGptSignIn.TokenKind) throw new UnauthorizedAccessException("Access denied.");
        Values[kind] = apiKey;
    }
    public ProviderCredential? Load(string kind) => Values.TryGetValue(kind, out var v) ? new ProviderCredential(kind, v) : null;
    public void Delete(string kind) => Values.Remove(kind);
    public bool Has(string kind) => Values.ContainsKey(kind);
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Answers by URL instead of by order; records every request.</summary>
internal sealed class RouterHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> route) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return route(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A fake OpenAI auth issuer: discovery, JWKS, token endpoint, and a scriptable "browser".</summary>
internal sealed class FakeIssuer : IDisposable
{
    public const string Issuer = "https://auth.openai.com";
    public readonly RSA Key = RSA.Create(2048);
    public readonly RSA OtherKey = RSA.Create(2048);
    public readonly MemoryStore Store = new();
    public readonly ManualTime Time = new(DateTimeOffset.UtcNow);
    public readonly RouterHandler Handler;
    public readonly ChatGptSignIn SignIn;

    public string IssuedClientId = "oaiapp_TEST1";
    public string? LastNonce;
    public Uri? LastAuthorizeUrl;
    public Dictionary<string, string>? LastTokenForm;
    public List<Dictionary<string, string>> TokenForms = [];
    public int RefreshCount;
    public bool OmitJwksUri;

    // Per-test knobs for the ID token and token endpoint.
    public Func<JsonObject, JsonObject> ShapeClaims = c => c;
    public bool SignWithOtherKey;
    public Func<Dictionary<string, string>, HttpResponseMessage?>? TokenOverride;

    public FakeIssuer(ChatGptSignInOptions? options = null)
    {
        Handler = new RouterHandler(Route);
        SignIn = new ChatGptSignIn(Store, new HttpClient(Handler), options ?? new ChatGptSignInOptions(), Time);
    }

    HttpResponseMessage Route(HttpRequestMessage req, string? body)
    {
        var url = req.RequestUri!.ToString();
        if (url == Issuer + "/.well-known/openid-configuration")
        {
            var doc = new JsonObject
            {
                ["issuer"] = Issuer,
                ["token_endpoint"] = Issuer + "/api/accounts/oauth/token",
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            };
            if (!OmitJwksUri) doc["jwks_uri"] = Issuer + "/.well-known/jwks.json";
            return RouterHandler.Json(HttpStatusCode.OK, doc.ToJsonString());
        }
        if (url == Issuer + "/.well-known/jwks.json")
        {
            var p = Key.ExportParameters(false);
            return RouterHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["keys"] = new JsonArray(new JsonObject
                {
                    ["kty"] = "RSA", ["kid"] = "k1", ["use"] = "sig", ["alg"] = "RS256",
                    ["n"] = ChatGptSignIn.Base64UrlEncode(p.Modulus!), ["e"] = ChatGptSignIn.Base64UrlEncode(p.Exponent!),
                }),
            }.ToJsonString());
        }
        if (url == Issuer + "/api/accounts/oauth/token")
        {
            var form = LoopbackCallback.ParseQuery(body ?? "");
            lock (TokenForms) TokenForms.Add(form);
            LastTokenForm = form;
            if (TokenOverride?.Invoke(form) is { } overridden) return overridden;
            if (form["grant_type"] == "refresh_token")
            {
                var n = Interlocked.Increment(ref RefreshCount);
                return RouterHandler.Json(HttpStatusCode.OK, new JsonObject
                {
                    ["access_token"] = $"ACCESS-{n + 1}", ["refresh_token"] = $"REFRESH-{n + 1}", ["expires_in"] = 3600, ["token_type"] = "Bearer",
                }.ToJsonString());
            }
            return RouterHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["access_token"] = "ACCESS-1", ["refresh_token"] = "REFRESH-1", ["expires_in"] = 3600, ["token_type"] = "Bearer",
                ["id_token"] = IdToken(), ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            }.ToJsonString());
        }
        return RouterHandler.Json(HttpStatusCode.NotFound, "{}");
    }

    public string IdToken()
    {
        var claims = ShapeClaims(new JsonObject
        {
            ["iss"] = Issuer, ["aud"] = IssuedClientId, ["sub"] = "user-123", ["email"] = "user@example.com",
            ["exp"] = Time.Now.AddMinutes(10).ToUnixTimeSeconds(), ["iat"] = Time.Now.ToUnixTimeSeconds(), ["nonce"] = LastNonce,
        });
        var header = ChatGptSignIn.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"k1","typ":"JWT"}"""));
        var payload = ChatGptSignIn.Base64UrlEncode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var sig = (SignWithOtherKey ? OtherKey : Key).SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + ChatGptSignIn.Base64UrlEncode(sig);
    }

    /// <summary>A browser that "approves" by calling the loopback redirect with the given parameters.</summary>
    public Func<Uri, bool> Browser(Func<Dictionary<string, string>, string> callbackQuery, bool sendFavicon = false) => url =>
    {
        LastAuthorizeUrl = url;
        var q = LoopbackCallback.ParseQuery(url.Query.TrimStart('?'));
        LastNonce = q["nonce"];
        var redirect = q["redirect_uri"];
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            if (sendFavicon)
            {
                var fav = await http.GetAsync(new Uri(new Uri(redirect), "/favicon.ico"));
                Assert.Equal(HttpStatusCode.NotFound, fav.StatusCode);
            }
            var res = await http.GetAsync(redirect + "?" + callbackQuery(q));
            CallbackPage = await res.Content.ReadAsStringAsync();
        });
        return true;
    };

    public string? CallbackPage;

    public Func<Uri, bool> Approve(bool sendFavicon = false) => Browser(q =>
        $"code=CODE-1&scope=chatgpt.tokens.use.direct+email+offline_access+openid+profile+resource.invoke&state={Uri.EscapeDataString(q["state"])}&client_id={IssuedClientId}", sendFavicon);

    public void Dispose()
    {
        Key.Dispose();
        OtherKey.Dispose();
    }
}

public sealed class ChatGptSignInTests
{
    [Fact]
    public async Task First_sign_in_uses_dynamic_registration_pkce_and_stores_tokens()
    {
        using var f = new FakeIssuer();
        var result = await f.SignIn.SignInAsync(f.Approve(sendFavicon: true), CancellationToken.None);
        Assert.True(result.Ok, result.Error);

        var url = f.LastAuthorizeUrl!;
        Assert.Equal("https://auth.openai.com/api/accounts/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal(f.SignIn.Issuer.Host, url.Host);
        var q = LoopbackCallback.ParseQuery(url.Query.TrimStart('?'));
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("dynamic_agent_client", q["client_id"]);
        Assert.Equal("Optimus", q["agent_name_hint"]);
        Assert.Equal("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", q["scope"]);
        Assert.Equal("https://api.openai.com/v1", q["resource"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/auth/callback$", q["redirect_uri"]);
        Assert.StartsWith("urn:uuid:", q["ext_agent_host_id"]);
        Assert.True(q["state"].Length >= 43 && q["nonce"].Length >= 43);
        Assert.NotEqual(q["state"], q["nonce"]);

        // Token exchange: issued client ID, same redirect_uri and resource, verifier matching the challenge.
        var form = f.LastTokenForm!;
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("oaiapp_TEST1", form["client_id"]);
        Assert.Equal("CODE-1", form["code"]);
        Assert.Equal(q["redirect_uri"], form["redirect_uri"]);
        Assert.Equal("https://api.openai.com/v1", form["resource"]);
        Assert.Equal(q["code_challenge"], ChatGptSignIn.Challenge(form["code_verifier"]));

        Assert.Contains("You can close this tab", f.CallbackPage);
        Assert.True(f.SignIn.IsSignedIn);
        Assert.Equal("user-123", f.SignIn.Account!.Subject);
        Assert.Equal("user@example.com", f.SignIn.Account.Email);
        Assert.Equal("ACCESS-1", (await f.SignIn.CredentialAsync(CancellationToken.None))!.ApiKey);
        Assert.Contains("oaiapp_TEST1", f.Store.Values[ChatGptSignIn.HostKind]);

        // Second sign-in: issued client_id, no name hint, same host id.
        f.SignIn.SignOut();
        Assert.False(f.SignIn.IsSignedIn);
        Assert.True(f.Store.Has(ChatGptSignIn.HostKind));
        Assert.True((await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Ok);
        var q2 = LoopbackCallback.ParseQuery(f.LastAuthorizeUrl!.Query.TrimStart('?'));
        Assert.Equal("oaiapp_TEST1", q2["client_id"]);
        Assert.False(q2.ContainsKey("agent_name_hint"));
        Assert.Equal(q["ext_agent_host_id"], q2["ext_agent_host_id"]);
        Assert.NotEqual(q["state"], q2["state"]);
    }

    [Fact]
    public async Task State_mismatch_is_rejected_and_nothing_is_stored()
    {
        using var f = new FakeIssuer();
        var r = await f.SignIn.SignInAsync(f.Browser(_ => "code=CODE-1&state=forged&client_id=oaiapp_TEST1"), CancellationToken.None);
        Assert.Equal("invalid", r.Error);
        Assert.Null(f.LastTokenForm);
        Assert.False(f.SignIn.IsSignedIn);
    }

    [Fact]
    public async Task User_cancel_is_access_denied()
    {
        using var f = new FakeIssuer();
        var r = await f.SignIn.SignInAsync(f.Browser(q => $"error=access_denied&state={Uri.EscapeDataString(q["state"])}"), CancellationToken.None);
        Assert.Equal(new SignInResult(false, "access_denied"), r);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("audience")]
    [InlineData("issuer")]
    public async Task Invalid_id_tokens_are_rejected(string defect)
    {
        using var f = new FakeIssuer();
        switch (defect)
        {
            case "nonce": f.ShapeClaims = c => { c["nonce"] = "other"; return c; }; break;
            case "signature": f.SignWithOtherKey = true; break;
            case "expired": f.ShapeClaims = c => { c["exp"] = f.Time.Now.AddMinutes(-5).ToUnixTimeSeconds(); return c; }; break;
            case "audience": f.ShapeClaims = c => { c["aud"] = "oaiapp_SOMEONE_ELSE"; return c; }; break;
            case "issuer": f.ShapeClaims = c => { c["iss"] = "https://evil.example"; return c; }; break;
        }
        var r = await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None);
        Assert.Equal("invalid", r.Error);
        Assert.False(f.SignIn.IsSignedIn);
        Assert.DoesNotContain("oaiapp", f.Store.Values.GetValueOrDefault(ChatGptSignIn.HostKind) ?? ""); // client id not kept
    }

    [Fact]
    public async Task Different_client_id_on_a_later_sign_in_is_rejected()
    {
        using var f = new FakeIssuer();
        Assert.True((await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Ok);
        f.IssuedClientId = "oaiapp_OTHER";
        var r = await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None);
        Assert.Equal("invalid", r.Error);
    }

    [Fact]
    public async Task Missing_plan_scope_or_403_is_not_eligible()
    {
        using var f = new FakeIssuer();
        f.TokenOverride = form => RouterHandler.Json(HttpStatusCode.Forbidden, """{"error":"subscription_sharing_user_not_eligible"}""");
        Assert.Equal("not_eligible", (await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Error);

        using var g = new FakeIssuer();
        g.TokenOverride = form => RouterHandler.Json(HttpStatusCode.OK, new JsonObject
        {
            ["access_token"] = "A", ["refresh_token"] = "R", ["expires_in"] = 3600, ["id_token"] = g.IdToken(), ["scope"] = "openid email",
        }.ToJsonString());
        Assert.Equal("not_eligible", (await g.SignIn.SignInAsync(g.Approve(), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Timeout_browser_refusal_and_discovery_without_jwks()
    {
        using var f = new FakeIssuer(new ChatGptSignInOptions { SignInTimeout = TimeSpan.FromMilliseconds(300) });
        Assert.Equal("timeout", (await f.SignIn.SignInAsync(_ => true, CancellationToken.None)).Error);
        Assert.Equal("browser", (await f.SignIn.SignInAsync(_ => false, CancellationToken.None)).Error);

        using var g = new FakeIssuer { OmitJwksUri = true };
        var opened = false;
        Assert.Equal("invalid", (await g.SignIn.SignInAsync(_ => opened = true, CancellationToken.None)).Error);
        Assert.False(opened); // discovery is checked before the browser opens
    }

    [Fact]
    public async Task Tokens_that_cannot_be_saved_are_storage_and_the_default_wait_is_three_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), new ChatGptSignInOptions().SignInTimeout);
        using var f = new FakeIssuer();
        f.Store.FailTokenSaves = true;
        Assert.Equal("storage", (await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Error);
        Assert.False(f.SignIn.IsSignedIn);
    }

    [Fact]
    public async Task Network_failure_is_network()
    {
        var store = new MemoryStore();
        var handler = new RouterHandler((_, _) => throw new HttpRequestException("down"));
        var signIn = new ChatGptSignIn(store, new HttpClient(handler));
        Assert.Equal("network", (await signIn.SignInAsync(_ => true, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Refresh_rotates_and_persists_and_runs_once_for_concurrent_callers()
    {
        using var f = new FakeIssuer();
        Assert.True((await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Ok);

        // Still valid: no refresh.
        Assert.Equal("ACCESS-1", (await f.SignIn.GetAccessTokenAsync()).ApiKey);
        Assert.Equal(0, f.RefreshCount);

        f.Time.Now = f.Time.Now.AddMinutes(59); // within the 2-minute skew
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => f.SignIn.GetAccessTokenAsync()));
        Assert.All(results, r => Assert.Equal("ACCESS-2", r.ApiKey));
        Assert.Equal(1, f.RefreshCount);
        var form = f.LastTokenForm!;
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("REFRESH-1", form["refresh_token"]);
        Assert.Equal("oaiapp_TEST1", form["client_id"]);

        // Rotation persisted: a new instance over the same store sees REFRESH-2 and uses it next.
        var again = new ChatGptSignIn(f.Store, new HttpClient(f.Handler), null, f.Time);
        f.Time.Now = f.Time.Now.AddHours(2);
        Assert.Equal("ACCESS-3", (await again.GetAccessTokenAsync()).ApiKey);
        Assert.Equal("REFRESH-2", f.LastTokenForm!["refresh_token"]);
        Assert.Contains("REFRESH-3", f.Store.Values[ChatGptSignIn.TokenKind]);
    }

    [Fact]
    public async Task Terminal_refresh_error_clears_tokens_but_network_error_keeps_them()
    {
        using var f = new FakeIssuer();
        Assert.True((await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Ok);
        f.Time.Now = f.Time.Now.AddHours(2);

        f.TokenOverride = _ => throw new HttpRequestException("down");
        var net = await Assert.ThrowsAsync<ProviderException>(() => f.SignIn.GetAccessTokenAsync());
        Assert.Equal("network", net.Code);
        Assert.True(f.SignIn.IsSignedIn);

        f.TokenOverride = _ => RouterHandler.Json(HttpStatusCode.BadRequest, """{"error":"refresh_token_reused"}""");
        var dead = await Assert.ThrowsAsync<ProviderException>(() => f.SignIn.GetAccessTokenAsync());
        Assert.Equal("auth", dead.Code);
        Assert.False(f.SignIn.IsSignedIn);
        Assert.True(f.Store.Has(ChatGptSignIn.HostKind)); // registration kept
        Assert.Null(await f.SignIn.CredentialAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tokens_never_appear_in_errors_or_results()
    {
        using var f = new FakeIssuer();
        Assert.True((await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Ok);
        f.Time.Now = f.Time.Now.AddHours(2);
        f.TokenOverride = form => RouterHandler.Json(HttpStatusCode.BadRequest, $$$"""{"error":"weird {{{form["refresh_token"]}}}"}""");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => f.SignIn.GetAccessTokenAsync());
        Assert.DoesNotContain("REFRESH", ex.ToString());
        Assert.DoesNotContain("ACCESS", ex.ToString());
        Assert.DoesNotContain("ACCESS", (await Task.FromResult(new ProviderCredential("chatgpt_plan", "ACCESS-9"))).ToString());
    }
}

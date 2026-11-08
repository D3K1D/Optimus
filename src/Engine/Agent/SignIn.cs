namespace OptimizationTool.Engine.Agent;

// Browser sign-in seam for provider kinds that use an account instead of an API key (contracts/onboarding.md §4,
// docs/chatgpt-signin.md). Implemented in src/Providers (ChatGPT plan: PKCE loopback sign-in, DPAPI token storage,
// refresh). The gateway never sees or stores tokens except as a short-lived ProviderCredential for one turn.

/// <summary>Result of a sign-in. Error: access_denied | not_eligible | network | timeout (the browser never came back) | browser | storage (tokens could not be saved) | invalid.</summary>
public sealed record SignInResult(bool Ok, string? Error = null);

/// <summary>One provider kind's account sign-in (e.g. "chatgpt_plan").</summary>
public interface IProviderSignIn
{
    string Kind { get; }

    /// <summary>The issuer the authorization URL must belong to (scheme https, same host). The host refuses to open anything else.</summary>
    Uri Issuer { get; }

    /// <summary>True when a usable (or refreshable) sign-in is stored.</summary>
    bool IsSignedIn { get; }

    /// <summary>
    /// Runs the whole sign-in: starts the loopback listener, asks <paramref name="openBrowser"/> to open the exact
    /// authorization URL it built (false = refused, abort), waits for the callback, exchanges and stores tokens.
    /// </summary>
    Task<SignInResult> SignInAsync(Func<Uri, bool> openBrowser, CancellationToken ct);

    /// <summary>A fresh credential for one call (refreshing if needed), or null when signed out. Never logged.</summary>
    Task<ProviderCredential?> CredentialAsync(CancellationToken ct);

    /// <summary>Deletes the stored sign-in (tokens). Keeps the non-secret client registration.</summary>
    void SignOut();
}

/// <summary>
/// Optional for adapters: a cheap feature test of tool calling for this account and model. When it reports false the
/// provider entry carries capabilities {tools:false} and tasks for it are explain-only (report review).
/// </summary>
public interface IToolCapability
{
    /// <param name="model">The model the account will use (the one picked by the check).</param>
    Task<bool> SupportsToolsAsync(ProviderCredential credential, string model, CancellationToken ct);
}

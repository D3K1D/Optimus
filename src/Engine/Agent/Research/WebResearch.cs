namespace OptimizationTool.Engine.Agent;

// M9 §6.3 research call seam (contracts/m9-ai-powers.md). One call researches ONE subject with the fixed prompt research-v1
// and nothing else: no PC tools, no chat history, no session resume, no user customisations. The adapter declares only
// its provider's web search tool, returns the model's text as it came, the sources the PROVIDER observed (search results,
// provider citations: never a URL the model merely wrote) and usage. The host parses the text and applies its own rules.

/// <summary>
/// One research call: the model, a fixed system prompt (research-v1 for one subject, checkup-v2 for one check-up area,
/// m9 §6.8), the data the call is about (JSON) and the caps. <paramref name="MaxSearches"/> is passed to providers that
/// can cap their searches; <paramref name="KillAboveSearches"/> (0: none) is enforced by runners that cannot cap them but
/// see each search as it happens (Claude Code's stream, the ChatGPT plan stream): above it the call is killed.
/// </summary>
public sealed record ResearchCall(string Model, string System, string Subject, int MaxSearches, int MaxOutputTokens, TimeSpan Timeout, int KillAboveSearches = 0);

/// <summary>A URL the provider itself returned (a search result, an opened page or a citation), with its title when given.</summary>
public sealed record ObservedSource(string Url, string? Title);

/// <summary>
/// What one call produced. <paramref name="Text"/>: the model's answer text only (never tool-result content).
/// <paramref name="Observed"/>: provider-observed sources, in the order they appeared. <paramref name="Searches"/>: how many
/// searches the provider ran.
/// </summary>
public sealed record ResearchAnswer(string? Text, IReadOnlyList<ObservedSource> Observed, int Searches, ProviderUsage Usage);

/// <summary>
/// An in-app adapter (or later a headless runner) that can run one research call. Throws <see cref="ProviderException"/>
/// for auth, quota, network, rate limits and timeouts (availability unchanged) and <see cref="WebResearchUnsupportedException"/>
/// when the provider refuses the search tool or exposes no results (availability → "no").
/// </summary>
public interface IWebResearch
{
    Task<ResearchAnswer> ResearchAsync(ProviderCredential credential, ResearchCall call, CancellationToken ct);
}

/// <summary>The provider refused web research for this model (the tool or flag was rejected, or it exposed no results).</summary>
public sealed class WebResearchUnsupportedException(string reason) : Exception($"Web research is not available ({reason}).")
{
    /// <summary>tool_rejected | no_results_exposed | not_available_yet.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Reads a file's identity locally (m9 §6.1): the Authenticode state of the file the evidence names (offline, no
/// revocation check) and, only for a validly signed file, its signer and product name. Implemented in src/Windows.
/// </summary>
public interface IFileIdentity
{
    /// <param name="evidencePath">The normalized image path as evidence stores it (e.g. "%ProgramFiles%\App\app.exe").</param>
    FileIdentityInfo Read(string evidencePath);
}

/// <summary>signed: valid | none | unknown. Publisher and Product only when signed is valid.</summary>
public sealed record FileIdentityInfo(string Signed, string? Publisher, string? Product)
{
    public static readonly FileIdentityInfo Unknown = new("unknown", null, null);
}

using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>The in-app providers by kind. The Host builds one with <see cref="CreateDefault"/> and disposes it on exit.</summary>
public sealed class ProviderRegistry : IDisposable
{
    readonly Dictionary<string, IProviderAdapter> _byKind;
    readonly HttpClient? _ownedClient;

    public ProviderRegistry(IEnumerable<IProviderAdapter> adapters, ChatGptSignIn? chatGpt = null) : this(adapters, chatGpt, ownedClient: null) { }

    ProviderRegistry(IEnumerable<IProviderAdapter> adapters, ChatGptSignIn? chatGpt, HttpClient? ownedClient)
    {
        _byKind = adapters.ToDictionary(a => a.Kind, StringComparer.Ordinal);
        ChatGpt = chatGpt;
        _ownedClient = ownedClient;
    }

    /// <summary>
    /// Anthropic, OpenAI and ChatGPT-plan adapters. The plan adapter refreshes tokens itself when given <paramref name="chatGpt"/>
    /// (or a <paramref name="store"/> to build one from);
    /// all share one HttpClient (owned by the registry unless <paramref name="http"/> is given).
    /// </summary>
    public static ProviderRegistry CreateDefault(HttpClient? http = null, ICredentialStore? store = null, ChatGptSignIn? chatGpt = null)
    {
        var client = http ?? ProviderHttp.CreateClient();
        chatGpt ??= store is null ? null : new ChatGptSignIn(store, client);
        // Without a sign-in the plan adapter uses the credential the gateway passes (from IProviderSignIn.CredentialAsync).
        var adapters = new List<IProviderAdapter> { new AnthropicAdapter(client), new OpenAiAdapter(client), new ChatGptPlanAdapter(client, chatGpt) };
        return new ProviderRegistry(adapters, chatGpt, http is null ? client : null);
    }

    /// <summary>ChatGPT sign-in for kind "chatgpt_plan" (null when built without a credential store).</summary>
    public ChatGptSignIn? ChatGpt { get; }

    public IReadOnlyCollection<IProviderAdapter> All => _byKind.Values;

    public IReadOnlyCollection<string> Kinds => _byKind.Keys;

    public bool TryGet(string kind, out IProviderAdapter adapter) => _byKind.TryGetValue(kind, out adapter!);

    public IProviderAdapter Get(string kind) =>
        _byKind.TryGetValue(kind, out var a) ? a : throw new KeyNotFoundException($"No provider adapter for kind '{kind}'.");

    public string? DefaultModel(string kind) => _byKind.TryGetValue(kind, out var a) ? a.DefaultModel : null;

    /// <summary>Reads the retry-after hint (seconds) an adapter attached to a rate_limited / overloaded error, if any.</summary>
    public static int? RetryAfterSeconds(ProviderException e) =>
        e.Data[ProviderHttp.RetryAfterSecondsKey] is int s ? s : null;

    public void Dispose() => _ownedClient?.Dispose();
}

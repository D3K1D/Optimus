using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M3 seam between the agent gateway (src/Engine/Agent) and the in-app model adapters (src/Providers).
// See contracts/m3-agent.md. The gateway owns the loop, budgets, grants and tool dispatch; an adapter only
// translates one model turn to and from its provider's wire format. Adapters never see Windows or the store.

/// <summary>One in-app model provider: Anthropic Messages or OpenAI Responses.</summary>
public interface IProviderAdapter
{
    /// <summary>"anthropic_api" or "openai_api".</summary>
    string Kind { get; }
    string DisplayName { get; }
    string DefaultModel { get; }

    /// <summary>Validates the key without a billable call (e.g. list models). Never logs or echoes the key.</summary>
    Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct);

    /// <summary>Runs exactly one complete (non-streamed or fully drained) model turn. Throws <see cref="ProviderException"/>.</summary>
    Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct);
}

/// <summary>A key held in memory only for the duration of a call. ToString never reveals it.</summary>
public sealed class ProviderCredential(string kind, string apiKey)
{
    public string Kind { get; } = kind;
    public string ApiKey { get; } = apiKey;
    public override string ToString() => $"{Kind} credential";
}

/// <summary>
/// The result of a key / sign-in check. <paramref name="Code"/>: the <see cref="ProviderException"/> code of a failure
/// (auth | quota | rate_limited | overloaded | network | timeout | not_eligible | incomplete | bad_request); null with
/// Ok=false means auth. <paramref name="Model"/>: the model this account should use, picked from what it lists (null:
/// the adapter's default). Error is free text for logs only and is never forwarded.
/// </summary>
public sealed record ProviderCheck(bool Ok, string? Account, IReadOnlyList<string> Models, string? Error, string? Code = null, string? Model = null);

/// <summary>A tool the model may call. Schema is JSON Schema for the arguments object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

public sealed record ProviderRequest(string Model, string System, IReadOnlyList<ProviderMessage> Messages, IReadOnlyList<ToolSpec> Tools, int MaxOutputTokens);

/// <summary>Provider-neutral transcript. The gateway appends; adapters translate the whole list each turn.</summary>
public abstract record ProviderMessage;

public sealed record UserText(string Text) : ProviderMessage;

/// <summary>
/// The model's turn. <see cref="ProviderState"/> keeps provider-required items verbatim (Anthropic content blocks,
/// OpenAI output items incl. reasoning) so the next request can replay them exactly.
/// </summary>
public sealed record AssistantTurn(string? Text, IReadOnlyList<ToolCall> Calls, JsonNode? ProviderState) : ProviderMessage;

/// <summary>Results for every call of the previous assistant turn, in the same order, matched by call ID.</summary>
public sealed record ToolResults(IReadOnlyList<ToolResult> Results) : ProviderMessage;

public sealed record ToolCall(string CallId, string Name, JsonObject Arguments);

public sealed record ToolResult(string CallId, JsonNode Content, bool IsError);

/// <summary>stopReason: end | tool_use | max_tokens | refusal | other.</summary>
public sealed record ProviderTurn(AssistantTurn Turn, string StopReason, ProviderUsage Usage);

public sealed record ProviderUsage(int InputTokens, int OutputTokens);

/// <summary>code: auth | quota | rate_limited | overloaded | incomplete | network | bad_request | timeout | not_eligible (403: account, workspace, region or route not allowed).</summary>
public sealed class ProviderException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>Per-user secret storage (current-user DPAPI). Implemented in src/Providers.</summary>
public interface ICredentialStore
{
    /// <summary>remember=false keeps the key in memory for this host process only.</summary>
    void Save(string kind, string apiKey, bool remember);
    ProviderCredential? Load(string kind);
    void Delete(string kind);
    bool Has(string kind);
}

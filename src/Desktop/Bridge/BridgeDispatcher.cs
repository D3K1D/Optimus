using System.Text.Json.Nodes;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>The backend as the bridge sees it.</summary>
public interface IBackend
{
    /// <summary>Returns the raw JSON-RPC response (result or error). Throws <see cref="BackendUnavailableException"/> when there is no ready connection or it dropped mid-call.</summary>
    Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct);
}

public sealed class BackendUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Turns one renderer message into the bridge message to post back (or null). Testable without WebView2.
/// </summary>
public sealed class BridgeDispatcher(BridgeRouter router, IBackend backend, DesktopLog log,
    SettingsLauncher? settings = null, ApiKeyRequest? apiKeys = null, PlanApproval? approvals = null,
    ClientConnector? clients = null, SettingsLauncher? links = null)
{
    public const int MaxInFlight = 64;
    private readonly SettingsLauncher _settings = settings ?? new SettingsLauncher();
    private readonly SettingsLauncher _links = links ?? SettingsLauncher.ForWebLinks();
    private int _inFlight;

    public async Task<string?> HandleAsync(string? json, string? source, bool isTopLevel, CancellationToken ct)
    {
        var decision = router.Evaluate(json, source, isTopLevel);
        switch (decision)
        {
            case BridgeDecision.Ignore ignore:
                log.Warn($"bridge: ignored message ({ignore.Reason})");
                return null;
            case BridgeDecision.Reply reply:
                log.Warn($"bridge: rejected message ({reply.Reason})");
                return reply.Json;
            case BridgeDecision.Shell shell when shell.Method == SettingsLauncher.Method:
                return _settings.Handle(shell.Id, shell.Params, log);
            case BridgeDecision.Shell shell when shell.Method == SettingsLauncher.UrlMethod:
                return _links.Handle(shell.Id, shell.Params, log);
            case BridgeDecision.Shell shell when shell.Method == ApiKeyRequest.Method:
                return apiKeys is null
                    ? BridgeMessages.Error(shell.Id, -32013, "Not available.", new JsonObject { ["reason"] = "unavailable" })
                    : await apiKeys.HandleAsync(shell.Id, shell.Params, backend, log, ct);
            case BridgeDecision.Shell shell when shell.Method == PlanApproval.Method:
                return approvals is null
                    ? BridgeMessages.Error(shell.Id, -32013, "Not available.", new JsonObject { ["reason"] = "unavailable" })
                    : await approvals.HandleAsync(shell.Id, shell.Params, backend, log, ct);
            case BridgeDecision.Shell shell when ClientConnector.Methods.Contains(shell.Method):
                return clients is null
                    ? BridgeMessages.Error(shell.Id, -32013, "Not available.", new JsonObject { ["reason"] = "unavailable" })
                    : await clients.HandleAsync(shell.Method, shell.Id, shell.Params, log, ct, backend);
            case BridgeDecision.Forward fwd:
                return await ForwardAsync(fwd, ct);
            default:
                return null;
        }
    }

    private async Task<string> ForwardAsync(BridgeDecision.Forward fwd, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
        {
            Interlocked.Decrement(ref _inFlight);
            log.Warn($"bridge: too many calls in flight, refused {fwd.Method}");
            return BridgeMessages.Unavailable(fwd.Id);
        }
        try
        {
            var response = await backend.CallAsync(fwd.Method, fwd.Params, ct);
            return BridgeMessages.FromResponse(fwd.Id, response);
        }
        catch (BackendUnavailableException)
        {
            log.Info($"bridge: {fwd.Method} id={fwd.Id.ToJsonString()} failed, backend unavailable (retryable)");
            return BridgeMessages.Unavailable(fwd.Id);
        }
        catch (OperationCanceledException)
        {
            return BridgeMessages.Unavailable(fwd.Id);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

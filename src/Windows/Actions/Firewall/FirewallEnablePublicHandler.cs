using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Firewall;

/// <summary>
/// <c>firewall.enable_public</c> (contracts/m4-actions.md §2). Reads and plans in the host as the user; the write
/// happens only in the elevated broker through <see cref="IElevation"/>, which re-resolves the plan itself.
/// Gated by <see cref="ActionContext.EnableFirewallHandler"/>: when off, every resolve is ineligible.
/// </summary>
public sealed class FirewallEnablePublicHandler(ActionContext context, IPublicFirewallReader? reader = null) : IActionHandler
{
    private readonly IPublicFirewallReader _reader = reader ?? new WindowsPublicFirewallReader();

    public string ActionId => FirewallPlan.ActionId;
    public string Version => "1.0.0";
    public string Label => "Turn on firewall for public networks";
    public string Privilege => "admin";
    public bool SupportsUndo => true;
    public string Kind => "setting";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
    {
        if (!context.EnableFirewallHandler)
            return Task.FromResult(FirewallPlan.Resolve(Unread, "enable", handlerEnabled: false));
        if (request.ActionId != ActionId) return Task.FromResult(FirewallPlan.Ineligible("bad_request", "This change isn't supported."));
        var mode = FirewallPlan.Mode(request.Parameters);
        if (mode is null || !FirewallPlan.ValidTarget(request.Target))
            return Task.FromResult(FirewallPlan.Ineligible("bad_request", "This change isn't supported."));
        return Task.Run(() => FirewallPlan.Resolve(_reader.Read(ct), mode, handlerEnabled: true, request.Target), ct);
    }

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct)
    {
        if (!FirewallPlan.ValidTarget(target)) throw new ArgumentException("Unsupported firewall target.", nameof(target));
        return Task.Run(() =>
        {
            var s = _reader.Read(ct);
            return FirewallPlan.State(s.LocalEnabled, s.EffectiveEnabled);
        }, ct);
    }

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        if (!context.EnableFirewallHandler)
            return new ActionOutcome("failed", execution.Before.DeepClone().AsObject(), "Nothing changed.", "disabled_pending_review",
                "Turning the firewall on from this app isn't available yet.");
        if (FirewallPlan.Mode(execution.Parameters) is null || !FirewallPlan.ValidTarget(execution.Target))
            return new ActionOutcome("failed", execution.Before.DeepClone().AsObject(), "Nothing changed.", "bad_request", "This change isn't supported.");
        progress?.Report("Waiting for Windows permission");
        // The only write path: the one-shot elevated broker (it re-resolves and refuses if the plan drifted).
        return await context.Elevation.RunAsync(ActionId, execution, ct).ConfigureAwait(false);
    }

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (!FirewallPlan.ValidTarget(target)) return null;
        var local = FirewallPlan.Bool(before["publicProfile"]?["localEnabled"]);
        // Undo restores Before: an enable is undone by a fresh "disable" plan (with its warning row), and vice versa.
        return local switch
        {
            false => new ActionRequest(ActionId, FirewallPlan.Target(), new JsonObject { ["mode"] = "disable" }, null),
            true => new ActionRequest(ActionId, FirewallPlan.Target(), new JsonObject { ["mode"] = "enable" }, null),
            _ => null,
        };
    }

    private static readonly PublicFirewallSnapshot Unread = new(null, null, null, false, false, null, false, false, false);
}

/// <summary>Security domain's M4 handlers (registered in WindowsModules.Actions).</summary>
public sealed class FirewallActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) => [new FirewallEnablePublicHandler(context)];
}

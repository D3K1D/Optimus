using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Firewall;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.RestorePoint;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Startup;
using ProtectionResult = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Windows.Actions.Broker;

/// <summary>The broker's own record of one step it ran (its protected journal): what an undo must name (m9 §3.5).</summary>
public sealed record BrokerRecordStep(string ReceiptId, string PlanId, int N, string ActionId, JsonObject Target, JsonObject Before, JsonObject After,
    string? State, JsonObject? Observed);

/// <summary>
/// One administrator action as the elevated broker runs it (m9 §3.5). Compiled into the broker; it shares its resolve and
/// write code with the host-side handler of the same actionId and version. No shell, no generic registry or file API:
/// each handler writes only what its own typed resolution says.
/// </summary>
public interface IBrokerHandler
{
    string ActionId { get; }
    /// <summary>Equals the host handler's Version; a request for another version is refused.</summary>
    string Version { get; }
    /// <summary>The broker window's step title (never host text).</summary>
    string Label { get; }
    /// <summary>The host handler's own consequence flags (the window's badges add each resolution's).</summary>
    IReadOnlyList<string> Flags { get; }

    /// <summary>The step restores a recorded preimage (its own parameters or target say so): it needs the broker's record of the original.</summary>
    bool IsUndo(BrokerStepRequest step);

    /// <summary>
    /// Re-resolves the step from live state with the shared plan code: target, before, after, rows and flags. Only an
    /// administrator-scope target is eligible here (a user-scope step never runs elevated).
    /// </summary>
    ActionResolution Resolve(BrokerStepRequest step);

    /// <summary>The broker's own protected and hardware lists for this resolution (second line), or null.</summary>
    ProtectionResult? Protect(ActionResolution resolution);

    /// <summary>Writes exactly the resolution's After, then reads back with the same code: verified | completed | failed | indeterminate.</summary>
    ActionOutcome WriteAndReadBack(ActionResolution resolution, BrokerStepRequest step, CancellationToken ct);

    /// <summary>
    /// A long step's own deadline (a repair tool); null for a setting step, which the session's watchdog bounds at 45 s
    /// (m9 §3.5). The session's budget and window use it.
    /// </summary>
    TimeSpan? Deadline => null;

    /// <summary>
    /// Runs the step with the session's context: the plan, a progress channel to the host (long tools) and the stop signal.
    /// Setting handlers just write and read back.
    /// </summary>
    ActionOutcome Run(ActionResolution resolution, BrokerStepRequest step, BrokerRun run) => WriteAndReadBack(resolution, step, run.Cancel);
}

/// <summary>What the session gives a running step: its plan, a progress line channel (sent as progress frames) and the stop signal.</summary>
public sealed record BrokerRun(string PlanId, IProgress<string> Progress, CancellationToken Cancel);

/// <summary>The administrator actionIds, identical in host and broker (asserted by test).</summary>
public static class BrokerCatalog
{
    public static readonly IReadOnlyList<string> ActionIds =
    [
        FirewallPlan.ActionId, SetSettingHandler.Id, StartupDisableHandler.Id, RestorePointPlan.ActionId,
        Services.ServiceDisableHandler.Id, Tasks.TaskDisableHandler.Id, BlockAppHandler.Id, Repair.RepairKind.Sfc.ActionId, Repair.RepairKind.Dism.ActionId,
    ];
}

/// <summary>The outcome of a write the broker refused or could not read back.</summary>
internal static class BrokerOutcomes
{
    public static ActionOutcome Classify(JsonObject observed, ActionResolution r, string verifiedText, string failedText, string otherText, string? writeError) =>
        JsonNode.DeepEquals(observed, r.After) ? new ActionOutcome("verified", observed, verifiedText)
        : JsonNode.DeepEquals(observed, r.Before) ? new ActionOutcome("failed", observed, failedText, writeError is null ? "not_applied" : "write_failed", writeError)
        : new ActionOutcome("indeterminate", observed, otherText, "unexpected_state", writeError);
}

/// <summary>firewall.enable_public in the broker: the Public profile's EnableFirewall in the local store, nothing else.</summary>
public sealed class FirewallBrokerHandler(IPublicFirewallReader reader, IPublicFirewallWriter writer) : IBrokerHandler
{
    public string ActionId => FirewallPlan.ActionId;
    public string Version => FirewallPlan.HandlerVersion;
    public string Label => FirewallPlan.Label;
    public IReadOnlyList<string> Flags => [ActionFlags.Network];

    public bool IsUndo(BrokerStepRequest step) => FirewallPlan.Mode(step.Parameters) == "disable";

    public ActionResolution Resolve(BrokerStepRequest step)
    {
        var mode = FirewallPlan.Mode(step.Parameters);
        if (mode is null || !FirewallPlan.ValidTarget(step.Target)) return FirewallPlan.Ineligible("bad_request", "This change isn't supported.");
        return FirewallPlan.Resolve(reader.Read(CancellationToken.None), mode, handlerEnabled: true, step.Target);
    }

    public ProtectionResult? Protect(ActionResolution resolution) => null;

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct)
    {
        var enable = FirewallPlan.Bool(r.After["publicProfile"]?["localEnabled"]) == true;
        string? error = null;
        try { writer.SetPublicEnabled(enable); }
        catch (Exception ex) { error = ex.GetType().Name; }
        var live = reader.Read(CancellationToken.None);
        var observed = FirewallPlan.State(live.LocalEnabled, live.EffectiveEnabled);
        if (FirewallPlan.SameState(observed, r.After))
            return new ActionOutcome("verified", observed, enable ? "Firewall for public networks is on, read back from Windows." : "Firewall for public networks is off, read back from Windows.");
        if (FirewallPlan.SameState(observed, r.Before))
            return new ActionOutcome("failed", observed, "Windows still reports the previous setting. Nothing changed.", error is null ? "not_applied" : "write_error", error);
        return new ActionOutcome("indeterminate", observed, "Windows reported an unexpected firewall state. Check Windows Security.", "unexpected_state", error);
    }
}

/// <summary>setting.set machine-scope rows in the broker (the same SetSettingHandler code; user rows are refused here).</summary>
internal sealed class MachineSettingBrokerHandler(IRegistryStore store, Func<int> osBuild) : IBrokerHandler
{
    private readonly SetSettingHandler _handler = new(store, new NoNotifier(), osBuild);

    public string ActionId => SetSettingHandler.Id;
    public string Version => SetSettingHandler.HandlerVersion;
    public string Label => SetSettingHandler.LabelText;
    public IReadOnlyList<string> Flags => [];

    public bool IsUndo(BrokerStepRequest step) => step.Parameters?["restore"] is not null;

    public ActionResolution Resolve(BrokerStepRequest step)
    {
        var r = _handler.Resolve(new ActionRequest(ActionId, step.Target, step.Parameters, null) { Recheck = true, Undo = IsUndo(step) });
        return r.Eligible && r.Privilege != "admin" ? ActionResolution.Ineligible("not_admin_scope", "This change doesn't need administrator rights.") : r;
    }

    public ProtectionResult? Protect(ActionResolution resolution) => null;

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) =>
        _handler.Execute(new ActionExecution("broker", "broker", r.Target, r.Before, r.After, step.Parameters));

    private sealed class NoNotifier : ISettingNotifier
    {
        public void Notify(string kind) { } // machine rows notify nobody (notify: none); the user's session refreshes itself
    }
}

/// <summary>startup.disable for all-users entries in the broker (the same StartupDisableHandler code; per-user entries are refused).</summary>
internal sealed class MachineStartupBrokerHandler(IRegistryStore store, IStartupFiles files, ISecurityProducts? products = null) : IBrokerHandler
{
    private readonly StartupDisableHandler _handler = new(store, files, products: products);

    public string ActionId => StartupDisableHandler.Id;
    public string Version => StartupDisableHandler.HandlerVersion;
    public string Label => StartupDisableHandler.LabelText;
    public IReadOnlyList<string> Flags => [];

    public bool IsUndo(BrokerStepRequest step) => step.Target["expect"] is not null;

    public ActionResolution Resolve(BrokerStepRequest step)
    {
        // The host's publisher is never trusted here: the broker's own lists use the entry's name and image only.
        var target = (JsonObject)step.Target.DeepClone();
        target.Remove("publisher");
        var r = _handler.Resolve(new ActionRequest(ActionId, target, step.Parameters, null) { Recheck = true, Undo = IsUndo(step) });
        return r.Eligible && r.Privilege != "admin" ? ActionResolution.Ineligible("not_admin_scope", "This change doesn't need administrator rights.") : r;
    }

    /// <summary>
    /// The broker's own lists: the entry's name and image, and the image's publisher read by the broker itself from the file's
    /// signature (m9 §3.7 gap closed: the host's publisher is never used here).
    /// </summary>
    public ProtectionResult? Protect(ActionResolution r)
    {
        var image = r.Before["fingerprint"]?["image"]?.GetValue<string>();
        string? publisher = null;
        if (image is not null)
            try { publisher = files.Publisher(image); } catch (Exception) { publisher = null; }
        return ProtectedTargets.StartupEntry(r.Target["label"]?.GetValue<string>(), image, publisher, (products ?? NoSecurityProducts.Instance).Names());
    }

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) =>
        _handler.Execute(new ActionExecution("broker", "broker", r.Target, r.Before, r.After, step.Parameters));
}

/// <summary>restore_point.create in the broker: one request to Windows for a restore point; the result says created or reused.</summary>
public sealed class RestorePointBrokerHandler(IRestorePointSystem system) : IBrokerHandler
{
    public string ActionId => RestorePointPlan.ActionId;
    public string Version => RestorePointPlan.HandlerVersion;
    public string Label => RestorePointPlan.Label;
    public IReadOnlyList<string> Flags => [];

    public bool IsUndo(BrokerStepRequest step) => false;

    public ActionResolution Resolve(BrokerStepRequest step) =>
        step.Parameters is { Count: > 0 } || !RestorePointPlan.ValidTarget(step.Target)
            ? ActionResolution.Ineligible("bad_request", "This change isn't supported.")
            : RestorePointPlan.Resolve(system.Unavailable());

    public ProtectionResult? Protect(ActionResolution resolution) => null;

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) => RestorePointPlan.Outcome(system.Create(RestorePointPlan.Description));
}

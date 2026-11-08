using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Tests;

/// <summary>The fake PC the fake handlers act on. Nothing here touches real Windows settings (m4 testing rule).</summary>
public sealed class FakePc
{
    public volatile string Scheme = "high";
    public int Writes;
    public int ProviderRuns;
    public volatile string LastScan = "2026-11-01T00:00:00Z";
    public Func<ActionOutcome?>? Override;
    public TimeSpan WriteDelay;
    public TaskCompletionSource ProviderGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A user-scope setting handler shaped like power.select_scheme.</summary>
public sealed class FakeSchemeHandler(FakePc pc) : IActionHandler
{
    public string ActionId => "power.select_scheme";
    public string Version => "1.0.0";
    public string Label => "Change power plan";
    public string Privilege => "none";
    public bool SupportsUndo => true;
    public string Kind => "setting";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
    {
        var desired = request.Parameters?["scheme"]?.GetValue<string>() ?? (request.FindingSubject is not null ? "balanced" : null);
        if (desired is null) return Task.FromResult(ActionResolution.Ineligible("no_target", "Choose a plan."));
        if (desired == pc.Scheme) return Task.FromResult(ActionResolution.Ineligible("already_active", "That plan is already active."));
        var target = new JsonObject { ["type"] = "power_scheme", ["id"] = "active", ["label"] = "Active plan" };
        return Task.FromResult(new ActionResolution(true, target,
            new JsonObject { ["scheme"] = pc.Scheme }, new JsonObject { ["scheme"] = desired },
            [new PlanRow("Active power plan", pc.Scheme, desired)], [new("Power", "On battery · 64%")], ["Slightly lower peak speed"], $"Only while {desired} is still active"));
    }

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["scheme"] = pc.Scheme });

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        if (pc.WriteDelay > TimeSpan.Zero) await Task.Delay(pc.WriteDelay, ct);
        Interlocked.Increment(ref pc.Writes);
        if (pc.Override?.Invoke() is { } forced) return forced;
        pc.Scheme = execution.After["scheme"]!.GetValue<string>();
        return new ActionOutcome("verified", new JsonObject { ["scheme"] = pc.Scheme }, $"{pc.Scheme} is active, read back.");
    }

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) =>
        new(ActionId, target, new JsonObject { ["scheme"] = before["scheme"]!.DeepClone() }, null);
}

/// <summary>A provider operation shaped like defender.quick_scan: progress lines, waits on a gate, then completed.</summary>
public sealed class FakeScanHandler(FakePc pc) : IActionHandler
{
    public string ActionId => "defender.quick_scan";
    public string Version => "1.0.0";
    public string Label => "Run a quick scan";
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "provider";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "defender", ["id"] = "scan" },
            new JsonObject { ["lastScan"] = pc.LastScan }, new JsonObject { ["scan"] = "quick" },
            [new PlanRow("Scan", "Last: " + pc.LastScan, "Quick scan")], [], ["Uses some CPU for a few minutes"], null));

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["lastScan"] = pc.LastScan });

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref pc.ProviderRuns);
        progress?.Report("Scanning · quick");
        await pc.ProviderGate.Task.WaitAsync(ct);
        progress?.Report("Finishing");
        pc.LastScan = "2026-11-04T00:00:00Z";
        return new ActionOutcome("completed", new JsonObject { ["result"] = "no_threats" }, "Quick scan finished. No threats found.");
    }

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
}

/// <summary>A machine-scope handler shaped like firewall.enable_public: it only writes through IElevation.</summary>
public sealed class FakeFirewallHandler(IElevation elevation) : IActionHandler
{
    public string ActionId => "firewall.enable_public";
    public string Version => "1.0.0";
    public string Label => "Turn on the Public firewall";
    public string Privilege => "admin";
    public bool SupportsUndo => true;
    public string Kind => "setting";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "firewall_profile", ["id"] = "public" },
            new JsonObject { ["enabled"] = false }, new JsonObject { ["enabled"] = true }, [new PlanRow("Public firewall", "Off", "On")], [], [], null));

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["enabled"] = false });
    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => elevation.RunAsync(ActionId, execution, ct);
    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
}

/// <summary>An elevation route whose UAC prompt the user cancels.</summary>
public sealed class CancelledUac : IElevation
{
    public int Calls;
    public Task<ActionOutcome> RunAsync(string actionId, ActionExecution execution, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ActionOutcome("declined", [], "Approval was cancelled.", "uac_cancelled", "UAC cancelled."));
    }
}

public sealed class FakeActionModule(params IActionHandler[] handlers) : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) => handlers;
}

public static class Act
{
    public static Task<JsonObject> Preview(Client c, string actionId = "power.select_scheme", string? scheme = "balanced", string? findingId = null, string? key = null)
    {
        var p = new JsonObject { ["idempotencyKey"] = key ?? Guid.NewGuid().ToString(), ["actionId"] = actionId };
        if (scheme is not null) p["parameters"] = new JsonObject { ["scheme"] = scheme };
        if (findingId is not null) p["findingId"] = findingId;
        return c.Call("action.preview", p);
    }

    public static async Task<string> Nonce(Client c, string planId) => (await c.Call("action.challenge", new JsonObject { ["planId"] = planId })).Str("nonce");

    public static JsonObject ApproveParams(JsonObject plan, string nonce) => new()
    {
        ["planId"] = plan.Str("planId"), ["planHash"] = plan.Str("planHash"), ["nonce"] = nonce,
    };

    public static async Task<JsonObject> Approve(Client c, JsonObject plan) =>
        await c.Call("action.approve", ApproveParams(plan, await Nonce(c, plan.Str("planId"))));
}

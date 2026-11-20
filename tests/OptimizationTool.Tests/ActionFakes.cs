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
    /// <summary>What the scheme handler's resolution adds (m9 §3.1): changing one after a preview must make the plan stale.</summary>
    public volatile string[]? SchemeFlags;
    public volatile string? SchemePrivilege;
    public volatile string[] SchemeResources = ["power:ACTIVE"];
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
    public string Group => "power";
    public string What => "Switches Windows to another installed power plan.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => true;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
    {
        var desired = request.Parameters?["scheme"]?.GetValue<string>() ?? (request.FindingSubject is not null ? "balanced" : null);
        if (desired is null) return Task.FromResult(ActionResolution.Ineligible("no_target", "Choose a plan."));
        if (desired == pc.Scheme) return Task.FromResult(ActionResolution.Ineligible("already_active", "That plan is already active."));
        var target = new JsonObject { ["type"] = "power_scheme", ["id"] = "active", ["label"] = "Active plan" };
        return Task.FromResult(new ActionResolution(true, target,
            new JsonObject { ["scheme"] = pc.Scheme }, new JsonObject { ["scheme"] = desired },
            [new PlanRow("Active power plan", pc.Scheme, desired)], [new("Power", "On battery · 64%")], ["Slightly lower peak speed"], $"You can undo this while {desired} is still the active plan.",
            Privilege: pc.SchemePrivilege, Flags: pc.SchemeFlags, Resources: pc.SchemeResources));
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
    public string Group => "security";
    public string What => "Asks Microsoft Defender for a quick scan.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => true;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "defender", ["id"] = "scan" },
            new JsonObject { ["lastScan"] = pc.LastScan }, new JsonObject { ["scan"] = "quick" },
            [new PlanRow("Scan", "Last: " + pc.LastScan, "Quick scan")], [], ["Uses some CPU for a few minutes"], null, Resources: ["defender:quick_scan"]));

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
    public string Group => "security";
    public string What => "Turns Windows Firewall back on for public networks.";
    public IReadOnlyList<string> Flags => [ActionFlags.Network];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "firewall_profile", ["id"] = "public" },
            new JsonObject { ["enabled"] = false }, new JsonObject { ["enabled"] = true }, [new PlanRow("Public firewall", "Off", "On")], [], [], null,
            Resources: ["fw:profile:public"]));

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["enabled"] = false });
    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("failed", new JsonObject(), "Runs through the broker.", "admin_only")); // the engine sends admin steps to IElevation itself
    public IElevation Elevation => elevation;
    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
}

/// <summary>
/// A fake broker route (m9 §3.5): records every session; <see cref="Answer"/> decides each step's outcome (default:
/// verified, observed = after); <see cref="Records"/> is its protected journal for host-death recovery.
/// </summary>
public class FakeElevation : IElevation
{
    public int Calls;
    public readonly List<ElevatedRequest> Requests = [];
    public Func<ElevatedRequest, ElevatedResult>? Answer;
    public ElevatedJournal? Records;
    public bool JournalUnreadable;
    public TaskCompletionSource? Gate;
    public CancellationToken LastStop;

    public virtual async Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        lock (Requests) Requests.Add(request);
        LastStop = stop;
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        return Answer?.Invoke(request)
            ?? new ElevatedResult([.. request.Steps.Select(s => (s.N, new ActionOutcome("verified", (JsonObject)s.After.DeepClone(), "Done, read back.")))],
                $"rcpt-{Calls:x16}", true);
    }

    public ElevatedJournal? Journal(string planId) => JournalUnreadable ? null : Records ?? new ElevatedJournal(null, []);
}

/// <summary>An elevation route whose UAC prompt the user cancels.</summary>
public sealed class CancelledUac : FakeElevation
{
    public CancelledUac() =>
        Answer = r => new ElevatedResult([.. r.Steps.Select(s => (s.N, new ActionOutcome("declined", [], "Approval was cancelled.", "uac_cancelled", "UAC cancelled.")))], null, false);
}

/// <summary>
/// A provider-shaped handler that holds its resources until <see cref="Gate"/> opens; the keys come from the request's
/// "keys" parameter (test only) so two handlers can be pointed at overlapping resources. It takes no lease, so only the
/// resource locks can refuse it.
/// </summary>
public sealed class FakeHoldHandler(string actionId) : IActionHandler
{
    public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs;
    public string ActionId => actionId;
    public string Version => "1.0.0";
    public string Label => "Hold " + actionId;
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "provider";
    public string Group => "test";
    public string What => "Holds its resources until the test releases it.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
    {
        var keys = request.Parameters?["keys"]?.AsArray().Select(k => k!.GetValue<string>()).ToArray() ?? [];
        return Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "hold", ["id"] = string.Join("|", keys) },
            new JsonObject(), new JsonObject { ["held"] = true }, [new PlanRow("Hold", "No", "Yes")], [], [], null, Resources: keys));
    }

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref Runs);
        Started.TrySetResult();
        await Gate.Task.WaitAsync(ct);
        return new ActionOutcome("completed", new JsonObject { ["result"] = "done" }, "Done.");
    }

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
}

/// <summary>
/// A setting-kind handler whose write ignores cancellation and keeps running until <see cref="Gate"/> opens (a handler
/// stuck in synchronous work): the engine's deadline passes while it may still be writing. Resource: test:stubborn.
/// </summary>
public sealed class FakeStubbornHandler(string actionId = "test.stubborn") : IActionHandler
{
    public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs;
    public string ActionId => actionId;
    public string Version => "1.0.0";
    public string Label => "Stubborn " + actionId;
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "setting";
    public string Group => "test";
    public string What => "Keeps writing after its deadline.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "stubborn", ["id"] = "x" }, new JsonObject { ["v"] = 0 }, new JsonObject { ["v"] = 1 },
            [new PlanRow("Value", "0", "1")], [], [], null, Resources: ["test:stubborn"]));

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["v"] = 0 });

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref Runs);
        await Gate.Task; // ignores ct
        Ended.TrySetResult();
        return new ActionOutcome("verified", new JsonObject { ["v"] = 1 }, "Done.");
    }

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
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

/// <summary>
/// The fake PC's named settings for fix plans (m9 §4): each setting holds one value. What the handlers resolve can be
/// changed per setting (privilege, flags, resources, extra rows, label) so the tests can drift every compared field.
/// </summary>
public sealed class FakeSettings
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Values = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Privilege = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> Flags = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> Resources = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Rows = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Labels = new();
    /// <summary>settingId → the value its resolution says it will write instead of the requested one (drifts after).</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> After = new();
    /// <summary>settingId → the outcome its write reports instead of writing ("failed" leaves the value; "indeterminate" writes something else).</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Fail = new();
    public volatile string Version = "1.0.0";
    public int Writes;
    /// <summary>Called inside a write (before it lands), with the settingId: the test's chance to stop or drift mid-run.</summary>
    public Action<string>? DuringWrite;
    /// <summary>Called at the start of every resolve, with the settingId: the test's chance to drift another setting.</summary>
    public Action<string>? DuringResolve;
    public TimeSpan WriteDelay;

    public FakeSettings Set(string id, string value)
    {
        Values[id] = value;
        return this;
    }
}

/// <summary>A setting-kind handler shaped like setting.set: {settingId, value}; undo restores the recorded value.</summary>
public sealed class FakeSettingHandler(FakeSettings pc, string actionId = "setting.set", string privilege = "none", string kind = "setting") : IActionHandler
{
    public string ActionId => actionId;
    public string Version => pc.Version;
    public string Label => "Change a Windows setting";
    public string Privilege => privilege;
    public bool SupportsUndo => true;
    public string Kind => kind;
    public string Group => "settings";
    public string What => "Sets one listed Windows setting to one of its listed values.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
    {
        var p = request.Parameters ?? [];
        var id = p["settingId"]?.GetValue<string>();
        if (id is not null) pc.DuringResolve?.Invoke(id);
        if (id is null || !pc.Values.TryGetValue(id, out var current)) return Task.FromResult(ActionResolution.Ineligible("invalid_arguments", "Not a listed setting."));
        if (p["restore"] is not null && !request.Undo) return Task.FromResult(ActionResolution.Ineligible("invalid_arguments", "restore is engine-only."));
        var desired = request.Undo ? p["restore"]?.GetValue<string>() : pc.After.TryGetValue(id, out var forced) ? forced : p["value"]?.GetValue<string>();
        if (desired is null) return Task.FromResult(ActionResolution.Ineligible("invalid_arguments", "Not a listed value."));
        if (desired == current) return Task.FromResult(ActionResolution.Ineligible("already_set", $"{id} is already {current}."));
        var rows = new List<PlanRow> { new(id, current, desired) };
        for (var i = 1; i < pc.Rows.GetValueOrDefault(id, 1); i++) rows.Add(new PlanRow($"{id} detail {i}", "a", "b"));
        var target = new JsonObject { ["type"] = "setting", ["id"] = id, ["label"] = pc.Labels.GetValueOrDefault(id, id) };
        return Task.FromResult(new ActionResolution(true, target, new JsonObject { ["value"] = current }, new JsonObject { ["value"] = desired },
            rows, [], ["A trade-off"], $"You can undo this while it is still {desired}.",
            Privilege: pc.Privilege.TryGetValue(id, out var pr) ? pr : null, Flags: pc.Flags.GetValueOrDefault(id),
            Resources: pc.Resources.GetValueOrDefault(id) ?? [$@"reg:64:HKCU\TEST\{id.ToUpperInvariant()}"]));
    }

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) =>
        Task.FromResult(new JsonObject { ["value"] = pc.Values.GetValueOrDefault(target["id"]!.GetValue<string>()) });

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        var id = execution.Target["id"]!.GetValue<string>();
        if (pc.WriteDelay > TimeSpan.Zero) await Task.Delay(pc.WriteDelay, ct);
        pc.DuringWrite?.Invoke(id);
        Interlocked.Increment(ref pc.Writes);
        if (pc.Fail.TryGetValue(id, out var fail))
        {
            if (fail == "indeterminate") pc.Values[id] = "something_else";
            return new ActionOutcome(fail, new JsonObject { ["value"] = pc.Values[id] }, "Windows did not take it.", fail == "failed" ? "write_failed" : "read_back_mismatch", "Not changed.");
        }
        pc.Values[id] = execution.After["value"]!.GetValue<string>();
        return new ActionOutcome("verified", new JsonObject { ["value"] = pc.Values[id] }, $"{id} is {pc.Values[id]}, read back.");
    }

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) =>
        new(ActionId, target, new JsonObject { ["settingId"] = target["id"]!.DeepClone(), ["restore"] = before["value"]!.DeepClone() }, null);
}

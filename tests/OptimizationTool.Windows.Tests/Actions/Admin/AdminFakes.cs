using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Repair;
using OptimizationTool.Windows.Actions.Services;
using OptimizationTool.Windows.Actions.Tasks;

namespace OptimizationTool.Windows.Tests.Actions.Admin;

// Fakes only (m9 P3b-2): no test touches this PC's services, tasks, firewall, a repair tool or elevation.

internal sealed class FakeFiles : IFileFacts
{
    public readonly Dictionary<string, string?> Hashes = new(StringComparer.OrdinalIgnoreCase); // path → sha256 (null: unreadable)
    public readonly Dictionary<string, string> Publishers = new(StringComparer.OrdinalIgnoreCase);
    public bool Exists(string path) => Hashes.ContainsKey(path);
    public string? Sha256(string path) => Hashes.TryGetValue(path, out var h) ? h : null;
    public string? Publisher(string path) => Publishers.TryGetValue(path, out var p) ? p : null;
}

internal sealed class FakeScm : IServiceControl
{
    public readonly Dictionary<string, ServiceInfo> Services = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Writes = [];
    /// <summary>Writes are recorded but nothing changes (a write that doesn't stick).</summary>
    public bool Ignore;
    public IReadOnlyList<string> Names() => [.. Services.Keys];
    public ServiceInfo? Read(string name) => Services.TryGetValue(name, out var s) ? s : null;

    public void SetStartType(string name, int startType, bool delayed)
    {
        Writes.Add($"start:{name}:{startType}:{delayed}");
        if (!Ignore) Services[name] = Services[name] with { StartType = startType, DelayedAuto = startType == 2 && delayed };
    }

    public void Stop(string name, TimeSpan wait)
    {
        Writes.Add($"stop:{name}");
        if (!Ignore) Services[name] = Services[name] with { State = 1 };
    }

    public void Start(string name, TimeSpan wait)
    {
        Writes.Add($"run:{name}");
        if (!Ignore) Services[name] = Services[name] with { State = 4 };
    }
}

internal sealed class FakeScheduler : ITaskScheduler
{
    public readonly Dictionary<string, TaskInfo> Tasks = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Writes = [];
    public bool Ignore;
    public IReadOnlyList<string> Paths() => [.. Tasks.Keys.Where(p => !p.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))];
    public TaskInfo? Read(string path) => Tasks.TryGetValue(path, out var t) ? t : null;

    public void SetEnabled(string path, bool enabled)
    {
        Writes.Add($"{(enabled ? "on" : "off")}:{path}");
        if (!Ignore) Tasks[path] = Tasks[path] with { Enabled = enabled };
    }
}

internal sealed class FakeRules : IFirewallRules
{
    public readonly List<FirewallRuleInfo> Rules = [];
    public readonly List<string> Writes = [];
    public bool Ignore;
    public IReadOnlyList<FirewallRuleInfo> ForProgram(string applicationPath) =>
        [.. Rules.Where(r => string.Equals(r.ApplicationPath, applicationPath, StringComparison.OrdinalIgnoreCase))];
    public IReadOnlyList<FirewallRuleInfo> ByName(string name) => [.. Rules.Where(r => r.Name == name)];

    public void Add(FirewallRuleInfo rule)
    {
        Writes.Add("add:" + rule.Name);
        if (!Ignore) Rules.Add(rule);
    }

    public void Remove(string name)
    {
        Writes.Add("remove:" + name);
        if (!Ignore) Rules.RemoveAll(r => r.Name == name);
    }
}

internal sealed class FakeFirewallState : IPublicFirewallReader
{
    public PublicFirewallSnapshot State = new(true, true, true, false, false, OtherFirewallOn: false, true, false, false, Listening: [], InboundRules: []);
    public PublicFirewallSnapshot Read(CancellationToken ct) => State;
}

/// <summary>The repair tool, faked: never starts a process. It records the argv it was given, plays lines, then exits or waits.</summary>
internal sealed class FakeRepairTool : IRepairTool
{
    public string? Problem;
    public readonly List<(string Path, IReadOnlyList<string> Args)> Runs = [];
    public List<string> Lines = [];
    public int Exit { get; set; }
    public TaskCompletionSource? Gate;
    public volatile bool Killed;
    public (string? Path, string? Problem) Locate(string fileName) => Problem is null ? (@"Q:\Windows\System32\" + fileName, null) : (null, Problem);

    public async Task<int> RunAsync(string path, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct)
    {
        lock (Runs) Runs.Add((path, arguments));
        foreach (var l in Lines) onLine(l);
        if (Gate is { } g)
            try { await g.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Killed = true; throw; }
        return Exit;
    }
}

internal static class Admin
{
    public const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";
    public const string Hash2 = "2222222222222222222222222222222222222222222222222222222222222222";

    public static BrokerStepRequest Step(string actionId, ActionResolution r, JsonObject? parameters, int n = 1, ElevatedUndoOf? undoOf = null) =>
        BrokerStepRequest.From(new ElevatedStep(n, actionId, VersionOf(actionId), r.Target, r.Before, r.After, parameters, undoOf));

    /// <summary>The handler version the host would send for this action (the broker's content hash binds it).</summary>
    private static string VersionOf(string actionId) => actionId switch
    {
        ServiceDisableHandler.Id => ServiceDisableHandler.HandlerVersion,
        TaskDisableHandler.Id => TaskDisableHandler.HandlerVersion,
        BlockAppHandler.Id => BlockAppHandler.HandlerVersion,
        _ when RepairKind.All.Any(k => k.ActionId == actionId) => RepairPlan.HandlerVersion,
        _ => throw new ArgumentException($"No handler version for {actionId}.", nameof(actionId)),
    };

    public static ActionExecution Exec(ActionResolution r, JsonObject? parameters = null) => new("plan-1", "hash", r.Target, r.Before, r.After, parameters);
}

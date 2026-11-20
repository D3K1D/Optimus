using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Security.Common;
using ProtectionResult = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Windows.Actions.Firewall;

/// <summary>
/// One Windows Firewall rule as the handlers compare it. Direction in | out; Action block | allow; Protocol 256 = any. The
/// scope (addresses, ports, interface types and interfaces, service, edge traversal) decides what the rule actually blocks;
/// its defaults are what a rule Optimus adds reads back as. ScopeRead false: the scope couldn't be read (undo refuses).
/// </summary>
public sealed record FirewallRuleInfo(string Name, string? ApplicationPath, string Direction, string Action, int Profiles, bool Enabled, int Protocol, string? Grouping,
    string? LocalAddresses = "*", string? RemoteAddresses = "*", string? LocalPorts = null, string? RemotePorts = null, string? InterfaceTypes = "All",
    string? Interfaces = null, string? ServiceName = null, bool EdgeTraversal = false, bool ScopeRead = true);

/// <summary>
/// The firewall rule seam (m9 §3.2 firewall.block_app), beside the Public-profile reader/writer: find rules for one program
/// or by name, add one rule, remove one rule by name. Only the elevated broker adds or removes. Tests use a fake; the real
/// one (<see cref="WindowsFirewallRules"/>, INetFwPolicy2.Rules) has never been run.
/// </summary>
public interface IFirewallRules
{
    /// <summary>Every rule whose program is <paramref name="applicationPath"/> (compared without case, environment tokens expanded).</summary>
    IReadOnlyList<FirewallRuleInfo> ForProgram(string applicationPath);
    /// <summary>Every rule with exactly this name (names are not unique in Windows Firewall).</summary>
    IReadOnlyList<FirewallRuleInfo> ByName(string name);
    void Add(FirewallRuleInfo rule);
    void Remove(string name);
}

/// <summary>
/// firewall.block_app {targetRef} (contracts/m9-ai-powers.md §3.2): adds ONE inbound block rule, all profiles, any
/// protocol, for the program a listener finding names. The rule's name is fixed: "Optimus: block inbound · &lt;file&gt; ·
/// &lt;id&gt;", where id is a random 8-hex id chosen at preview and bound to the plan by its hash (the rule group is
/// "Optimus"). Refused: a protected program or a system component, an existing enabled inbound block rule of Optimus for
/// the same program (already_set), and any firewall state where Windows Firewall is not clearly the active firewall (as
/// firewall.enable_public checks). Undo removes exactly that rule, matched by its name and every recorded property.
/// </summary>
public sealed class BlockAppHandler(IFirewallRules rules, IPublicFirewallReader firewall, IFileFacts files, Func<string?, string?>? expand = null,
    ISecurityProducts? products = null) : IActionHandler
{
    public const string Id = "firewall.block_app";
    public const string HandlerVersion = "1.1.0", LabelText = "Block a program's incoming connections";
    public const string Marker = "Optimus: block inbound", Group_ = "Optimus";
    private const string ListenerCollector = "win.security.listeners";
    internal const int AllProfiles = 0x7FFFFFFF, AnyProtocol = 256;
    private readonly Func<string?, string?> _expand = expand ?? DefaultExpand;

    public string ActionId => Id;
    public string Version => HandlerVersion;
    public string Label => LabelText;
    public string Privilege => "admin";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "security";
    public string What => "Adds one Windows Firewall rule that blocks incoming connections to a program; undo removes exactly that rule.";
    public IReadOnlyList<string> Flags => [ActionFlags.Network];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    public string? TargetKind => "listening program";

    private static string? DefaultExpand(string? normalized) =>
        normalized is { Length: > 2 } n && n[0] == '%' && n.IndexOf('%', 1) > 1 ? PathRoots.FromEnvironment().ExpandKnown(n) : null;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var (program, name) = FromTarget(target) ?? throw new ArgumentException("Not a block rule target.", nameof(target));
        return State(program, name);
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "admin_only", "This runs through the administrator helper."));

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) =>
        FromTarget(target) is null ? null : new ActionRequest(Id, (JsonObject)target.DeepClone(), new JsonObject { ["mode"] = "remove" }, null) { Undo = true };

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        var remove = request.Parameters is { Count: 1 } p && Str(p["mode"]) == "remove";
        if (request.Undo != remove || (!remove && request.Parameters is { Count: > 0 })) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");

        string program, ruleName;
        if (request.Recheck || request.Undo)
        {
            if (request.Target is null || FromTarget(request.Target) is not { } t) return Unknown();
            (program, ruleName) = t;
        }
        else
        {
            if (request.Evidence is not { } ev) return Unknown();
            var located = Locate(ev);
            if (located.Refusal is { } refusal) return refusal;
            program = located.Program!;
            ruleName = RuleName(program, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        }

        if (Gate(firewall.Read(CancellationToken.None)) is { } gate) return gate;
        if (!files.Exists(program)) return ActionResolution.Ineligible("not_found", "That program is no longer there. Check again.", FirewallPlan.ManualUri);
        if (Refuse(program, files, products) is { } prot) return ActionResolution.Ineligible(prot.Code, prot.Reason, FirewallPlan.ManualUri);

        JsonObject live;
        IReadOnlyList<FirewallRuleInfo> forProgram;
        try
        {
            live = State(program, ruleName);
            forProgram = rules.ForProgram(program);
        }
        catch (Exception) { return ActionResolution.Ineligible("unreadable", "Windows didn't report its firewall rules.", FirewallPlan.ManualUri); }
        var label = FileLabel(program);
        var target = Target(program, ruleName, label);
        var resources = new[] { "fw:rule:" + ruleName.ToUpperInvariant() };

        if (remove)
        {
            var added = Added(program, ruleName);
            if (live["present"]?.GetValue<bool>() != true) return ActionResolution.Ineligible("already_set", "That firewall rule is already gone.");
            if (!JsonNode.DeepEquals(live, added))
                return ActionResolution.Ineligible("changed_since", "The firewall rule changed since Optimus added it. Remove it in Windows Security if you want.", FirewallPlan.ManualUri);
            return new ActionResolution(true, target, live, Absent(),
                [new PlanRow("Incoming connections", "Blocked", "Allowed by other rules"), new PlanRow("Program", label, label)],
                [new("Rule", ruleName)], ["Other devices can reach the program again where other rules allow it."], null,
                Privilege: "admin", Resources: resources);
        }

        if (forProgram.Any(IsOptimusBlock) || live["present"]?.GetValue<bool>() == true)
            return ActionResolution.Ineligible("already_set", "Optimus already blocks incoming connections for that program.");
        return new ActionResolution(true, target, live, Added(program, ruleName),
            [new PlanRow("Incoming connections", "As other rules allow", "Blocked"), new PlanRow("Program", label, label)],
            [new("Rule", ruleName), new("Networks", "All (private, public, domain)")],
            ["Other devices on your networks can't connect to it; it can still connect out.", "Features that need incoming connections (casting, sharing, multiplayer hosting) may stop working."],
            "You can undo this unless something else changes this firewall rule first.",
            Privilege: "admin", Resources: resources);
    }

    /// <summary>The program a listener finding names: a known-root path (%ProgramFiles%\…), expanded; never a system or unknown owner.</summary>
    private (string? Program, ActionResolution? Refusal) Locate(TargetEvidence ev)
    {
        if (ev.CollectorId != ListenerCollector || !ev.Subject.StartsWith("net.listener.", StringComparison.Ordinal)) return (null, Unknown());
        if (ev.Value["isSystem"] is JsonValue s && s.TryGetValue<bool>(out var system) && system)
            return (null, ActionResolution.Ineligible(ProtectionResult.ProtectedCode, "This is part of Windows.", FirewallPlan.ManualUri));
        var expanded = _expand(Str(ev.Value["image"]));
        if (expanded is null || !Path.IsPathFullyQualified(expanded) || expanded.Contains('%') || expanded.Any(char.IsControl)) return (null, Unknown());
        return (expanded, null);
    }

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to a program on this PC.");

    /// <summary>Windows Firewall must clearly be the firewall in charge, on an unmanaged PC, with a readable state (as firewall.enable_public).</summary>
    internal static ActionResolution? Gate(PublicFirewallSnapshot s)
    {
        if (s.ReadError is not null) return FirewallPlan.Ineligible("unreadable", "Windows didn't report the firewall state.");
        if (s.PolicyConfigured || s.MdmConfigured || s.Managed) return FirewallPlan.Ineligible("managed", "Your organization's policy controls the firewall.");
        if (s.OtherFirewallOn is null) return FirewallPlan.Ineligible("provider_unknown", "Windows didn't say which firewall is in charge.");
        if (s.OtherFirewallOn == true) return FirewallPlan.Ineligible("other_provider", "Another firewall app is in charge.");
        return null;
    }

    /// <summary>System components (under the Windows folder) and protected programs (security, Optimus, hardware tools), publisher read live.</summary>
    internal static ProtectionResult? Refuse(string program, IFileFacts files, ISecurityProducts? products = null)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && Full(program) is { } full && full.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return new ProtectionResult(ProtectionResult.ProtectedCode, "This is part of Windows.");
        string? publisher = null;
        try { publisher = files.Publisher(program); } catch (Exception) { publisher = null; }
        return ProtectedTargets.Process(program, publisher, (products ?? NoSecurityProducts.Instance).Names());
    }

    private static string? Full(string p)
    {
        try { return Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IsOptimusBlock(FirewallRuleInfo r) =>
        r.Name.StartsWith(Marker, StringComparison.Ordinal) && r.Direction == "in" && r.Action == "block" && r.Enabled;

    /// <summary>{present, name, app, direction, action, profiles, protocol, enabled, group} of the one rule with that name.</summary>
    internal JsonObject State(string program, string name)
    {
        var found = rules.ByName(name);
        if (found.Count == 0) return Absent();
        if (found.Count > 1) return new JsonObject { ["present"] = true, ["count"] = found.Count };
        return Rule(found[0]);
    }

    private static JsonObject Absent() => new() { ["present"] = false };

    /// <summary>The rule Optimus adds, as it reads back: inbound block, all profiles, any protocol, the default (unrestricted) scope.</summary>
    internal static JsonObject Added(string program, string name) => Rule(new FirewallRuleInfo(name, program, "in", "block", AllProfiles, true, AnyProtocol, Group_));

    /// <summary>
    /// The rule as plans, receipts and undo compare it, its behavioural scope included (normalised: an unrestricted
    /// address or port list is "*", all interface types "all"); scope null when it couldn't be read, which never matches.
    /// </summary>
    private static JsonObject Rule(FirewallRuleInfo r) => new()
    {
        ["present"] = true, ["name"] = r.Name, ["app"] = r.ApplicationPath is not { } app ? null : Full(app)?.ToUpperInvariant() ?? app.ToUpperInvariant(),
        ["direction"] = r.Direction, ["action"] = r.Action, ["profiles"] = r.Profiles, ["protocol"] = r.Protocol, ["enabled"] = r.Enabled, ["group"] = r.Grouping,
        ["scope"] = !r.ScopeRead ? null : new JsonObject
        {
            ["localAddresses"] = Any(r.LocalAddresses), ["remoteAddresses"] = Any(r.RemoteAddresses),
            ["localPorts"] = Any(r.LocalPorts), ["remotePorts"] = Any(r.RemotePorts),
            ["interfaceTypes"] = string.IsNullOrWhiteSpace(r.InterfaceTypes) || r.InterfaceTypes.Trim().Equals("All", StringComparison.OrdinalIgnoreCase)
                ? "all" : r.InterfaceTypes.Trim().ToLowerInvariant(),
            ["interfaces"] = string.IsNullOrWhiteSpace(r.Interfaces) ? null : r.Interfaces.Trim(),
            ["service"] = string.IsNullOrWhiteSpace(r.ServiceName) ? null : r.ServiceName.Trim(),
            ["edgeTraversal"] = r.EdgeTraversal,
        },
    };

    private static string Any(string? list) => string.IsNullOrWhiteSpace(list) || list.Trim() == "*" ? "*" : list.Trim().ToUpperInvariant();

    internal ActionOutcome Execute(ActionExecution ex)
    {
        if (FromTarget(ex.Target) is not { } t) return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        var (program, name) = t;
        var adding = ex.After["present"]?.GetValue<bool>() == true;
        string? error = null;
        try
        {
            if (adding) rules.Add(new FirewallRuleInfo(name, program, "in", "block", AllProfiles, true, AnyProtocol, Group_));
            else rules.Remove(name);
        }
        catch (Exception e) { error = e.GetType().Name; }
        JsonObject observed;
        try { observed = State(program, name); }
        catch (Exception) { return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report its firewall rules after the change.", "unexpected_state", error); }
        if (JsonNode.DeepEquals(observed, ex.After))
            return new ActionOutcome("verified", observed, adding ? "The block rule is in Windows Firewall, read back." : "The block rule is gone from Windows Firewall, read back.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, "Windows Firewall is unchanged.", error is null ? "not_applied" : "write_failed", error);
        return new ActionOutcome("indeterminate", observed, "Windows Firewall changed in a way that wasn't expected. Check it in Windows Security.", "unexpected_state", error);
    }

    internal static string RuleName(string program, string id) => $"{Marker} · {FileLabel(program)} · {id}";

    private static string FileLabel(string program)
    {
        var name = PathRoots.FileName(program);
        return name.Length == 0 ? "program" : name.Length > 60 ? name[..60] : name;
    }

    internal static JsonObject Target(string program, string ruleName, string label) => new()
    {
        ["type"] = "firewall_rule", ["id"] = ruleName.ToUpperInvariant(), ["program"] = program, ["ruleName"] = ruleName, ["label"] = label,
    };

    /// <summary>The program and rule name of a well-formed target: a fully qualified path, and the fixed name pattern for that program.</summary>
    internal static (string Program, string RuleName)? FromTarget(JsonObject target)
    {
        if (Str(target["type"]) != "firewall_rule" || Str(target["program"]) is not { Length: > 3 and <= 520 } program || Str(target["ruleName"]) is not { } name) return null;
        if (!Path.IsPathFullyQualified(program) || program.Any(char.IsControl) || program.Contains('%')) return null;
        var prefix = $"{Marker} · {FileLabel(program)} · ";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 8 || !name[prefix.Length..].All(char.IsAsciiHexDigitLower)) return null;
        return Str(target["id"]) == name.ToUpperInvariant() ? (program, name) : null;
    }

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>firewall.block_app in the broker: the same code with the broker's own firewall and file reads, plus its own lists.</summary>
public sealed class BlockAppBrokerHandler(IFirewallRules rules, IPublicFirewallReader firewall, IFileFacts files, ISecurityProducts? products = null) : IBrokerHandler
{
    private readonly BlockAppHandler _handler = new(rules, firewall, files, products: products);

    public string ActionId => BlockAppHandler.Id;
    public string Version => BlockAppHandler.HandlerVersion;
    public string Label => BlockAppHandler.LabelText;
    public IReadOnlyList<string> Flags => [ActionFlags.Network];

    public bool IsUndo(BrokerStepRequest step) => BlockAppHandler.Str(step.Parameters?["mode"]) == "remove";

    public ActionResolution Resolve(BrokerStepRequest step) =>
        _handler.Resolve(new ActionRequest(ActionId, step.Target, step.Parameters, null) { Recheck = true, Undo = IsUndo(step) });

    /// <summary>The broker's own lists for the program the resolution names (an undo only removes Optimus's rule, so it is never refused here).</summary>
    public ProtectionResult? Protect(ActionResolution r) =>
        r.After["present"]?.GetValue<bool>() != true ? null
        : BlockAppHandler.Str(r.Target["program"]) is { } program ? BlockAppHandler.Refuse(program, files)
        : new ProtectionResult(ProtectionResult.ProtectedCode, "Optimus can't tell what this program is.");

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) =>
        _handler.Execute(new ActionExecution("broker", "broker", r.Target, r.Before, r.After, step.Parameters));
}

/// <summary>
/// Windows Firewall rules through INetFwPolicy2.Rules (HNetCfg.FwPolicy2 and HNetCfg.FWRule, late bound; NEVER RUN on the
/// development PC). Reads enumerate the rules (capped); the writes are Rules.Add of one new rule and Rules.Remove by name.
/// </summary>
public sealed class WindowsFirewallRules : IFirewallRules
{
    private const int RuleCap = 5000;
    private readonly PathRoots _roots = PathRoots.FromEnvironment();

    public IReadOnlyList<FirewallRuleInfo> ForProgram(string applicationPath) => All(r => r.ApplicationPath is { } a && Same(a, applicationPath));

    public IReadOnlyList<FirewallRuleInfo> ByName(string name) => All(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    public void Add(FirewallRuleInfo rule)
    {
        object? policy = null, rules = null, r = null;
        try
        {
            policy = Automation.Create("HNetCfg.FwPolicy2");
            rules = Automation.Get(policy, "Rules")!;
            r = Automation.Create("HNetCfg.FWRule");
            void Set(string p, object v) => r!.GetType().InvokeMember(p, BindingFlags.SetProperty, null, r, [v], CultureInfo.InvariantCulture);
            Set("Name", rule.Name);
            Set("Description", "Added by Optimus. Remove it in Optimus (Undo) or in Windows Security.");
            Set("Grouping", rule.Grouping ?? BlockAppHandler.Group_);
            Set("ApplicationName", rule.ApplicationPath!);
            Set("Protocol", rule.Protocol);
            Set("Direction", rule.Direction == "in" ? 1 : 2);
            Set("Action", rule.Action == "block" ? 0 : 1);
            Set("Profiles", rule.Profiles);
            Set("Enabled", rule.Enabled);
            Automation.Call(rules, "Add", r);
        }
        finally
        {
            Automation.Release(r);
            Automation.Release(rules);
            Automation.Release(policy);
        }
    }

    public void Remove(string name)
    {
        object? policy = null, rules = null;
        try
        {
            policy = Automation.Create("HNetCfg.FwPolicy2");
            rules = Automation.Get(policy, "Rules")!;
            Automation.Call(rules, "Remove", name);
        }
        finally
        {
            Automation.Release(rules);
            Automation.Release(policy);
        }
    }

    private List<FirewallRuleInfo> All(Func<FirewallRuleInfo, bool> keep)
    {
        object? policy = null, rules = null;
        var list = new List<FirewallRuleInfo>();
        try
        {
            policy = Automation.Create("HNetCfg.FwPolicy2");
            rules = Automation.Get(policy, "Rules")!;
            foreach (var rule in Automation.Items(rules, RuleCap))
                try
                {
                    var app = Automation.Get(rule, "ApplicationName") as string;
                    var info = new FirewallRuleInfo(Automation.Get(rule, "Name") as string ?? "", app is { Length: > 0 } ? _roots.ExpandKnown(app) : null,
                        Int(Automation.Get(rule, "Direction")) == 1 ? "in" : "out", Int(Automation.Get(rule, "Action")) == 0 ? "block" : "allow",
                        Int(Automation.Get(rule, "Profiles")), Automation.Get(rule, "Enabled") is true, Int(Automation.Get(rule, "Protocol")),
                        Automation.Get(rule, "Grouping") as string);
                    if (keep(info)) list.Add(Scope(rule, info));
                }
                finally { Automation.Release(rule); }
        }
        finally
        {
            Automation.Release(rules);
            Automation.Release(policy);
        }
        return list;
    }

    /// <summary>The rule's behavioural scope (INetFwRule): what it actually blocks. Anything unreadable: ScopeRead false.</summary>
    private static FirewallRuleInfo Scope(object rule, FirewallRuleInfo info)
    {
        try
        {
            var interfaces = Automation.Get(rule, "Interfaces") is object[] list ? string.Join(",", list.Select(i => i?.ToString())) : null;
            return info with
            {
                LocalAddresses = Automation.Get(rule, "LocalAddresses") as string, RemoteAddresses = Automation.Get(rule, "RemoteAddresses") as string,
                LocalPorts = Automation.Get(rule, "LocalPorts") as string, RemotePorts = Automation.Get(rule, "RemotePorts") as string,
                InterfaceTypes = Automation.Get(rule, "InterfaceTypes") as string, Interfaces = interfaces,
                ServiceName = Automation.Get(rule, "ServiceName") as string, EdgeTraversal = Automation.Get(rule, "EdgeTraversal") is true,
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException or InvalidCastException or MissingMethodException)
        {
            return info with { ScopeRead = false };
        }
    }

    private static int Int(object? o) => o is null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture);

    private static bool Same(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Firewall;

/// <summary>
/// Pure plan builder for <c>firewall.enable_public</c>, shared by the host handler (preview) and the elevated broker
/// (re-resolve from live state). Mode "enable" turns the Public profile on; "disable" is only the undo of a verified
/// enable and carries a strong warning row.
/// </summary>
public static class FirewallPlan
{
    public const string ActionId = "firewall.enable_public";
    public const string ManualUri = "windowsdefender://network";
    public const string Field = "PublicProfile.EnableFirewall";

    public static JsonObject Target() => new() { ["type"] = "firewall_profile", ["id"] = "public", ["label"] = "Public network" };

    public static JsonObject State(bool? local, bool? effective) =>
        new() { ["publicProfile"] = new JsonObject { ["localEnabled"] = local, ["effectiveEnabled"] = effective } };

    /// <summary>"enable" | "disable" | null (invalid). Parameters may be absent or exactly {"mode": "enable"|"disable"}.</summary>
    public static string? Mode(JsonObject? parameters)
    {
        if (parameters is null || parameters.Count == 0) return "enable";
        if (parameters.Count != 1 || parameters["mode"] is not JsonValue v || !v.TryGetValue(out string? m)) return null;
        return m is "enable" or "disable" ? m : null;
    }

    /// <summary>Target may be absent or {type:"firewall_profile", id:"public"[, label]}; anything else is refused.</summary>
    public static bool ValidTarget(JsonObject? target)
    {
        if (target is null || target.Count == 0) return true;
        foreach (var (key, _) in target)
            if (key is not ("type" or "id" or "label")) return false;
        return Str(target["id"]) == "public" && (target["type"] is null || Str(target["type"]) == "firewall_profile");
    }

    public static ActionResolution Resolve(PublicFirewallSnapshot s, string? mode, bool handlerEnabled, JsonObject? target = null)
    {
        if (!handlerEnabled)
            return Ineligible("disabled_pending_review", "Turning the firewall on from this app isn't available yet. You can do it in Windows Security.");
        if (mode is null || !ValidTarget(target)) return Ineligible("bad_request", "This change isn't supported.");
        if (s.ReadError is not null) return Ineligible("unreadable", "Windows didn't report the firewall state.");
        if (s.PolicyConfigured || s.MdmConfigured) return Ineligible("policy_managed", "Your organization's policy controls this setting.");
        if (s.Managed) return Ineligible("managed_device", "This PC is managed by an organization.");
        if (s.OtherFirewallOn is null) return Ineligible("provider_unknown", "Windows didn't say which firewall is in charge.");
        if (s.OtherFirewallOn == true) return Ineligible("other_provider", "Another firewall app is in charge.");
        if (s.RemoteSession || !s.ConsoleSession) return Ineligible("remote_session", "This change needs someone at the PC, not a remote session.");

        var target2 = Target();
        if (mode == "enable")
        {
            if (s.LocalEnabled == true || s.EffectiveEnabled == true) return Ineligible("already_on", "The firewall for public networks is already on.");
            if (s.LocalEnabled is null || s.EffectiveEnabled is null) return Ineligible("not_explicit", "Windows didn't report a clear setting to change.");
            if (s.PublicActive != true) return Ineligible("public_not_active", "You're not on a public network right now.");
            if (s.Listening is null || s.InboundRules is null)
                return Ineligible("inbound_unknown", "Windows didn't say which apps accept incoming connections.");

            var rows = new List<PlanRow> { new("Firewall on public networks", "Off", "On") };
            var tradeoffs = new List<string> { "Applies to every public network connection.", "Apps that accept incoming connections may be blocked." };
            if (BlockedLine(s.InboundRules) is { } line) tradeoffs.Add(line);

            // Inbound dependence: something listening right now that a Public allow rule lets in → guidance, not one click.
            var inUse = InboundInUse(s.Listening, s.InboundRules);
            if (inUse.Count > 0)
            {
                var programs = inUse.Select(m => DisplayName(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var context = inUse
                    .GroupBy(m => DisplayName(m), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(g => new KeyValuePair<string, string>("Accepts connections",
                        $"{g.Key} · {string.Join(", ", g.Select(m => $"{m.Protocol.ToUpperInvariant()} {m.Port}").Distinct().Take(4))}"))
                    .ToList();
                var count = programs.Count == 1 ? "1 program" : $"{programs.Count} programs";
                return new ActionResolution(false, target2, State(false, false), State(true, true), rows, context, tradeoffs, null,
                    "inbound_in_use", $"{count} on this PC accept incoming connections on public networks. Review them in Windows Security first.", ManualUri);
            }

            return new ActionResolution(true, target2, State(false, false), State(true, true), rows,
                [new("Network", "Public network in use")], tradeoffs,
                "Only while the public firewall is still on");
        }

        // disable = undo of a verified enable
        if (s.LocalEnabled != true || s.EffectiveEnabled != true)
            return Ineligible("changed_since", "The firewall setting changed since it was turned on.");
        return new ActionResolution(true, target2, State(true, true), State(false, false),
            [
                new PlanRow("Firewall on public networks", "On", "Off"),
                new PlanRow("Warning", "Protected", "Not protected on public networks"),
            ],
            [new("Network", s.PublicActive == true ? "Public network in use" : "No public network right now")],
            ["Other devices on public networks can reach apps that accept connections.", "Windows Security will warn that your PC is at risk."],
            "Only while the public firewall is still off");
    }

    public static ActionResolution Ineligible(string code, string message) => ActionResolution.Ineligible(code, message, ManualUri);

    /// <summary>
    /// Listening programs that an enabled inbound Public allow rule added by an app or the user matches (by app path,
    /// service and local port). Windows' built-in rule groups are left out: several carry remote-scope keywords
    /// (Wi-Fi Direct, Teredo) that INetFwRule doesn't expose, so they would be false matches; they still count in the
    /// trade-off line.
    /// </summary>
    public static IReadOnlyList<ListeningProgram> InboundInUse(IReadOnlyList<ListeningProgram> listening, IReadOnlyList<InboundAllowRule> rules) =>
        listening.Where(p => rules.Any(r => !r.BuiltIn && Matches(r, p))).ToList();

    internal static bool Matches(InboundAllowRule r, ListeningProgram p)
    {
        if (r.PackageScoped) return false; // app-package rules can't be tied to a listening process here
        if (r.Protocol == "other" || (r.Protocol != "any" && r.Protocol != p.Protocol)) return false;
        if (!string.IsNullOrWhiteSpace(r.ApplicationPath))
        {
            var app = r.ApplicationPath.Trim().Trim('"');
            var match = app.Equals("System", StringComparison.OrdinalIgnoreCase)
                ? p.IsSystem && p.ImagePath is null
                : p.ImagePath is not null && SamePath(app, p.ImagePath);
            if (!match) return false;
        }
        if (!string.IsNullOrWhiteSpace(r.ServiceName))
        {
            var ok = r.ServiceName == "*" ? p.Services.Count > 0 : p.Services.Contains(r.ServiceName, StringComparer.OrdinalIgnoreCase);
            if (!ok) return false;
        }
        return PortMatches(r.LocalPorts, p.Port);
    }

    internal static bool PortMatches(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var raw in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(200))
        {
            var dash = raw.IndexOf('-');
            if (dash > 0 && int.TryParse(raw[..dash], out var lo) && int.TryParse(raw[(dash + 1)..], out var hi))
            { if (port >= lo && port <= hi) return true; continue; }
            if (int.TryParse(raw, out var single)) { if (single == port) return true; continue; }
            if (raw.Equals("RPC-EPMap", StringComparison.OrdinalIgnoreCase) && port == 135) return true;
            // Other keywords (RPC, IPHTTPS, Teredo, …) name dynamic ports we can't map; they don't match.
        }
        return false;
    }

    /// <summary>"May block incoming connections for: …" when any enabled inbound Public allow rule exists.</summary>
    internal static string? BlockedLine(IReadOnlyList<InboundAllowRule> rules)
    {
        if (rules.Count == 0) return null;
        var names = rules.Where(r => !r.BuiltIn).Select(RuleName).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var builtIn = rules.Count(r => r.BuiltIn);
        var parts = names.Take(5).ToList();
        if (names.Count > 5) parts.Add($"{names.Count - 5} more apps");
        if (builtIn > 0) parts.Add(builtIn == 1 ? "1 Windows feature" : $"{builtIn} Windows features");
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"May block incoming connections for: {list}.";
    }

    private static string RuleName(InboundAllowRule r) =>
        r.ApplicationPath is { Length: > 0 } a && !a.Equals("System", StringComparison.OrdinalIgnoreCase)
            ? Security.Common.PathRoots.FileName(a.Trim('"')) : Security.Common.Text.Clean(r.Name, 60);

    private static string DisplayName(ListeningProgram p) =>
        p.ImagePath is { } i && !(p.Services.Count > 0 && Security.Common.PathRoots.FileName(i).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            ? Security.Common.PathRoots.FileName(i)
            : p.Services.Count > 0 ? Security.Common.Text.Clean(p.Services[0], 60)
            : p.IsSystem ? "Windows (System)" : "An unidentified program";

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// SHA-256 over the canonical (sorted-key) JSON of the fields the broker recomputes: action, mode, target id,
    /// before and after. The broker refuses unless its live recomputation hashes to the same value.
    /// </summary>
    public static string ContentHash(string actionId, string mode, JsonObject target, JsonObject before, JsonObject after)
    {
        var doc = new JsonObject
        {
            ["actionId"] = actionId,
            ["mode"] = mode,
            ["targetId"] = Str(target["id"]),
            ["before"] = before.DeepClone(),
            ["after"] = after.DeepClone(),
        };
        var sb = new StringBuilder();
        Canonical(doc, sb);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void Canonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    Canonical(v, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Canonical(a[i], sb); }
                sb.Append(']');
                break;
            default: sb.Append(node.ToJsonString()); break;
        }
    }

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>Observed {publicProfile:{…}} compared with a Before/After state object.</summary>
    public static bool SameState(JsonObject a, JsonObject b) =>
        Bool(a["publicProfile"]?["localEnabled"]) == Bool(b["publicProfile"]?["localEnabled"])
        && Bool(a["publicProfile"]?["effectiveEnabled"]) == Bool(b["publicProfile"]?["effectiveEnabled"])
        && Bool(a["publicProfile"]?["localEnabled"]) is not null;
}

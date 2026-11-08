using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// Allowlist projectors for src/Windows/Security collectors. Field names mirror the collectors' typed values; names
// become aliases (or sanitized text with share.names); paths, PIDs, process start times, registry locations,
// arguments, threat IDs, HRESULT-free free text and the collectors' own Display sentences never leave.

/// <summary>win.security.listeners: owner alias, port, protocol, family, bind scope. No addresses, PIDs or paths.</summary>
internal sealed class ListenersProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.listeners"];
    public override string Source(string collectorId) => "Windows network tables";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject == "net.listeners.summary")
        {
            var tcp = o.Int("tcpCount", v["tcpCount"]) ?? 0;
            var udp = o.Int("udpCount", v["udpCount"]) ?? 0;
            o.Int("wildcardTcpCount", v["wildcardTcpCount"]);
            o.Int("unknownOwnerRows", v["unknownOwnerRows"]);
            o.Bool("truncated", v["truncated"]);
            return new("net.listeners.summary", $"{Say.Plural(tcp, "TCP listener", "TCP listeners")} and {Say.Plural(udp, "UDP endpoint", "UDP endpoints")} found.");
        }
        if (subject.StartsWith("net.exposed.", StringComparison.Ordinal)) return Exposed(v, o);
        if (!subject.StartsWith("net.listener.", StringComparison.Ordinal)) return null;

        var protocol = o.Enum("protocol", v["protocol"], "tcp", "udp");
        var port = o.Int("port", v["port"], 0, 65535);
        if (protocol is null || port is null) return null;
        var family = o.Enum("family", v["family"], "ipv4", "ipv6") ?? "ip";
        var scope = o.Enum("scope", v["scope"], "loopback", "specific", "wildcard");
        var isSystem = o.Bool("isSystem", v["isSystem"]) == true;
        o.Int("bindCount", v["bindCount"], 1, 65535);
        var (owner, slug) = Owner(o, Read.Str(v["image"]), isSystem);
        o.V["imageKnown"] = slug is not ("system" or "unknown");

        var tags = Exposure.Tags(protocol, port.Value);
        if (tags.Count > 0) o.V["exposure"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        return new($"net.listener.{protocol}.{Say.N(port.Value)}.{family}.{slug}", Exposure.Sentence(owner, protocol, scope, [port.Value], tags));
    }

    /// <summary>Gateway-derived "exposed" item: one owner, protocol and scope with its ports.</summary>
    private static Mapped? Exposed(JsonObject v, Out o)
    {
        var protocol = o.Enum("protocol", v["protocol"], "tcp", "udp");
        var scope = o.Enum("scope", v["scope"], "loopback", "specific", "wildcard");
        if (protocol is null || v["ports"] is not JsonArray arr) return null;
        var ports = new SortedSet<long>();
        foreach (var x in arr.Take(256))
        {
            if (Read.Long(x) is { } p && p is >= 0 and <= 65535) ports.Add(p);
            else o.Dropped(x ?? JsonValue.Create(0));
        }
        if (ports.Count == 0) return null;
        var list = ports.Take(64).ToList();
        o.V["ports"] = new JsonArray(list.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        if (ports.Count > list.Count) o.V["portsTruncated"] = true;
        var isSystem = o.Bool("isSystem", v["isSystem"]) == true;
        var (owner, slug) = Owner(o, Read.Str(v["app"]) ?? Read.Str(v["image"]), isSystem);
        var tags = list.SelectMany(p => Exposure.Tags(protocol, p)).Distinct().ToList();
        if (tags.Count > 0) o.V["exposure"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        return new($"net.exposed.{protocol}.{scope ?? "unknown"}.{slug}", Exposure.Sentence(owner, protocol, scope, list, tags));
    }

    /// <summary>Owner label (process alias or name), its subject slug, and ownerClass.</summary>
    private static (string Owner, string Slug) Owner(Out o, string? rawImage, bool isSystem)
    {
        var image = FileName(rawImage);
        string owner, slug, ownerClass;
        if (image is not null && o.Name("process", "process", image) is { } label)
        {
            owner = label;
            slug = Slug(o.AliasOf("process", image));
            ownerClass = isSystem ? "windows" : "app";
        }
        else if (isSystem) { owner = "Windows (System)"; slug = "system"; ownerClass = "system"; }
        else { owner = "An unidentified process"; slug = "unknown"; ownerClass = "unknown"; }
        o.Const("ownerClass", ownerClass);
        return (owner, slug);
    }
}

/// <summary>
/// Fixed, reviewed table of what a listening port is commonly used for. A tag says what a port is usually for,
/// never that anything is vulnerable. media_discovery is UDP only and usually low concern; the rest are TCP.
/// </summary>
public static class Exposure
{
    private static readonly (string Tag, string Label, long[] Ports)[] Tcp =
    [
        ("remote_access", "remote access", [22, 23, 3389, 5900, 5901, 5902, 5903, 5985, 5986, 5938]),
        ("file_sharing", "file sharing", [21, 139, 445, 2049]),
        ("database", "database", [1433, 1521, 3306, 5432, 6379, 9200, 11211, 27017]),
        ("container_api", "container or device control", [2375, 2376, 5555]),
        ("dev_server", "development server", [3000, 5000, 5173, 8000, 8080, 8443, 8888]),
    ];

    private static readonly long[] MediaDiscoveryUdp = [1900, 5353, 5355];

    public static IReadOnlyList<string> Tags(string protocol, long port)
    {
        if (protocol == "udp") return MediaDiscoveryUdp.Contains(port) ? ["media_discovery"] : [];
        return Tcp.Where(t => t.Ports.Contains(port)).Select(t => t.Tag).ToList();
    }

    public static string Label(string tag) => tag == "media_discovery"
        ? "media and device discovery, usually low concern"
        : Tcp.FirstOrDefault(t => t.Tag == tag).Label ?? tag.Replace('_', ' ');

    public static string Sentence(string owner, string protocol, string? scope, IReadOnlyList<long> ports, IReadOnlyList<string> tags)
    {
        var proto = protocol.ToUpperInvariant();
        var shown = ports.Take(8).Select(p => Say.N(p)).ToList();
        var portText = (ports.Count == 1 ? $"{proto} {shown[0]}" : $"{proto} ports {string.Join(", ", shown)}") + (ports.Count > 8 ? $" and {Say.N(ports.Count - 8)} more" : "");
        var tagText = tags.Count == 0 ? "" : $" ({string.Join("; ", tags.Select(Label))})";
        return (protocol, scope) switch
        {
            ("tcp", "wildcard" or "specific") => $"{owner} accepts connections from other devices on {portText}{tagText}.",
            ("udp", "wildcard" or "specific") => $"{owner} receives traffic from other devices on {portText}{tagText}.",
            (_, "loopback") => $"{owner} listens on {portText} on this PC only{tagText}.",
            _ => $"{owner} listens on {portText}{tagText}.",
        };
    }
}

/// <summary>win.security.startup: entry alias, location class, scope, enabled, signer status, publisher alias. No paths/arguments.</summary>
internal sealed class StartupProjector : Projector
{
    private static readonly string[] Sources = ["run", "runonce", "folder", "task", "service"];

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.startup"];
    public override string Source(string collectorId) => "Startup locations";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject == "startup.summary")
        {
            o.Int("total", v["total"]);
            o.Int("signInCount", v["signInCount"]);
            var signInEnabled = o.Int("signInEnabledCount", v["signInEnabledCount"]) ?? 0;
            if (v["bySource"] is JsonObject by)
            {
                var outBy = new JsonObject();
                foreach (var (k, n) in by)
                {
                    if (Array.IndexOf(Sources, k) >= 0 && Read.Long(n) is { } c && c >= 0) outBy[k] = c;
                    else o.Dropped(n ?? JsonValue.Create(0));
                }
                o.V["bySource"] = outBy;
            }
            foreach (var k in new[] { "unsignedCount", "microsoftServicesOmitted", "microsoftTasksOmitted", "taskFoldersDenied", "tasksUnreadable", "servicesUnreadable" })
                o.Int(k, v[k]);
            o.Bool("truncated", v["truncated"]);
            return new("startup.summary", signInEnabled == 1 ? "1 app starts when you sign in." : $"{Say.N(signInEnabled)} apps start when you sign in.");
        }
        if (!subject.StartsWith("startup.", StringComparison.Ordinal)) return null;

        var source = o.Enum("source", v["source"], Sources);
        if (source is null) return null;
        var location = Read.Str(v["location"]);
        var (locationClass, scope) = source switch
        {
            "run" or "runonce" => ("run_key", location == "hkcu" ? "user" : location == "hklm" ? "machine" : "unknown"),
            "folder" => ("startup_folder", location == "user" ? "user" : location == "common" ? "machine" : "unknown"),
            "task" => ("scheduled_task", "unknown"),
            _ => ("service", "machine"),
        };
        o.Const("locationClass", locationClass);
        o.Const("scope", scope);
        o.Enum("view", v["view"], "64", "32");
        var kind = source switch { "task" => "task", "service" => "service", _ => "app" };
        var rawName = Read.Str(v["name"]);
        var name = o.Name("entry", kind, rawName) ?? "An unnamed entry";
        var enabled = o.Bool("enabled", v["enabled"]);
        o.Bool("hasArguments", v["hasArguments"]);
        var signature = o.Enum("signature", v["signature"], "valid", "unsigned", "invalid", "not_found", "unknown");
        o.Bool("catalogSigned", v["catalogSigned"]);
        var trigger = o.Enum("trigger", v["trigger"], "logon", "boot");
        o.Bool("signIn", v["signIn"]);
        var signer = Read.Str(v["signer"]);
        var publisher = o.Name("publisher", "publisher", signer);
        if (publisher is not null)
        {
            var f = Sanitize.Fold(signer);
            o.V["publisherIsMicrosoft"] = (f.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                && !f.Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase);
        }

        var where = source switch
        {
            "run" => scope == "user" ? "starts at sign-in for you (Run key)" : "starts at sign-in for everyone (Run key)",
            "runonce" => "runs once at next sign-in (RunOnce key)",
            "folder" => scope == "user" ? "starts at sign-in from your Startup folder" : "starts at sign-in from the shared Startup folder",
            "task" => trigger == "logon" ? "is a scheduled task that runs at sign-in" : "is a scheduled task that runs at startup",
            _ => "is a service that starts automatically",
        };
        var sig = signature switch
        {
            "valid" when publisher is not null => $" Signed by {publisher}.",
            "valid" => " Signed.",
            "unsigned" => " Not signed.",
            "invalid" => " Its signature did not verify.",
            "not_found" => " Its file was not found.",
            _ => "",
        };
        var off = enabled == false ? " Turned off in startup settings." : "";
        return new($"startup.{source}.{scope}.{Slug(o.AliasOf(kind, rawName))}", $"{name} {where}.{sig}{off}");
    }
}

/// <summary>win.security.defender: mode, protection switches, signature age/version, scan times, threat counts and per-threat status.</summary>
internal sealed class DefenderProjector : Projector
{
    private static readonly string[] Statuses = ["detected", "cleaned", "quarantined", "removed", "allowed", "blocked",
        "quarantine_failed", "remove_failed", "allow_failed", "abandoned", "block_failed", "unknown"];

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.defender"];
    public override string Source(string collectorId) => "Microsoft Defender Antivirus";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        switch (subject)
        {
            case "security.defender.status":
            {
                var mode = o.Enum("mode", v["mode"], "primary", "passive", "side_by_side", "disabled", "unknown");
                o.Bool("amServiceEnabled", v["amServiceEnabled"]);
                o.Bool("antivirusEnabled", v["antivirusEnabled"]);
                var rtp = o.Bool("realTimeProtection", v["realTimeProtection"]);
                o.Bool("tamperProtected", v["tamperProtected"]);
                o.Version("signatureVersion", v["signatureVersion"]);
                o.Time("signatureUpdatedAt", v["signatureUpdatedAt"]);
                o.Int("signatureAgeDays", v["signatureAgeDays"], 0, 65534);
                o.Time("quickScanEndAt", v["quickScanEndAt"]);
                o.Time("fullScanEndAt", v["fullScanEndAt"]);
                var display = mode switch
                {
                    "primary" => $"Microsoft Defender is the active antivirus; real-time protection is {(rtp == true ? "on" : rtp == false ? "off" : "unknown")}.",
                    "passive" => "Microsoft Defender runs in passive mode; another antivirus is primary.",
                    "side_by_side" => "Microsoft Defender runs side by side with another antivirus.",
                    "disabled" => "Microsoft Defender Antivirus is turned off or not running.",
                    _ => "Microsoft Defender reported an unrecognized running mode.",
                };
                return new(subject, display);
            }
            case "security.defender.threats":
            {
                var readable = o.Bool("readable", v["readable"]);
                o.Enum("reason", v["reason"], "query_failed", "needs_admin");
                o.Int("detectionCount", v["detectionCount"]);
                var open = o.Int("actionNeededCount", v["actionNeededCount"]);
                o.Int("historyCount", v["historyCount"]);
                o.Bool("truncated", v["truncated"]);
                var display = readable != true ? "Microsoft Defender's threat history needs administrator rights to read."
                    : open is null or 0 ? "Microsoft Defender reports no threats that still need action."
                    : open == 1 ? "Microsoft Defender reports 1 threat that still needs action." : $"Microsoft Defender reports {Say.N(open.Value)} threats that still need action.";
                return new(subject, display);
            }
        }
        if (!subject.StartsWith("security.defender.threat.", StringComparison.Ordinal)) return null;
        var rawName = Read.Str(v["threatName"]);
        var name = o.Name("threat", "threat", rawName) ?? "An unnamed threat";
        o.Enum("classification", v["classification"], "pua", "malware");
        var status = o.Enum("status", v["status"], Statuses) ?? "unknown";
        o.Bool("actionNeeded", v["actionNeeded"]);
        o.Bool("active", v["active"]);
        o.Int("severityId", v["severityId"], 0, 10);
        o.Time("detectedAt", v["detectedAt"]);
        return new($"security.defender.threat.{Slug(o.AliasOf("threat", rawName))}", $"Microsoft Defender detected {name}; its status is {status.Replace('_', ' ')}.");
    }
}

/// <summary>win.security.firewall: per-profile on/off, in use, default inbound, policy scope.</summary>
internal sealed class FirewallProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.firewall"];
    public override string Source(string collectorId) => "Windows Firewall effective profiles";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject == "firewall.active_profiles")
        {
            var names = new List<string>();
            foreach (var (k, label) in new[] { ("domain", "Domain"), ("private", "Private"), ("public", "Public") })
                if (o.Bool(k, v[k]) == true) names.Add(label);
            return new(subject, names.Count == 0 ? "No network profile is active." : $"Active network profile: {string.Join(", ", names)}.");
        }
        var profile = subject switch { "firewall.profile.domain" => "Domain", "firewall.profile.private" => "Private", "firewall.profile.public" => "Public", _ => null };
        if (profile is null) return null;
        o.Const("profile", profile);
        var enabled = o.Bool("enabled", v["enabled"]);
        o.Bool("localEnabled", v["localEnabled"]);
        var active = o.Bool("active", v["active"]);
        o.Enum("defaultInbound", v["defaultInbound"], "block", "allow");
        o.Bool("blockAllInbound", v["blockAllInbound"]);
        var managed = o.Bool("managedByPolicy", v["managedByPolicy"]);
        o.Enum("policyOwner", v["policyOwner"], "group_policy", "local");
        var state = enabled == true ? "on" : enabled == false ? "off" : "unknown";
        return new(subject, $"The {profile} firewall profile is {state}{(active == true ? " and in use now" : "")}.{(managed == true ? " It is set by policy." : "")}");
    }
}

/// <summary>win.security.secureboot: firmware type and Secure Boot state.</summary>
internal sealed class SecureBootProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.secureboot"];
    public override string Source(string collectorId) => "Secure Boot state";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject != "security.secureboot") return null;
        var firmware = o.Enum("firmware", v["firmware"], "bios", "uefi", "unknown");
        var enabled = o.Bool("secureBootEnabled", v["secureBootEnabled"]);
        o.Bool("capable", v["capable"]);
        return new(subject, firmware switch
        {
            "bios" => "This PC starts in legacy BIOS mode, so Secure Boot is not in use.",
            "uefi" when enabled == true => "Secure Boot is on.",
            "uefi" when enabled == false => "This PC uses UEFI, and Secure Boot is off.",
            _ => "Secure Boot state is not reported.",
        });
    }
}

/// <summary>win.security.uac: effective state and the enumerated policy values (no edition string).</summary>
internal sealed class UacProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.uac"];
    public override string Source(string collectorId) => "UAC policy registry";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject != "security.uac") return null;
        var enabled = o.Bool("enabled", v["enabled"]);
        o.Bool("enableLuaPresent", v["enableLuaPresent"]);
        o.Int("enableLua", v["enableLua"], 0, 1);
        o.Enum("enableLuaKind", v["enableLuaKind"], "REG_SZ", "REG_EXPAND_SZ", "REG_MULTI_SZ", "REG_BINARY", "REG_DWORD", "REG_QWORD", "other");
        var consent = o.Int("consentPromptBehaviorAdmin", v["consentPromptBehaviorAdmin"], 0, 5);
        o.Int("promptOnSecureDesktop", v["promptOnSecureDesktop"], 0, 1);
        return new(subject, enabled switch
        {
            false => "User Account Control is turned off.",
            true when consent == 0 => "User Account Control is on, but admin requests are approved without asking.",
            true => "User Account Control is on.",
            _ => "User Account Control has an unexpected setting type.",
        });
    }
}

/// <summary>win.security.wsc: Security Center health, product counts, per-product alias/state/role.</summary>
internal sealed class WscProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.security.wsc"];
    public override string Source(string collectorId) => "Windows Security Center";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject is "security.wsc.health.antivirus" or "security.wsc.health.firewall")
        {
            var kind = subject.EndsWith("antivirus", StringComparison.Ordinal) ? "antivirus" : "firewall";
            o.Const("provider", kind);
            var usable = o.Bool("usable", v["usable"]);
            var health = o.Enum("health", v["health"], "good", "not_monitored", "poor", "snoozed", "unknown", "unavailable");
            if (Read.Str(v["hresult"])?.ToLowerInvariant() is { } hr && Out.IsHex(hr)) o.V["hresult"] = hr; else o.Dropped(v["hresult"]);
            return new(subject, usable == true && health is not null
                ? $"Windows Security Center reports {kind} protection as {health.Replace('_', ' ')}."
                : $"Windows Security Center did not give a usable {kind} status.");
        }
        if (subject is "security.wsc.products.av" or "security.wsc.products.fw")
        {
            var kind = subject.EndsWith(".av", StringComparison.Ordinal) ? "antivirus" : "firewall";
            o.Const("provider", kind);
            var count = o.Int("count", v["count"]) ?? 0;
            o.Int("onCount", v["onCount"]);
            return new(subject, count == 1 ? $"1 {kind} product is registered with Windows." : $"{Say.N(count)} {kind} products are registered with Windows.");
        }
        string prefix;
        if (subject.StartsWith("security.av.product.", StringComparison.Ordinal)) prefix = "av";
        else if (subject.StartsWith("security.fw.product.", StringComparison.Ordinal)) prefix = "fw";
        else return null;
        var provider = prefix == "av" ? "antivirus" : "firewall";
        o.Const("provider", provider);
        var rawName = Read.Str(v["name"]);
        var name = o.Name("product", "product", rawName) ?? "An unnamed product";
        var state = o.Enum("state", v["state"], "on", "off", "snoozed", "expired", "unknown") ?? "unknown";
        if (prefix == "av") o.Enum("signatures", v["signatures"], "up_to_date", "out_of_date");
        o.Enum("role", v["role"], "primary", "active", "inactive");
        o.Bool("isDefault", v["isDefault"]);
        o.Bool("isMicrosoft", v["isMicrosoft"]);
        return new($"security.{prefix}.product.{Slug(o.AliasOf("product", rawName))}", $"{name} is registered for {provider} and reports {(state == "unknown" ? "an unknown state" : "it is " + state)}.");
    }
}

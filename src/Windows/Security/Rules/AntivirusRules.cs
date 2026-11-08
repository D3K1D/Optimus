using System.Globalization;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Rules;

/// <summary>av.none_active — Security Center (S_OK) lists no antivirus that is on.</summary>
internal sealed class AvNoneActiveRule : IRule
{
    public string Id => "av.none_active";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [WscCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, WscCollector.CollectorId)) return RuleKit.NotCovered(WscCollector.CollectorId);
        var health = c.Get("security.wsc.health.antivirus");
        var list = c.Get("security.wsc.products.av");
        if (health is null || J.Bool(health.Value["usable"]) != true)
            return RuleResult.Insufficient("Security Center gave no usable antivirus status.");
        if (list is null) return RuleResult.Insufficient("Antivirus products could not be listed.");
        var products = c.WithPrefix("security.av.product.").ToList();
        if (products.Any(p => J.Str(p.Value["state"]) == "on")) return RuleResult.Clear();
        if (J.Str(health.Value["health"]) == "good") return RuleResult.Clear();

        var evidence = new List<string> { health.Id, list.Id };
        evidence.AddRange(products.Select(p => p.Id));
        return RuleResult.Found(new FindingDraft(
            "security.av.none_active", "No antivirus is turned on", "high", "high", evidence,
            RuleKit.Card("Windows Security Center shows no antivirus turned on. Would you like to review protection?",
                "windowsdefender://threat",
                "Open Windows Security › Virus & threat protection.",
                "If you use another antivirus, open it and turn protection on.",
                "Otherwise, turn on Microsoft Defender real-time protection.")));
    }
}

/// <summary>av.rtp_off — Defender is the primary antivirus and real-time protection is off.</summary>
internal sealed class AvRtpOffRule : IRule
{
    public string Id => "av.rtp_off";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [DefenderCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, DefenderCollector.CollectorId)) return RuleKit.NotCovered(DefenderCollector.CollectorId);
        var s = c.Get("security.defender.status");
        if (s is null) return RuleResult.Insufficient("No Defender status evidence.");
        if (J.Str(s.Value["mode"]) != "primary") return RuleResult.Inapplicable("Microsoft Defender is not the primary antivirus.");
        var rtp = J.Bool(s.Value["realTimeProtection"]);
        if (rtp is null) return RuleResult.Insufficient("Real-time protection state unknown.");
        if (rtp == true) return RuleResult.Clear();
        return RuleResult.Found(new FindingDraft(
            "security.defender.rtp", "Real-time protection is off", "high", "high", [s.Id],
            RuleKit.Card("Microsoft Defender real-time protection is off. Would you like to review it?",
                "windowsdefender://threat",
                "Open Windows Security › Virus & threat protection.",
                "Under Virus & threat protection settings, choose Manage settings.",
                "Turn Real-time protection on.")));
    }
}

/// <summary>av.signatures_stale — the primary antivirus's definitions are more than 7 days old.</summary>
internal sealed class AvSignaturesStaleRule : IRule
{
    public const int MaxAgeDays = 7;
    public string Id => "av.signatures_stale";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [DefenderCollector.CollectorId, WscCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        var defender = RuleKit.Usable(c, DefenderCollector.CollectorId) ? c.Get("security.defender.status") : null;
        if (defender is not null && J.Str(defender.Value["mode"]) == "primary")
        {
            var age = AgeDays(defender);
            if (age is null) return RuleResult.Insufficient("Definition age unknown.");
            if (age <= MaxAgeDays) return RuleResult.Clear();
            var days = age.Value.ToString(CultureInfo.InvariantCulture);
            return RuleResult.Found(new FindingDraft(
                "security.defender.signatures", "Virus definitions are out of date", "medium", "high", [defender.Id],
                RuleKit.Card($"Microsoft Defender's virus definitions are {days} days old. Would you like to see how to update them?",
                    "windowsdefender://threat",
                    "Open Windows Security › Virus & threat protection.",
                    "Under Virus & threat protection updates, choose Protection updates.",
                    "Choose Check for updates.")));
        }

        // Another product is primary: rely on what Security Center attests for it.
        if (!RuleKit.Usable(c, WscCollector.CollectorId))
            return defender is null ? RuleKit.NotCovered(DefenderCollector.CollectorId) : RuleKit.NotCovered(WscCollector.CollectorId);
        var primary = c.WithPrefix("security.av.product.")
            .Where(p => J.Str(p.Value["state"]) == "on")
            .OrderByDescending(p => J.Bool(p.Value["isDefault"]) == true)
            .FirstOrDefault();
        if (primary is null) return RuleResult.Insufficient("No active antivirus product to check.");
        if (J.Str(primary.Value["signatures"]) != "out_of_date") return RuleResult.Clear();
        var name = J.Str(primary.Value["name"]) ?? "Your antivirus";
        return RuleResult.Found(new FindingDraft(
            "security.av.signatures." + primary.Subject.Split('.').Last(), "Antivirus definitions are out of date", "medium", "medium", [primary.Id],
            RuleKit.Card($"Windows says {name} has out-of-date definitions. Would you like to see how to update them?",
                "windowsdefender://threat",
                $"Open {name}.",
                "Look for an update or check-for-updates option.",
                "Run the update, then scan again.")));
    }

    internal static long? AgeDays(EvidenceItem status)
    {
        var updated = J.Time(status.Value["signatureUpdatedAt"]);
        var now = RuleKit.ObservedAt(status);
        if (updated is not null && now is not null) return Math.Max(0, (long)Math.Floor((now.Value - updated.Value).TotalDays));
        return J.Long(status.Value["signatureAgeDays"]);
    }
}

/// <summary>
/// av.active_threat — Defender reports a detection whose disposition still needs action. Malware → high;
/// a potentially unwanted app keeps its own classification → medium. Quarantined/removed/allowed are history.
/// </summary>
internal sealed class AvActiveThreatRule : IRule
{
    private const int FindingCap = 5;
    public string Id => "av.active_threat";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [DefenderCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, DefenderCollector.CollectorId)) return RuleKit.NotCovered(DefenderCollector.CollectorId);
        var summary = c.Get("security.defender.threats");
        if (summary is null) return RuleResult.Insufficient("No Defender threat evidence.");
        if (J.Bool(summary.Value["readable"]) != true) return RuleResult.Insufficient("Defender threat history needs administrator rights.");
        var open = c.WithPrefix("security.defender.threat.").Where(t => J.Bool(t.Value["actionNeeded"]) == true).ToList();
        if (open.Count == 0) return RuleResult.Clear();

        var findings = open.Take(FindingCap).Select(t =>
        {
            var name = J.Str(t.Value["threatName"]) ?? "a threat";
            var pua = J.Str(t.Value["classification"]) == "pua";
            var text = pua
                ? $"Microsoft Defender flagged {name} as a potentially unwanted app, and it still needs action. Would you like to review it?"
                : $"Microsoft Defender found {name}, and it still needs action. Would you like to review it?";
            return new FindingDraft(t.Subject, pua ? "Potentially unwanted app needs action" : "Threat needs action",
                pua ? "medium" : "high", "high", [t.Id, summary.Id],
                RuleKit.Card(text, "windowsdefender://threat",
                    "Open Windows Security › Virus & threat protection.",
                    "Under Current threats, choose the item.",
                    "Pick an action and choose Start actions."));
        }).ToArray();
        return RuleResult.Found(findings);
    }
}

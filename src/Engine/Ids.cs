using System.Security.Cryptography;
using System.Text;

namespace OptimizationTool.Engine;

/// <summary>
/// Deterministic record IDs (protocol §6), shared with the UI's fixture adapter.
/// Each hash is SHA-256 over the UTF-8 string of its parts joined with "\u0000" (NUL, the ‖ separator),
/// truncated to the first 16 lowercase hex characters:
///   evidence     ev-   + H(scanId \0 collectorId \0 subject)
///   fingerprint        H(ruleId \0 subject of the rule's first evidence)
///   finding      fnd-  + H(scanId \0 ruleId \0 fingerprint)
///   report       report- + H(scanId)
///   card         card- + H(reportId \0 findingId)
///   gap          gap-  + H(scanId \0 collectorId)
/// Scan and inventory IDs are random: "scan-" / "inv-" + 16 hex.
/// </summary>
public static class Ids
{
    public static string Hash16(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts)));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    public static string Evidence(string scanId, string collectorId, string subject) => "ev-" + Hash16(scanId, collectorId, subject);
    public static string Fingerprint(string ruleId, string firstSubject) => Hash16(ruleId, firstSubject);
    public static string Finding(string scanId, string ruleId, string fingerprint) => "fnd-" + Hash16(scanId, ruleId, fingerprint);
    public static string Report(string scanId) => "report-" + Hash16(scanId);
    public static string Card(string reportId, string findingId) => "card-" + Hash16(reportId, findingId);
    public static string Gap(string scanId, string collectorId) => "gap-" + Hash16(scanId, collectorId);

    public static string NewScan() => "scan-" + RandomHex();
    public static string NewInventory() => "inv-" + RandomHex();
    public static string RandomHex(int bytes = 8) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}

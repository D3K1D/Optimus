using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Broker;

/// <summary>
/// The broker's protected journal on disk (m9 §3.5, §13 R1): <c>%ProgramData%\OptimizationTool\journal</c> (a new leaf name, so a
/// folder an older build adopted is never trusted), created and validated by the broker as a trusted chain, written only by the
/// elevated broker (administrators-only write, the user reads). Append-only, one file per record, each created new:
/// <c>&lt;receiptId&gt;.session.json</c> {receiptId, planId, planHash, at, steps:[{n, actionId}]},
/// <c>&lt;receiptId&gt;.&lt;n&gt;.prepared.json</c> {n, actionId, target, before (the preimage), after, contentHash, at} — written
/// before the write — and <c>&lt;receiptId&gt;.&lt;n&gt;.outcome.json</c> {n, state, observed, code, at}. The host reads it
/// after a restart; the broker reads it to find the original of an undo.
/// Every record carries a MAC (<see cref="MacField"/>) made with the broker's key (<see cref="KeyFile"/>, SYSTEM and
/// administrators only), so only the broker can verify it: the broker's <see cref="Find"/> trusts nothing else. The host's
/// <see cref="Read"/> can't verify it and is used for restart classification only, never as authority for an undo.
/// </summary>
public static class BrokerJournalFiles
{
    public const long MaxFileBytes = 64 * 1024;
    private const int MaxSessions = 2000;

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OptimizationTool", "journal");

    public static string SessionFile(string receiptId) => receiptId + ".session.json";
    public static string PreparedFile(string receiptId, int n) => $"{receiptId}.{n}.prepared.json";
    public static string OutcomeFile(string receiptId, int n) => $"{receiptId}.{n}.outcome.json";

    /// <summary>The broker's MAC key in the journal folder: 32 random bytes, owner Administrators, SYSTEM + Administrators only.</summary>
    public const string KeyFile = "journal.key";

    /// <summary>Folders the broker moved pre-existing contents into when it first provisioned the journal (never read).</summary>
    public const string QuarantinePrefix = "quarantine-";

    /// <summary>
    /// The journal's records for one plan (the newest session for it); null when the folder can't be read. Unauthenticated
    /// (the host can't read the key): restart classification only, which then reads live state.
    /// </summary>
    public static ElevatedJournal? Read(string root, string planId)
    {
        try
        {
            if (!Directory.Exists(root)) return new ElevatedJournal(null, []);
            if (IsReparse(root)) return null;
            JsonObject? best = null;
            foreach (var file in Directory.EnumerateFiles(root, "rcpt-*.session.json").Take(MaxSessions))
                if (Load(file) is { } s && Str(s["planId"]) == planId && (best is null || string.CompareOrdinal(Str(s["at"]), Str(best["at"])) > 0)) best = s;
            if (best is null || Str(best["receiptId"]) is not { } receiptId || !BrokerWire.IsReceiptId(receiptId)) return new ElevatedJournal(null, []);
            var steps = new List<JournalStep>();
            foreach (var n in Enumerable.Range(1, BrokerRequest.MaxSteps))
            {
                var prepared = Load(Path.Combine(root, PreparedFile(receiptId, n)));
                if (prepared is null) continue;
                var outcome = Load(Path.Combine(root, OutcomeFile(receiptId, n)));
                steps.Add(new JournalStep(n, Str(prepared["actionId"]) ?? "", true, Str(outcome?["state"]), outcome?["observed"]?.DeepClone() as JsonObject));
            }
            return new ElevatedJournal(receiptId, steps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The broker's own record of step <paramref name="n"/> of session <paramref name="receiptId"/>, or null. Only the
    /// elevated broker calls this (m9 §3.5 "as built": provenance): every record must carry a MAC made with the broker's
    /// <paramref name="key"/> (readable by SYSTEM and administrators only) over its file name and contents, and the three
    /// records must agree on the receipt, the step and the action. A record with a missing or wrong MAC, or that belongs to
    /// another session or step, is ignored and reported through <paramref name="rejected"/>.
    /// </summary>
    public static BrokerRecordStep? Find(string root, string receiptId, int n, byte[] key, Action<string>? rejected = null)
    {
        if (!BrokerWire.IsReceiptId(receiptId) || n is < 1 or > BrokerRequest.MaxSteps || key.Length < 16) return null;
        try
        {
            if (!Directory.Exists(root) || IsReparse(root)) return null;
            var session = Authentic(root, SessionFile(receiptId), key, rejected);
            var prepared = Authentic(root, PreparedFile(receiptId, n), key, rejected);
            var outcome = Authentic(root, OutcomeFile(receiptId, n), key, rejected);
            if (session is null || prepared is null) return null;
            var actionId = Str(prepared["actionId"]);
            var listed = session["steps"] is JsonArray steps && steps.OfType<JsonObject>().Any(s => Int(s["n"]) == n && Str(s["actionId"]) == actionId);
            if (Str(session["receiptId"]) != receiptId || Str(prepared["receiptId"]) != receiptId || Int(prepared["n"]) != n || actionId is null || !listed)
            {
                rejected?.Invoke(PreparedFile(receiptId, n));
                return null;
            }
            if (outcome is not null && (Str(outcome["receiptId"]) != receiptId || Int(outcome["n"]) != n))
            {
                rejected?.Invoke(OutcomeFile(receiptId, n));
                outcome = null; // an outcome of another step never vouches for this one
            }
            if (prepared["target"] is not JsonObject t || prepared["before"] is not JsonObject b || prepared["after"] is not JsonObject a) return null;
            return new BrokerRecordStep(receiptId, Str(session["planId"]) ?? "", n, actionId, (JsonObject)t.DeepClone(), (JsonObject)b.DeepClone(),
                (JsonObject)a.DeepClone(), Str(outcome?["state"]), outcome?["observed"]?.DeepClone() as JsonObject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The record's MAC field name. The MAC is HMAC-SHA256(key, fileName + "\n" + the record without it, as written).</summary>
    public const string MacField = "mac";

    /// <summary>Adds the MAC to a record the broker is about to write as <paramref name="fileName"/>; returns the bytes to write.</summary>
    public static byte[] Seal(string fileName, JsonObject record, byte[] key)
    {
        record.Remove(MacField);
        record[MacField] = Mac(fileName, record, key);
        return System.Text.Encoding.UTF8.GetBytes(record.ToJsonString());
    }

    /// <summary>True when <paramref name="record"/> (as read from <paramref name="fileName"/>) carries the broker's MAC.</summary>
    public static bool Verify(string fileName, JsonObject record, byte[] key)
    {
        if (Str(record[MacField]) is not { } mac) return false;
        var body = (JsonObject)record.DeepClone();
        body.Remove(MacField);
        byte[] claimed;
        try { claimed = Convert.FromHexString(mac); }
        catch (FormatException) { return false; }
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(claimed, Convert.FromHexString(Mac(fileName, body, key)));
    }

    private static string Mac(string fileName, JsonObject body, byte[] key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(fileName + "\n" + body.ToJsonString());
        return Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(key, bytes));
    }

    /// <summary>A record that exists and carries a valid MAC, or null (a present record that fails the MAC is reported).</summary>
    public static JsonObject? Authentic(string root, string fileName, byte[] key, Action<string>? rejected)
    {
        var path = Path.Combine(root, fileName);
        if (!File.Exists(path)) return null;
        var record = Load(path);
        if (record is not null && Verify(fileName, record, key)) return record;
        rejected?.Invoke(fileName);
        return null;
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static JsonObject? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 }) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static bool IsReparse(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Broker;

// Broker protocol v2 (contracts/m9-ai-powers.md §3.5), shared by the host (BrokerElevation) and the elevated broker.
// Host → broker: one "request" frame, then at most one "stop" frame. Broker → host: "progress" frames (defined for later
// long tools, not sent yet) and exactly one "outcome" frame. Every frame is length-prefixed JSON, ≤ 64 KB, depth ≤ 16.
// Only v2 exists: the host always speaks it, the firewall action included.

/// <summary>One step of a request, as the host approved it. The broker trusts none of it: it re-resolves and compares the hash.</summary>
public sealed record BrokerStepRequest(int N, string ActionId, string HandlerVersion, string ContentHash, JsonObject Target, JsonObject Before,
    JsonObject After, JsonObject? Parameters, ElevatedUndoOf? UndoOf)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["n"] = N, ["actionId"] = ActionId, ["handlerVersion"] = HandlerVersion, ["contentHash"] = ContentHash,
            ["target"] = Target.DeepClone(), ["before"] = Before.DeepClone(), ["after"] = After.DeepClone(), ["parameters"] = Parameters?.DeepClone(),
        };
        if (UndoOf is { } u) o["undoOf"] = new JsonObject { ["receiptId"] = u.ReceiptId, ["n"] = u.N };
        return o;
    }

    /// <summary>The step as built from an approved plan step: the hash is computed here, over exactly what is sent.</summary>
    public static BrokerStepRequest From(ElevatedStep s) =>
        new(s.N, s.ActionId, s.HandlerVersion, BrokerContent.Hash(s.ActionId, s.HandlerVersion, s.Target, s.Before, s.After, s.Parameters),
            (JsonObject)s.Target.DeepClone(), (JsonObject)s.Before.DeepClone(), (JsonObject)s.After.DeepClone(), s.Parameters?.DeepClone() as JsonObject, s.UndoOf);
}

/// <summary><c>{type:"request", version:2, nonce, planId, planHash, steps:[…]}</c>: ≤ 6 steps, n strictly ascending.</summary>
public sealed record BrokerRequest(string Nonce, string PlanId, string PlanHash, IReadOnlyList<BrokerStepRequest> Steps)
{
    public const int Version = 2;
    public const int MaxSteps = 6;

    public JsonObject ToJson() => new()
    {
        ["type"] = "request", ["version"] = Version, ["nonce"] = Nonce, ["planId"] = PlanId, ["planHash"] = PlanHash,
        ["steps"] = new JsonArray([.. Steps.Select(s => (JsonNode?)s.ToJson())]),
    };

    private static readonly HashSet<string> TopKeys = ["type", "version", "nonce", "planId", "planHash", "steps"];
    private static readonly HashSet<string> StepKeys = ["n", "actionId", "handlerVersion", "contentHash", "target", "before", "after", "parameters", "undoOf"];

    /// <summary>Null when any field is missing, unknown, of the wrong type, or out of shape (strict: v2 only).</summary>
    public static BrokerRequest? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || BrokerWire.Str(o["type"]) != "request" || BrokerWire.Int(o["version"]) != Version) return null;
        if (o.Any(kv => !TopKeys.Contains(kv.Key))) return null;
        var nonce = BrokerWire.Str(o["nonce"], 64);
        var planId = BrokerWire.Str(o["planId"], 80);
        var planHash = BrokerWire.Str(o["planHash"], 128);
        if (nonce is null || planId is null || planHash is null || o["steps"] is not JsonArray arr || arr.Count is 0 or > MaxSteps) return null;
        var steps = new List<BrokerStepRequest>();
        var last = 0;
        foreach (var item in arr)
        {
            if (item is not JsonObject s || s.Any(kv => !StepKeys.Contains(kv.Key))) return null;
            var n = BrokerWire.Int(s["n"]);
            var actionId = BrokerWire.Str(s["actionId"], 64);
            var version = BrokerWire.Str(s["handlerVersion"], 32);
            var hash = BrokerWire.Str(s["contentHash"], 64);
            if (n is not { } nn || nn <= last || nn > MaxSteps || actionId is null || version is null || hash is null || !BrokerWire.IsHash(hash)) return null;
            if (s["target"] is not JsonObject t || s["before"] is not JsonObject b || s["after"] is not JsonObject a) return null;
            if (s["parameters"] is not (null or JsonObject)) return null;
            ElevatedUndoOf? undoOf = null;
            if (s["undoOf"] is JsonObject u)
            {
                if (u.Count != 2 || BrokerWire.Str(u["receiptId"], 64) is not { } rid || !BrokerWire.IsReceiptId(rid) || BrokerWire.Int(u["n"]) is not { } un || un is < 1 or > MaxSteps)
                    return null;
                undoOf = new ElevatedUndoOf(rid, un);
            }
            else if (s["undoOf"] is not null) return null;
            last = nn;
            steps.Add(new BrokerStepRequest(nn, actionId, version, hash, (JsonObject)t.DeepClone(), (JsonObject)b.DeepClone(), (JsonObject)a.DeepClone(),
                s["parameters"]?.DeepClone() as JsonObject, undoOf));
        }
        return new BrokerRequest(nonce, planId, planHash, steps);
    }
}

/// <summary>One step's result. State: verified | completed | failed | indeterminate | skipped | declined.</summary>
public sealed record BrokerStepOutcome(int N, string State, JsonObject Observed, string ReceiptText, string? Code = null, string? Message = null)
{
    public static readonly string[] States = ["verified", "completed", "failed", "indeterminate", "skipped", "declined"];

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["n"] = N, ["state"] = State, ["observed"] = Observed.DeepClone(), ["receiptText"] = ReceiptText };
        if (Code is not null) o["code"] = Code;
        if (Message is not null) o["message"] = Message;
        return o;
    }

    public ActionOutcome ToOutcome() => new(State, (JsonObject)Observed.DeepClone(), ReceiptText, Code, Message);
}

/// <summary><c>{type:"outcome", nonce, steps:[{n, state, observed, receiptText, code?, message?}], receiptId}</c>.</summary>
public sealed record BrokerResponse(string Nonce, IReadOnlyList<BrokerStepOutcome> Steps, string? ReceiptId)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = "outcome", ["nonce"] = Nonce, ["steps"] = new JsonArray([.. Steps.Select(s => (JsonNode?)s.ToJson())]), ["receiptId"] = ReceiptId,
    };

    public static BrokerResponse? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || BrokerWire.Str(o["type"]) != "outcome" || BrokerWire.Str(o["nonce"], 64) is not { } nonce) return null;
        if (o["steps"] is not JsonArray arr || arr.Count > BrokerRequest.MaxSteps) return null;
        var steps = new List<BrokerStepOutcome>();
        foreach (var item in arr)
        {
            if (item is not JsonObject s || BrokerWire.Int(s["n"]) is not { } n || BrokerWire.Str(s["state"]) is not { } state || !BrokerStepOutcome.States.Contains(state)) return null;
            steps.Add(new BrokerStepOutcome(n, state, (s["observed"] as JsonObject)?.DeepClone() as JsonObject ?? [],
                BrokerWire.Clip(BrokerWire.Str(s["receiptText"]), 300) ?? "", BrokerWire.Clip(BrokerWire.Str(s["code"]), 64), BrokerWire.Clip(BrokerWire.Str(s["message"]), 300)));
        }
        var receiptId = BrokerWire.Str(o["receiptId"], 64);
        return new BrokerResponse(nonce, steps, receiptId is not null && BrokerWire.IsReceiptId(receiptId) ? receiptId : null);
    }
}

/// <summary>Host → broker <c>{type:"stop", nonce}</c>: finish the step in flight, start no further step.</summary>
public static class BrokerStop
{
    public static JsonObject ToJson(string nonce) => new() { ["type"] = "stop", ["nonce"] = nonce };
    public static bool Is(JsonNode? node, string nonce) =>
        node is JsonObject o && BrokerWire.Str(o["type"]) == "stop" && BrokerWire.Str(o["nonce"], 64) is { } n && BrokerProtocol.NonceEquals(n, nonce);
}

/// <summary>Broker → host <c>{type:"progress", nonce, n, text}</c>: a long tool's progress line (≤ 120 chars), any number before the outcome.</summary>
public static class BrokerProgress
{
    public static JsonObject ToJson(string nonce, int n, string text) => new() { ["type"] = "progress", ["nonce"] = nonce, ["n"] = n, ["text"] = BrokerWire.Clip(text, 120) };
    public static bool Is(JsonNode? node) => node is JsonObject o && BrokerWire.Str(o["type"]) == "progress";

    /// <summary>(n, text) of a well-formed progress frame for this session's nonce; null otherwise (unknown keys, another nonce, control characters).</summary>
    public static (int N, string Text)? Parse(JsonNode? node, string nonce)
    {
        if (node is not JsonObject o || o.Count != 4 || BrokerWire.Str(o["type"]) != "progress" || BrokerWire.Str(o["nonce"], 64) is not { } n
            || !BrokerProtocol.NonceEquals(n, nonce) || BrokerWire.Int(o["n"]) is not { } step || step is < 1 or > BrokerRequest.MaxSteps
            || BrokerWire.Str(o["text"], 120) is not { } text || text.Any(char.IsControl))
            return null;
        return (step, text);
    }
}

/// <summary>
/// Host → broker, an elevated script (m9 §5.4): <c>{type:"script", version:1, nonce, planId, operationId, sha256,
/// bytes:&lt;base64&gt;, options:{bypassPolicy}}</c>. The broker trusts none of it: it re-checks size, the one serialisation,
/// the hash, the guard, the policy and shows the bytes' text in its own window. bypassPolicy is what the user's card said
/// ("runs even though scripts are normally off"); the broker reads the policy itself and refuses a mismatch.
/// </summary>
public sealed record BrokerScriptRequest(string Nonce, string PlanId, string OperationId, string Sha256, byte[] Bytes, bool BypassPolicy)
{
    public const int Version = 1;
    /// <summary>The script cap (§5.1: 8 KB with the BOM); the frame is far below 64 KB.</summary>
    public const int MaxBytes = Engine.Actions.Scripts.ScriptText.MaxBytes;

    public JsonObject ToJson() => new()
    {
        ["type"] = "script", ["version"] = Version, ["nonce"] = Nonce, ["planId"] = PlanId, ["operationId"] = OperationId, ["sha256"] = Sha256,
        ["bytes"] = Convert.ToBase64String(Bytes), ["options"] = new JsonObject { ["bypassPolicy"] = BypassPolicy },
    };

    public static bool IsScript(JsonNode? node) => node is JsonObject o && BrokerWire.Str(o["type"]) == "script";

    private static readonly HashSet<string> Keys = ["type", "version", "nonce", "planId", "operationId", "sha256", "bytes", "options"];

    /// <summary>Null when anything is missing, unknown, mistyped or over the size cap (strict).</summary>
    public static BrokerScriptRequest? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || BrokerWire.Str(o["type"]) != "script" || BrokerWire.Int(o["version"]) != Version || o.Any(kv => !Keys.Contains(kv.Key))) return null;
        var nonce = BrokerWire.Str(o["nonce"], 64);
        var planId = BrokerWire.Str(o["planId"], 80);
        var operationId = BrokerWire.Str(o["operationId"], 64);
        var sha = BrokerWire.Str(o["sha256"], 64);
        var b64 = BrokerWire.Str(o["bytes"], (MaxBytes + 2) / 3 * 4);
        if (nonce is null || planId is null || operationId is null || sha is null || !BrokerWire.IsHash(sha) || b64 is null) return null;
        if (o["options"] is not JsonObject opt || opt.Count != 1 || opt["bypassPolicy"] is not JsonValue bv || !bv.TryGetValue<bool>(out var bypass)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        return bytes.Length is 0 or > MaxBytes ? null : new BrokerScriptRequest(nonce, planId, operationId, sha, bytes, bypass);
    }
}

/// <summary>
/// Broker → host frames of an elevated script run (m9 §5.4), each sequence-numbered from 1 with no gaps:
/// <c>{type:"script_started", nonce, seq}</c> once the process is in its job and running; any number of
/// <c>{type:"transcript", nonce, seq, lines:[{err, text}]}</c> (≤ 64 lines of ≤ 4096 characters each, ≤ 1 MiB of text in
/// all, the rest only counted); and the terminal <c>{type:"script_end", nonce, seq, state:"exited"|"killed"|"not_started",
/// exitCode, code, lines, truncated}</c>, always last, always sent (the caps leave room for it).
/// </summary>
public static class BrokerScriptFrames
{
    public const int MaxLinesPerFrame = 64, MaxLineChars = 4096, MaxTranscriptChars = 1 << 20;

    public static JsonObject Started(string nonce, int seq) => new() { ["type"] = "script_started", ["nonce"] = nonce, ["seq"] = seq };

    public static JsonObject Transcript(string nonce, int seq, IReadOnlyList<(bool Err, string Text)> lines) => new()
    {
        ["type"] = "transcript", ["nonce"] = nonce, ["seq"] = seq,
        ["lines"] = new JsonArray([.. lines.Take(MaxLinesPerFrame).Select(l => (JsonNode?)new JsonObject { ["err"] = l.Err, ["text"] = BrokerWire.Clip(l.Text, MaxLineChars) })]),
    };

    public static JsonObject End(string nonce, int seq, string state, int? exitCode, string? code, int lines, bool truncated) => new()
    {
        ["type"] = "script_end", ["nonce"] = nonce, ["seq"] = seq, ["state"] = state, ["exitCode"] = exitCode, ["code"] = code, ["lines"] = lines, ["truncated"] = truncated,
    };

    /// <summary>One parsed frame for this nonce: kind started | transcript | end, or null when malformed or foreign.</summary>
    public sealed record Frame(string Kind, int Seq, IReadOnlyList<(bool Err, string Text)> Lines, string? State, int? ExitCode, string? Code);

    public static Frame? Parse(JsonNode? node, string nonce)
    {
        if (node is not JsonObject o || BrokerWire.Str(o["nonce"], 64) is not { } n || !BrokerProtocol.NonceEquals(n, nonce) || BrokerWire.Int(o["seq"]) is not { } seq || seq < 1)
            return null;
        switch (BrokerWire.Str(o["type"]))
        {
            case "script_started" when o.Count == 3:
                return new Frame("started", seq, [], null, null, null);
            case "transcript" when o.Count == 4 && o["lines"] is JsonArray { Count: > 0 and <= MaxLinesPerFrame } arr:
                var lines = new List<(bool, string)>();
                foreach (var item in arr)
                {
                    if (item is not JsonObject l || l.Count != 2 || l["err"] is not JsonValue ev || !ev.TryGetValue<bool>(out var err)
                        || l["text"] is not JsonValue tv || !tv.TryGetValue<string>(out var text) || text.Length > MaxLineChars) return null;
                    lines.Add((err, text));
                }
                return new Frame("transcript", seq, lines, null, null, null);
            case "script_end" when o.Count == 8:
                var state = BrokerWire.Str(o["state"]);
                if (state is not ("exited" or "killed" or "not_started")) return null;
                int? exit = o["exitCode"] is null ? null : BrokerWire.Int(o["exitCode"]) ?? -1;
                return new Frame("end", seq, [], state, state == "exited" ? exit : null, BrokerWire.Clip(BrokerWire.Str(o["code"]), 64));
            default:
                return null;
        }
    }
}

/// <summary>One check of a read request: <c>{n, id, handlerVersion, window}</c>, window = whole days or null.</summary>
public sealed record BrokerReadCheck(int N, string Id, string HandlerVersion, int? Window)
{
    public JsonObject ToJson() => new() { ["n"] = N, ["id"] = Id, ["handlerVersion"] = HandlerVersion, ["window"] = Window };
}

/// <summary>
/// Host → helper, administrator checks (m9 §17.2): <c>{type:"read", version:1, nonce, requestId, checks:[{n, id,
/// handlerVersion, window|null}]}</c>, 1–4 checks, n strictly ascending from 1 to at most 4. One connection carries one
/// request and its type is fixed by the first frame: after a read frame the helper accepts nothing but <c>stop</c>.
/// The helper trusts none of it: it looks every id up in its own compiled catalog and adapters.
/// </summary>
public sealed record BrokerReadRequest(string Nonce, string RequestId, IReadOnlyList<BrokerReadCheck> Checks)
{
    public const int Version = 1;
    public const int MaxChecks = Engine.Agent.AdminChecks.MaxChecksPerRequest;

    public JsonObject ToJson() => new()
    {
        ["type"] = "read", ["version"] = Version, ["nonce"] = Nonce, ["requestId"] = RequestId,
        ["checks"] = new JsonArray([.. Checks.Select(c => (JsonNode?)c.ToJson())]),
    };

    public static bool IsRead(JsonNode? node) => node is JsonObject o && BrokerWire.Str(o["type"]) == "read";

    private static readonly HashSet<string> Keys = ["type", "version", "nonce", "requestId", "checks"];
    private static readonly HashSet<string> CheckKeys = ["n", "id", "handlerVersion", "window"];

    public static bool IsValidRequestId(string? id) => id is not null && BrokerWire.IsRequestId(id);

    /// <summary>A well-formed request id: 1–64 of [A-Za-z0-9_.:-], or null.</summary>
    public static string? RequestIdOf(JsonNode? node) =>
        node is JsonObject o && BrokerWire.Str(o["requestId"], 64) is { } id && BrokerWire.IsRequestId(id) ? id : null;

    /// <summary>Null when anything is missing, unknown, mistyped or out of shape (strict). Every check key must be present.</summary>
    public static BrokerReadRequest? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || BrokerWire.Str(o["type"]) != "read" || BrokerWire.Int(o["version"]) != Version || o.Count != Keys.Count || o.Any(kv => !Keys.Contains(kv.Key)))
            return null;
        var nonce = BrokerWire.Str(o["nonce"], 64);
        var requestId = RequestIdOf(o);
        if (nonce is null || requestId is null || o["checks"] is not JsonArray arr || arr.Count is 0 or > MaxChecks) return null;
        var checks = new List<BrokerReadCheck>();
        var last = 0;
        foreach (var item in arr)
        {
            if (item is not JsonObject c || c.Count != CheckKeys.Count || c.Any(kv => !CheckKeys.Contains(kv.Key))) return null;
            var n = BrokerWire.Int(c["n"]);
            var id = BrokerWire.Str(c["id"], 64);
            var version = BrokerWire.Str(c["handlerVersion"], 32);
            if (n is not { } nn || nn != last + 1 || id is null || version is null) return null;
            int? window = null;
            if (c["window"] is not null)
            {
                if (BrokerWire.Int(c["window"]) is not { } w || w is < 1 or > 3650) return null;
                window = w;
            }
            last = nn;
            checks.Add(new BrokerReadCheck(nn, id, version, window));
        }
        return new BrokerReadRequest(nonce, requestId, checks);
    }
}

/// <summary>What one check's result covers (see Engine.Actions.ElevatedReadCoverage).</summary>
public sealed record BrokerReadCoverage(bool Complete, int Total, int Returned)
{
    public JsonObject ToJson() => new() { ["complete"] = Complete, ["total"] = Total, ["returned"] = Returned };
}

/// <summary>
/// Helper → host frames of a read session (m9 §17.2). Every frame carries <c>nonce</c>, <c>requestId</c> and <c>seq</c>
/// (from 1, no gaps):
/// <list type="bullet">
/// <item><c>{type:"read_started", nonce, requestId, seq}</c> once the user pressed Read in the helper's window and the journal line is written;</item>
/// <item><c>{type:"read_result", nonce, requestId, seq, n, state, code?, coverage:{complete, total, returned}, items:[…]}</c>, exactly one per
/// requested check, in order; state ok | failed | unsupported | skipped | cancelled; items only with ok (≤ 200, whole frame ≤ 64 KB);</item>
/// <item><c>{type:"read_end", nonce, requestId, seq, state, code?}</c> always last; state complete | declined | stopped | timeout | refused.</item>
/// </list>
/// A refusal before the window (bad request, nonce, replay, different administrator, unknown check, journal) sends no
/// read_started: one failed result per check (when the request could be parsed) and read_end refused with the code. A
/// request that couldn't be parsed has <c>requestId: null</c> when its id was unusable. Cancel in the helper's window: one
/// cancelled result per check (broker_cancelled) and read_end declined.
/// </summary>
public static class BrokerReadFrames
{
    public static readonly string[] ResultStates = ["ok", "failed", "unsupported", "skipped", "cancelled"];
    public static readonly string[] EndStates = ["complete", "declined", "stopped", "timeout", "refused"];

    public static JsonObject Started(string nonce, string requestId, int seq) =>
        new() { ["type"] = "read_started", ["nonce"] = nonce, ["requestId"] = requestId, ["seq"] = seq };

    public static JsonObject Result(string nonce, string requestId, int seq, int n, string state, string? code, BrokerReadCoverage coverage, IEnumerable<JsonObject> items)
    {
        var o = new JsonObject { ["type"] = "read_result", ["nonce"] = nonce, ["requestId"] = requestId, ["seq"] = seq, ["n"] = n, ["state"] = state };
        if (code is not null) o["code"] = code;
        o["coverage"] = coverage.ToJson();
        o["items"] = new JsonArray([.. items.Select(i => (JsonNode?)i.DeepClone())]);
        return o;
    }

    public static JsonObject End(string nonce, string? requestId, int seq, string state, string? code)
    {
        var o = new JsonObject { ["type"] = "read_end", ["nonce"] = nonce, ["requestId"] = requestId, ["seq"] = seq, ["state"] = state };
        if (code is not null) o["code"] = code;
        return o;
    }

    /// <summary>One parsed frame: kind started | result | end; null when malformed or for another nonce. Items are not checked against a field list here.</summary>
    public sealed record Frame(string Kind, int Seq, string? RequestId, int N, string? State, string? Code, BrokerReadCoverage? Coverage, IReadOnlyList<JsonObject> Items);

    private static readonly HashSet<string> StartedKeys = ["type", "nonce", "requestId", "seq"];
    private static readonly HashSet<string> ResultKeys = ["type", "nonce", "requestId", "seq", "n", "state", "code", "coverage", "items"];
    private static readonly HashSet<string> EndKeys = ["type", "nonce", "requestId", "seq", "state", "code"];

    public static Frame? Parse(JsonNode? node, string nonce)
    {
        if (node is not JsonObject o || BrokerWire.Str(o["nonce"], 64) is not { } n || !BrokerProtocol.NonceEquals(n, nonce) || BrokerWire.Int(o["seq"]) is not { } seq || seq < 1)
            return null;
        if (!o.ContainsKey("requestId")) return null;
        var requestId = BrokerReadRequest.RequestIdOf(o);
        if (requestId is null && o["requestId"] is not null) return null;
        string? code = null;
        if (o["code"] is { } codeNode)
        {
            code = BrokerWire.Str(codeNode, 64);
            if (code is null || !BrokerWire.IsCode(code)) return null;
        }
        switch (BrokerWire.Str(o["type"]))
        {
            case "read_started" when o.Count == StartedKeys.Count && o.All(kv => StartedKeys.Contains(kv.Key)) && requestId is not null:
                return new Frame("started", seq, requestId, 0, null, null, null, []);
            case "read_result" when o.All(kv => ResultKeys.Contains(kv.Key)) && o.Count >= ResultKeys.Count - 1 && requestId is not null:
                if (BrokerWire.Int(o["n"]) is not { } num || num is < 1 or > BrokerReadRequest.MaxChecks) return null;
                if (BrokerWire.Str(o["state"]) is not { } state || !ResultStates.Contains(state)) return null;
                if (o["coverage"] is not JsonObject cov || cov.Count != 3 || cov["complete"] is not JsonValue cv || !cv.TryGetValue<bool>(out var complete)
                    || BrokerWire.Int(cov["total"]) is not { } total || BrokerWire.Int(cov["returned"]) is not { } returned) return null;
                if (o["items"] is not JsonArray arr || arr.Count > Engine.Agent.AdminChecks.MaxItems) return null;
                var items = new List<JsonObject>();
                foreach (var i in arr)
                {
                    if (i is not JsonObject io) return null;
                    items.Add((JsonObject)io.DeepClone());
                }
                if (returned != items.Count || total < returned || (state != "ok" && items.Count > 0) || (complete && total != returned)) return null;
                return new Frame("result", seq, requestId, num, state, code, new BrokerReadCoverage(complete, total, returned), items);
            case "read_end" when o.All(kv => EndKeys.Contains(kv.Key)) && o.Count >= EndKeys.Count - 1:
                if (BrokerWire.Str(o["state"]) is not { } endState || !EndStates.Contains(endState)) return null;
                return new Frame("end", seq, requestId, 0, endState, code, null, []);
            default:
                return null;
        }
    }
}

/// <summary>
/// The content hash of one step (host and broker compute it the same way): SHA-256 over the canonical (sorted-key) JSON of
/// actionId, handlerVersion, the target's id, before, after and parameters. The broker refuses a step unless its own live
/// resolution hashes to the value the host sent.
/// </summary>
public static class BrokerContent
{
    public static string Hash(string actionId, string handlerVersion, JsonObject target, JsonObject before, JsonObject after, JsonObject? parameters)
    {
        var doc = new JsonObject
        {
            ["actionId"] = actionId, ["handlerVersion"] = handlerVersion, ["targetId"] = target["id"]?.DeepClone(),
            ["before"] = before.DeepClone(), ["after"] = after.DeepClone(), ["parameters"] = parameters?.DeepClone(),
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
}

internal static partial class BrokerWire
{
    public static string? Str(JsonNode? n, int max = int.MaxValue) =>
        n is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 && s.Length <= max ? s : null;

    public static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        return null;
    }

    public static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public static bool IsHash(string s) => HashRegex().IsMatch(s);
    public static bool IsReceiptId(string s) => ReceiptRegex().IsMatch(s);
    public static bool IsRequestId(string s) => RequestIdRegex().IsMatch(s);
    /// <summary>A result or refusal code: lower-case words joined by underscores.</summary>
    public static bool IsCode(string s) => CodeRegex().IsMatch(s);

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex RequestIdRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    [GeneratedRegex("^rcpt-[0-9a-f]{16}$")]
    private static partial Regex ReceiptRegex();
}

/// <summary>Pipe name, nonce and framing shared by the host's BrokerElevation and the broker.</summary>
public static partial class BrokerProtocol
{
    public const string PipePrefix = "OptimizationTool.Broker.";
    /// <summary>Frames are small; anything larger is refused before allocating.</summary>
    public const int MaxFrameBytes = 64 * 1024;
    public const int MaxDepth = 16;

    public static string NewPipeName() => PipePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string NewReceiptId() => "rcpt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    public static bool IsValidPipeName(string? s) => s is not null && PipeRegex().IsMatch(s);
    public static bool IsValidNonce(string? s) => s is not null && NonceRegex().IsMatch(s);

    /// <summary>True when a read failed because the stream ended mid-frame (the other side is gone), not because of malformed input.</summary>
    public static bool StreamEnded(Exception ex) => ex is InvalidDataException { InnerException: EndOfStreamException };

    public static bool NonceEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame; throws <see cref="InvalidDataException"/> for oversize, truncated or non-JSON frames. A frame cut
    /// short by the end of the stream (the other side gone) carries an <see cref="EndOfStreamException"/> inside (see
    /// <see cref="StreamEnded"/>), so a reader can tell a lost connection from malformed input.
    /// </summary>
    public static async Task<JsonNode?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (await Fill(stream, header, ct).ConfigureAwait(false) < 4) throw new InvalidDataException("Truncated header.", new EndOfStreamException());
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var payload = new byte[length];
        if (await Fill(stream, payload, ct).ConfigureAwait(false) < length) throw new InvalidDataException("Truncated frame.", new EndOfStreamException());
        try { return JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException) { throw new InvalidDataException("Not JSON."); }
    }

    private static async Task<int> Fill(Stream s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    [GeneratedRegex("^OptimizationTool\\.Broker\\.[0-9a-f]{32}$")]
    private static partial Regex PipeRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex NonceRegex();
}

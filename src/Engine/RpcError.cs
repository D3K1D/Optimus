using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine;

/// <summary>Error codes from contracts/protocol-v1.md §3.</summary>
public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int NotFound = -32001;
    public const int StaleRevision = -32002;
    public const int IdempotencyMismatch = -32003;
    public const int ScanActive = -32004;
    public const int IncompatibleProtocol = -32010;
    public const int HandshakeRequired = -32011;
    public const int MethodNotAllowed = -32012;
    public const int InvalidState = -32013;
}

/// <summary>
/// A JSON-RPC error the API returns to the client. Thrown inside a store transaction it also rolls the transaction back.
/// Records where it was raised (the helpers record their caller) and an optional error-site id; the host logs both,
/// neither is sent to the client (docs/error-logging.md §1).
/// </summary>
public sealed class RpcError(int code, string message, JsonNode? data = null,
    [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;

    /// <summary>The error-site id (<c>E-XXX-nnn</c>) when this place is worth naming; null otherwise.</summary>
    public string? Site { get; init; }

    /// <summary>Where it was raised: <c>src/...cs</c>.</summary>
    public string RaisedFile { get; } = CodeLocation.From(file);
    public int RaisedLine { get; } = line;

    /// <summary><c>src/...cs:line</c>.</summary>
    public string RaisedAt => RaisedFile + ":" + RaisedLine.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static RpcError Params(string field, string message, string? site = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        new(ErrorCodes.InvalidParams, message, new JsonObject { ["field"] = field }, file, line) { Site = site };

    public static RpcError NotFound(string kind, string id, string? site = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        new(ErrorCodes.NotFound, $"No {kind} {id}.", new JsonObject { ["kind"] = kind, ["id"] = id }, file, line) { Site = site };

    public static RpcError State(string reason, string message, string? site = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        new(ErrorCodes.InvalidState, message, new JsonObject { ["reason"] = reason }, file, line) { Site = site };

    public static RpcError Corrupt(string kind, string id, string? site = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        new(ErrorCodes.InvalidState, $"Stored {kind} {id} could not be read.",
            new JsonObject { ["reason"] = "corrupt_record", ["kind"] = kind, ["id"] = id }, file, line) { Site = site };
}

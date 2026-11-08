using System.Text.Json.Nodes;

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

/// <summary>A JSON-RPC error the API returns to the client. Thrown inside a store transaction it also rolls the transaction back.</summary>
public sealed class RpcError(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;

    public static RpcError Params(string field, string message) =>
        new(ErrorCodes.InvalidParams, message, new JsonObject { ["field"] = field });

    public static RpcError NotFound(string kind, string id) =>
        new(ErrorCodes.NotFound, $"No {kind} {id}.", new JsonObject { ["kind"] = kind, ["id"] = id });

    public static RpcError State(string reason, string message) =>
        new(ErrorCodes.InvalidState, message, new JsonObject { ["reason"] = reason });

    public static RpcError Corrupt(string kind, string id) =>
        new(ErrorCodes.InvalidState, $"Stored {kind} {id} could not be read.",
            new JsonObject { ["reason"] = "corrupt_record", ["kind"] = kind, ["id"] = id });
}

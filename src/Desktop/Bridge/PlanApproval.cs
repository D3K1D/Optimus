using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Desktop.Bridge;

public enum PlanDecision { Decline, Approve, Expired }

/// <summary>The trusted approval surface (native dialog in production). Anything but an explicit Approve is a decline.</summary>
public interface IPlanApprovalPrompt
{
    Task<PlanDecision> ConfirmAsync(PlanView view, CancellationToken ct);
}

/// <summary>
/// Shell-handled <c>shell.approvePlan {planId}</c> (m4-actions.md §1.2, §4 "Shell approval flow"). The shell reads the
/// plan from the host, shows it natively, and on Approve sends <c>action.challenge</c> then
/// <c>action.approve {planId, planHash, nonce}</c> on its own connection. The renderer only gets <c>{status, operationId?}</c>.
/// </summary>
public sealed partial class PlanApproval
{
    public const string Method = "shell.approvePlan";
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly IPlanApprovalPrompt _prompt;
    private readonly Func<long> _nowTicks;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Lock _lock = new();
    private bool _open;
    private long _lastStartTicks = long.MinValue;

    public PlanApproval(IPlanApprovalPrompt prompt, Func<long>? nowTicks = null, Func<DateTimeOffset>? utcNow = null)
    {
        _prompt = prompt;
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<string> HandleAsync(JsonNode id, JsonObject? parameters, IBackend backend, DesktopLog log, CancellationToken ct)
    {
        if (parameters is null || parameters.Count != 1
            || parameters["planId"] is not JsonValue v || !v.TryGetValue<string>(out var planId) || !PlanIdPattern().IsMatch(planId))
        {
            log.Warn($"shell: refused {Method} (invalid params)");
            return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = "planId" });
        }

        lock (_lock)
        {
            if (_open)
                return BridgeMessages.Error(id, -32013, "An approval is already open.", new JsonObject { ["reason"] = "busy" });
            var now = _nowTicks();
            if (_lastStartTicks != long.MinValue && now - _lastStartTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
                return BridgeMessages.Error(id, -32013, "Too many requests.", new JsonObject { ["reason"] = "rate_limited" });
            _open = true;
            _lastStartTicks = now;
        }

        try
        {
            var outcome = await RunAsync(planId, backend, log, ct);
            log.Info($"shell: {Method} {planId} status={outcome["status"]!.GetValue<string>()}");
            return BridgeMessages.Result(id, outcome);
        }
        catch (BackendUnavailableException)
        {
            return BridgeMessages.Result(id, Status("error", "backend_unavailable"));
        }
        catch (OperationCanceledException)
        {
            return BridgeMessages.Result(id, Status("error", "cancelled"));
        }
        finally
        {
            lock (_lock) _open = false;
        }
    }

    private async Task<JsonObject> RunAsync(string planId, IBackend backend, DesktopLog log, CancellationToken ct)
    {
        // 1. The plan as the host has it.
        var got = await backend.CallAsync("action.get", new JsonObject { ["planId"] = planId }, ct);
        if (got["error"] is JsonObject getError) return FromError(getError);
        if (got["result"] is not JsonObject plan) return Status("error", "bad_plan");

        var status = Str(plan["status"]);
        if (status == "expired" || status is null) return Status(status ?? "error", status is null ? "bad_plan" : null);
        if (status != "awaiting_user") return Status("unavailable", CodeOrNull(status));
        if (Str(plan["planHash"]) is not { Length: > 0 and <= 128 } planHash) return Status("error", "bad_plan");
        if (Str(plan["planId"]) is { } echoed && echoed != planId) return Status("error", "bad_plan");

        var view = PlanView.From(plan);
        if (view is null) return Status("error", "bad_plan");
        if (view.ExpiresAt <= _utcNow()) return Status("expired");

        // 2. The user decides on exactly this plan.
        var decision = await _prompt.ConfirmAsync(view, ct);
        if (decision == PlanDecision.Expired || (decision == PlanDecision.Approve && view.ExpiresAt <= _utcNow()))
            return Status("expired");
        if (decision != PlanDecision.Approve)
        {
            var declined = await backend.CallAsync("action.decline", new JsonObject { ["planId"] = planId }, ct);
            return declined["error"] is JsonObject declineError ? FromError(declineError) : Status("declined");
        }

        // 3. Re-read: the plan must still be the one the user saw.
        var again = await backend.CallAsync("action.get", new JsonObject { ["planId"] = planId }, ct);
        if (again["error"] is JsonObject againError) return FromError(againError);
        if (Str(again["result"]?["planHash"]) != planHash)
        {
            log.Warn($"shell: {Method} {planId} changed after it was shown; not approved");
            return Status("stale");
        }
        if (Str(again["result"]?["status"]) is { } s2 && s2 != "awaiting_user")
            return s2 == "expired" ? Status("expired") : Status("unavailable", CodeOrNull(s2));

        // 4. One-time nonce, then approve the exact hash that was shown.
        var challenge = await backend.CallAsync("action.challenge", new JsonObject { ["planId"] = planId }, ct);
        if (challenge["error"] is JsonObject challengeError) return FromError(challengeError);
        if (Str(challenge["result"]?["nonce"]) is not { Length: > 0 and <= 256 } nonce) return Status("error", "bad_challenge");

        var approved = await backend.CallAsync("action.approve",
            new JsonObject { ["planId"] = planId, ["planHash"] = planHash, ["nonce"] = nonce }, ct);
        if (approved["error"] is JsonObject approveError) return FromError(approveError);

        var result = Status("approved");
        if (Str(approved["result"]?["operationId"]) is { } opId && OperationIdPattern().IsMatch(opId)) result["operationId"] = opId;
        return result;
    }

    /// <summary>-32002 → stale; -32013 with an expiry reason → expired; otherwise a short code. Messages and data are not relayed.</summary>
    private static JsonObject FromError(JsonObject error)
    {
        var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0;
        var reason = Str(error["data"]?["reason"]);
        return code switch
        {
            -32002 => Status("stale"),
            -32013 when reason is "expired" or null => Status("expired"),
            -32013 => Status("unavailable", CodeOrNull(reason)),
            -32001 => Status("error", "not_found"),
            _ => Status("error", "backend_error"),
        };
    }

    private static JsonObject Status(string status, string? error = null)
    {
        var o = new JsonObject { ["status"] = status };
        if (error is not null) o["error"] = error;
        return o;
    }

    private static string? CodeOrNull(string? s) => s is not null && CodePattern().IsMatch(s) ? s : null;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex("^plan-[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex PlanIdPattern();

    [GeneratedRegex("^op-[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex OperationIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex CodePattern();
}

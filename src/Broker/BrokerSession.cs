using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker;

/// <summary>Who is on one end of the broker pipe.</summary>
public sealed record PeerInfo(uint ProcessId, string? ImagePath, uint SessionId, SecurityIdentifier? User);

/// <summary>Writes exactly one field: the Public profile's EnableFirewall in the local store (fakes in tests).</summary>
public interface IPublicFirewallWriter
{
    void SetPublicEnabled(bool enabled);
}

/// <summary>Protected receipts (SYSTEM + Administrators full, the interactive user read).</summary>
public interface IReceiptStore
{
    /// <summary>Writes a new receipt and returns its ID.</summary>
    string Write(JsonObject receipt);
    /// <summary>The most recent verified receipt for <paramref name="field"/>, or null.</summary>
    JsonObject? LatestVerified(string field);
}

/// <summary>Single-use nonces across broker launches.</summary>
public interface INonceLedger
{
    bool WasUsed(string nonce);
    void MarkUsed(string nonce);
}

public sealed record BrokerSessionOptions(
    string Nonce,
    string ExpectedHostImage,
    PeerInfo Self,
    IPublicFirewallReader Reader,
    IPublicFirewallWriter Writer,
    IReceiptStore Receipts,
    INonceLedger Ledger,
    TimeProvider? Time = null);

/// <summary>Result for the caller and tests: the refusal/outcome code, the response sent (if any), and whether a write happened.</summary>
public sealed record BrokerResult(string Code, BrokerResponse? Sent, bool Wrote);

/// <summary>
/// One broker run (contracts/m4-actions.md §5): verify the pipe server is the installed host in this session and
/// account, read one bounded request, check the nonce (matches the launch, never used before), accept only
/// <c>firewall.enable_public</c>, re-resolve from live state and require the same content hash, write only the
/// Public EnableFirewall field, read back, write a protected receipt, answer once.
/// </summary>
public sealed class BrokerSession(BrokerSessionOptions o)
{
    private readonly TimeProvider _time = o.Time ?? TimeProvider.System;

    public async Task<BrokerResult> RunAsync(Stream pipe, PeerInfo server, CancellationToken ct)
    {
        // 1. Server identity. An unverified server gets no answer at all.
        if (server.ImagePath is null || !SamePath(server.ImagePath, o.ExpectedHostImage))
            return new BrokerResult("server_mismatch", null, false);
        if (server.SessionId != o.Self.SessionId)
            return new BrokerResult("session_mismatch", null, false);
        if (server.User is null || o.Self.User is null || !server.User.Equals(o.Self.User))
            return await Refuse(pipe, "different_admin",
                "Windows permission came from a different account. Use Windows Security instead.", ct).ConfigureAwait(false);

        // 2. One bounded, well-formed request.
        BrokerRequest? request;
        try { request = BrokerRequest.Parse(await BrokerProtocol.ReadAsync(pipe, ct).ConfigureAwait(false)); }
        catch (InvalidDataException) { request = null; }
        if (request is null) return await Refuse(pipe, "bad_request", "The request was not valid.", ct).ConfigureAwait(false);

        // 3. Nonce: the one this broker was launched with, never seen before. Consumed now, whatever happens next.
        if (!BrokerProtocol.IsValidNonce(request.Nonce) || !BrokerProtocol.NonceEquals(request.Nonce, o.Nonce))
            return await Refuse(pipe, "bad_nonce", "The request did not match this approval.", ct).ConfigureAwait(false);
        if (o.Ledger.WasUsed(o.Nonce))
            return await Refuse(pipe, "replayed", "This approval was already used.", ct).ConfigureAwait(false);
        o.Ledger.MarkUsed(o.Nonce);

        // 4. Only the compiled firewall action (and its undo).
        if (request.ActionId != FirewallPlan.ActionId || request.Mode is not ("enable" or "disable"))
            return await Refuse(pipe, "action_not_allowed", "This change can't run here.", ct).ConfigureAwait(false);
        if (!FirewallPlan.ValidTarget(request.Target))
            return await Refuse(pipe, "bad_request", "This change isn't supported.", ct).ConfigureAwait(false);
        var sentHash = FirewallPlan.ContentHash(request.ActionId, request.Mode, request.Target, request.Before, request.After);
        if (sentHash != request.ContentHash)
            return await Refuse(pipe, "plan_mismatch", "The plan didn't match what was approved.", ct).ConfigureAwait(false);

        // 5. Re-resolve from live state with the same handler code; the recomputed plan must hash the same.
        var live = o.Reader.Read(ct);
        var resolution = FirewallPlan.Resolve(live, request.Mode, handlerEnabled: true, request.Target);
        if (!resolution.Eligible)
            return await Refuse(pipe, resolution.IneligibleCode ?? "ineligible", resolution.IneligibleMessage ?? "This change isn't possible right now.", ct, live).ConfigureAwait(false);
        var liveHash = FirewallPlan.ContentHash(FirewallPlan.ActionId, request.Mode, resolution.Target, resolution.Before, resolution.After);
        if (liveHash != request.ContentHash)
            return await Refuse(pipe, "plan_mismatch", "The setting changed since you approved. Preview it again.", ct, live).ConfigureAwait(false);

        // 6. Undo only against our own protected receipt for this exact field.
        if (request.Mode == "disable" && !HasEnableReceipt())
            return await Refuse(pipe, "no_receipt", "There's no record of this app turning it on.", ct, live).ConfigureAwait(false);

        // 7. Write the single field, then read back with the same reader.
        var target = request.Mode == "enable";
        string? writeError = null;
        try { o.Writer.SetPublicEnabled(target); }
        catch (Exception ex) { writeError = ex.GetType().Name; }
        var after = o.Reader.Read(CancellationToken.None);
        var observed = FirewallPlan.State(after.LocalEnabled, after.EffectiveEnabled);
        var state = FirewallPlan.SameState(observed, resolution.After) ? "verified"
            : FirewallPlan.SameState(observed, resolution.Before) ? "failed" : "indeterminate";

        var text = state switch
        {
            "verified" => target ? "Firewall for public networks is on, read back from Windows." : "Firewall for public networks is off, read back from Windows.",
            "failed" => "Windows still reports the previous setting. Nothing changed.",
            _ => "Windows reported an unexpected firewall state. Check Windows Security.",
        };

        string? receiptId = null;
        string? code = writeError is null ? null : "write_error";
        try
        {
            receiptId = o.Receipts.Write(new JsonObject
            {
                ["actionId"] = FirewallPlan.ActionId,
                ["mode"] = request.Mode,
                ["field"] = FirewallPlan.Field,
                ["planId"] = request.PlanId,
                ["planHash"] = request.PlanHash,
                ["contentHash"] = request.ContentHash,
                ["state"] = state,
                ["before"] = resolution.Before.DeepClone(),
                ["after"] = resolution.After.DeepClone(),
                ["observed"] = observed.DeepClone(),
                ["at"] = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["userSid"] = o.Self.User?.Value,
            });
        }
        catch (Exception) { code ??= "receipt_failed"; }

        var response = new BrokerResponse(o.Nonce, state, observed, text, code, writeError, receiptId);
        await Send(pipe, response, ct).ConfigureAwait(false);
        return new BrokerResult(state, response, true);
    }

    private bool HasEnableReceipt()
    {
        var r = o.Receipts.LatestVerified(FirewallPlan.Field);
        return r is not null && FirewallPlan.Str(r["actionId"]) == FirewallPlan.ActionId && FirewallPlan.Str(r["mode"]) == "enable";
    }

    private async Task<BrokerResult> Refuse(Stream pipe, string code, string message, CancellationToken ct, PublicFirewallSnapshot? live = null)
    {
        var observed = live is null ? new JsonObject() : FirewallPlan.State(live.LocalEnabled, live.EffectiveEnabled);
        var response = new BrokerResponse(o.Nonce, "failed", observed, "Nothing changed.", code, message, null);
        try { await Send(pipe, response, ct).ConfigureAwait(false); }
        catch (IOException) { }
        return new BrokerResult(code, response, false);
    }

    private static Task Send(Stream pipe, BrokerResponse response, CancellationToken ct) => BrokerProtocol.WriteAsync(pipe, response.ToJson(), ct);

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

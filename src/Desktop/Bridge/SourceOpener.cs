using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// Shell-handled <c>shell.openSource {sourceId}</c> (contracts/m9-ai-powers.md §6.2, §6.5, §8). The page passes only a
/// source ID; the shell asks the host for the stored URL through the shell-only <c>research.source</c> (never callable by the
/// renderer), validates it again (<see cref="SourceUrl"/>) and opens it in the user's browser through the system handler,
/// as the onboarding launcher does. Result: <c>{status:"opened"|"unknown"|"blocked"}</c>; a missing or malformed sourceId
/// is -32602. The five-URL launcher (<see cref="SettingsLauncher"/>) is not involved and unchanged.
/// </summary>
public sealed partial class SourceOpener(Action<string>? open = null, Func<long>? nowTicks = null)
{
    public const string Method = "shell.openSource";
    public const string HostMethod = "research.source";
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly Action<string> _open = open ?? SettingsLauncher.OpenWithSystem;
    private readonly Func<long> _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
    private readonly Lock _lock = new();
    private long _lastOpenTicks = long.MinValue;

    [GeneratedRegex("^src-[0-9a-f]{8,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdPattern();

    public async Task<string> HandleAsync(JsonNode id, JsonObject? parameters, IBackend backend, AppLog log, CancellationToken ct)
    {
        if (parameters is null || parameters.Count != 1 || parameters["sourceId"] is not JsonValue v || !v.TryGetValue<string>(out var sourceId)
            || !SourceIdPattern().IsMatch(sourceId))
        {
            log.Warn($"shell: refused {Method} (invalid params)");
            return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = "sourceId" });
        }

        JsonObject response;
        try { response = await backend.CallAsync(HostMethod, new JsonObject { ["sourceId"] = sourceId }, ct); }
        catch (BackendUnavailableException) { return BridgeMessages.Unavailable(id); }
        catch (OperationCanceledException) { return BridgeMessages.Unavailable(id); }

        if (response["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0;
            var status = code == -32013 ? "blocked" : "unknown";
            log.Info($"shell: {Method} {sourceId} {status}");
            return BridgeMessages.Result(id, new JsonObject { ["status"] = status });
        }
        var stored = response["result"]?["url"] is JsonValue uv && uv.TryGetValue<string>(out var s) ? s : null;
        if (stored is null) return BridgeMessages.Result(id, new JsonObject { ["status"] = "unknown" });

        // Validated again here: the shell trusts neither the page nor the stored value.
        if (SourceUrl.Clean(stored) is not { } url || url != stored)
        {
            log.Warn($"shell: {Method} {sourceId} blocked (URL failed the check)");
            return BridgeMessages.Result(id, new JsonObject { ["status"] = "blocked" });
        }

        lock (_lock)
        {
            var now = _nowTicks();
            if (_lastOpenTicks != long.MinValue && now - _lastOpenTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
                return BridgeMessages.Error(id, -32013, "Too many requests.", new JsonObject { ["reason"] = "rate_limited" });
            _lastOpenTicks = now;
        }
        try { _open(url); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"shell: could not open source {sourceId} ({ex.GetType().Name})");
            return BridgeMessages.Error(id, -32013, "Could not open.", new JsonObject { ["reason"] = "open_failed" });
        }
        log.Info($"shell: opened source {sourceId}"); // the ID only: the URL is not logged
        return BridgeMessages.Result(id, new JsonObject { ["status"] = "opened" });
    }
}

/// <summary>
/// The shell's copy of the host's URL hygiene (m9 §6.3, src/Engine UrlHygiene): https only, a public host name (no IP
/// literal, credentials or port; IDN in punycode), at most 300 characters, fragment removed. Null when refused.
/// </summary>
public static class SourceUrl
{
    public const int MaxLength = 300;

    private static readonly string[] PrivateSuffixes =
        [".local", ".localhost", ".internal", ".lan", ".home", ".corp", ".intranet", ".private", ".home.arpa", ".invalid", ".test", ".example", ".onion", ".arpa"];

    public static string? Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 2048) return null;
        foreach (var ch in raw)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\' || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) return null;
        if (!raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = raw["https://".Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        if (authority.Length == 0 || authority.Contains('@') || authority.Contains(':') || authority.Contains('[') || authority.Contains('%')) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        if (u.UserInfo.Length > 0 || !u.IsDefaultPort || u.HostNameType != UriHostNameType.Dns) return null;
        string host;
        try { host = u.IdnHost.ToLowerInvariant(); }
        catch (UriFormatException) { return null; }
        if (!PublicHost(host)) return null;
        var cleaned = "https://" + host + u.PathAndQuery;
        return cleaned.Length <= MaxLength ? cleaned : null;
    }

    private static bool PublicHost(string host)
    {
        if (host.Length is 0 or > 253 || host.EndsWith('.') || host.StartsWith('.')) return false;
        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')) return false;
            if (!label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        }
        var tld = labels[^1];
        if (tld.Length < 2 || tld.All(char.IsAsciiDigit)) return false;
        if (!tld.StartsWith("xn--", StringComparison.Ordinal) && !tld.All(char.IsAsciiLetter)) return false;
        var dotted = "." + host;
        return !PrivateSuffixes.Any(s => dotted.EndsWith(s, StringComparison.Ordinal)) && host != "localhost";
    }
}

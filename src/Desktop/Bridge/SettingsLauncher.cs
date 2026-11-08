using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// Shell-handled <c>shell.openSettings {uri}</c> (protocol-v1.md §9). Opens only the reviewed catalog in
/// contracts/m2-collectors.md §5, by exact case-sensitive match, through the system handler. Never forwarded.
/// </summary>
public sealed class SettingsLauncher
{
    public const string Method = "shell.openSettings";

    public static readonly FrozenSet<string> Catalog = new[]
    {
        "windowsdefender://threat",
        "windowsdefender://network",
        "ms-settings:windowsdefender",
        "ms-settings:startupapps",
        "ms-settings:powersleep",
        "ms-settings:storagesense",
        "ms-settings:appsfeatures",
        "ms-settings:privacy-feedback",
        "ms-settings:display-advanced",
        "ms-settings:personalization-start",
        "ms-settings:windowsupdate-history",
        "ms-settings:troubleshoot",
    }.ToFrozenSet(StringComparer.Ordinal);

    public const string UrlMethod = "shell.openUrl";

    /// <summary>contracts/onboarding.md: the only web pages the shell opens (exact, case-sensitive).</summary>
    public static readonly FrozenSet<string> WebLinks = new[]
    {
        "https://claude.com/claude-code",
        "https://developers.openai.com/codex",
        "https://chatgpt.com/",
        "https://claude.com/download",
        "https://chatgpt.com/codex",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly Action<string> _open;
    private readonly Func<long> _nowTicks;
    private readonly Lock _lock = new();
    private long _lastOpenTicks = long.MinValue;
    private readonly string _method;
    private readonly string _param;
    private readonly FrozenSet<string> _allowed;

    /// <param name="open">Opens a catalog URI; defaults to the system handler.</param>
    /// <param name="nowTicks">Monotonic clock in <see cref="Stopwatch"/> ticks; for tests.</param>
    public SettingsLauncher(Action<string>? open = null, Func<long>? nowTicks = null)
        : this(Method, "uri", Catalog, open, nowTicks) { }

    /// <summary><c>shell.openUrl {url}</c>: the same exact-match, rate-limited launcher for the onboarding web links.</summary>
    public static SettingsLauncher ForWebLinks(Action<string>? open = null, Func<long>? nowTicks = null) =>
        new(UrlMethod, "url", WebLinks, open, nowTicks);

    private SettingsLauncher(string method, string param, FrozenSet<string> allowed, Action<string>? open, Func<long>? nowTicks)
    {
        _method = method;
        _param = param;
        _allowed = allowed;
        _open = open ?? OpenWithSystem;
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Returns the bridge reply for this call.</summary>
    public string Handle(JsonNode id, JsonObject? parameters, DesktopLog log)
    {
        // Exactly {<param>:"<allowed entry>"}: no extra fields, no normalization.
        if (parameters is null || parameters.Count != 1
            || parameters[_param] is not JsonValue v || !v.TryGetValue<string>(out var uri) || !_allowed.Contains(uri))
        {
            log.Warn($"shell: refused {_method} (not allowed)");
            return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = _param });
        }

        lock (_lock)
        {
            var now = _nowTicks();
            if (_lastOpenTicks != long.MinValue && now - _lastOpenTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
            {
                log.Warn($"shell: {_method} rate limited");
                return BridgeMessages.Error(id, -32013, "Too many requests.", new JsonObject { ["reason"] = "rate_limited" });
            }
            _lastOpenTicks = now;
        }

        try
        {
            _open(uri);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"shell: could not open {uri} ({ex.GetType().Name})");
            return BridgeMessages.Error(id, -32013, "Could not open.", new JsonObject { ["reason"] = "open_failed" });
        }
        log.Info($"shell: opened {uri}");
        return BridgeMessages.Result(id, new JsonObject { ["opened"] = true });
    }

    // Only ever called with an allowlisted entry (fixed ms-settings:/windowsdefender: URIs or the five https links).
    private static void OpenWithSystem(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
}

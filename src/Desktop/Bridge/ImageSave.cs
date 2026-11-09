using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// A path the Save dialog confirmed. <see cref="OverwriteConfirmed"/> is true only when the file already existed and the
/// dialog's overwrite prompt was accepted for exactly this path.
/// </summary>
public sealed record PickedPath(string Path, bool OverwriteConfirmed);

/// <summary>The native Save dialog. Returns the confirmed path, or null on cancel. The path never leaves the shell.</summary>
public interface ISaveImagePrompt
{
    Task<PickedPath?> PickPathAsync(string defaultName, CancellationToken ct);
}

/// <summary>
/// Shell-handled <c>shell.saveImage {pngBase64, suggestedName?}</c> → <c>{saved}</c> (m6-benchmark.md §5). Accepts only
/// a well-formed PNG (every chunk walked and CRC-checked) of ≤ 4 MB and ≤ 4096×4096, asks for a place with the native
/// Save dialog (fixed *.png filter, overwrite prompt) and writes the bytes to exactly the confirmed path, never renamed.
/// The chosen path is never returned to the renderer. Never forwarded.
/// </summary>
public sealed partial class ImageSave
{
    public const string Method = "shell.saveImage";
    public const int MaxPngBytes = 4 * 1024 * 1024;
    public const int MaxDimension = 4096;
    /// <summary>Base64 length of <see cref="MaxPngBytes"/> (padded).</summary>
    public const int MaxBase64Chars = (MaxPngBytes + 2) / 3 * 4;
    public const string DataUrlPrefix = "data:image/png;base64,";
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    /// <summary>Upper bound on chunks walked; a 1200×630 canvas PNG has a handful.</summary>
    public const int MaxChunks = 4096;

    private readonly ISaveImagePrompt _prompt;
    private readonly Action<string, byte[], bool> _write;
    private readonly Func<long> _nowTicks;
    private readonly Func<DateTime> _today;
    private readonly Lock _lock = new();
    private bool _open;
    private long _lastStartTicks = long.MinValue;

    /// <param name="write">(path, bytes, overwrite): overwrite false must fail if the file exists.</param>
    public ImageSave(ISaveImagePrompt prompt, Action<string, byte[], bool>? write = null, Func<long>? nowTicks = null, Func<DateTime>? today = null)
    {
        _prompt = prompt;
        _write = write ?? WriteFile;
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        _today = today ?? (() => DateTime.Now);
    }

    public async Task<string> HandleAsync(JsonNode id, JsonObject? parameters, DesktopLog log, CancellationToken ct)
    {
        var error = TryRead(parameters, out var png, out var suggested);
        if (error is not null)
        {
            log.Warn($"shell: refused {Method} ({error})");
            return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = error });
        }

        lock (_lock)
        {
            if (_open)
                return BridgeMessages.Error(id, -32013, "A save dialog is already open.", new JsonObject { ["reason"] = "busy" });
            var now = _nowTicks();
            if (_lastStartTicks != long.MinValue && now - _lastStartTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
                return BridgeMessages.Error(id, -32013, "Too many requests.", new JsonObject { ["reason"] = "rate_limited" });
            _open = true;
            _lastStartTicks = now;
        }

        try
        {
            var name = SafeName(suggested) ?? DefaultName(_today());
            PickedPath? picked;
            try { picked = await _prompt.PickPathAsync(name, ct); }
            catch (OperationCanceledException) { picked = null; }
            if (picked is null || string.IsNullOrEmpty(picked.Path))
            {
                log.Info($"shell: {Method} cancelled");
                return Saved(id, false);
            }
            // Never rename: a different path is one the dialog (and its overwrite prompt) never confirmed.
            if (!picked.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                log.Warn($"shell: {Method} refused (not a .png name)");
                return Saved(id, false);
            }

            try { _write(picked.Path, png!, picked.OverwriteConfirmed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                           or System.Security.SecurityException)
            {
                log.Warn($"shell: {Method} write failed ({ex.GetType().Name})");
                return Saved(id, false);
            }
            log.Info($"shell: {Method} saved {png!.Length} bytes");
            return Saved(id, true);
        }
        finally
        {
            lock (_lock) _open = false;
        }
    }

    /// <summary>Validates the params. Returns the offending field name, or null with the decoded PNG.</summary>
    public static string? TryRead(JsonObject? parameters, out byte[]? png, out string? suggestedName)
    {
        png = null;
        suggestedName = null;
        if (parameters is null) return "pngBase64";
        foreach (var (key, _) in parameters)
            if (key is not ("pngBase64" or "suggestedName")) return key.Length <= 40 ? key : "params";

        if (parameters["suggestedName"] is { } sn)
        {
            if (sn is not JsonValue snv || !snv.TryGetValue<string>(out var s)) return "suggestedName";
            suggestedName = s;
        }

        if (parameters["pngBase64"] is not JsonValue v || !v.TryGetValue<string>(out var b64)) return "pngBase64";
        png = DecodePng(b64);
        return png is null ? "pngBase64" : null;
    }

    /// <summary>Base64 (optionally a data:image/png;base64, URL) → bytes of a valid PNG within the limits, or null.</summary>
    public static byte[]? DecodePng(string b64)
    {
        var text = b64.AsSpan();
        if (text.StartsWith(DataUrlPrefix, StringComparison.Ordinal)) text = text[DataUrlPrefix.Length..];
        if (text.Length == 0 || text.Length > MaxBase64Chars) return null;

        var buffer = new byte[text.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(text, buffer, out var written)) return null;
        if (written > MaxPngBytes) return null;
        var bytes = buffer.AsSpan(0, written);
        return IsValidPng(bytes) ? bytes.ToArray() : null;
    }

    /// <summary>
    /// Walks every chunk with bounds checks: signature; IHDR first (13 bytes, sane fields, ≤ 4096×4096); at least one
    /// IDAT; IEND last (empty) with nothing after it; every chunk's CRC correct; at most <see cref="MaxChunks"/> chunks.
    /// </summary>
    public static bool IsValidPng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 + 25 + 12 || png.Length > MaxPngBytes) return false;
        if (!png[..8].SequenceEqual(Signature)) return false;

        var pos = 8;
        var chunks = 0;
        var sawIdat = false;
        while (true)
        {
            if (++chunks > MaxChunks) return false;
            if (png.Length - pos < 12) return false;
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            if (length > (uint)(png.Length - pos - 12)) return false;
            var len = (int)length;
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 8 + len)..]);
            if (Crc32(png.Slice(pos + 4, 4 + len)) != crc) return false;
            foreach (var b in type)
                if (b is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z'))) return false;

            if (chunks == 1)
            {
                if (!type.SequenceEqual("IHDR"u8) || !IsValidIhdr(data)) return false;
            }
            else if (type.SequenceEqual("IHDR"u8)) return false;
            else if (type.SequenceEqual("IDAT"u8)) sawIdat = true;
            else if (type.SequenceEqual("IEND"u8))
                return len == 0 && sawIdat && pos + 12 == png.Length;

            pos += 12 + len;
        }
    }

    private static bool IsValidIhdr(ReadOnlySpan<byte> ihdr)
    {
        if (ihdr.Length != 13) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        var height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension) return false;
        var bitDepth = ihdr[8];
        var colorType = ihdr[9];
        var ok = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => false,
        };
        return ok && ihdr[10] == 0 && ihdr[11] == 0 && ihdr[12] <= 1; // compression, filter, interlace
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>PNG/zlib CRC-32 (ISO 3309) over the chunk type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>"Optimus-benchmark-YYYY-MM-DD.png" for <paramref name="date"/>.</summary>
    public static string DefaultName(DateTime date) => $"Optimus-benchmark-{date:yyyy-MM-dd}.png";

    /// <summary>
    /// The file-name part of <paramref name="suggested"/> when it is a plain, safe name (letters, digits, space, dot, dash,
    /// underscore; not a device name; no leading dot, no trailing dot or space), with ".png" ensured. Otherwise null.
    /// </summary>
    public static string? SafeName(string? suggested)
    {
        if (string.IsNullOrWhiteSpace(suggested) || suggested.Length > 260) return null;
        var name = suggested[(suggested.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!SafeStem().IsMatch(name)) return null;
        var device = name.Split('.')[0].TrimEnd();
        if (ReservedName().IsMatch(device)) return null;
        return name + ".png";
    }

    private static string Saved(JsonNode id, bool saved) => BridgeMessages.Result(id, new JsonObject { ["saved"] = saved });

    /// <summary>CreateNew unless the dialog confirmed overwriting exactly this path.</summary>
    private static void WriteFile(string path, byte[] bytes, bool overwrite)
    {
        using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9 ._-]{0,79}(?<![ .])$")]
    private static partial Regex SafeStem();

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\\$|CONOUT\\$)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}

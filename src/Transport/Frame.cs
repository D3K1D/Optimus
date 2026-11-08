using System.Buffers.Binary;

namespace OptimizationTool.Transport;

/// <summary>
/// Wire framing for the local pipe: a 4-byte unsigned little-endian payload length (prefix excluded),
/// then that many bytes of UTF-8 JSON. See contracts/protocol-v1.md.
/// </summary>
public static class Frame
{
    public const int MaxPayloadBytes = 1 << 20; // 1 MiB, v1 cap

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new FrameException(FrameError.TooLarge, $"Payload of {payload.Length} bytes exceeds {MaxPayloadBytes}.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Returns null on a clean end of stream before a header starts.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        var got = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new FrameException(FrameError.Truncated, "Stream ended inside a frame header.");

        // Check the length before allocating anything.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxPayloadBytes)
            throw new FrameException(FrameError.TooLarge, $"Declared payload of {length} bytes exceeds {MaxPayloadBytes}.");

        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, ct).ConfigureAwait(false) < length)
            throw new FrameException(FrameError.Truncated, "Stream ended inside a frame payload.");
        return payload;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

public enum FrameError { TooLarge, Truncated }

public sealed class FrameException(FrameError error, string message) : IOException(message)
{
    public FrameError Error { get; } = error;
}

using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public class ImageSaveTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";

    private sealed class FakePrompt(string? path, bool overwrite = false) : ISaveImagePrompt
    {
        public int Calls;
        public string? DefaultName;
        public Task<PickedPath?> PickPathAsync(string defaultName, CancellationToken ct)
        {
            Calls++;
            DefaultName = defaultName;
            return Task.FromResult(path is null ? null : new PickedPath(path, overwrite));
        }
    }

    // ---- PNG building blocks (valid CRCs) ----
    private static byte[] Chunk(string type, ReadOnlySpan<byte> data)
    {
        var c = new byte[12 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(c, 4);
        data.CopyTo(c.AsSpan(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8 + data.Length), ImageSave.Crc32(c.AsSpan(4, 4 + data.Length)));
        return c;
    }

    private static readonly byte[] Sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Ihdr(uint w = 8, uint h = 8)
    {
        var d = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d, w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), h);
        d[8] = 8; d[9] = 6;
        return Chunk("IHDR", d);
    }

    private static byte[] Idat => Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
    private static byte[] Iend => Chunk("IEND", []);
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Re-computes the CRC of the chunk at <paramref name="offset"/> after a test edits its bytes.</summary>
    private static void FixCrc(byte[] png, int offset)
    {
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + 8 + len), ImageSave.Crc32(png.AsSpan(offset + 4, 4 + len)));
    }

    private sealed class NoBackend : IBackend
    {
        public int Calls;
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls++;
            throw new BackendUnavailableException("none");
        }
    }

    private static byte[] Png(int width = 1200, int height = 630)
    {
        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.SteelBlue);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static readonly DateTime Today = new(2026, 11, 9);

    private static (ImageSave Save, FakePrompt Prompt, List<(string Path, byte[] Bytes, bool Overwrite)> Writes) Make(
        string? path = @"C:\Users\x\Pictures\card.png", bool overwrite = false)
    {
        var prompt = new FakePrompt(path, overwrite);
        var writes = new List<(string, byte[], bool)>();
        long ticks = 0;
        var save = new ImageSave(prompt, (p, b, o) => writes.Add((p, b, o)), () => ticks += 10 * System.Diagnostics.Stopwatch.Frequency, () => Today);
        return (save, prompt, writes);
    }

    private static JsonObject Call(ImageSave save, JsonObject? parameters) =>
        JsonNode.Parse(save.HandleAsync(JsonValue.Create(1), parameters, DesktopLog.Disabled, CancellationToken.None).GetAwaiter().GetResult())!.AsObject();

    [Fact]
    public void Saves_a_valid_png_and_returns_only_saved_true()
    {
        var (save, prompt, writes) = Make();
        var png = Png();
        var reply = Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(png) });

        Assert.Equal("rpc.result", reply["type"]!.GetValue<string>());
        var result = reply["result"]!.AsObject();
        Assert.Single(result);
        Assert.True(result["saved"]!.GetValue<bool>());
        Assert.Equal("Optimus-benchmark-2026-11-09.png", prompt.DefaultName);
        var w = Assert.Single(writes);
        Assert.Equal(png, w.Bytes);
        Assert.DoesNotContain("Pictures", reply.ToJsonString()); // the path never reaches the renderer
    }

    [Fact]
    public void Accepts_a_data_url()
    {
        var (save, _, writes) = Make();
        var reply = Call(save, new JsonObject { ["pngBase64"] = ImageSave.DataUrlPrefix + Convert.ToBase64String(Png(10, 10)) });
        Assert.True(reply["result"]!["saved"]!.GetValue<bool>());
        Assert.Single(writes);
    }

    [Fact]
    public void Cancel_returns_saved_false_and_writes_nothing()
    {
        var (save, prompt, writes) = Make(path: null);
        var reply = Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(Png(8, 8)) });
        Assert.False(reply["result"]!["saved"]!.GetValue<bool>());
        Assert.Equal(1, prompt.Calls);
        Assert.Empty(writes);
    }

    [Theory]
    [InlineData(@"C:\tmp\card")]
    [InlineData(@"C:\tmp\card.jpg")]
    [InlineData(@"C:\tmp\card.png.txt")]
    public void Never_renames_a_confirmed_path_that_is_not_png(string path)
    {
        var (save, _, writes) = Make(path: path, overwrite: true);
        var reply = Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(Png(8, 8)) });
        Assert.False(reply["result"]!["saved"]!.GetValue<bool>());
        Assert.Empty(writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Passes_the_dialogs_overwrite_confirmation_to_the_writer(bool overwrite)
    {
        var (save, _, writes) = Make(overwrite: overwrite);
        Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(Png(8, 8)) });
        Assert.Equal(overwrite, Assert.Single(writes).Overwrite);
    }

    [Fact]
    public void Default_writer_never_replaces_a_file_the_dialog_did_not_confirm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ot-saveimage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "card.png");
            File.WriteAllText(path, "keep");
            long ticks = 0;
            Func<long> clock = () => ticks += 10 * System.Diagnostics.Stopwatch.Frequency;
            var png = Png(8, 8);

            // Appeared after the dialog (not confirmed): CreateNew fails, the file is untouched.
            var reply = Call(new ImageSave(new FakePrompt(path, overwrite: false), nowTicks: clock), new JsonObject { ["pngBase64"] = Convert.ToBase64String(png) });
            Assert.False(reply["result"]!["saved"]!.GetValue<bool>());
            Assert.Equal("keep", File.ReadAllText(path));

            // Confirmed overwrite of exactly this path: replaced.
            reply = Call(new ImageSave(new FakePrompt(path, overwrite: true), nowTicks: clock), new JsonObject { ["pngBase64"] = Convert.ToBase64String(png) });
            Assert.True(reply["result"]!["saved"]!.GetValue<bool>());
            Assert.Equal(png, File.ReadAllBytes(path));

            // New file: created.
            var fresh = Path.Combine(dir, "new.png");
            reply = Call(new ImageSave(new FakePrompt(fresh), nowTicks: clock), new JsonObject { ["pngBase64"] = Convert.ToBase64String(png) });
            Assert.True(reply["result"]!["saved"]!.GetValue<bool>());
            Assert.Equal(png, File.ReadAllBytes(fresh));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_failure_returns_saved_false()
    {
        var prompt = new FakePrompt(@"C:\x.png");
        var save = new ImageSave(prompt, (_, _, _) => throw new UnauthorizedAccessException("no"));
        var reply = Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(Png(8, 8)) });
        Assert.False(reply["result"]!["saved"]!.GetValue<bool>());
    }

    [Fact]
    public void Minimal_hand_built_png_is_accepted() =>
        Assert.True(ImageSave.IsValidPng(Join(Sig, Ihdr(), Idat, Iend)));

    [Fact]
    public void Crc32_matches_the_png_reference_value() =>
        Assert.Equal(0xAE426082u, ImageSave.Crc32("IEND"u8));

    public static TheoryData<string, byte[]> Malformed => new()
    {
        { "no IDAT", Join(Sig, Ihdr(), Iend) },
        { "IDAT before IHDR", Join(Sig, Idat, Ihdr(), Iend) },
        { "second IHDR", Join(Sig, Ihdr(), Ihdr(), Idat, Iend) },
        { "data after IEND", Join(Sig, Ihdr(), Idat, Iend, [0]) },
        { "chunk after IEND", Join(Sig, Ihdr(), Idat, Iend, Idat) },
        { "IEND with data", Join(Sig, Ihdr(), Idat, Chunk("IEND", [1])) },
        { "no IEND", Join(Sig, Ihdr(), Idat, Idat, Idat) },
        { "truncated chunk", Join(Sig, Ihdr(), Idat, Iend)[..^1] },
        { "bad type bytes", Join(Sig, Ihdr(), Chunk("ID1T", [0]), Idat, Iend) },
        { "IHDR wrong length", Join(Sig, Chunk("IHDR", new byte[12]), Idat, Iend) },
        { "huge length", Join(Sig, Ihdr(), [0xFF, 0xFF, 0xFF, 0xF0, 0x49, 0x44, 0x41, 0x54], Idat, Iend) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Rejects_structurally_invalid_png(string why, byte[] png)
    {
        Assert.False(ImageSave.IsValidPng(png), why);
        Assert.Null(ImageSave.DecodePng(Convert.ToBase64String(png)));
    }

    [Theory]
    [InlineData(0)]  // in IHDR
    [InlineData(1)]  // in IDAT
    [InlineData(2)]  // in IEND
    public void Rejects_a_bad_crc_in_any_chunk(int which)
    {
        var png = Join(Sig, Ihdr(), Idat, Iend);
        var crcOffset = which switch { 0 => 8 + 8 + 13, 1 => 8 + 25 + 8 + 8, _ => png.Length - 4 };
        png[crcOffset] ^= 0x01;
        Assert.False(ImageSave.IsValidPng(png));
    }

    [Fact]
    public void Rejects_a_flipped_data_byte_from_a_real_encoder()
    {
        var png = Png(16, 16);
        png[^20] ^= 0x40; // inside the last IDAT's data
        Assert.False(ImageSave.IsValidPng(png));
    }

    [Fact]
    public void Rejects_too_many_chunks()
    {
        var parts = new List<byte[]> { Sig, Ihdr() };
        for (var i = 0; i < ImageSave.MaxChunks; i++) parts.Add(Idat);
        parts.Add(Iend);
        Assert.False(ImageSave.IsValidPng(Join([.. parts])));
    }

    public static TheoryData<string> BadBase64 => new()
    {
        "", "not base64!!", "QUJD$", "iVBORw0KGgo", // truncated signature only
        Convert.ToBase64String("GIF89a.........................................................."u8.ToArray()),
        Convert.ToBase64String(new byte[100]),
    };

    [Theory]
    [MemberData(nameof(BadBase64))]
    public void Rejects_bad_or_non_png_data_before_any_dialog(string b64)
    {
        var (save, prompt, writes) = Make();
        var reply = Call(save, new JsonObject { ["pngBase64"] = b64 });
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal("pngBase64", reply["error"]!["data"]!["field"]!.GetValue<string>());
        Assert.Equal(0, prompt.Calls);
        Assert.Empty(writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{}""")]
    [InlineData("""{"pngBase64":42}""")]
    [InlineData("""{"pngBase64":null}""")]
    [InlineData("""{"pngBase64":"AAAA","path":"C:\\x.png"}""")]
    [InlineData("""{"pngBase64":"AAAA","suggestedName":7}""")]
    public void Rejects_malformed_params(string? json)
    {
        var (save, prompt, _) = Make();
        var reply = Call(save, json is null ? null : JsonNode.Parse(json)!.AsObject());
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public void Rejects_oversize_png()
    {
        Assert.Null(ImageSave.DecodePng(new string('A', ImageSave.MaxBase64Chars + 4)));
        // A structurally valid PNG padded past 4 MB.
        var png = Png(8, 8);
        var big = new byte[ImageSave.MaxPngBytes + 1];
        png.AsSpan(0, png.Length - 12).CopyTo(big);
        png.AsSpan(png.Length - 12).CopyTo(big.AsSpan(big.Length - 12));
        Assert.False(ImageSave.IsValidPng(big));
    }

    [Theory]
    [InlineData(0u, 10u)]
    [InlineData(10u, 0u)]
    [InlineData(4097u, 10u)]
    [InlineData(10u, 5000u)]
    [InlineData(uint.MaxValue, 10u)]
    public void Rejects_unreasonable_dimensions(uint width, uint height)
    {
        var png = Png(8, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        FixCrc(png, 8); // so the dimension rule, not the CRC, is what refuses it
        Assert.False(ImageSave.IsValidPng(png));
        Assert.Null(ImageSave.DecodePng(Convert.ToBase64String(png)));
    }

    [Fact]
    public void Accepts_the_maximum_dimensions() => Assert.True(ImageSave.IsValidPng(Png(4096, 1)));

    [Fact]
    public void Rejects_png_without_iend() =>
        Assert.False(ImageSave.IsValidPng(Png(8, 8).AsSpan()[..^12]));

    [Theory]
    [InlineData("card.png", "card.png")]
    [InlineData("card", "card.png")]
    [InlineData("Optimus-benchmark-2026-11-09.png", "Optimus-benchmark-2026-11-09.png")]
    [InlineData(@"..\..\Windows\System32\evil.png", "evil.png")]
    [InlineData("C:/Users/x/Desktop/score.PNG", "score.png")]
    [InlineData(@"\\server\share\x.png", "x.png")]
    [InlineData("C:card.png", "card.png")]
    [InlineData("..", null)]
    [InlineData(".hidden.png", null)]
    [InlineData("name.", null)]
    [InlineData("a/", null)]
    [InlineData("CON.png", null)]
    [InlineData("nul", null)]
    [InlineData("com1.txt.png", null)]
    [InlineData("a*b.png", null)]
    [InlineData("a?.png", null)]
    [InlineData("a<b>.png", null)]
    [InlineData("card.png:stream", "stream.png")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void SafeName_strips_paths_and_refuses_unsafe_names(string? suggested, string? expected) =>
        Assert.Equal(expected, ImageSave.SafeName(suggested));

    [Fact]
    public void Unsafe_suggested_name_falls_back_to_the_default()
    {
        var (save, prompt, _) = Make();
        Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(Png(8, 8)), ["suggestedName"] = "CON" });
        Assert.Equal("Optimus-benchmark-2026-11-09.png", prompt.DefaultName);
    }

    [Fact]
    public void Path_suggested_name_offers_only_the_file_name()
    {
        var (save, prompt, _) = Make();
        Call(save, new JsonObject { ["pngBase64"] = Convert.ToBase64String(Png(8, 8)), ["suggestedName"] = @"C:\Windows\x.png" });
        Assert.Equal("x.png", prompt.DefaultName);
    }

    [Fact]
    public async Task Dispatcher_handles_saveImage_in_the_shell_never_forwarding()
    {
        var backend = new NoBackend();
        var prompt = new FakePrompt(path: null);
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, DesktopLog.Disabled, images: new ImageSave(prompt, (_, _, _) => { }));
        var b64 = Convert.ToBase64String(Png());
        var reply = await dispatcher.HandleAsync($$$"""{"type":"rpc","id":9,"method":"shell.saveImage","params":{"pngBase64":"{{{b64}}}"}}""", Doc, true, CancellationToken.None);
        var json = JsonNode.Parse(reply!)!;
        Assert.Equal(9, json["id"]!.GetValue<int>());
        Assert.False(json["result"]!["saved"]!.GetValue<bool>());
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Dispatcher_without_handler_reports_unavailable()
    {
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled);
        var reply = await dispatcher.HandleAsync("""{"type":"rpc","id":2,"method":"shell.saveImage","params":{"pngBase64":"AAAA"}}""", Doc, true, CancellationToken.None);
        Assert.Equal(-32013, JsonNode.Parse(reply!)!["error"]!["code"]!.GetValue<int>());
    }
}

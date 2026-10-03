using System.Buffers.Binary;
using System.Text;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Whether a PNG's colour description changes its pixels.
///
/// The tool converts what it reads into sRGB, so its checks and the window show a Display-P3 or
/// gamma-tagged picture in the colours meant. The game does not: Texture2D.LoadImage ignores
/// iCCP and gAMA and uses the stored numbers as they are (measured with Unity 6000.0.59f2, the
/// game's version). "cks install" places the file byte for byte, so such a sheet showed in the game
/// in other colours than the ones cks had checked, with nothing said (the test campaign of
/// 2026-09-30). The window re-encodes before placing, so it was never affected.
/// </summary>
public sealed class ColourProfileTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cks-colour-{Guid.NewGuid():N}");

    public ColourProfileTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the operating system clears the temp directory eventually
        }
    }

    [Theory]
    [InlineData(TaggedSheet.Gamma100000)]
    [InlineData(TaggedSheet.DisplayP3)]
    public void 色の記述で画素が変わるPNGを見分ける(TaggedSheet kind)
    {
        string path = TaggedSheets.Write(kind, Path.Combine(_work, $"{kind}.png"));

        Assert.True(PixelOps.ColourProfileChangesPixels(path));
    }

    [Theory]
    [InlineData(TaggedSheet.Plain)]
    [InlineData(TaggedSheet.SrgbWithGamma)]
    public void sRGBのPNGは色が変わらないと答える(TaggedSheet kind)
    {
        // A plain file is what this tool writes; sRGB with gAMA 45455 is what System.Drawing and WIC
        // write. The sRGB chunk overrides the gamma, so converting it is the identity.
        string path = TaggedSheets.Write(kind, Path.Combine(_work, $"{kind}.png"));

        Assert.False(PixelOps.ColourProfileChangesPixels(path));
    }

    [Fact]
    public void 見える画素の色が変わらなければ透明な画素の色が変わっても注意しない()
    {
        // A grey picture tagged Display P3: P3 shares sRGB's tone curve and white point, so a grey
        // converts to itself and nothing visible changes. Its fully transparent pixels still hold a
        // colour - some editors keep one there - and that colour does move. Neither the game nor
        // this tool shows those pixels, so the notice that the colours will differ was untrue
        // (the test campaign of 2026-10-01).
        string path = TaggedSheets.Write(TaggedSheet.DisplayP3GreyColouredTransparent, Path.Combine(_work, "grey-p3.png"));

        using (SKCodec codec = SKCodec.Create(path))
        {
            // The premise: the file really keeps a colour in its transparent pixels and is tagged
            SKImageInfo info = new(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using SKBitmap stored = new(info);
            codec.GetPixels(info, stored.GetPixels());
            Assert.Contains(stored.Pixels, p => p.Alpha == 0 && p.Red == 200);
            Assert.False(codec.Info.ColorSpace?.IsSrgb ?? true, "前提が崩れた: sRGB 以外の色の記述が付いていない");
        }

        Assert.False(PixelOps.ColourProfileChangesPixels(path));
    }

    [Fact]
    public void 開けないファイルは色が変わらないと答えて例外を投げない()
    {
        // Asked only after the decode has succeeded, so a file gone in between is the one case
        string missing = Path.Combine(_work, "gone.png");

        Assert.False(PixelOps.ColourProfileChangesPixels(missing));
    }
}

/// <summary>Kinds of sheet by the colour description they carry.</summary>
public enum TaggedSheet
{
    /// <summary>No colour chunk at all, as this tool writes it.</summary>
    Plain,

    /// <summary>gAMA 1.0 alone: stored as linear, so every converted value moves.</summary>
    Gamma100000,

    /// <summary>An iCCP describing Display P3, as Skia writes it for a P3 image.</summary>
    DisplayP3,

    /// <summary>sRGB with gAMA 45455, the pair System.Drawing and WIC write.</summary>
    SrgbWithGamma,

    /// <summary>Grey art tagged Display P3, its fully transparent pixels holding a colour.</summary>
    DisplayP3GreyColouredTransparent,
}

/// <summary>Writes a valid sheet carrying a given colour description.</summary>
internal static class TaggedSheets
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    /// <summary>Signature (8) and IHDR (4 length + 4 type + 13 data + 4 CRC).</summary>
    private const int AfterHeader = 33;

    public static string Write(TaggedSheet kind, string path)
    {
        using SKBitmap sheet = CreateSheet();

        switch (kind)
        {
            case TaggedSheet.DisplayP3:
                WriteDisplayP3(sheet, path);
                break;

            case TaggedSheet.DisplayP3GreyColouredTransparent:
                WriteGreyWithColouredTransparent(sheet, path);
                break;

            default:
                PixelOps.EncodePng(sheet, path);
                byte[] plain = File.ReadAllBytes(path);
                byte[] tagged = kind switch
                {
                    TaggedSheet.Gamma100000 => InsertAfterHeader(plain, Chunk("gAMA", BigEndian(100000))),
                    TaggedSheet.SrgbWithGamma => InsertAfterHeader(plain, Chunk("sRGB", [0]), Chunk("gAMA", BigEndian(45455))),
                    _ => plain,
                };
                File.WriteAllBytes(path, tagged);
                break;
        }

        return path;
    }

    /// <summary>A sheet with art in every frame, well inside the cells, in colours a conversion moves.</summary>
    private static SKBitmap CreateSheet()
    {
        using SKBitmap sprite = PixelOps.CreateEmpty(6, 10);
        SKColor[] pixels = sprite.Pixels;
        Array.Fill(pixels, new SKColor(200, 100, 50));
        sprite.Pixels = pixels;

        return SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static)).Sheet;
    }

    /// <summary>The same numbers, tagged as Display P3, so Skia embeds an iCCP describing it.</summary>
    private static void WriteDisplayP3(SKBitmap sheet, string path)
    {
        using SKColorSpace p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        SKImageInfo info = new(sheet.Width, sheet.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, p3);
        using SKBitmap tagged = new(info);
        sheet.GetPixelSpan().CopyTo(tagged.GetPixelSpan());

        using SKImage image = SKImage.FromBitmap(tagged);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>
    /// The sheet's art in grey, its transparent pixels holding RGB(200,40,10), tagged Display P3.
    ///
    /// Put together by hand: Skia's encoder writes transparent pixels as zero, so the colour
    /// some editors keep there would not survive it. The profile is the iCCP chunk Skia writes
    /// for a P3 image; the pixels go in as one IDAT of unfiltered RGBA rows.
    /// </summary>
    private static void WriteGreyWithColouredTransparent(SKBitmap sheet, string path)
    {
        WriteDisplayP3(sheet, path);
        byte[] iccp = ChunksOf(File.ReadAllBytes(path)).First(c => c.Type == "iCCP").Whole;

        SKColor[] pixels = sheet.Pixels;
        using MemoryStream raw = new();
        for (int y = 0; y < sheet.Height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < sheet.Width; x++)
            {
                SKColor p = pixels[(y * sheet.Width) + x];
                byte[] rgba = p.Alpha == 0 ? [200, 40, 10, 0] : [128, 128, 128, 255];
                raw.Write(rgba);
            }
        }

        using MemoryStream compressed = new();
        using (System.IO.Compression.ZLibStream zlib = new(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        byte[] header = [.. BigEndian((uint)sheet.Width), .. BigEndian((uint)sheet.Height), 8, 6, 0, 0, 0];
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", header),
            .. iccp,
            .. Chunk("IDAT", compressed.ToArray()),
            .. Chunk("IEND", []),
        ];
        File.WriteAllBytes(path, png);
    }

    /// <summary>The chunks of a PNG, each with its type and its bytes from length to CRC.</summary>
    private static IEnumerable<(string Type, byte[] Whole)> ChunksOf(byte[] png)
    {
        int position = 8;
        while (position + 12 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            string type = Encoding.ASCII.GetString(png, position + 4, 4);
            yield return (type, png[position..(position + 12 + length)]);
            position += 12 + length;
        }
    }

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] InsertAfterHeader(byte[] png, params byte[][] chunks) =>
        [.. png[..AfterHeader], .. chunks.SelectMany(c => c), .. png[AfterHeader..]];

    /// <summary>One chunk: length, type, data and the CRC-32 of type and data.</summary>
    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        byte[] length = BigEndian((uint)data.Length);
        return [.. length, .. typeAndData, .. BigEndian(Crc32(typeAndData))];
    }

    /// <summary>The CRC-32 PNG uses (ISO 3309, reflected, polynomial 0xEDB88320).</summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}

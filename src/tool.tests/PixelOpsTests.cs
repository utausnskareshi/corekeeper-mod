using CoreKeeperSkinTool;
using CoreKeeperSkinTool.Imaging;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>Verifies that each stage of the image conversion behaves as intended.</summary>
public sealed class PixelOpsTests
{
    /// <summary>Creates a bitmap filled with the given colour.</summary>
    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, color);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static SKColor At(SKBitmap bitmap, int x, int y) => bitmap.Pixels[(y * bitmap.Width) + x];

    private static void Set(SKBitmap bitmap, int x, int y, SKColor color)
    {
        SKColor[] pixels = bitmap.Pixels;
        pixels[(y * bitmap.Width) + x] = color;
        bitmap.Pixels = pixels;
    }

    [Fact]
    public void HardenAlpha_しきい値0でも透明ピクセルは透明のまま()
    {
        // Regression: the test was "alpha < threshold", and no alpha is below 0, so a threshold
        // of 0 turned every fully transparent pixel into opaque black and filled the background.
        using SKBitmap bitmap = Solid(2, 2, SKColors.Transparent);
        Set(bitmap, 0, 0, new SKColor(10, 20, 30, 255));

        using SKBitmap result = PixelOps.HardenAlpha(bitmap, 0);

        Assert.Equal(255, At(result, 0, 0).Alpha);
        Assert.Equal(0, At(result, 1, 0).Alpha);
        Assert.Equal(0, At(result, 0, 1).Alpha);
        Assert.Equal(0, At(result, 1, 1).Alpha);
    }

    [Fact]
    public void HardenAlpha_しきい値未満は透明で以上は不透明になる()
    {
        using SKBitmap bitmap = Solid(3, 1, SKColors.Transparent);
        Set(bitmap, 0, 0, new SKColor(255, 0, 0, 127));
        Set(bitmap, 1, 0, new SKColor(255, 0, 0, 128));
        Set(bitmap, 2, 0, new SKColor(255, 0, 0, 200));

        using SKBitmap result = PixelOps.HardenAlpha(bitmap, 128);

        Assert.Equal(0, At(result, 0, 0).Alpha);
        Assert.Equal(255, At(result, 1, 0).Alpha);
        Assert.Equal(255, At(result, 2, 0).Alpha);
    }

    [Fact]
    public void Crop_画像の外にはみ出す範囲を拒否する()
    {
        // Regression: an overhanging rectangle used to keep reading along the pixel array
        // and silently splice in the following row.
        using SKBitmap bitmap = Solid(4, 4, SKColors.Red);

        Assert.Throws<ToolException>(() => PixelOps.Crop(bitmap, new SKRectI(2, 0, 6, 4)));
        Assert.Throws<ToolException>(() => PixelOps.Crop(bitmap, new SKRectI(0, 2, 4, 7)));
        Assert.Throws<ToolException>(() => PixelOps.Crop(bitmap, new SKRectI(-1, 0, 3, 3)));
    }

    [Fact]
    public void FindOpaqueBounds_全て透明なら空の矩形を返す()
    {
        using SKBitmap bitmap = Solid(5, 5, SKColors.Transparent);

        Assert.True(PixelOps.FindOpaqueBounds(bitmap).IsEmpty);
    }

    [Fact]
    public void FindOpaqueBounds_しきい値未満の半透明を範囲に含めない()
    {
        using SKBitmap bitmap = Solid(5, 5, SKColors.Transparent);
        Set(bitmap, 0, 0, new SKColor(255, 0, 0, 40));
        Set(bitmap, 2, 2, new SKColor(255, 0, 0, 255));

        SKRectI loose = PixelOps.FindOpaqueBounds(bitmap, 1);
        SKRectI strict = PixelOps.FindOpaqueBounds(bitmap, 128);

        Assert.Equal(new SKRectI(0, 0, 3, 3), loose);
        Assert.Equal(new SKRectI(2, 2, 3, 3), strict);
    }

    [Fact]
    public void CreateEmpty_全ピクセルが透明になる()
    {
        using SKBitmap bitmap = PixelOps.CreateEmpty(4, 3);

        Assert.Equal(4, bitmap.Width);
        Assert.Equal(3, bitmap.Height);
        Assert.All(bitmap.Pixels, p => Assert.Equal(0, p.Alpha));
    }

    [Fact]
    public void Pixels_不透明度を落とさずに往復できる()
    {
        // Unless the internal form is unpremultiplied, semi-transparent colour channels are lost
        SKColor translucentRed = new(255, 0, 0, 128);
        using SKBitmap bitmap = Solid(2, 2, translucentRed);

        Assert.All(bitmap.Pixels, p =>
        {
            Assert.Equal(255, p.Red);
            Assert.Equal(128, p.Alpha);
        });
    }

    [Fact]
    public void RemoveBackground_四隅と地続きの色だけが透明になる()
    {
        // Put a single red dot at the centre of a white field
        using SKBitmap source = Solid(5, 5, SKColors.White);
        Set(source, 2, 2, SKColors.Red);

        using SKBitmap result = PixelOps.RemoveBackground(source, tolerance: 0);

        Assert.Equal(0, At(result, 0, 0).Alpha);
        Assert.Equal(0, At(result, 4, 4).Alpha);
        Assert.Equal(SKColors.Red, At(result, 2, 2));
    }

    [Fact]
    public void RemoveBackground_地続きでない同色は残る()
    {
        // White enclosed by a red border is not connected to the corners, so it must survive
        using SKBitmap source = Solid(5, 5, SKColors.White);
        for (int i = 1; i <= 3; i++)
        {
            Set(source, i, 1, SKColors.Red);
            Set(source, i, 3, SKColors.Red);
            Set(source, 1, i, SKColors.Red);
            Set(source, 3, i, SKColors.Red);
        }

        using SKBitmap result = PixelOps.RemoveBackground(source, tolerance: 0);

        Assert.Equal(0, At(result, 0, 0).Alpha);
        Assert.Equal(SKColors.White, At(result, 2, 2));
    }

    [Fact]
    public void RemoveBackground_許容差の範囲内なら近い色もまとめて消える()
    {
        using SKBitmap source = Solid(4, 4, SKColors.White);
        Set(source, 1, 1, new SKColor(250, 250, 250));  // ほぼ白

        using SKBitmap result = PixelOps.RemoveBackground(source, tolerance: 10);

        Assert.Equal(0, At(result, 1, 1).Alpha);
    }

    [Fact]
    public void RemoveBackground_既に透明な隅は種にしない()
    {
        using SKBitmap source = PixelOps.CreateEmpty(3, 3);
        Set(source, 1, 1, SKColors.Blue);

        using SKBitmap result = PixelOps.RemoveBackground(source, tolerance: 0);

        // An already-transparent image must not lose its content
        Assert.Equal(SKColors.Blue, At(result, 1, 1));
    }

    [Fact]
    public void FindOpaqueBounds_不透明部分の外接矩形を返す()
    {
        using SKBitmap source = PixelOps.CreateEmpty(10, 10);
        Set(source, 3, 4, SKColors.Green);
        Set(source, 6, 8, SKColors.Green);

        SKRectI bounds = PixelOps.FindOpaqueBounds(source);

        Assert.Equal(3, bounds.Left);
        Assert.Equal(4, bounds.Top);
        Assert.Equal(4, bounds.Width);   // x 3..6
        Assert.Equal(5, bounds.Height);  // y 4..8
    }

    [Fact]
    public void FindOpaqueBounds_全て透明なら空を返す()
    {
        using SKBitmap source = PixelOps.CreateEmpty(4, 4);

        Assert.True(PixelOps.FindOpaqueBounds(source).IsEmpty);
    }

    [Fact]
    public void Crop_指定範囲だけを切り出す()
    {
        using SKBitmap source = Solid(4, 4, SKColors.White);
        Set(source, 1, 2, SKColors.Red);

        using SKBitmap result = PixelOps.Crop(source, new SKRectI(1, 2, 3, 4));

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(SKColors.Red, At(result, 0, 0));
    }

    [Fact]
    public void Crop_空の範囲は例外にする()
    {
        using SKBitmap source = Solid(4, 4, SKColors.White);

        Assert.Throws<ToolException>(() => PixelOps.Crop(source, SKRectI.Empty));
    }

    [Theory]
    [InlineData(ResampleMode.Smooth)]
    [InlineData(ResampleMode.Nearest)]
    public void ResizeToFit_縦横比を保って箱に収まる(ResampleMode mode)
    {
        using SKBitmap source = Solid(100, 200, SKColors.Blue);

        using SKBitmap result = PixelOps.ResizeToFit(source, 16, 19, mode);

        Assert.True(result.Width <= 16, $"幅が箱を超えた: {result.Width}");
        Assert.True(result.Height <= 19, $"高さが箱を超えた: {result.Height}");
        // The input is tall, so height hits the limit and width ends up at half the ratio
        Assert.Equal(19, result.Height);
        Assert.Equal(10, result.Width);
    }

    [Fact]
    public void ResizeToFit_透明の隣の色が縁に滲まない()
    {
        // Opaque blue on the left half, transparent on the right. Interpolating unpremultiplied
        // would blend the transparent side's black (0,0,0,0) in and darken the edge.
        using SKBitmap source = PixelOps.CreateEmpty(40, 10);
        SKColor[] pixels = source.Pixels;
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                pixels[(y * 40) + x] = SKColors.Blue;
            }
        }

        source.Pixels = pixels;

        using SKBitmap result = PixelOps.ResizeToFit(source, 20, 5, ResampleMode.Smooth);

        // Pixels that stay opaque must keep their blue tone rather than drifting towards black
        foreach (SKColor color in result.Pixels.Where(c => c.Alpha > 200))
        {
            Assert.True(color.Blue > 200, $"青が黒に寄っている: {color}");
        }
    }

    [Fact]
    public void ResizeToFit_サイズがゼロ以下なら例外にする()
    {
        using SKBitmap source = Solid(10, 10, SKColors.Blue);

        Assert.Throws<ToolException>(() => PixelOps.ResizeToFit(source, 0, 5, ResampleMode.Smooth));
    }

    [Fact]
    public void HardenAlpha_しきい値で完全透明と完全不透明に振り分ける()
    {
        using SKBitmap source = PixelOps.CreateEmpty(3, 1);
        Set(source, 0, 0, new SKColor(10, 20, 30, 127));
        Set(source, 1, 0, new SKColor(10, 20, 30, 128));
        Set(source, 2, 0, new SKColor(10, 20, 30, 255));

        using SKBitmap result = PixelOps.HardenAlpha(source, 128);

        Assert.Equal(0, At(result, 0, 0).Alpha);
        Assert.Equal(255, At(result, 1, 0).Alpha);
        Assert.Equal(255, At(result, 2, 0).Alpha);
        // The colour channels must be preserved
        Assert.Equal(10, At(result, 1, 0).Red);
    }

    [Fact]
    public void AddOutline_縦横が2ずつ増え元絵が中央に来る()
    {
        using SKBitmap source = PixelOps.CreateEmpty(3, 3);
        Set(source, 1, 1, SKColors.White);

        using SKBitmap result = PixelOps.AddOutline(source, SKColors.Black);

        Assert.Equal(5, result.Width);
        Assert.Equal(5, result.Height);
        Assert.Equal(SKColors.White, At(result, 2, 2));
    }

    [Fact]
    public void AddOutline_不透明部分の上下左右だけを縁取る()
    {
        using SKBitmap source = PixelOps.CreateEmpty(3, 3);
        Set(source, 1, 1, SKColors.White);

        using SKBitmap result = PixelOps.AddOutline(source, SKColors.Black);

        // The four orthogonal neighbours are outlined
        Assert.Equal(SKColors.Black, At(result, 1, 2));
        Assert.Equal(SKColors.Black, At(result, 3, 2));
        Assert.Equal(SKColors.Black, At(result, 2, 1));
        Assert.Equal(SKColors.Black, At(result, 2, 3));
        // Diagonals are not outlined
        Assert.Equal(0, At(result, 1, 1).Alpha);
    }

    [Fact]
    public void Quantize_指定色数以下に収まり透明部分は変わらない()
    {
        // The fifth pixel is left transparent on purpose. Every pixel used to carry a colour,
        // so the "transparent stays transparent" half of this test had nothing to look at and
        // a quantiser that folded transparent pixels into an opaque average would have passed.
        using SKBitmap source = PixelOps.CreateEmpty(5, 1);
        Set(source, 0, 0, new SKColor(255, 0, 0));
        Set(source, 1, 0, new SKColor(250, 0, 0));
        Set(source, 2, 0, new SKColor(0, 0, 255));
        Set(source, 3, 0, new SKColor(0, 0, 250));

        using SKBitmap result = PixelOps.Quantize(source, 2);

        int distinct = result.Pixels.Where(p => p.Alpha != 0).Select(p => (uint)p).Distinct().Count();
        Assert.Equal(2, distinct);
        // Similar reds and similar blues must be merged together
        Assert.Equal(At(result, 0, 0), At(result, 1, 0));
        Assert.Equal(At(result, 2, 0), At(result, 3, 0));
        Assert.NotEqual(At(result, 0, 0), At(result, 2, 0));

        // The transparent pixel is still transparent: filling it in would paint a background
        // over art the user cut out on purpose.
        Assert.Equal(0, At(result, 4, 0).Alpha);
    }

    /// <summary>
    /// Asking for more colours than the picture uses must leave it alone. It used to split
    /// anyway, and the cut fell inside a run of one colour, so a flat sprite came back with the
    /// same colour rendered as two or three shades - and more colours than it started with.
    /// </summary>
    [Fact]
    public void Quantize_色数が足りているときは変更しない()
    {
        SKColor[] palette =
        [
            new(0xE8, 0xC0, 0x9A), new(0xA6, 0x86, 0x66), new(0x3C, 0x2D, 0x23),
            new(0x2A, 0x1F, 0x18), new(0x63, 0xA9, 0x6B), new(0x45, 0x77, 0x4B),
            new(0x14, 0x14, 0x1E), new(0xFF, 0xD1, 0x66),
        ];

        using SKBitmap source = CreateFrom(palette, [90, 40, 45, 20, 60, 30, 4, 6]);
        using SKBitmap result = PixelOps.Quantize(source, 16);

        Assert.Equal(source.Pixels, result.Pixels);
    }

    /// <summary>A run of one colour must never be split into several shades.</summary>
    [Fact]
    public void Quantize_同じ色を複数の色に割らない()
    {
        using SKBitmap source = CreateFrom([SKColors.Black, SKColors.White], [4, 1]);
        using SKBitmap result = PixelOps.Quantize(source, 2);

        SKColor[] pixels = result.Pixels;

        Assert.Equal(4, pixels.Count(p => p == SKColors.Black));
        Assert.Equal(1, pixels.Count(p => p == SKColors.White));
    }

    /// <summary>Builds a one-row image holding each colour the given number of times.</summary>
    private static SKBitmap CreateFrom(SKColor[] palette, int[] counts)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(counts.Sum(), 1);
        SKColor[] pixels = bitmap.Pixels;

        int at = 0;
        for (int c = 0; c < palette.Length; c++)
        {
            for (int n = 0; n < counts[c]; n++)
            {
                pixels[at++] = palette[c];
            }
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    [Fact]
    public void Quantize_色数が2未満なら例外にする()
    {
        using SKBitmap source = Solid(2, 2, SKColors.Red);

        Assert.Throws<ToolException>(() => PixelOps.Quantize(source, 1));
    }

    [Fact]
    public void Quantize_全て透明でも落ちない()
    {
        using SKBitmap source = PixelOps.CreateEmpty(3, 3);

        using SKBitmap result = PixelOps.Quantize(source, 8);

        Assert.All(result.Pixels, p => Assert.Equal(0, p.Alpha));
    }

    [Fact]
    public void EncodePngとDecode_内容を保ったまま往復できる()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cks-test-{Guid.NewGuid():N}.png");
        try
        {
            using SKBitmap source = PixelOps.CreateEmpty(3, 2);
            Set(source, 0, 0, new SKColor(12, 34, 56, 255));
            Set(source, 2, 1, new SKColor(200, 100, 50, 128));

            PixelOps.EncodePng(source, path);
            using SKBitmap loaded = PixelOps.Decode(path);

            Assert.Equal(3, loaded.Width);
            Assert.Equal(2, loaded.Height);
            Assert.Equal(new SKColor(12, 34, 56, 255), At(loaded, 0, 0));
            Assert.Equal(new SKColor(200, 100, 50, 128), At(loaded, 2, 1));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Decode_存在しないファイルは例外にする()
    {
        Assert.Throws<ToolException>(() => PixelOps.Decode(Path.Combine(Path.GetTempPath(), "no-such-file.png")));
    }

    /// <summary>
    /// Writes a JPEG carrying an EXIF orientation tag. The pixels are stored sideways and the
    /// tag says so, which is exactly how a phone stores a portrait photograph.
    /// </summary>
    private static string WriteRotatedJpeg(string path, SKEncodedOrigin origin)
    {
        // Stored 40 wide by 20 high, with the left half red; upright it should be 20x40 with
        // the top half red.
        using SKBitmap stored = PixelOps.CreateEmpty(40, 20);
        SKColor[] pixels = stored.Pixels;
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                pixels[(y * 40) + x] = x < 20 ? SKColors.Red : SKColors.Blue;
            }
        }

        stored.Pixels = pixels;

        using SKImage image = SKImage.FromBitmap(stored);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 100)!;
        byte[] jpeg = data.ToArray();

        // Splice a minimal APP1/EXIF block carrying only the orientation tag, right after SOI
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, // APP1, length 34
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00, // "Exif\0\0"
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian TIFF header
            0x00, 0x01, // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)((int)origin >> 8), (byte)origin, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, // next IFD
        ];

        using (FileStream stream = File.Create(path))
        {
            stream.Write(jpeg, 0, 2);
            stream.Write(exif, 0, exif.Length);
            stream.Write(jpeg, 2, jpeg.Length - 2);
        }

        return path;
    }

    [Fact]
    public void Decode_最後まで読めた画像はcompleteが真になる()
    {
        // The flag decides whether the window warns, whether validate reports a problem, and
        // whether a sheet is allowed into the game. Nothing checked it until now.
        string path = Path.Combine(Path.GetTempPath(), $"cks-whole-{Guid.NewGuid():N}.png");

        try
        {
            using (SKBitmap source = Solid(40, 20, SKColors.Red))
            {
                PixelOps.EncodePng(source, path);
            }

            using SKBitmap decoded = PixelOps.Decode(path, out bool complete);

            Assert.True(complete, "完全な画像が切れている扱いになっている");
            Assert.Equal(40, decoded.Width);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Decode_途中で切れた画像はcompleteが偽になり読めた分だけ返る()
    {
        // Skia zero-fills what it could not read, so the file comes back as a valid image of the
        // right size with a transparent lower half. Only this flag tells the two apart.
        string path = Path.Combine(Path.GetTempPath(), $"cks-cut-{Guid.NewGuid():N}.png");
        string whole = Path.Combine(Path.GetTempPath(), $"cks-src-{Guid.NewGuid():N}.png");

        try
        {
            using (SKBitmap source = Solid(200, 200, SKColors.Red))
            {
                PixelOps.EncodePng(source, whole);
            }

            byte[] bytes = File.ReadAllBytes(whole);
            File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

            using SKBitmap decoded = PixelOps.Decode(path, out bool complete);

            Assert.False(complete, "切れた画像が完全な扱いになっている");
            Assert.Equal(200, decoded.Width);
            Assert.Equal(200, decoded.Height);

            // The part that could not be read comes back transparent, which is why it passes
            // every other check unless the flag is looked at
            Assert.Contains(decoded.Pixels, p => p.Alpha == 0);
        }
        finally
        {
            foreach (string each in new[] { path, whole })
            {
                if (File.Exists(each))
                {
                    File.Delete(each);
                }
            }
        }
    }

    [Fact]
    public void Decode_他のアプリが排他で開いているファイルは形式のせいにしない()
    {
        // SKCodec.Create returns null without saying why, and the commonest reason is not the
        // format: an image editor or a sync client has the file open for its exclusive use.
        // Told it was an unsupported format, people convert a perfectly good PNG and get nowhere.
        string path = Path.Combine(Path.GetTempPath(), $"cks-locked-{Guid.NewGuid():N}.png");

        try
        {
            using (SKBitmap source = Solid(8, 8, SKColors.Red))
            {
                PixelOps.EncodePng(source, path);
            }

            using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

            ToolException ex = Assert.Throws<ToolException>(() => PixelOps.Decode(path));

            Assert.Equal("error.image.locked", ex.MessageKey);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Decode_EXIFの回転指示どおりに向きを直す()
    {
        // A photograph from a phone is stored the way the sensor read it, with a tag saying
        // which way up it goes. Ignoring the tag put the picture into all thirty-nine frames
        // lying on its side, and because the trim and the scale work on whatever shape arrives,
        // the proportions came out wrong too.
        string path = WriteRotatedJpeg(
            Path.Combine(Path.GetTempPath(), $"cks-exif-{Guid.NewGuid():N}.jpg"),
            SKEncodedOrigin.RightTop);

        try
        {
            using SKBitmap decoded = PixelOps.Decode(path);

            // Upright, the stored 40x20 becomes 20x40
            Assert.Equal(20, decoded.Width);
            Assert.Equal(40, decoded.Height);

            // And the red half is now the top, not the left
            Assert.True(At(decoded, 10, 5).Red > At(decoded, 10, 5).Blue, "上半分が赤になっていない");
            Assert.True(At(decoded, 10, 34).Blue > At(decoded, 10, 34).Red, "下半分が青になっていない");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Writes a JPEG whose four quarters are told apart by colour, carrying an orientation tag.
    /// Four distinct quarters pin down mirrors as well as turns; a picture split in half only
    /// pins down half of them, which is how two broken matrices went unnoticed.
    /// </summary>
    private static string WriteQuadrantJpeg(string path, SKEncodedOrigin origin)
    {
        // Stored 80 wide by 40 high: red, green, blue, white clockwise from the top left. Larger
        // than it needs to be so every sample sits well clear of a boundary, where JPEG rings.
        using SKBitmap stored = PixelOps.CreateEmpty(80, 40);
        SKColor[] pixels = stored.Pixels;
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                pixels[(y * 80) + x] = (x < 40, y < 20) switch
                {
                    (true, true) => SKColors.Red,
                    (false, true) => SKColors.Green,
                    (true, false) => SKColors.Blue,
                    _ => SKColors.White,
                };
            }
        }

        stored.Pixels = pixels;

        using SKImage image = SKImage.FromBitmap(stored);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 100)!;
        byte[] jpeg = data.ToArray();

        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22,
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)((int)origin >> 8), (byte)origin, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        using (FileStream stream = File.Create(path))
        {
            stream.Write(jpeg, 0, 2);
            stream.Write(exif, 0, exif.Length);
            stream.Write(jpeg, 2, jpeg.Length - 2);
        }

        return path;
    }

    /// <summary>Names the quarter's colour, allowing for what JPEG does to a hard edge.</summary>
    private static string Quarter(SKBitmap bitmap, bool right, bool bottom)
    {
        // Sampled at the middle of the quarter, as far from every edge as the picture allows.
        int x = right ? bitmap.Width * 3 / 4 : bitmap.Width / 4;
        int y = bottom ? bitmap.Height * 3 / 4 : bitmap.Height / 4;
        SKColor c = At(bitmap, x, y);

        if (c.Alpha == 0)
        {
            return "透明";
        }

        (string Name, SKColor Value)[] candidates =
        [
            ("赤", SKColors.Red), ("緑", SKColors.Green), ("青", SKColors.Blue), ("白", SKColors.White),
        ];

        return candidates.MinBy(candidate =>
            ((c.Red - candidate.Value.Red) * (c.Red - candidate.Value.Red))
            + ((c.Green - candidate.Value.Green) * (c.Green - candidate.Value.Green))
            + ((c.Blue - candidate.Value.Blue) * (c.Blue - candidate.Value.Blue))).Name;
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 80, 40, "赤", "緑", "青", "白")]
    [InlineData(SKEncodedOrigin.TopRight, 80, 40, "緑", "赤", "白", "青")]
    [InlineData(SKEncodedOrigin.BottomRight, 80, 40, "白", "青", "緑", "赤")]
    [InlineData(SKEncodedOrigin.BottomLeft, 80, 40, "青", "白", "赤", "緑")]
    [InlineData(SKEncodedOrigin.LeftTop, 40, 80, "赤", "青", "緑", "白")]
    [InlineData(SKEncodedOrigin.RightTop, 40, 80, "青", "赤", "白", "緑")]
    [InlineData(SKEncodedOrigin.RightBottom, 40, 80, "白", "緑", "青", "赤")]
    [InlineData(SKEncodedOrigin.LeftBottom, 40, 80, "緑", "白", "赤", "青")]
    public void Decode_EXIFの向き8種すべてを正しく起こす(
        SKEncodedOrigin origin, int width, int height,
        string topLeft, string topRight, string bottomLeft, string bottomRight)
    {
        // Two of the eight matrices moved the picture clean off the canvas, so the decode
        // returned something fully transparent and the conversion then failed with a message
        // about background removal - advice that could not fix it, for a cause it never named.
        // Only one orientation was covered before, and it happened to be a working one.
        string path = WriteQuadrantJpeg(
            Path.Combine(Path.GetTempPath(), $"cks-exif-{origin}-{Guid.NewGuid():N}.jpg"),
            origin);

        try
        {
            using SKBitmap decoded = PixelOps.Decode(path, out bool complete);

            // Checked before the colours. A file this test wrote a moment ago can still come
            // back short - a scanner or an indexer gets to it first on a busy machine - and the
            // missing part arrives transparent, which showed up as a quarter being the wrong
            // colour. That reads as a broken matrix when it is nothing of the sort, so the two
            // are told apart here rather than left to whoever sees the failure.
            Assert.True(complete, $"{origin}: 一時ファイルを最後まで読めなかった（テスト環境の問題）");

            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);

            Assert.True(
                decoded.Pixels.Any(p => p.Alpha != 0),
                $"{origin}: 起こした結果が全部透明になっている");

            Assert.Equal(topLeft, Quarter(decoded, right: false, bottom: false));
            Assert.Equal(topRight, Quarter(decoded, right: true, bottom: false));
            Assert.Equal(bottomLeft, Quarter(decoded, right: false, bottom: true));
            Assert.Equal(bottomRight, Quarter(decoded, right: true, bottom: true));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Decode_幅広色域の画像はsRGBへ変換して読む()
    {
        // A screenshot from a recent phone or Mac is tagged Display-P3, where the same three
        // numbers mean a different colour than they do in sRGB. A decode that asks for no
        // conversion copies them straight through and everything downstream reads them as sRGB,
        // so every colour in the sheet, the preview and the game came out shifted with nothing
        // saying why. Measured before the fix, this file decoded to (187, 105, 62).
        //
        // The other direction - that an untagged file still decodes to exactly its own bytes -
        // is what EncodePngとDecode_内容を保ったまま往復できる already holds, using this colour.
        string path = Path.Combine(Path.GetTempPath(), $"cks-test-{Guid.NewGuid():N}.png");
        try
        {
            using SKColorSpace p3 = SKColorSpace.CreateRgb(
                SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);

            // SetPixel takes an sRGB colour and stores it in the bitmap's own space, so what the
            // file ends up holding is the P3 encoding of this colour, not these three numbers
            SKColor colour = new(200, 100, 50, 255);
            SKImageInfo info = new(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul, p3);
            using SKBitmap tagged = new(info);
            for (int y = 0; y < info.Height; y++)
            {
                for (int x = 0; x < info.Width; x++)
                {
                    tagged.SetPixel(x, y, colour);
                }
            }

            using (SKImage image = SKImage.FromBitmap(tagged))
            using (SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream file = File.Create(path))
            {
                encoded.SaveTo(file);
            }

            // Without this the test could pass while proving nothing: an untagged file decodes
            // to the same numbers whether the destination names a colour space or not.
            using (SKCodec codec = SKCodec.Create(path))
            {
                Assert.NotNull(codec.Info.ColorSpace);
                Assert.False(codec.Info.ColorSpace!.IsSrgb, "テスト用の画像に P3 が付いていない");
            }

            using SKBitmap loaded = PixelOps.Decode(path);
            SKColor got = At(loaded, 0, 0);

            // Room for a step of rounding, not for a different colour. Measured exact.
            Assert.InRange(got.Red, colour.Red - 2, colour.Red + 2);
            Assert.InRange(got.Green, colour.Green - 2, colour.Green + 2);
            Assert.InRange(got.Blue, colour.Blue - 2, colour.Blue + 2);
            Assert.Equal(colour.Alpha, got.Alpha);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

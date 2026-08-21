using SkiaSharp;

namespace CoreKeeperSkinTool.Imaging;

/// <summary>Interpolation used when scaling.</summary>
public enum ResampleMode
{
    /// <summary>Area averaging; the default, and best for shrinking photos and illustrations.</summary>
    Smooth,

    /// <summary>Nearest neighbour; suits pixel art scaled by an integer factor.</summary>
    Nearest,
}

/// <summary>
/// Side-effect-free transformations over bitmaps.
/// Each returns a new <see cref="SKBitmap"/> and leaves the argument untouched; the caller disposes both.
/// The internal representation is consistently RGBA8888 with unpremultiplied alpha.
/// </summary>
public static class PixelOps
{
    private static readonly SKImageInfo InfoTemplate =
        new(0, 0, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    /// <summary>
    /// What every load converts into.
    ///
    /// Held once rather than created per call: it is handed to the codec on every decode, decodes
    /// run on worker threads, and this wraps an unmanaged handle. Colour spaces are immutable, so
    /// one shared instance is safe.
    /// </summary>
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>Creates a working bitmap filled with transparency.</summary>
    public static SKBitmap CreateEmpty(int width, int height)
    {
        SKBitmap bitmap = new(InfoTemplate.WithSize(width, height));
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    /// <summary>Loads an image file. PNG, JPEG, BMP, GIF and WEBP are supported.</summary>
    public static SKBitmap Decode(string path) => Decode(path, out _);

    /// <summary>Loads an image file, and says whether the whole of it could be read.</summary>
    /// <param name="complete">
    /// False when only part of the file could be read and the rest came back transparent - either
    /// because it was cut short or because the data itself had an error in it. Reported this way
    /// rather than by refusing the file, because some otherwise usable images report an
    /// incomplete read, and because the caller is the one that can say so usefully.
    ///
    /// Returned here rather than left in a static property. As a property it was shared by the
    /// whole process, and this is called from a worker thread while other decodes are in
    /// flight: measured over four hundred concurrent decodes, a quarter of the callers read a
    /// verdict belonging to somebody else's file - in both directions.
    /// </param>
    public static SKBitmap Decode(string path, out bool complete)
    {
        if (!File.Exists(path))
        {
            throw new ToolException("error.image.notFound", [path], $"入力画像が見つからない: {path}");
        }

        using SKCodec? codec = SKCodec.Create(path);
        if (codec is null)
        {
            // Null says only that it could not be opened, never why, and the commonest reason is
            // not the format at all: another program - an image editor, a sync client - has the
            // file open for its exclusive use. Told it was an unsupported format, people convert
            // a perfectly good PNG and get nowhere. Asking the file system settles it.
            try
            {
                // Shared as widely as possible. The question is whether the bytes can be read at
                // all, not whether this program can keep others out: asking for FileShare.Read
                // failed against any program merely holding a write handle, so an unsupported
                // format that happened to be open elsewhere was reported as "another program is
                // using it" and the user was told to close something instead of to convert it.
                using FileStream probe = File.Open(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone between the check at the top and here. Cloud sync, a virus scanner
                // quarantining it, a build script replacing it: measured at 472 occurrences in
                // 4000 attempts against a file being deleted and recreated. Reporting that as
                // "another program is using it" sends the user looking for a program to close.
                throw new ToolException(
                    "error.image.notFound", [path], $"入力画像が見つからない: {path}");
            }
            catch (PathTooLongException)
            {
                // Skia opens the file by its narrow path, so it stops at the old limit even
                // where .NET does not - which is how a perfectly good PNG in a deep folder was
                // reported as an unsupported format. The advice for that sends the user off
                // converting a file that was never the problem.
                throw new ToolException(
                    "error.image.pathTooLong", [path.Length, path],
                    $"パスが長すぎて画像を開けない（{path.Length} 文字）: {path}" + Environment.NewLine +
                    "  もっと浅いフォルダへ移してから実行すること。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ToolException(
                    "error.image.locked", [path, ex.Message],
                    $"画像を開けない（他のアプリが使用中か、読み取りが許可されていない）: {path}" +
                    Environment.NewLine + $"  {ex.Message}");
            }

            // The probe read it, so the bytes are reachable and the format really is the problem
            // - unless the path is long enough that Skia's own narrow-path open gave up where
            // .NET's did not, which File.Open cannot detect on this side.
            if (path.Length >= 260)
            {
                throw new ToolException(
                    "error.image.pathTooLong", [path.Length, path],
                    $"パスが長すぎて画像を開けない可能性がある（{path.Length} 文字）: {path}" + Environment.NewLine +
                    "  もっと浅いフォルダへ移してから実行すること。");
            }

            throw new ToolException(
                "error.image.unreadable", [path],
                $"画像として読み込めない（対応形式は PNG / JPEG / BMP / GIF / WEBP）: {path}");
        }

        // Checked before allocating, not after. The header is the file's own claim about its
        // size, and honouring it first meant a 250-byte PNG declaring 60000x60000 asked for
        // 14 GB and surfaced as SkiaSharp's own English failure with a stack trace, rather than
        // as the "image is too large" message the pipeline already had ready.
        EnsureDecodable(codec.Info.Width, codec.Info.Height, path);

        SKImageInfo info = InfoTemplate.WithSize(codec.Info.Width, codec.Info.Height);
        SKBitmap bitmap = new(info);

        // The destination is named as sRGB so that Skia converts into it. A destination with no
        // colour space asks for no conversion at all: the file's own numbers are copied straight
        // through and then treated as sRGB from there on, so a Display-P3 file - what a recent
        // phone or Mac screenshot is - arrived with every colour shifted, and nothing said so.
        // Measured on a P3 file holding the colour sRGB(200,100,50): without this it decoded to
        // (187,105,62), with it to (200,100,50).
        //
        // Only the description handed to the codec carries the colour space; the bitmap itself
        // stays untagged as every other working bitmap here is. Converting sRGB to sRGB is the
        // identity, so an untagged file, a plain sRGB file and anything this tool wrote itself
        // all decode to exactly the bytes they did before.
        SKCodecResult result = codec.GetPixels(info.WithColorSpace(Srgb), bitmap.GetPixels());
        // ErrorInInput belongs with IncompleteInput, not with the failures. Skia defines it as
        // the same thing except that the shortfall was an error in the data rather than the data
        // running out, and the partial picture is just as usable - which is the case the verdict
        // below was written for. Refusing it instead put an untranslated Skia identifier on
        // screen and threw away a file the tool could have opened with a warning.
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput
            or SKCodecResult.ErrorInInput))
        {
            bitmap.Dispose();
            throw new ToolException(
                "error.image.decodeFailed", [result, path],
                $"画像のデコードに失敗した ({result}): {path}");
        }

        // Skia zero-fills whatever it could not decode, so a truncated file arrives looking like
        // a valid image with a transparent lower half. The pipeline then trims to the opaque
        // part and scales it up, quietly reframing the picture, so this cannot pass unremarked.
        complete = result == SKCodecResult.Success;

        // A photograph taken on a phone is stored the way the sensor read it, with a separate
        // note saying which way up it goes. Ignoring that note put the picture into all
        // thirty-nine frames lying on its side - and because the trim and the scale work on
        // whatever shape arrives, the proportions came out wrong as well.
        return ApplyOrientation(bitmap, codec.EncodedOrigin);
    }

    /// <summary>
    /// Turns a decoded image the right way up according to the orientation the file declares.
    /// Returns the bitmap untouched when it is already upright.
    /// </summary>
    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return bitmap;
        }

        // Quarter turns swap the sides, so the destination is measured accordingly
        bool quarterTurn = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        int width = quarterTurn ? bitmap.Height : bitmap.Width;
        int height = quarterTurn ? bitmap.Width : bitmap.Height;

        // The allocation is inside the try as well. A photograph near the sixty-four megapixel
        // limit is a quarter of a gigabyte, and turning it asks for another; if that throws, the
        // one already decoded has to be freed here rather than left to a finalizer, or a failure
        // on one picture makes the next one likelier to fail too.
        SKBitmap? upright = null;

        try
        {
            upright = CreateEmpty(width, height);

            using SKCanvas canvas = new(upright);

            // The matrices are the standard EXIF ones: each origin says where the first stored
            // pixel belongs, which is a rotation, a mirror, or both.
            canvas.SetMatrix(origin switch
            {
                SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(width, 0)),
                SKEncodedOrigin.BottomRight => SKMatrix.CreateScale(-1, -1).PostConcat(SKMatrix.CreateTranslation(width, height)),
                SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, height)),
                // Transpose, about the leading diagonal: (x,y) -> (y,x). The rotation puts it at
                // (-y,x) and the mirror brings it back to (y,x), which is already inside the
                // canvas - translating it as well pushed every column off the right edge.
                SKEncodedOrigin.LeftTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateScale(-1, 1)),
                SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(width, 0)),
                // Transverse, about the anti-diagonal: (x,y) -> (width-y, height-x). The chain
                // this replaced ended in a second mirror that undid the first, leaving every row
                // below the bottom edge.
                SKEncodedOrigin.RightBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateScale(-1, 1)).PostConcat(SKMatrix.CreateTranslation(width, height)),
                _ => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, height)),
            });

            canvas.DrawBitmap(bitmap, 0, 0);
        }
        catch
        {
            upright?.Dispose();
            throw;
        }
        finally
        {
            bitmap.Dispose();
        }

        return upright;
    }

    /// <summary>
    /// Largest image accepted, in pixels. The art ends up inside a 16x19 box, so this is far
    /// beyond any sane input.
    ///
    /// A stage does not hold one array of this size but several. At the background removal step
    /// the live set is the caller's bitmap, the clone the pipeline made, the copy `src.Pixels`
    /// hands back, the result array, the output bitmap and the flood fill's stack - so a picture
    /// right at this limit asks for something nearer 1.5 GB than the 256 MB one array would be.
    /// The limit is deliberately left where it is: lowering it would refuse pictures that work
    /// today. What it means is that on a machine short of memory the failure arrives as
    /// SkiaSharp's own English "Unable to allocate pixels for the bitmap" rather than as the
    /// message below, which is the one that says to shrink the picture first.
    /// </summary>
    public const long MaxSourcePixels = 64L * 1024 * 1024;

    private static void EnsureDecodable(int width, int height, string path)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ToolException(
                "error.image.badSize", [width, height, path],
                $"画像の寸法が不正（{width}x{height}）: {path}");
        }

        if ((long)width * height > MaxSourcePixels)
        {
            throw new ToolException(
                "error.image.tooLarge", [width, height, MaxSourcePixels / (1024 * 1024)],
                $"画像が大きすぎる: {width}x{height}" + Environment.NewLine +
                $"  {MaxSourcePixels / (1024 * 1024)} メガピクセルまで。あらかじめ縮小してから読み込むこと。");
        }
    }

    /// <summary>Writes the bitmap out as a PNG.</summary>
    public static void EncodePng(SKBitmap bitmap, string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ToolException(
                "error.image.encodeFailed", [path], $"PNG のエンコードに失敗した: {path}");
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Clears the background colour that is connected to the four corners.
    /// A corner that is already transparent is not used as a seed, so pre-cut images survive.
    /// </summary>
    /// <param name="tolerance">Per-channel tolerance (0-255); higher values treat a wider range as background.</param>
    public static SKBitmap RemoveBackground(SKBitmap src, int tolerance)
    {
        int width = src.Width;
        int height = src.Height;
        SKColor[] pixels = src.Pixels;
        bool[] isBackground = new bool[pixels.Length];

        Stack<int> pending = new();
        foreach ((int cx, int cy) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
        {
            int seed = (cy * width) + cx;
            if (pixels[seed].Alpha == 0 || isBackground[seed])
            {
                continue;
            }

            SKColor seedColor = pixels[seed];
            isBackground[seed] = true;
            pending.Push(seed);

            // Fill the region reachable from the seed within the colour tolerance
            while (pending.Count > 0)
            {
                int current = pending.Pop();
                int x = current % width;
                int y = current / width;

                foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    int neighbor = (ny * width) + nx;
                    if (isBackground[neighbor] || !IsWithinTolerance(pixels[neighbor], seedColor, tolerance))
                    {
                        continue;
                    }

                    isBackground[neighbor] = true;
                    pending.Push(neighbor);
                }
            }
        }

        SKColor[] result = new SKColor[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            result[i] = isBackground[i] ? SKColors.Transparent : pixels[i];
        }

        SKBitmap output = new(InfoTemplate.WithSize(width, height));
        output.Pixels = result;
        return output;
    }

    private static bool IsWithinTolerance(SKColor a, SKColor b, int tolerance) =>
        Math.Abs(a.Red - b.Red) <= tolerance &&
        Math.Abs(a.Green - b.Green) <= tolerance &&
        Math.Abs(a.Blue - b.Blue) <= tolerance &&
        Math.Abs(a.Alpha - b.Alpha) <= tolerance;

    /// <summary>Finds the bounding box of opaque pixels, returning an empty rectangle when all are transparent.</summary>
    public static SKRectI FindOpaqueBounds(SKBitmap src, byte alphaThreshold = 1)
    {
        SKColor[] pixels = src.Pixels;
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < src.Height; y++)
        {
            int rowOffset = y * src.Width;
            for (int x = 0; x < src.Width; x++)
            {
                if (pixels[rowOffset + x].Alpha < alphaThreshold)
                {
                    continue;
                }

                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }
        }

        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// Returns a copy with the head filled in, for the frames that show the character's back.
    ///
    /// One picture is placed into all thirty-nine frames, so the face drawn for the front ends
    /// up on the back of the head as well - eyes and all. The game's own character does not do
    /// that: measured from it, the back-facing frames carry no eyes at all, and the hair reaches
    /// two pixels further down than it does at the front, covering where the face would be.
    ///
    /// This reproduces that. The head is taken as the top 55% of the art, which is where those
    /// measurements put the hair on a back-facing frame, and every pixel there is replaced with
    /// the colour that dominates the top of the picture - the hair, or whatever is worn instead.
    /// Pixels touching transparency keep their own colour, so the silhouette and its outline
    /// survive and only the interior changes.
    ///
    /// A character with nothing on its head degrades quietly: the dominant colour up there is
    /// then the skin, and the result differs little from the original.
    /// </summary>
    public static SKBitmap HideFace(SKBitmap src)
    {
        ArgumentNullException.ThrowIfNull(src);

        SKColor[] pixels = src.Pixels;
        int width = src.Width;
        int height = src.Height;

        SKRectI bounds = FindOpaqueBounds(src, 1);
        if (bounds.IsEmpty)
        {
            return Clone(src);
        }

        // The top quarter is hair on any character drawn to the proportions the prompt asks for,
        // and is the least likely part of the head to be anything else.
        //
        // Only the interior counts. At this size the outline is a large share of the picture -
        // on a spiky-haired character the top rows are mostly outline - so tallying every pixel
        // picked the outline colour and filled the whole head with black, which is worse than
        // the face it was replacing. The pixels sampled are exactly the pixels replaced.
        int sampleRows = Math.Max(1, (int)Math.Round(bounds.Height * 0.25));
        Dictionary<uint, int> tally = [];

        for (int y = bounds.Top; y < bounds.Top + sampleRows; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                SKColor colour = pixels[(y * width) + x];
                if (colour.Alpha == 0 || TouchesTransparency(pixels, width, height, x, y))
                {
                    continue;
                }

                tally[(uint)colour] = tally.GetValueOrDefault((uint)colour) + 1;
            }
        }

        // Nothing but outline up there - a very small or very thin picture. Left alone rather
        // than guessed at.
        if (tally.Count == 0)
        {
            return Clone(src);
        }

        SKColor hair = (SKColor)tally.MaxBy(pair => pair.Value).Key;

        // 0.55 comes from the measured character: on a back-facing frame the hair box ends a
        // little past halfway down, and the eye band of a front-facing frame ends well above it.
        int headBottom = bounds.Top + (int)Math.Round(bounds.Height * 0.55);

        SKColor[] result = (SKColor[])pixels.Clone();

        for (int y = bounds.Top; y < headBottom && y < height; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                int index = (y * width) + x;
                if (pixels[index].Alpha == 0)
                {
                    continue;
                }

                // Anything touching transparency is the silhouette, outline included, and is
                // what gives the head its shape. Only the inside is painted over.
                if (TouchesTransparency(pixels, width, height, x, y))
                {
                    continue;
                }

                result[index] = hair;
            }
        }

        SKBitmap output = CreateEmpty(width, height);
        output.Pixels = result;
        return output;
    }

    /// <summary>Whether any of the four neighbours is transparent or off the edge.</summary>
    private static bool TouchesTransparency(SKColor[] pixels, int width, int height, int x, int y)
    {
        if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
        {
            return true;
        }

        return pixels[(y * width) + x - 1].Alpha == 0
               || pixels[(y * width) + x + 1].Alpha == 0
               || pixels[((y - 1) * width) + x].Alpha == 0
               || pixels[((y + 1) * width) + x].Alpha == 0;
    }

    /// <summary>
    /// Mirrors the image horizontally.
    ///
    /// Needed because the game stores only right-facing frames and mirrors them to face left.
    /// A picture that faces left therefore keeps facing left when the character walks right.
    /// Mirroring what goes into the side-facing frames makes the art follow the direction of
    /// travel instead.
    /// </summary>
    public static SKBitmap FlipHorizontal(SKBitmap src)
    {
        ArgumentNullException.ThrowIfNull(src);

        SKColor[] source = src.Pixels;
        SKColor[] flipped = new SKColor[source.Length];

        for (int y = 0; y < src.Height; y++)
        {
            int row = y * src.Width;
            for (int x = 0; x < src.Width; x++)
            {
                flipped[row + x] = source[row + src.Width - 1 - x];
            }
        }

        SKBitmap output = new(InfoTemplate.WithSize(src.Width, src.Height));
        output.Pixels = flipped;
        return output;
    }

    /// <summary>Crops to the given rectangle.</summary>
    public static SKBitmap Crop(SKBitmap src, SKRectI rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ToolException(
                "error.image.emptyTrim", [], "切り詰める範囲が無い。画像がすべて透明である可能性がある。");
        }

        // Without this, a rectangle reaching past the right edge would keep reading along the
        // pixel array and silently pull in the following row instead of failing.
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > src.Width || rect.Bottom > src.Height)
        {
            throw new ToolException(
                $"切り出し範囲が画像の外にはみ出している: " +
                $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) / 画像 {src.Width}x{src.Height}");
        }

        SKColor[] source = src.Pixels;
        SKColor[] cropped = new SKColor[rect.Width * rect.Height];
        for (int y = 0; y < rect.Height; y++)
        {
            int from = ((rect.Top + y) * src.Width) + rect.Left;
            Array.Copy(source, from, cropped, y * rect.Width, rect.Width);
        }

        SKBitmap output = new(InfoTemplate.WithSize(rect.Width, rect.Height));
        output.Pixels = cropped;
        return output;
    }

    /// <summary>
    /// Scales to the largest size that fits the given box while preserving the aspect ratio.
    /// Interpolation happens in premultiplied space to stop background colour bleeding into the edges.
    /// </summary>
    public static SKBitmap ResizeToFit(SKBitmap src, int boxWidth, int boxHeight, ResampleMode mode)
    {
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            throw new ToolException($"配置先の箱のサイズが不正: {boxWidth}x{boxHeight}");
        }

        double scale = Math.Min((double)boxWidth / src.Width, (double)boxHeight / src.Height);
        int targetWidth = Math.Max(1, (int)Math.Round(src.Width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(src.Height * scale));

        return ResizeExact(src, targetWidth, targetHeight, mode);
    }

    /// <summary>
    /// Scales to exactly the given size, ignoring the aspect ratio.
    ///
    /// Used to squash and stretch a frame by a pixel or two, which is what makes a walk cycle
    /// built from one picture read as movement rather than as the same image nudged up and down.
    /// </summary>
    public static SKBitmap ResizeExact(SKBitmap src, int width, int height, ResampleMode mode)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ToolException($"拡大縮小後のサイズが不正: {width}x{height}");
        }

        SKSamplingOptions sampling = mode == ResampleMode.Nearest
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);

        // Interpolating unpremultiplied bleeds colour from transparent areas, so convert to premultiplied first
        using SKBitmap premultiplied = ConvertAlpha(src, SKAlphaType.Premul);
        using SKBitmap resized =
            premultiplied.Resize(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), sampling)
            ?? throw new ToolException(
                "error.image.scaleFailed", [src.Width, src.Height, width, height],
                $"画像の拡大縮小に失敗した ({src.Width}x{src.Height} -> {width}x{height})");

        return ConvertAlpha(resized, SKAlphaType.Unpremul);
    }

    /// <summary>Converts between premultiplied and unpremultiplied alpha.</summary>
    private static SKBitmap ConvertAlpha(SKBitmap src, SKAlphaType alphaType)
    {
        SKBitmap output = new(new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, alphaType));
        if (!src.CopyTo(output))
        {
            output.Dispose();

            // Given a key like every other failure that can reach the window. Without one
            // Loc.Describe falls back to the plain text, so an English-language window showed
            // this sentence in Japanese. The size is worth carrying: the way this fails in
            // practice is a full-size allocation that could not be met.
            throw new ToolException(
                "error.image.alphaFailed", [src.Width, src.Height],
                $"ビットマップのアルファ形式変換に失敗した ({src.Width}x{src.Height})。");
        }

        return output;
    }

    /// <summary>
    /// Discards semi-transparency to sharpen the edges, giving a pixel-art silhouette.
    /// Anything below the threshold becomes fully transparent, anything above fully opaque.
    /// </summary>
    public static SKBitmap HardenAlpha(SKBitmap src, byte threshold)
    {
        SKColor[] pixels = src.Pixels;
        SKColor[] result = new SKColor[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            SKColor c = pixels[i];

            // A fully transparent pixel always stays transparent. Comparing against the
            // threshold alone would turn the whole image opaque when the threshold is 0,
            // because no alpha is below 0, filling the background with opaque black.
            result[i] = c.Alpha == 0 || c.Alpha < threshold
                ? SKColors.Transparent
                : new SKColor(c.Red, c.Green, c.Blue, 255);
        }

        SKBitmap output = new(InfoTemplate.WithSize(src.Width, src.Height));
        output.Pixels = result;
        return output;
    }

    /// <summary>
    /// Adds a one-pixel outline around the opaque area so the silhouette reads on dark backgrounds.
    /// The outline adds a pixel on every side, so the result is two pixels wider and taller.
    /// </summary>
    public static SKBitmap AddOutline(SKBitmap src, SKColor color)
    {
        int width = src.Width + 2;
        int height = src.Height + 2;
        SKColor[] source = src.Pixels;
        SKColor[] result = new SKColor[width * height];

        // Place the original art at the centre, offset by (1,1)
        for (int y = 0; y < src.Height; y++)
        {
            Array.Copy(source, y * src.Width, result, ((y + 1) * width) + 1, src.Width);
        }

        SKColor[] withOutline = (SKColor[])result.Clone();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                if (result[index].Alpha != 0)
                {
                    continue;
                }

                bool touchesOpaque = false;
                foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    if (result[(ny * width) + nx].Alpha != 0)
                    {
                        touchesOpaque = true;
                        break;
                    }
                }

                if (touchesOpaque)
                {
                    withOutline[index] = color;
                }
            }
        }

        SKBitmap output = new(InfoTemplate.WithSize(width, height));
        output.Pixels = withOutline;
        return output;
    }

    /// <summary>
    /// Reduces the colour count using median cut. Transparent pixels are left alone.
    /// Used to give the result the flat colour blocks typical of pixel art.
    /// </summary>
    public static SKBitmap Quantize(SKBitmap src, int colorCount)
    {
        if (colorCount < 2)
        {
            throw new ToolException($"減色後の色数は2以上にすること（指定値: {colorCount}）");
        }

        SKColor[] pixels = src.Pixels;
        List<int> opaqueIndices = [];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].Alpha != 0)
            {
                opaqueIndices.Add(i);
            }
        }

        if (opaqueIndices.Count == 0)
        {
            return Clone(src);
        }

        // Reducing to more colours than the picture has cannot improve it, and running the split
        // anyway actively damaged it: the cut fell inside a run of one colour and turned it into
        // several shades, so a flat eight-colour sprite came back with nine.
        HashSet<uint> distinct = [];
        foreach (int i in opaqueIndices)
        {
            distinct.Add((uint)pixels[i].WithAlpha(255));
            if (distinct.Count > colorCount)
            {
                break;
            }
        }

        if (distinct.Count <= colorCount)
        {
            return Clone(src);
        }

        // Start with every pixel in one box and repeatedly split along the widest colour axis
        List<List<int>> buckets = [opaqueIndices];
        while (buckets.Count < colorCount)
        {
            int targetBucket = -1;
            int widestRange = 0;
            int splitChannel = 0;

            for (int b = 0; b < buckets.Count; b++)
            {
                if (buckets[b].Count < 2)
                {
                    continue;
                }

                for (int channel = 0; channel < 3; channel++)
                {
                    int min = 255;
                    int max = 0;
                    foreach (int i in buckets[b])
                    {
                        int value = ChannelOf(pixels[i], channel);
                        if (value < min) { min = value; }
                        if (value > max) { max = value; }
                    }

                    int range = max - min;
                    if (range > widestRange)
                    {
                        widestRange = range;
                        targetBucket = b;
                        splitChannel = channel;
                    }
                }
            }

            if (targetBucket < 0)
            {
                break; // これ以上分割できない
            }

            List<int> source = buckets[targetBucket];
            source.Sort((x, y) => ChannelOf(pixels[x], splitChannel).CompareTo(ChannelOf(pixels[y], splitChannel)));

            // Moved off the median to the nearest change of value. Cutting at the median
            // position tore a run of one colour in half, and the two halves then averaged to two
            // different colours - so the more of the picture a flat colour covered, the more
            // likely it was to come back as several shades.
            int median = source.Count / 2;
            int at = ChannelOf(pixels[source[median]], splitChannel);

            int after = median;
            while (after < source.Count && ChannelOf(pixels[source[after]], splitChannel) == at)
            {
                after++;
            }

            int before = median;
            while (before > 0 && ChannelOf(pixels[source[before - 1]], splitChannel) == at)
            {
                before--;
            }

            // Whichever boundary leaves both sides non-empty and moves the cut least
            int half =
                before <= 0 ? after
                : after >= source.Count ? before
                : (median - before) <= (after - median) ? before : after;

            if (half <= 0 || half >= source.Count)
            {
                break; // このバケットは単色で、これ以上分割できない
            }

            buckets[targetBucket] = source[..half];
            buckets.Add(source[half..]);
        }

        SKColor[] result = (SKColor[])pixels.Clone();
        foreach (List<int> bucket in buckets)
        {
            if (bucket.Count == 0)
            {
                continue;
            }

            long r = 0;
            long g = 0;
            long b2 = 0;
            foreach (int i in bucket)
            {
                r += pixels[i].Red;
                g += pixels[i].Green;
                b2 += pixels[i].Blue;
            }

            SKColor average = new(
                (byte)(r / bucket.Count),
                (byte)(g / bucket.Count),
                (byte)(b2 / bucket.Count),
                255);

            foreach (int i in bucket)
            {
                result[i] = average.WithAlpha(pixels[i].Alpha);
            }
        }

        SKBitmap output = new(InfoTemplate.WithSize(src.Width, src.Height));
        output.Pixels = result;
        return output;
    }

    private static int ChannelOf(SKColor color, int channel) => channel switch
    {
        0 => color.Red,
        1 => color.Green,
        _ => color.Blue,
    };

    /// <summary>Creates a new bitmap with the same contents.</summary>
    public static SKBitmap Clone(SKBitmap src)
    {
        SKBitmap output = new(InfoTemplate.WithSize(src.Width, src.Height));
        output.Pixels = src.Pixels;
        return output;
    }
}

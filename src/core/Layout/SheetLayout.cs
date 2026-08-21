using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreKeeperSkinTool.Layout;

/// <summary>A width and height pair.</summary>
public sealed record SizeSpec(int Width, int Height);

/// <summary>A rectangle inside a cell, with the origin at the cell's top-left corner.</summary>
public sealed record BoxSpec(int X, int Y, int Width, int Height);

/// <summary>
/// The standing reference area. <paramref name="BaselineY"/> is one row below the feet (an exclusive bound) and
/// <paramref name="CenterX"/> is the horizontal centre of the cell. Together they anchor replacement art by default.
/// </summary>
public sealed record StandingBoxSpec(int X, int Y, int Width, int Height, int BaselineY, int CenterX);

/// <summary>
/// One frame within the sprite sheet.
/// <paramref name="YUnity"/> is Unity's sprite coordinate with the origin at the bottom left;
/// <paramref name="YTopLeft"/> is the top-left-origin Y used for image processing.
/// </summary>
public sealed record FrameRect(
    int Index,
    string Anim,
    string Dir,
    int Frame,
    int X,
    int YUnity,
    int YTopLeft,
    int W,
    int H);

/// <summary>Sprite sheet layout of Core Keeper's player body layers.</summary>
public sealed record SheetLayout(
    int SchemaVersion,
    string GameVersion,
    string SourceTexture,
    SizeSpec Texture,
    SizeSpec Cell,
    int PixelsPerUnit,
    int FrameCount,
    BoxSpec ContentBox,
    StandingBoxSpec StandingBox,
    IReadOnlyDictionary<string, int[]> Animations,
    IReadOnlyList<FrameRect> Frames)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Whether an image of these dimensions is a finished sheet rather than something to convert.
    ///
    /// A sheet this program saved is exactly the texture's size, and putting one back through the
    /// conversion trimmed the whole 234x156 picture down to sixteen pixels and stamped that into
    /// all thirty-nine cells - so saving and reopening one's own work returned something else
    /// entirely. No ordinary source picture is exactly this size by accident, and one that is
    /// would be better opened as a sheet anyway.
    ///
    /// Kept here rather than at the window that asks, so that the rule can be tested by calling
    /// it. Written out a second time inside a test, it went on passing after the rule changed.
    /// </summary>
    public bool IsFinishedSheet(int width, int height) =>
        Texture is not null && width == Texture.Width && height == Texture.Height;

    /// <summary>
    /// Whether anything is drawn outside the frames, which a sheet this program made never has.
    ///
    /// Thirty-nine frames sit on a nine-by-six grid, so fifteen cells and the space around them
    /// belong to no frame and stay empty. A picture of the sheet's size that does use them is
    /// almost certainly not a sheet at all - somebody's artwork that happens to be 234x156 - and
    /// opening it as one leaves them with thirty-nine unrelated fragments and no way back.
    ///
    /// Only a hint, deliberately: it decides whether to ask, never what to do.
    /// </summary>
    /// <param name="alphaAt">Alpha of the pixel at the given sheet coordinates.</param>
    public bool PaintedOutsideFrames(Func<int, int, byte> alphaAt)
    {
        ArgumentNullException.ThrowIfNull(alphaAt);

        // The map below is one byte per pixel, so a layout naming a huge texture would ask for
        // gigabytes here even though Validate refuses such a layout elsewhere. Anything that
        // large is not a sheet this program made, which is all this method is asked to detect.
        if (Texture is null
            || (long)Texture.Width * Texture.Height > Imaging.PixelOps.MaxSourcePixels)
        {
            return false;
        }

        bool[,] inFrame = new bool[Texture.Width, Texture.Height];
        foreach (FrameRect frame in Frames)
        {
            for (int y = frame.YTopLeft; y < frame.YTopLeft + frame.H && y < Texture.Height; y++)
            {
                for (int x = frame.X; x < frame.X + frame.W && x < Texture.Width; x++)
                {
                    inFrame[x, y] = true;
                }
            }
        }

        for (int y = 0; y < Texture.Height; y++)
        {
            for (int x = 0; x < Texture.Width; x++)
            {
                if (!inFrame[x, y] && alphaAt(x, y) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Loads the default layout definition embedded in the executable.</summary>
    public static SheetLayout LoadEmbedded()
    {
        const string resourceName = "player-body-layout.json";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new ToolException($"埋め込みレイアウト定義 '{resourceName}' が見つからない。ビルド構成が壊れている可能性がある。");
        return Parse(stream, $"(埋め込み) {resourceName}");
    }

    /// <summary>Loads an external layout.json, for when a game update changes the sheet structure.</summary>
    public static SheetLayout LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ToolException($"レイアウト定義が見つからない: {path}");
        }

        using FileStream stream = File.OpenRead(path);
        return Parse(stream, path);
    }

    private static SheetLayout Parse(Stream stream, string source)
    {
        SheetLayout layout;
        try
        {
            layout = JsonSerializer.Deserialize<SheetLayout>(stream, JsonOptions)
                ?? throw new ToolException($"レイアウト定義が空だった: {source}");
        }
        catch (JsonException ex)
        {
            throw new ToolException($"レイアウト定義の JSON を解釈できない: {source}{Environment.NewLine}  {ex.Message}", ex);
        }

        layout.Validate(source);
        return layout;
    }

    /// <summary>
    /// Validates the consistency of a loaded definition.
    /// Always run this: using a stale definition after a game update breaks the output silently.
    /// </summary>
    public void Validate(string source)
    {
        List<string> errors = [];

        if (Texture is null || Texture.Width <= 0 || Texture.Height <= 0)
        {
            errors.Add("texture の幅・高さが不正");
        }
        else if ((long)Texture.Width * Texture.Height > Imaging.PixelOps.MaxSourcePixels)
        {
            // Only the lower end was checked. A definition naming 60000x60000 passed validation
            // and then failed inside SkiaSharp with "Unable to allocate pixels for the bitmap" -
            // an unhandled exception, a stack trace, and no mention of the file that caused it.
            // The frame rectangles were widened to long for the same reason; the texture itself
            // was left behind.
            errors.Add(
                $"texture が大きすぎる: {Texture.Width}x{Texture.Height} " +
                $"(上限 {Imaging.PixelOps.MaxSourcePixels} 画素)");
        }

        if (Cell is null || Cell.Width <= 0 || Cell.Height <= 0)
        {
            errors.Add("cell の幅・高さが不正");
        }
        else if (Texture is not null
                 && (Cell.Width > Texture.Width || Cell.Height > Texture.Height))
        {
            errors.Add(
                $"cell が texture より大きい: {Cell.Width}x{Cell.Height} " +
                $"(texture {Texture.Width}x{Texture.Height})");
        }

        if (Frames is null || Frames.Count == 0)
        {
            errors.Add("frames が空");
        }
        else
        {
            if (Frames.Count != FrameCount)
            {
                errors.Add($"frameCount({FrameCount}) と frames の実数({Frames.Count})が一致しない");
            }

            HashSet<int> seenIndices = [];
            List<FrameRect> placed = [];
            foreach (FrameRect frame in Frames)
            {
                if (!seenIndices.Add(frame.Index))
                {
                    errors.Add($"index {frame.Index} が重複している");
                }

                if (frame.W <= 0 || frame.H <= 0)
                {
                    errors.Add($"index {frame.Index} の幅・高さが不正 ({frame.W}x{frame.H})");
                }
                else if (Cell is not null && (frame.W != Cell.Width || frame.H != Cell.Height))
                {
                    errors.Add(
                        $"index {frame.Index} の寸法 {frame.W}x{frame.H} が cell {Cell.Width}x{Cell.Height} と違う");
                }

                // Widened to long before adding. Near int.MaxValue the sum wrapped round to a
                // negative number, so a rectangle far outside the texture compared as inside,
                // passed validation, and reached the readers as an index past the end of the
                // pixel array - which surfaced as IndexOutOfRangeException and a stack trace.
                if (Texture is not null &&
                    (frame.X < 0 || frame.YTopLeft < 0 ||
                     (long)frame.X + frame.W > Texture.Width ||
                     (long)frame.YTopLeft + frame.H > Texture.Height))
                {
                    errors.Add($"index {frame.Index} の矩形がテクスチャの範囲外");
                }

                // Any shared pixel counts, not just a repeat of the same corner. Frames are
                // drawn one after another into a single texture with nothing but a per-cell
                // clip, so two frames that overlap at all mean the earlier one's art is
                // painted over without a word. Comparing only the top-left corner caught the
                // exact-duplicate case and let every partial overlap through.
                if (frame.W > 0 && frame.H > 0)
                {
                    FrameRect? clash = placed.FirstOrDefault(other => Overlaps(other, frame));
                    if (clash is not null)
                    {
                        errors.Add(
                            $"index {frame.Index} の矩形が index {clash.Index} と重なっている " +
                            $"({frame.X}, {frame.YTopLeft})");
                    }

                    placed.Add(frame);
                }
            }
        }

        // ContentBox is dereferenced by SkinOptions.WithDefaultsFrom to supply the default
        // placement size. System.Text.Json leaves a missing property null even on a
        // non-nullable property, so without this check a layout file that omits it fails
        // later with a NullReferenceException instead of a message naming the file.
        if (ContentBox is null)
        {
            errors.Add("contentBox が無い");
        }
        else if (ContentBox.Width <= 0 || ContentBox.Height <= 0)
        {
            errors.Add($"contentBox の幅・高さが不正 ({ContentBox.Width}x{ContentBox.Height})");
        }
        else if (Cell is not null &&
                 (ContentBox.X < 0 || ContentBox.Y < 0 ||
                  (long)ContentBox.X + ContentBox.Width > Cell.Width ||
                  (long)ContentBox.Y + ContentBox.Height > Cell.Height))
        {
            errors.Add("contentBox がセルの範囲を超えている");
        }

        if (StandingBox is null)
        {
            errors.Add("standingBox が無い");
        }
        else if (Cell is not null)
        {
            // CenterX is a column index, so it has to be inside the cell; BaselineY is the
            // bottom edge the art rests on, so it may sit exactly on the cell boundary.
            if (StandingBox.CenterX < 0 || StandingBox.CenterX >= Cell.Width
                || StandingBox.BaselineY < 0 || StandingBox.BaselineY > Cell.Height)
            {
                errors.Add("standingBox の基準位置がセルの範囲を超えている");
            }

            if (StandingBox.Width <= 0 || StandingBox.Height <= 0)
            {
                errors.Add($"standingBox の幅・高さが不正 ({StandingBox.Width}x{StandingBox.Height})");
            }
            else if (StandingBox.X < 0 || StandingBox.Y < 0
                     || (long)StandingBox.X + StandingBox.Width > Cell.Width
                     || (long)StandingBox.Y + StandingBox.Height > Cell.Height)
            {
                errors.Add("standingBox の矩形がセルの範囲を超えている");
            }
        }

        // Same reason as contentBox above: a missing property arrives as null even though the
        // property is not nullable, and SheetComposer.NeutralFor and the layout listing both
        // dereference this without asking. Skipping the block when it is null turned a layout
        // file with no animations into a NullReferenceException far from the file that caused it.
        if (Animations is null)
        {
            errors.Add("animations が無い");
        }
        else if (Animations.Count == 0)
        {
            errors.Add("animations が空");
        }

        // Every animation must reference frames that exist, otherwise composing the sheet
        // indexes past the end of the frame list.
        if (Animations is not null && Frames is not null)
        {
            HashSet<int> definedIndices = [.. Frames.Select(f => f.Index)];
            foreach (KeyValuePair<string, int[]> animation in Animations)
            {
                if (animation.Value is null || animation.Value.Length == 0)
                {
                    errors.Add($"animations.{animation.Key} が空");
                    continue;
                }

                foreach (int index in animation.Value.Where(i => !definedIndices.Contains(i)))
                {
                    errors.Add($"animations.{animation.Key} が存在しない index {index} を参照している");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ToolException(
                $"レイアウト定義が不正: {source}{Environment.NewLine}  - " + string.Join($"{Environment.NewLine}  - ", errors));
        }
    }

    /// <summary>Whether two frame rectangles share at least one pixel.</summary>
    private static bool Overlaps(FrameRect a, FrameRect b) =>
        a.X < b.X + b.W && b.X < a.X + a.W &&
        a.YTopLeft < b.YTopLeft + b.H && b.YTopLeft < a.YTopLeft + a.H;
}

using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Sheet;

/// <summary>Settings for converting a single image into a sprite sheet.</summary>
/// <param name="RemoveBackground">Whether to clear the background connected to the four corners.</param>
/// <param name="BackgroundTolerance">Colour tolerance for treating a pixel as background (0-255).</param>
/// <param name="Trim">Whether to crop automatically to the opaque bounding box.</param>
/// <param name="BoxWidth">Maximum width allowed inside a cell.</param>
/// <param name="BoxHeight">Maximum height allowed inside a cell.</param>
/// <param name="Resample">Interpolation used when scaling.</param>
/// <param name="AlphaThreshold">Cut-off at which semi-transparent pixels are discarded.</param>
/// <param name="Colors">Number of colours to reduce to; zero or below disables reduction.</param>
/// <param name="Outline">Outline colour; null means no outline.</param>
/// <param name="OffsetX">Horizontal nudge; positive moves right.</param>
/// <param name="OffsetY">Vertical nudge; positive moves down.</param>
/// <param name="Animation">How motion is applied per frame.</param>
/// <param name="MirrorSideFrames">
/// Mirror the art in the side-facing frames so an asymmetric picture faces the way the
/// character is walking. See <see cref="PlacementOptions.MirrorSideFrames"/>.
/// </param>
public sealed record SkinOptions(
    bool RemoveBackground = false,
    int BackgroundTolerance = 16,
    bool Trim = true,
    int BoxWidth = 0,
    int BoxHeight = 0,
    ResampleMode Resample = ResampleMode.Smooth,
    byte AlphaThreshold = 128,
    int Colors = 0,
    SKColor? Outline = null,
    int OffsetX = 0,
    int OffsetY = 0,
    AnimationStyle Animation = AnimationStyle.Lively,
    bool MirrorSideFrames = false,
    bool HideFaceOnBackFrames = true)
{
    /// <summary>Returns these settings with unset sizes filled in from the layout's safe drawing area.</summary>
    public SkinOptions WithDefaultsFrom(SheetLayout layout) => this with
    {
        BoxWidth = BoxWidth > 0 ? BoxWidth : layout.ContentBox.Width,
        BoxHeight = BoxHeight > 0 ? BoxHeight : layout.ContentBox.Height,
    };
}

/// <summary>Conversion result. Disposing <see cref="Sheet"/> is the caller's responsibility.</summary>
/// <param name="Sheet">The generated sprite sheet.</param>
/// <param name="SourceSize">Dimensions of the source image.</param>
/// <param name="TrimmedSize">Dimensions after margins were trimmed.</param>
/// <param name="SpriteSize">Dimensions of the art actually placed into each frame.</param>
/// <param name="ClippedPixels">Number of pixels discarded for falling outside the cell.</param>
public sealed record SkinBuildResult(
    SKBitmap Sheet,
    (int Width, int Height) SourceSize,
    (int Width, int Height) TrimmedSize,
    (int Width, int Height) SpriteSize,
    int ClippedPixels,
    (int Width, int Height) BoxSize = default)
{
    /// <summary>
    /// Whether the art ended up using much less of the frame than it could have.
    ///
    /// The art keeps its aspect ratio, so a source taller than it is wide runs out of height
    /// first and leaves columns of the frame empty. At this size that is real detail lost: a
    /// picture measured at 0.45 as wide as it is tall used nine of the sixteen columns
    /// available, and every one of those seven columns was information that could have been
    /// kept. There is nothing the conversion can do about it - the fix is a wider source.
    /// </summary>
    /// <remarks>
    /// Measured against the box's own proportions, not against its width alone. The art fits
    /// whichever side runs out first, so how much of the width it uses depends on the shape of
    /// the box as much as on the shape of the picture. Comparing widths only, a perfectly square
    /// source was told it was "tall and narrow" as soon as the height was reduced - and the box
    /// is a setting the help invites people to change.
    /// </remarks>
    public bool UsesLittleOfBox =>
        BoxSize.Width > 0 && BoxSize.Height > 0 && SpriteSize.Height > 0
        && (long)SpriteSize.Width * BoxSize.Height * 10
           <= (long)BoxSize.Width * SpriteSize.Height * 7;
}

/// <summary>
/// The sequence of steps that turns an image into a sprite sheet.
///
/// Both the CLI and the GUI call this, guaranteeing identical output for identical settings.
/// The stages live in <see cref="PixelOps"/>; this type only owns their order and conditions.
/// </summary>
public static class SkinPipeline
{
    /// <summary>
    /// Runs the conversion. The input bitmap is left unchanged.
    /// A fully transparent result would make the character invisible, so it throws instead.
    /// </summary>
    /// <summary>
    /// Largest source image accepted, in pixels.
    ///
    /// The same bound <see cref="PixelOps.Decode"/> applies before it allocates, which is where
    /// it actually protects anything: by the time a bitmap reaches here it has already been
    /// materialised. Kept as a second check because Build also accepts bitmaps that were never
    /// decoded from a file.
    /// </summary>
    private const long MaxSourcePixels = PixelOps.MaxSourcePixels;

    /// <summary>
    /// Refuses a layout that does not belong with the measurements this build carries.
    ///
    /// The measurements are always the embedded ones, while the layout can be supplied from a
    /// file (<c>--layout</c>) so the tool can be pointed at a newer game version. The motion is
    /// looked up by the layout's frame indices, so a layout that renumbers or extends the frames
    /// silently takes the squash, stretch and foot line of an unrelated pose - a frame that is
    /// now idle picking up the lift of a sitting one, with nothing to say so.
    /// </summary>
    private static void EnsureMeasurementsMatch(SheetLayout layout, PartsLayout parts)
    {
        if (string.IsNullOrWhiteSpace(parts.Version)
            || string.IsNullOrWhiteSpace(layout.GameVersion)
            || string.Equals(parts.Version, layout.GameVersion, StringComparison.Ordinal))
        {
            return;
        }

        throw new ToolException(
            "error.pipeline.versionMismatch", [layout.GameVersion, parts.Version],
            "レイアウトと実測データのゲーム版が違うため、動きを正しく付けられない。" + Environment.NewLine +
            $"  レイアウト  : {layout.GameVersion}" + Environment.NewLine +
            $"  実測データ  : {parts.Version}（このアプリに同梱）" + Environment.NewLine +
            "  --layout で新しいレイアウトを指定した場合は、" + Environment.NewLine +
            "  scripts/generate-parts-layout.ps1 で実測データも作り直すこと。");
    }

    public static SkinBuildResult Build(SKBitmap source, SheetLayout layout, SkinOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);

        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new ToolException(
                "error.pipeline.badSize", [source.Width, source.Height],
                $"画像の寸法が不正: {source.Width}x{source.Height}");
        }

        long pixelCount = (long)source.Width * source.Height;
        if (pixelCount > MaxSourcePixels)
        {
            throw new ToolException(
                "error.pipeline.tooLarge",
                [source.Width, source.Height, MaxSourcePixels / (1024 * 1024)],
                $"画像が大きすぎる: {source.Width}x{source.Height}" + Environment.NewLine +
                $"  {MaxSourcePixels / (1024 * 1024)} メガピクセルまで。あらかじめ縮小してから読み込むこと。");
        }

        SkinOptions resolved = options.WithDefaultsFrom(layout);

        // The outline grows one pixel outwards, so shrink the fit box by that much
        int fitWidth = resolved.Outline is null ? resolved.BoxWidth : resolved.BoxWidth - 2;
        int fitHeight = resolved.Outline is null ? resolved.BoxHeight : resolved.BoxHeight - 2;

        if (fitWidth <= 0 || fitHeight <= 0)
        {
            // Two keys rather than one with a conditional tail: a sentence assembled from
            // fragments cannot be worded naturally in every language, and the outline case has
            // its own advice to give.
            throw new ToolException(
                resolved.Outline is null
                    ? "error.pipeline.boxTooSmall"
                    : "error.pipeline.boxTooSmallOutline",
                [resolved.BoxWidth, resolved.BoxHeight],
                $"配置サイズが小さすぎる: {resolved.BoxWidth}x{resolved.BoxHeight}" +
                (resolved.Outline is null ? string.Empty : "（輪郭線ありでは縦横それぞれ3以上必要）"));
        }

        (int Width, int Height) sourceSize = (source.Width, source.Height);
        SKBitmap current = PixelOps.Clone(source);
        (int Width, int Height) trimmedSize;

        try
        {
            if (resolved.RemoveBackground)
            {
                current = Replace(current, b => PixelOps.RemoveBackground(b, resolved.BackgroundTolerance));
            }

            if (resolved.Trim)
            {
                // Trim by the same threshold that HardenAlpha will apply later. Trimming at
                // alpha>=1 would keep a soft anti-aliased fringe inside the bounding box, the
                // art would be scaled so that the fringe touches the box, and HardenAlpha would
                // then erase the fringe, leaving the character floating above the foot line.
                SKRectI bounds = PixelOps.FindOpaqueBounds(current, Math.Max((byte)1, resolved.AlphaThreshold));
                if (bounds.IsEmpty)
                {
                    throw new ToolException(
                        "error.pipeline.noOpaque", [],
                        "不透明なピクセルが1つも残っていない。" + Environment.NewLine +
                        "  背景とみなす色の許容差（--bg-tolerance）が大きすぎるか、半透明を切り捨てる境目（--alpha-threshold）が高すぎる可能性がある。");
                }

                current = Replace(current, b => PixelOps.Crop(b, bounds));
            }

            trimmedSize = (current.Width, current.Height);

            current = Replace(current, b => PixelOps.ResizeToFit(b, fitWidth, fitHeight, resolved.Resample));
            current = Replace(current, b => PixelOps.HardenAlpha(b, resolved.AlphaThreshold));

            if (resolved.Colors > 0)
            {
                current = Replace(current, b => PixelOps.Quantize(b, resolved.Colors));
            }

            if (resolved.Outline is not null)
            {
                current = Replace(current, b => PixelOps.AddOutline(b, resolved.Outline.Value));
            }

            // The motion is derived from the game's measured character, so those measurements
            // have to travel with the placement.
            PartsLayout parts = PartsLayout.LoadEmbedded();
            EnsureMeasurementsMatch(layout, parts);

            PlacementOptions placement = new(
                resolved.OffsetX, resolved.OffsetY, resolved.Animation, parts,
                resolved.MirrorSideFrames, resolved.HideFaceOnBackFrames);
            ComposeResult composed = SheetComposer.Compose(layout, current, placement);

            if (composed.Sheet.Pixels.All(p => p.Alpha == 0))
            {
                composed.Sheet.Dispose();

                // Blaming the threshold and the background removal is only right when the art
                // was already empty. When the placement pushed every pixel outside the cell the
                // art is perfectly good and the offsets are what did it - and the number that
                // says so was sitting in the result and being thrown away, so the user was sent
                // to adjust two settings that had nothing to do with it.
                if (composed.ClippedPixels > 0)
                {
                    throw new ToolException(
                        "error.pipeline.offsetOutside",
                        [resolved.OffsetX, resolved.OffsetY, resolved.BoxWidth, resolved.BoxHeight],
                        $"位置調整で絵がコマの外へ出た（水平 {resolved.OffsetX} / 垂直 {resolved.OffsetY}、" +
                        $"配置サイズ {resolved.BoxWidth}x{resolved.BoxHeight}）。" + Environment.NewLine +
                        "  位置調整を 0 に近づけるか、配置サイズを小さくすること。");
                }

                throw new ToolException(
                    "error.pipeline.allTransparent", [],
                    "生成結果が完全に透明になった。" + Environment.NewLine +
                    "  半透明を切り捨てる境目（--alpha-threshold）が高すぎるか、背景の透過（--remove-bg）で絵ごと消えた可能性がある。");
            }

            return new SkinBuildResult(
                composed.Sheet,
                sourceSize,
                trimmedSize,
                (current.Width, current.Height),
                composed.ClippedPixels,
                (resolved.BoxWidth, resolved.BoxHeight));
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>Applies one stage and disposes the previous bitmap.</summary>
    private static SKBitmap Replace(SKBitmap current, Func<SKBitmap, SKBitmap> operation)
    {
        SKBitmap next = operation(current);
        current.Dispose();
        return next;
    }
}

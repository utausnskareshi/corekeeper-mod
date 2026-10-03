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

/// <summary>
/// The pictures a sheet is built from.
///
/// Only the front is required. Supplying the other facings is what the game itself does - its
/// side view is drawn narrower than its front, and its back has no face - and a drawing made for
/// each reads better at this size than one picture turned to face three ways.
/// </summary>
public sealed record SkinSources(SKBitmap Front, SKBitmap? Right = null, SKBitmap? Up = null)
{
    /// <summary>Every picture that was supplied, front first.</summary>
    public IEnumerable<SKBitmap> All
    {
        get
        {
            yield return Front;

            if (Right is not null)
            {
                yield return Right;
            }

            if (Up is not null)
            {
                yield return Up;
            }
        }
    }
}

/// <summary>Conversion result. Disposing <see cref="Sheet"/> is the caller's responsibility.</summary>
/// <param name="Sheet">The generated sprite sheet.</param>
/// <param name="SourceSize">Dimensions of the source image.</param>
/// <param name="TrimmedSize">Dimensions after margins were trimmed.</param>
/// <param name="SpriteSize">Dimensions of the art placed into the front-facing frames.</param>
/// <param name="ClippedPixels">Number of pixels discarded for falling outside the cell.</param>
/// <param name="BoxSize">The box the front view was fitted into.</param>
/// <param name="RightSize">
/// Dimensions of the art placed into the right-facing frames, or null when no picture was
/// supplied for that facing and the front's art is used there.
///
/// Reported separately because it is not the front's. Only the front is fitted to
/// <paramref name="BoxSize"/>; the other facings are scaled to the height the front reached and
/// their width falls where the aspect ratio puts it, so a caller that quotes
/// <paramref name="SpriteSize"/> alongside the box is describing one facing and implying all
/// three. A square side view beside a 40x60 front lands at 19 wide against a box of 16.
/// </param>
/// <param name="UpSize">
/// Dimensions of the art placed into the back-facing frames, on the same terms as
/// <paramref name="RightSize"/>.
/// </param>
/// <param name="RightClippedSideways">
/// The most pixels a right-facing frame drawn from its own picture lost past the sides of its cell
/// (<see cref="ComposeResult.RightClippedSideways"/>). What tells a facing too wide for its frame
/// from a vertical offset that cuts every facing alike.
/// </param>
/// <param name="UpClippedSideways">The same for the back-facing picture.</param>
public sealed record SkinBuildResult(
    SKBitmap Sheet,
    (int Width, int Height) SourceSize,
    (int Width, int Height) TrimmedSize,
    (int Width, int Height) SpriteSize,
    int ClippedPixels,
    (int Width, int Height) BoxSize = default,
    (int Width, int Height)? RightSize = null,
    (int Width, int Height)? UpSize = null,
    int RightClippedSideways = 0,
    int UpClippedSideways = 0)
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

    /// <summary>
    /// Runs the conversion from a single picture, which is placed into every frame.
    /// </summary>
    public static SkinBuildResult Build(SKBitmap source, SheetLayout layout, SkinOptions options)
    {
        // Checked here as well as below, so that a null picture is still reported as the missing
        // argument it is rather than as a null front view inside a record nobody passed
        ArgumentNullException.ThrowIfNull(source);
        return Build(new SkinSources(source), layout, options);
    }

    public static SkinBuildResult Build(SkinSources sources, SheetLayout layout, SkinOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sources.Front);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);

        SKBitmap source = sources.Front;

        foreach (SKBitmap picture in sources.All)
        {
            if (picture.Width <= 0 || picture.Height <= 0)
            {
                throw new ToolException(
                    "error.pipeline.badSize", [picture.Width, picture.Height],
                    $"画像の寸法が不正: {picture.Width}x{picture.Height}");
            }

            if ((long)picture.Width * picture.Height > MaxSourcePixels)
            {
                throw new ToolException(
                    "error.pipeline.tooLarge",
                    [picture.Width, picture.Height, MaxSourcePixels / (1024 * 1024)],
                    $"画像が大きすぎる: {picture.Width}x{picture.Height}" + Environment.NewLine +
                    $"  {MaxSourcePixels / (1024 * 1024)} メガピクセルまで。あらかじめ縮小してから読み込むこと。");
            }
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

        // Prepared alongside the front view and disposed with it. Null unless the caller supplied
        // a picture for that facing.
        SKBitmap? right = null;
        SKBitmap? up = null;

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
                    // The likeliest cause first. Blaming the background tolerance alone sent the
                    // user to a setting that is not even used unless the background is removed,
                    // while an untouched template or a picture exported with its layer hidden -
                    // transparent from the start - was never mentioned.
                    throw new ToolException(
                        "error.pipeline.noOpaque", [],
                        "不透明なピクセルが1つも残っていない。" + Environment.NewLine +
                        "  元の画像がもともと完全に透明か（未編集のテンプレート、レイヤーを隠したまま書き出した絵など）、" + Environment.NewLine +
                        "  半透明を切り捨てる境目（--alpha-threshold）が高すぎるか、--remove-bg を付けたときは" +
                        "背景とみなす色の許容差（--bg-tolerance）が大きすぎる可能性がある。");
                }

                current = Replace(current, b => PixelOps.Crop(b, bounds));
            }

            trimmedSize = (current.Width, current.Height);

            current = Replace(current, b => PixelOps.ResizeToFit(b, fitWidth, fitHeight, resolved.Resample));
            current = Replace(current, b => PixelOps.HardenAlpha(b, resolved.AlphaThreshold));

            // The front checked as PrepareSide checks the other facings, but only when there are
            // other facings. Alone, a vanished front leaves the sheet empty outright and the
            // whole-sheet message below names the right cause. With a side or back view beside
            // it the sheet is not empty, and the per-facing check said the offsets, the box size
            // or a facing's proportions had put the art outside the cells - none of it true.
            if ((sources.Right is not null || sources.Up is not null)
                && Array.TrueForAll(current.Pixels, p => p.Alpha == 0))
            {
                throw new ToolException(
                    "error.pipeline.facingVanishedFront", [],
                    "正面の絵が縮めた段階で消えた。" + Environment.NewLine +
                    "  線が細すぎるか、半透明を切り捨てる境目（--alpha-threshold）が高い可能性がある。" +
                    Environment.NewLine +
                    "  このまま進めると、正面のコマと、絵を渡していない向きのコマが何も描かれていないシートになる。");
            }

            // How wide a facing is worth scaling to. The composer centres it on the standing box's
            // centre plus the offset and the frame's own motion, then discards everything outside
            // the 26-pixel cell, so a column more than half of this from the middle cannot appear
            // in any frame however the frames are arranged. The margin covers the motion, which
            // moves the art a few pixels within the cell, and leaves the cut well clear of the
            // columns that do land so that the resampling reads the same neighbours as before.
            //
            // The floor of 4096 is not needed for that, and is there to keep the cut away from
            // anything an ordinary run does. The sheet comes out identical either way - measured
            // over twelve combinations of facing width, offsets, outline, colour reduction and
            // animation - but two numbers the tool prints are counted after the cut: the width of
            // the placed facing, and the pixels the composer discarded. With the floor, a 2000-dot
            // side view still reports the figures it always did, and only art wide enough to be a
            // mistake - a 60000 by 1 PNG became a 1,440,000 by 24 bitmap and 17.6 seconds - is
            // touched at all.
            // 64 covers the motion with room to spare. The motion never moves the art sideways -
            // FrameMotion is built with a horizontal step of 0 - but it does resize it frame by
            // frame, and a frame narrower than the idle pose widens the window that survives in
            // proportion. Measured over the parts layout, the narrowest frame is 0.769 of its
            // idle pose, so the widest window any frame can see is 26 / 0.769 = 33.8 dots.
            //
            // Counted in long and clamped because --offset-x takes any int: Math.Abs throws on
            // int.MinValue, and doubling a large offset overflows.
            const int MotionAndFilterMargin = 64;
            const int LeaveOrdinaryRunsAlone = 4096;
            long reach = (long)layout.StandingBox.CenterX + Math.Abs((long)resolved.OffsetX)
                + layout.Cell.Width + MotionAndFilterMargin;
            int maxScaledWidth = (int)Math.Clamp(
                (2 * reach) + 1, LeaveOrdinaryRunsAlone, int.MaxValue);

            // The other facings are fitted to the height the front view ended up at, not to the
            // box. The game's own character is the same height from every side and two pixels
            // narrower from the side; scaling each picture to the box independently would make
            // the character change size as it turned.
            right = PrepareSide(
                sources.Right, resolved, current.Height, maxScaledWidth,
                "error.pipeline.facingVanishedRight", "error.pipeline.noOpaqueRight", "右向き");
            up = PrepareSide(
                sources.Up, resolved, current.Height, maxScaledWidth,
                "error.pipeline.facingVanishedBack", "error.pipeline.noOpaqueBack", "背面");

            if (resolved.Colors > 0)
            {
                // Reduced together, so that one palette serves every facing. Quantising each
                // picture on its own gave each of them its own palette, and the character then
                // changed colour as it turned - the very thing drawing three views is meant to
                // avoid.
                (current, right, up) = QuantizeTogether(current, right, up, resolved.Colors);
            }

            if (resolved.Outline is not null)
            {
                current = Replace(current, b => PixelOps.AddOutline(b, resolved.Outline.Value));
                right = ReplaceOptional(right, b => PixelOps.AddOutline(b, resolved.Outline.Value));
                up = ReplaceOptional(up, b => PixelOps.AddOutline(b, resolved.Outline.Value));
            }

            // The motion is derived from the game's measured character, so those measurements
            // have to travel with the placement.
            PartsLayout parts = PartsLayout.LoadEmbedded();
            EnsureMeasurementsMatch(layout, parts);

            PlacementOptions placement = new(
                resolved.OffsetX, resolved.OffsetY, resolved.Animation, parts,
                resolved.MirrorSideFrames, resolved.HideFaceOnBackFrames);
            ComposeResult composed = SheetComposer.Compose(
                layout, new DirectionalArt(current, right, up), placement);

            // Taken once and shared by both checks below: Pixels copies the whole sheet, and
            // asking for it twice copied 234x156 colours for nothing.
            SKColor[] sheetPixels = composed.Sheet.Pixels;

            if (sheetPixels.All(p => p.Alpha == 0))
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

            // A facing at a time, after the placement. The check above asks whether the sheet is
            // empty, and one surviving facing is enough to answer no: --offset-x 21 together with
            // a --side picture came out with all thirteen down frames and all thirteen up frames
            // blank, exit 0, and generate --install then put that into the game. The very same
            // file handed to cks validate has always been refused - "26 コマが完全に空。そのポーズ
            // でキャラクターが消える。" - so the measurement existed and generate never took it.
            //
            // Placed after the whole-sheet check rather than before it, so that a sheet which is
            // empty outright keeps the message naming the alpha threshold and the background
            // removal, where a facing's name would say nothing. Note that one picture does NOT
            // guarantee the facings empty together: the motion shifts and resizes frame by frame
            // and differs by facing, so at the boundary offsets - measured at --offset-x 19 and
            // -20, and --offset-y 22 - one facing keeps a pixel or two, the sheet-wide test
            // passes, and this check is the one that speaks. Those runs used to exit 0 with a
            // sheet cks validate calls broken, so stopping them is the point rather than a
            // side effect.
            int sheetWidth = composed.Sheet.Width;
            Dictionary<string, int> opaqueByFacing = layout.OpaqueByDirection(
                (x, y) => sheetPixels[(y * sheetWidth) + x].Alpha);

            if (FirstEmptyFacing(opaqueByFacing) is { } empty)
            {
                composed.Sheet.Dispose();

                // What the sentence may claim is only what is known here: these cells are empty,
                // and turning that way in game shows nothing. It must not say the other facings
                // are fine - two can empty at once and only the first is named - and it must not
                // blame the offsets, which are 0 in the case that a facing wider than its cell
                // leaves nothing in the middle. Both wordings were tried and both were false on a
                // measured run.
                //
                // ClippedPixels is not consulted either. It is one maximum over all frames with no
                // facing attached, so a merely wide front makes it positive; branching on it would
                // blame the offsets for a lost back view they had nothing to do with, the mistake
                // the comment on the check above records having just fixed.
                object?[] arguments = empty.NameIsArgument
                    ? [empty.Name, resolved.OffsetX, resolved.OffsetY, resolved.BoxWidth, resolved.BoxHeight]
                    : [resolved.OffsetX, resolved.OffsetY, resolved.BoxWidth, resolved.BoxHeight];

                throw new ToolException(
                    empty.Key,
                    arguments,
                    $"{empty.Name}のコマに絵が1つも入らなかった（水平 {resolved.OffsetX} / 垂直 {resolved.OffsetY}、" +
                    $"配置サイズ {resolved.BoxWidth}x{resolved.BoxHeight}）。" + Environment.NewLine +
                    $"  このまま進めると、{empty.Name}を向いたときにキャラクターが消える。" + Environment.NewLine +
                    "  位置調整・配置サイズ・向き別の絵の縦横比のいずれかで、絵がコマから外れている。");
            }

            return new SkinBuildResult(
                composed.Sheet,
                sourceSize,
                trimmedSize,
                (current.Width, current.Height),
                composed.ClippedPixels,
                (resolved.BoxWidth, resolved.BoxHeight),

                // Measured here rather than where PrepareSide hands the picture back, because
                // the outline is added after that and grows every facing by a pixel on each
                // side. Taken at the earlier point, the reported size would be two short of
                // what the sheet actually carries whenever an outline is asked for, and the
                // front's size beside it is measured here - so the two would not even be
                // comparable.
                right is null ? null : (right.Width, right.Height),
                up is null ? null : (up.Width, up.Height),
                composed.RightClippedSideways,
                composed.UpClippedSideways);
        }
        finally
        {
            current.Dispose();
            right?.Dispose();
            up?.Dispose();
        }
    }

    /// <summary>
    /// One facing whose frames came out with nothing in them, or null when all of them have art.
    ///
    /// The three facings the sheet has get a key each rather than one message with the name filled
    /// in, for the reason <see cref="PrepareSide"/> gives about its own pair of keys: the window
    /// resolves the template in whichever language the user chose, and a name handed over as an
    /// argument would stay Japanese inside an English sentence. They are looked at in the order
    /// the game draws them.
    ///
    /// Every other spelling the layout offers is looked at as well, and those do pass their name
    /// as an argument. Dir is free-form and --layout may hand over any spelling, so consulting
    /// only the three literals left the whole check inert: measured with the embedded layout's
    /// "down" and "up" capitalised, the very sheet this exists to refuse - 26 empty frames, as
    /// cks validate confirms - came back out of generate with exit 0 and no warning. A name that
    /// came from the user's own layout file is an identifier rather than a word to translate, so
    /// repeating it verbatim says the same thing in either language.
    ///
    /// Only the first is named. The run stops either way and one facing is enough to point at the
    /// settings that did it; naming every combination would take a key per subset.
    /// </summary>
    private static (string Key, string Name, bool NameIsArgument)? FirstEmptyFacing(
        Dictionary<string, int> opaque)
    {
        (string Dir, string Key, string Name)[] known =
        [
            ("down", "error.pipeline.facingEmptyFront", "正面"),
            ("right", "error.pipeline.facingEmptyRight", "右向き"),
            ("up", "error.pipeline.facingEmptyBack", "背面"),
        ];

        foreach ((string dir, string key, string name) in known)
        {
            // TryGetValue rather than an index: a layout that names no frames for a facing has
            // not lost one, and reporting a facing the sheet does not have would send the user
            // looking for art they were never asked for.
            if (opaque.TryGetValue(dir, out int count) && count == 0)
            {
                return (key, name, false);
            }
        }

        // Ordered by name, so that a layout with two unusual facings reports them in a settled
        // order rather than in whichever order it happened to list its frames.
        foreach (string dir in opaque.Keys
            .Where(candidate => !Array.Exists(known, entry => entry.Dir == candidate))
            .Order(StringComparer.Ordinal))
        {
            if (opaque[dir] == 0)
            {
                return ("error.pipeline.facingEmptyNamed", dir, true);
            }
        }

        return null;
    }

    /// <summary>
    /// Runs a facing other than the front through the same preparation, ending at the height the
    /// front view reached rather than at the box.
    /// </summary>
    /// <param name="messageKey">
    /// Names the message for the facing that disappeared. One key per facing rather than one
    /// template with the name filled in: the window resolves the template in whichever language
    /// the user chose, and a name passed as an argument would stay Japanese inside the English
    /// sentence.
    /// </param>
    /// <param name="noOpaqueKey">
    /// Names the message for this facing's picture having nothing opaque in it at all, one key per
    /// facing for the same reason. It used to share the front's, so an empty --side was reported
    /// as if the front were empty.
    /// </param>
    /// <param name="facing">The Japanese name, for the written message the command line prints.</param>
    /// <param name="maxScaledWidth">
    /// How far from its own centre this facing's art can be and still land inside a cell. Columns
    /// beyond it are cut before the scaling rather than after, because it is the scaling that
    /// would have had to hold them: see the comment at the resize.
    /// </param>
    private static SKBitmap? PrepareSide(
        SKBitmap? source, SkinOptions resolved, int frontHeight, int maxScaledWidth,
        string messageKey, string noOpaqueKey, string facing)
    {
        if (source is null)
        {
            return null;
        }

        SKBitmap current = PixelOps.Clone(source);

        try
        {
            if (resolved.RemoveBackground)
            {
                current = Replace(current, b => PixelOps.RemoveBackground(b, resolved.BackgroundTolerance));
            }

            if (resolved.Trim)
            {
                SKRectI bounds = PixelOps.FindOpaqueBounds(current, Math.Max((byte)1, resolved.AlphaThreshold));
                if (bounds.IsEmpty)
                {
                    throw new ToolException(
                        noOpaqueKey, [],
                        $"{facing}の絵に不透明なピクセルが1つも残っていない。" + Environment.NewLine +
                        "  元の画像がもともと完全に透明か（レイヤーを隠したまま書き出した絵など）、" + Environment.NewLine +
                        "  半透明を切り捨てる境目（--alpha-threshold）が高すぎるか、--remove-bg を付けたときは" +
                        "背景とみなす色の許容差（--bg-tolerance）が大きすぎる可能性がある。");
                }

                current = Replace(current, b => PixelOps.Crop(b, bounds));
            }

            // Scaled by height alone, so the character really is the height the front view ended
            // up at. Fitting into a box "fitWidth by frontHeight" keeps the aspect ratio and
            // takes whichever limit bites first, which is the width as soon as the drawing is
            // wider than the front - a hat brim, a tail, an arm held out. A square side view
            // beside a 40x60 front then came out 16 pixels tall against the front's 19, so the
            // character lost a sixth of its height every time it turned, which is the opposite
            // of what fitting to the front's height is for.
            //
            // The width the scaling would produce is capped, by cutting the source down first.
            // Nothing capped it before, and the width of the picture this makes is decided by the
            // source's own proportions rather than by any argument: a 368-byte PNG 60000 dots wide
            // and 1 tall, handed to --side, became a 1,440,000 by 24 bitmap and took 17.1 seconds
            // to produce a sheet, with 20000 dots taking 3.6. The reason --width and --height are
            // capped at 1 to 26 is written on RequirePositiveOrUnset and is the same reason -
            // "a mistyped 4000 took eighteen seconds, 8000 never finished, and 100000 ended in
            // SkiaSharp's own allocation failure" - and that cap never reached this path.
            //
            // The cut cannot change the sheet. The composer centres this art on
            // StandingBox.CenterX + OffsetX + the frame's own motion and discards every pixel
            // outside the cell, so a column further from the centre than half of it has nowhere to
            // land in any frame. The margin inside it also keeps the cut far from the columns
            // that do land, so the resampling that reads neighbouring columns reads the same ones.
            // Measured: sheets built with and without the cap are identical pixel for pixel.
            double scale = (double)frontHeight / current.Height;
            double columnsToKeep = Math.Ceiling(maxScaledWidth / scale);

            // Kept as a double until it is known to be smaller than a width, because a very tall
            // source makes the scale small enough for the division to leave the range of an int.
            if (Math.Round(current.Width * scale) > maxScaledWidth && columnsToKeep < current.Width)
            {
                int keep = Math.Max(1, (int)columnsToKeep);
                int left = (current.Width - keep) / 2;
                current = Replace(
                    current, b => PixelOps.Crop(b, new SKRectI(left, 0, left + keep, b.Height)));
            }

            current = Replace(current, b => PixelOps.ResizeToHeight(b, frontHeight, resolved.Resample));
            current = Replace(current, b => PixelOps.HardenAlpha(b, resolved.AlphaThreshold));

            // Checked after the shrink, which is where a picture can be lost without either of
            // the existing guards noticing. The one above looks at the picture as it arrived, and
            // the one over the finished sheet only fires when the whole sheet is empty - so art
            // made of fine lines came through as thirteen blank frames, reported as a success.
            // In game that is a character who vanishes whenever they face that way.
            if (Array.TrueForAll(current.Pixels, p => p.Alpha == 0))
            {
                throw new ToolException(
                    messageKey, [],
                    $"{facing}の絵が縮めた段階で消えた。" + Environment.NewLine +
                    "  線が細すぎるか、半透明を切り捨てる境目（--alpha-threshold）が高い可能性がある。" +
                    Environment.NewLine +
                    "  このまま進めると、その向きのコマだけ何も描かれていないシートになる。");
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reduces every facing to one shared palette.
    ///
    /// The pictures are laid side by side, reduced in one pass and cut apart again, because
    /// median cut works on whatever it is given: run three times it finds three palettes.
    /// With only the front view there is nothing to share, so it takes the original path and
    /// the result is unchanged from before.
    /// </summary>
    private static (SKBitmap Front, SKBitmap? Right, SKBitmap? Up) QuantizeTogether(
        SKBitmap front, SKBitmap? right, SKBitmap? up, int colours)
    {
        if (right is null && up is null)
        {
            return (Replace(front, b => PixelOps.Quantize(b, colours)), null, null);
        }

        List<SKBitmap> pictures = [front, .. new[] { right, up }.Where(b => b is not null).Select(b => b!)];

        int width = pictures.Sum(p => p.Width);
        int height = pictures.Max(p => p.Height);

        using SKBitmap stacked = PixelOps.CreateEmpty(width, height);
        SKColor[] canvas = stacked.Pixels;

        int offset = 0;
        List<int> offsets = [];

        foreach (SKBitmap picture in pictures)
        {
            offsets.Add(offset);
            SKColor[] pixels = picture.Pixels;

            for (int y = 0; y < picture.Height; y++)
            {
                for (int x = 0; x < picture.Width; x++)
                {
                    canvas[(y * width) + offset + x] = pixels[(y * picture.Width) + x];
                }
            }

            offset += picture.Width;
        }

        stacked.Pixels = canvas;

        using SKBitmap reduced = PixelOps.Quantize(stacked, colours);

        List<SKBitmap> results = [];
        for (int i = 0; i < pictures.Count; i++)
        {
            results.Add(PixelOps.Crop(
                reduced, new SKRectI(offsets[i], 0, offsets[i] + pictures[i].Width, pictures[i].Height)));
        }

        front.Dispose();
        right?.Dispose();
        up?.Dispose();

        int next = 1;
        SKBitmap? newRight = right is null ? null : results[next++];
        SKBitmap? newUp = up is null ? null : results[next];

        return (results[0], newRight, newUp);
    }

    /// <summary>Applies one stage and disposes the previous bitmap.</summary>
    private static SKBitmap Replace(SKBitmap current, Func<SKBitmap, SKBitmap> operation)
    {
        SKBitmap next = operation(current);
        current.Dispose();
        return next;
    }

    /// <summary>Applies one stage to a facing that may not have been supplied.</summary>
    private static SKBitmap? ReplaceOptional(SKBitmap? current, Func<SKBitmap, SKBitmap> operation) =>
        current is null ? null : Replace(current, operation);
}

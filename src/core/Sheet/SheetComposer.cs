using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Sheet;

/// <summary>How motion is applied per frame.</summary>
public enum AnimationStyle
{
    /// <summary>Place the same art in every frame. The character stands still but never looks broken.</summary>
    Static,

    /// <summary>Shift the art between frames. The picture is never distorted, only moved.</summary>
    Bob,

    /// <summary>
    /// Shift the art and squash or stretch it by a pixel.
    /// A walk built from one picture reads as walking rather than as the same image sliding
    /// up and down, at the cost of the outline shifting slightly between frames.
    /// </summary>
    Lively,
}

/// <summary>How the art is placed onto the sheet.</summary>
/// <param name="OffsetX">Horizontal nudge; positive moves right.</param>
/// <param name="OffsetY">Vertical nudge; positive moves down.</param>
/// <param name="Animation">How motion is applied.</param>
/// <param name="Parts">
/// Where the game's own character sits, frame by frame. The motion is taken from this rather
/// than from hand-written constants, so it matches the game instead of approximating it.
/// Null disables motion entirely.
/// </param>
/// <param name="MirrorSideFrames">
/// Mirror the art placed into the side-facing frames.
///
/// The game draws only right-facing frames and mirrors them to face left, so whatever goes into
/// those cells appears as drawn when walking right and mirrored when walking left. A picture
/// that faces left therefore keeps facing left while walking right. Turning this on stores the
/// mirrored copy instead, and the character then faces the way it is travelling in both
/// directions. It makes no difference to symmetrical art.
/// </param>
/// <param name="HideFaceOnBackFrames">
/// Fill in the head on the frames that show the character's back.
///
/// The same picture goes into all thirty-nine frames, so without this the face drawn for the
/// front appears on the back of the head too. The game's own character has no eyes at all on
/// those frames, and its hair reaches further down to cover where the face would be.
/// </param>
public sealed record PlacementOptions(
    int OffsetX, int OffsetY, AnimationStyle Animation, PartsLayout? Parts = null,
    bool MirrorSideFrames = false, bool HideFaceOnBackFrames = true);

/// <summary>
/// The pictures a sheet is built from, one per facing the game draws.
///
/// Only the front is required, and on its own it behaves exactly as a single picture always has:
/// placed into every frame, mirrored for the side and with its face blanked for the back. The
/// other two exist because the game's own character is drawn differently from each side - its
/// side view is two pixels narrower than its front - and a real drawing of that reads better at
/// this size than the front view turned sideways.
/// </summary>
/// <param name="Front">Facing the camera. Used for any facing that has no picture of its own.</param>
/// <param name="Right">Facing right. The game mirrors these frames for walking left.</param>
/// <param name="Up">Facing away. The game has no eye data for this direction.</param>
public sealed record DirectionalArt(SKBitmap Front, SKBitmap? Right = null, SKBitmap? Up = null)
{
    /// <summary>Whether more than the front picture was supplied.</summary>
    public bool HasDirections => Right is not null || Up is not null;
}

/// <summary>The composition result together with any problems detected along the way.</summary>
/// <param name="Sheet">The generated sprite sheet.</param>
/// <param name="ClippedPixels">Pixels discarded for falling outside the cell; anything above zero means the art is too large.</param>
/// <param name="RightClippedSideways">
/// The most pixels any frame drawn from the supplied right-facing picture lost past the left or
/// right edge of its cell; 0 when there is no such picture.
///
/// Kept apart from <paramref name="ClippedPixels"/>, which is one maximum over every frame with no
/// facing attached: a vertical offset cuts the top or bottom off every facing alike, and told from
/// that number alone it read as a facing being too wide. Only the sides say that.
/// </param>
/// <param name="UpClippedSideways">The same for the supplied back-facing picture.</param>
public sealed record ComposeResult(
    SKBitmap Sheet, int ClippedPixels, int RightClippedSideways = 0, int UpClippedSideways = 0);

/// <summary>Builds a sprite sheet from a single picture.</summary>
public static class SheetComposer
{
    /// <summary>
    /// How one frame is displaced and reshaped.
    ///
    /// Sizes are pixel deltas, not ratios: the art is around 13x19, so a percentage would round
    /// to nothing. The art is anchored at the foot line and the horizontal centre, so a negative
    /// height reads as the character compressing onto its feet and a positive one as stretching
    /// upwards.
    /// </summary>
    /// <param name="Dx">Horizontal shift; positive moves right.</param>
    /// <param name="Dy">Vertical shift; negative lifts the art.</param>
    /// <param name="Dw">Width change in pixels.</param>
    /// <param name="Dh">Height change in pixels.</param>
    private sealed record FrameMotion(int Dx, int Dy, int Dw, int Dh)
    {
        public static readonly FrameMotion None = new(0, 0, 0, 0);

        public bool ChangesShape => Dw != 0 || Dh != 0;
    }

    /// <summary>
    /// Works out how a frame differs from the neutral standing pose, by measuring the game's
    /// own character rather than by guessing.
    ///
    /// Every frame's body outline was measured from the game into <see cref="PartsLayout"/>.
    /// Comparing a frame against the idle frame of the same direction gives exactly how the
    /// game moves and reshapes the character there: how much shorter or wider it becomes, and
    /// whether its feet leave the floor. Applying those same differences to the user's art is
    /// what makes the result move the way the real character does.
    ///
    /// Sizes are scaled in proportion, so art that is not 13x18 is reshaped by the same
    /// fraction rather than the same number of pixels.
    /// </summary>
    private static FrameMotion MeasuredMotion(
        SheetLayout layout, PartsLayout parts, FrameRect frame, int spriteWidth, int spriteHeight)
    {
        PartPlacement? body = parts.Part("body");
        PartBox? current = body?.For(frame.Index);
        PartBox? neutral = NeutralFor(layout, body, frame.Dir);

        if (current is null || neutral is null || neutral.W <= 0 || neutral.H <= 0)
        {
            return FrameMotion.None;
        }

        // The feet stay planted through most of the animations, so the art is anchored at the
        // foot line and this is usually zero. Sitting is the exception: the game lifts the
        // character off the floor, by one pixel facing the camera and three facing away.
        int currentBottom = current.Y + current.H - 1;
        int neutralBottom = neutral.Y + neutral.H - 1;

        int width = (int)Math.Round((double)spriteWidth * current.W / neutral.W);
        int height = (int)Math.Round((double)spriteHeight * current.H / neutral.H);

        return new FrameMotion(
            0,
            currentBottom - neutralBottom,
            width - spriteWidth,
            height - spriteHeight);
    }

    /// <summary>The idle frame of a direction, which is the pose everything else is measured against.</summary>
    private static PartBox? NeutralFor(SheetLayout layout, PartPlacement? body, string direction)
    {
        if (body is null)
        {
            return null;
        }

        if (layout.Animations.TryGetValue($"idle_{direction}", out int[]? idle) && idle.Length > 0)
        {
            return body.For(idle[0]);
        }

        return null;
    }

    /// <summary>
    /// Builds the sheet by placing the same art into every frame.
    /// Art is anchored to the standing reference (foot line and horizontal centre); anything outside the cell is clipped.
    /// Drawing is always clipped to the cell rectangle so neighbouring frames stay clean.
    /// </summary>
    /// <summary>
    /// Composes from a single picture, which is placed into every frame.
    /// Kept as the way most callers arrive: one drawing of a character, seen from the front.
    /// </summary>
    public static ComposeResult Compose(SheetLayout layout, SKBitmap sprite, PlacementOptions options) =>
        Compose(layout, new DirectionalArt(sprite), options);

    public static ComposeResult Compose(SheetLayout layout, DirectionalArt art, PlacementOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);

        SKBitmap sheet = PixelOps.CreateEmpty(layout.Texture.Width, layout.Texture.Height);
        SKColor[] canvas = sheet.Pixels;
        int clipped = 0;
        int rightSideways = 0;
        int upSideways = 0;

        // Both of these stand in for art that was not supplied. A real side view already faces
        // the right way, and a real back view already has no face, so mirroring or blanking one
        // would undo the very thing it was drawn for.
        using SKBitmap? mirrored = options.MirrorSideFrames && art.Right is null
            ? PixelOps.FlipHorizontal(art.Front)
            : null;

        using SKBitmap? backFacing = options.HideFaceOnBackFrames && art.Up is null
            ? PixelOps.HideFace(art.Front)
            : null;

        // Reshaped copies are shared between the frames that need the same size: run alone would
        // otherwise rebuild the same three variants eighteen times. The key carries which source
        // the copy came from, because two directions can want the same size from different art.
        Dictionary<(int Source, int Width, int Height), SKBitmap> variants = [];

        try
        {
            foreach (FrameRect frame in layout.Frames)
            {
                int clippedInFrame = 0;
                int sidewaysInFrame = 0;

                // Measured against whichever picture this frame is drawn from: the side view is
                // its own size, and scaling the motion by the front view's would stretch it.
                (SKBitmap chosen, int sourceId) = SourceFor(frame.Dir, art, mirrored, backFacing);

                FrameMotion motion = MotionFor(
                    layout, options.Parts, frame, options.Animation, chosen.Width, chosen.Height);

                SKBitmap drawn = VariantFor(chosen, motion, variants, sourceId);
                SKColor[] source = drawn.Pixels;

                // Align the bottom centre of the art to the foot line (baselineY) and centre (centerX)
                int anchorX = frame.X + layout.StandingBox.CenterX + options.OffsetX + motion.Dx;
                int anchorY = frame.YTopLeft + layout.StandingBox.BaselineY + options.OffsetY + motion.Dy;
                int left = anchorX - (drawn.Width / 2);
                int top = anchorY - drawn.Height;

                for (int sy = 0; sy < drawn.Height; sy++)
                {
                    for (int sx = 0; sx < drawn.Width; sx++)
                    {
                        SKColor color = source[(sy * drawn.Width) + sx];
                        if (color.Alpha == 0)
                        {
                            continue;
                        }

                        int dx = left + sx;
                        int dy = top + sy;

                        // Never draw outside the cell; spilling over would corrupt the neighbour
                        if (dx < frame.X || dy < frame.YTopLeft ||
                            dx >= frame.X + frame.W || dy >= frame.YTopLeft + frame.H)
                        {
                            clippedInFrame++;
                            if (dx < frame.X || dx >= frame.X + frame.W)
                            {
                                sidewaysInFrame++;
                            }

                            continue;
                        }

                        canvas[(dy * layout.Texture.Width) + dx] = color;
                    }
                }

                // Report the worst single frame rather than the sum. The same sprite is drawn
                // into every frame, so adding them up multiplies one overflow by the frame count
                // and tells the user that 39 times more art was lost than actually was.
                clipped = Math.Max(clipped, clippedInFrame);

                // By the picture the frame was drawn from, not by its direction: only a supplied
                // picture has a width of its own that the caller may want to name
                if (sourceId == 1)
                {
                    rightSideways = Math.Max(rightSideways, sidewaysInFrame);
                }
                else if (sourceId == 3)
                {
                    upSideways = Math.Max(upSideways, sidewaysInFrame);
                }
            }
        }
        finally
        {
            foreach (SKBitmap variant in variants.Values)
            {
                variant.Dispose();
            }
        }

        sheet.Pixels = canvas;
        return new ComposeResult(sheet, clipped, rightSideways, upSideways);
    }

    /// <summary>
    /// Returns the art reshaped for one frame, reusing a copy already built for that size.
    ///
    /// The unmodified sprite is returned as-is when nothing changes shape, so a static sheet
    /// costs exactly what it did before and the original is never disposed by this method.
    /// </summary>
    /// <param name="mirrored">
    /// Whether this is the mirrored copy. The cache is keyed by size, so the copies must not
    /// share entries: a mirrored variant handed to an unmirrored frame would face the wrong way.
    /// </param>
    /// <param name="backFacing">
    /// Whether this is the copy with the head filled in. Same reason as above - handing it to a
    /// front-facing frame would put the back of the head on the character's face.
    /// </param>
    private static SKBitmap VariantFor(
        SKBitmap sprite,
        FrameMotion motion,
        Dictionary<(int Source, int Width, int Height), SKBitmap> cache,
        int sourceId)
    {
        if (!motion.ChangesShape)
        {
            return sprite;
        }

        int width = sprite.Width + motion.Dw;
        int height = sprite.Height + motion.Dh;

        // A sprite only a pixel or two tall cannot absorb a squash; leaving it alone is better
        // than reducing it to nothing.
        if (width < 1 || height < 1)
        {
            return sprite;
        }

        if (cache.TryGetValue((sourceId, width, height), out SKBitmap? existing))
        {
            return existing;
        }

        // Nearest neighbour: the change is a single pixel, and interpolating it would soften
        // edges that the alpha threshold was just used to sharpen.
        SKBitmap variant = PixelOps.ResizeExact(sprite, width, height, ResampleMode.Nearest);
        cache[(sourceId, width, height)] = variant;
        return variant;
    }

    /// <summary>
    /// Which picture a frame is drawn from, and a number identifying it for the variant cache.
    ///
    /// Art supplied for a direction always wins. What is left is the older behaviour: the front
    /// view mirrored for the side-facing frames, and the front view with its face blanked for
    /// the ones that show the character's back.
    /// </summary>
    private static (SKBitmap Sprite, int SourceId) SourceFor(
        string direction, DirectionalArt art, SKBitmap? mirrored, SKBitmap? backFacing) =>
        direction switch
        {
            "right" when art.Right is not null => (art.Right, 1),
            "right" when mirrored is not null => (mirrored, 2),
            "up" when art.Up is not null => (art.Up, 3),
            "up" when backFacing is not null => (backFacing, 4),
            _ => (art.Front, 0),
        };

    /// <summary>
    /// Returns the motion for a frame, taken from the game's own character.
    ///
    /// Only run and swing have more than one frame in the sheet; idle, aim, hold and sit are a
    /// single frame each, so there is nothing there to animate no matter what is applied. Those
    /// single frames can still differ from the standing pose - sitting is shorter and lifted -
    /// and that difference is reproduced too.
    /// </summary>
    private static FrameMotion MotionFor(
        SheetLayout layout, PartsLayout? parts, FrameRect frame, AnimationStyle style,
        int spriteWidth, int spriteHeight)
    {
        if (style == AnimationStyle.Static || parts is null)
        {
            return FrameMotion.None;
        }

        FrameMotion motion = MeasuredMotion(layout, parts, frame, spriteWidth, spriteHeight);

        // Bob moves the art without ever reshaping it, for sources whose outline must not change
        return style == AnimationStyle.Bob ? motion with { Dw = 0, Dh = 0 } : motion;
    }

    /// <summary>
    /// Creates an empty template with the correct dimensions and nothing drawn on it.
    /// It is a canvas for artists to draw on and contains none of the game's image data.
    /// </summary>
    public static SKBitmap CreateBlankTemplate(SheetLayout layout) =>
        PixelOps.CreateEmpty(layout.Texture.Width, layout.Texture.Height);

    /// <summary>
    /// Creates a guide image showing the frame grid and the standing reference.
    /// Kept as a separate file from the template, to be overlaid as a reference layer.
    /// </summary>
    public static SKBitmap CreateGuideOverlay(SheetLayout layout)
    {
        SKBitmap guide = PixelOps.CreateEmpty(layout.Texture.Width, layout.Texture.Height);
        SKColor[] canvas = guide.Pixels;
        int width = layout.Texture.Width;

        SKColor cellBorder = new(0, 200, 255, 90);      // コマの境界
        SKColor standingArea = new(255, 0, 200, 70);    // 立ち姿の推奨範囲
        SKColor baseline = new(255, 220, 0, 180);       // 足元の高さ
        SKColor centerLine = new(255, 220, 0, 90);      // 水平中心

        void Plot(int x, int y, SKColor color)
        {
            if (x < 0 || y < 0 || x >= width || y >= layout.Texture.Height)
            {
                return;
            }

            canvas[(y * width) + x] = color;
        }

        foreach (FrameRect frame in layout.Frames)
        {
            // Cell border
            for (int x = 0; x < frame.W; x++)
            {
                Plot(frame.X + x, frame.YTopLeft, cellBorder);
                Plot(frame.X + x, frame.YTopLeft + frame.H - 1, cellBorder);
            }

            for (int y = 0; y < frame.H; y++)
            {
                Plot(frame.X, frame.YTopLeft + y, cellBorder);
                Plot(frame.X + frame.W - 1, frame.YTopLeft + y, cellBorder);
            }

            // Recommended standing area
            StandingBoxSpec box = layout.StandingBox;
            for (int x = 0; x < box.Width; x++)
            {
                Plot(frame.X + box.X + x, frame.YTopLeft + box.Y, standingArea);
            }

            for (int y = 0; y < box.Height; y++)
            {
                Plot(frame.X + box.X, frame.YTopLeft + box.Y + y, standingArea);
                Plot(frame.X + box.X + box.Width - 1, frame.YTopLeft + box.Y + y, standingArea);
            }

            // Foot line and horizontal centre
            for (int x = 1; x < frame.W - 1; x++)
            {
                Plot(frame.X + x, frame.YTopLeft + box.BaselineY - 1, baseline);
            }

            for (int y = 1; y < frame.H - 1; y++)
            {
                Plot(frame.X + box.CenterX, frame.YTopLeft + y, centerLine);
            }
        }

        guide.Pixels = canvas;
        return guide;
    }
}

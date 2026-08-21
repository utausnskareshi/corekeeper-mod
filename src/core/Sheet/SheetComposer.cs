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

/// <summary>The composition result together with any problems detected along the way.</summary>
/// <param name="Sheet">The generated sprite sheet.</param>
/// <param name="ClippedPixels">Pixels discarded for falling outside the cell; anything above zero means the art is too large.</param>
public sealed record ComposeResult(SKBitmap Sheet, int ClippedPixels);

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
    /// Offset that separates the back-facing cache keys from the mirrored ones. Larger than any
    /// sprite that can reach here, since the art is placed inside a 26-pixel cell.
    /// </summary>
    private const int MaxSpriteWidth = 1 << 16;

    public static ComposeResult Compose(SheetLayout layout, SKBitmap sprite, PlacementOptions options)
    {
        SKBitmap sheet = PixelOps.CreateEmpty(layout.Texture.Width, layout.Texture.Height);
        SKColor[] canvas = sheet.Pixels;
        int clipped = 0;

        // Built once and shared by every side-facing frame rather than per frame
        using SKBitmap? mirrored = options.MirrorSideFrames ? PixelOps.FlipHorizontal(sprite) : null;

        // Likewise for the thirteen frames that show the character's back
        using SKBitmap? backFacing =
            options.HideFaceOnBackFrames ? PixelOps.HideFace(sprite) : null;

        // Reshaped copies are shared between the frames that need the same size: run alone would
        // otherwise rebuild the same three variants eighteen times.
        Dictionary<(int Width, int Height), SKBitmap> variants = [];

        try
        {
            foreach (FrameRect frame in layout.Frames)
            {
                int clippedInFrame = 0;
                FrameMotion motion = MotionFor(
                    layout, options.Parts, frame, options.Animation, sprite.Width, sprite.Height);

                // The side-facing frames are the only ones the game ever mirrors, so they are
                // the only ones worth mirroring here. The mirrored copy exists exactly when the
                // option is on, so testing it also settles that it is not null.
                bool mirror = mirrored is not null && frame.Dir == "right";
                bool back = backFacing is not null && frame.Dir == "up";

                SKBitmap chosen = mirror ? mirrored! : back ? backFacing! : sprite;
                SKBitmap art = VariantFor(chosen, motion, variants, mirror, back);
                SKColor[] source = art.Pixels;

                // Align the bottom centre of the art to the foot line (baselineY) and centre (centerX)
                int anchorX = frame.X + layout.StandingBox.CenterX + options.OffsetX + motion.Dx;
                int anchorY = frame.YTopLeft + layout.StandingBox.BaselineY + options.OffsetY + motion.Dy;
                int left = anchorX - (art.Width / 2);
                int top = anchorY - art.Height;

                for (int sy = 0; sy < art.Height; sy++)
                {
                    for (int sx = 0; sx < art.Width; sx++)
                    {
                        SKColor color = source[(sy * art.Width) + sx];
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
                            continue;
                        }

                        canvas[(dy * layout.Texture.Width) + dx] = color;
                    }
                }

                // Report the worst single frame rather than the sum. The same sprite is drawn
                // into every frame, so adding them up multiplies one overflow by the frame count
                // and tells the user that 39 times more art was lost than actually was.
                clipped = Math.Max(clipped, clippedInFrame);
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
        return new ComposeResult(sheet, clipped);
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
        Dictionary<(int, int), SKBitmap> cache,
        bool mirrored,
        bool backFacing)
    {
        if (!motion.ChangesShape)
        {
            return sprite;
        }

        int width = sprite.Width + motion.Dw;
        int height = sprite.Height + motion.Dh;

        // The three sets share one dictionary, so the key carries which one it belongs to.
        // A frame can never be both mirrored and back-facing: the game mirrors only the
        // side-facing frames, and the back-facing ones are a different direction entirely.
        int keyWidth = mirrored ? -width : backFacing ? -(width + MaxSpriteWidth) : width;

        // A sprite only a pixel or two tall cannot absorb a squash; leaving it alone is better
        // than reducing it to nothing.
        if (width < 1 || height < 1)
        {
            return sprite;
        }

        if (cache.TryGetValue((keyWidth, height), out SKBitmap? existing))
        {
            return existing;
        }

        // Nearest neighbour: the change is a single pixel, and interpolating it would soften
        // edges that the alpha threshold was just used to sharpen.
        SKBitmap variant = PixelOps.ResizeExact(sprite, width, height, ResampleMode.Nearest);
        cache[(keyWidth, height)] = variant;
        return variant;
    }

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

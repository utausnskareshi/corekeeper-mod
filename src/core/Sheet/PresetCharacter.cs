using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Sheet;

/// <summary>
/// Draws a preset character from its recipe.
///
/// Positions come from <see cref="PartsLayout"/>, measured from the game, so the head sits where
/// heads sit and the torso where torsos sit. Everything drawn into those positions is this
/// project's own: flat colours and simple shapes, no game artwork.
///
/// The art is about thirteen pixels across, so a character is separated from its neighbours
/// mostly by colour. Shape does what it can: the head is rounded or squared off, hair is short,
/// long or tied back, and a helmet can be a cap, a brim, a full helm, horns, a hood or a
/// pointed hat.
/// </summary>
public static class PresetCharacter
{
    /// <summary>Builds the full sheet for one preset.</summary>
    public static SKBitmap Build(SheetLayout layout, PartsLayout parts, PresetDefinition preset) =>
        Build(layout, parts, preset, sideViewEnabled: true);

    /// <summary>
    /// Builds the sheet from a picture rather than from a recipe.
    ///
    /// Goes through the whole of <see cref="SkinPipeline"/> rather than straight to the
    /// composer, with the settings left at their defaults - which are the ones <c>generate</c>
    /// uses. A preset and that same file handed to the command line therefore come out the
    /// same sheet, and the steps between matter: margins are trimmed at the alpha the sheet
    /// will end up with, so a soft fringe cannot leave the character floating above the foot
    /// line, and the half-transparent edge a generated picture arrives with is hardened, which
    /// is what <c>validate</c> asks of any sheet going into the game.
    /// </summary>
    private static SKBitmap Place(SheetLayout layout, string image)
    {
        using SKBitmap art = PresetArt.Load(image);

        SkinOptions options = new SkinOptions().WithDefaultsFrom(layout);

        return SkinPipeline.Build(art, layout, options).Sheet;
    }

    /// <summary>
    /// Builds the sheet with the side-view cues optionally suppressed.
    ///
    /// Suppressing them is only useful to a test: it is the only way to measure what the side
    /// view actually contributes. Comparing a right-facing frame against its own mirror image
    /// instead looks like a check but is not one, because the measured part boxes are not
    /// centred on the cell, so even a recipe that draws no side cues at all scores highly.
    /// </summary>
    internal static SKBitmap Build(
        SheetLayout layout, PartsLayout parts, PresetDefinition preset, bool sideViewEnabled)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(preset);

        if (!preset.IsDrawn)
        {
            // parts is not passed on: the pipeline loads the measurements itself, and checks
            // them against the layout while it is there.
            return Place(layout, preset.Image!);
        }

        SKBitmap sheet = PixelOps.CreateEmpty(layout.Texture.Width, layout.Texture.Height);
        SKColor[] canvas = sheet.Pixels;
        Palette palette = Palette.From(preset);

        foreach (FrameRect frame in layout.Frames)
        {
            PartBox? body = parts.Part("body")?.For(frame.Index);
            if (body is null)
            {
                continue;
            }

            PartBox head = parts.Part("hair")?.For(frame.Index) ?? Fallback(body, 0, 0, body.W, 8);
            PartBox torso = parts.Part("shirt")?.For(frame.Index) ?? Fallback(body, 0, 8, body.W, 6);
            PartBox legs = parts.Part("pants")?.For(frame.Index) ?? Fallback(body, 2, 13, body.W - 4, 5);
            PartBox? eyes = parts.Part("eyes")?.For(frame.Index);

            Painter painter = new(canvas, layout.Texture.Width, frame);

            // The game draws only right-facing side frames and mirrors them for left, and its
            // own art is almost entirely asymmetric in those frames: the hair sweeps back, the
            // near arm comes forward, the face turns. Without the same cues a character reads
            // as facing the camera no matter which way it walks.
            bool sideView = sideViewEnabled && frame.Dir == "right";
            Draw(painter, preset, palette, body, head, torso, legs, eyes, sideView);
        }

        sheet.Pixels = canvas;
        return sheet;
    }

    /// <summary>A box relative to the body, used when a part was not measured for this frame.</summary>
    private static PartBox Fallback(PartBox body, int dx, int dy, int w, int h) =>
        new() { X = body.X + dx, Y = body.Y + dy, W = Math.Max(1, w), H = Math.Max(1, h) };

    /// <param name="sideView">
    /// True for the right-facing frames. The game mirrors those for left, so every cue drawn
    /// here points right and turns into a left-pointing cue for free.
    /// </param>
    private static void Draw(
        Painter p, PresetDefinition preset, Palette palette,
        PartBox body, PartBox head, PartBox torso, PartBox legs, PartBox? eyes, bool sideView)
    {
        bool blob = preset.Has("blob");

        // Negative for a wide build, so the torso and arms spread a pixel past the measured body
        // box; Painter clips whatever falls outside the cell. "wide" used to share the normal
        // build's value, which made eleven of the thirty presets - and a third of every random
        // character - draw identically to a normal build while claiming otherwise.
        int inset = preset.Build switch { "slim" => 1, "wide" => -1, _ => 0 };

        // A creature with no separate limbs is one rounded mass rather than head plus torso
        if (blob)
        {
            // The build applies here too. Without it slime and frog ("wide") and ghost
            // ("normal") drew the same pixels whatever their build said, which is the same
            // dead-setting problem the limbed bodies had before "wide" was given its own inset.
            int blobX = body.X + inset;
            int blobW = Math.Max(1, body.W - (inset * 2));

            p.RoundedFill(blobX, body.Y, blobW, body.H, palette.Skin, palette.SkinDark, 2);

            if (sideView)
            {
                // A rounded mass is symmetric by construction, so without this a blob looked
                // exactly the same walking left and right. The far side falls into shadow and
                // the near side keeps a lit edge, which is the same cue the limbed bodies get.
                p.Fill(blobX + blobW - 2, body.Y + 2, 2, Math.Max(1, body.H - 4), palette.SkinDark);
                p.Fill(blobX + 1, body.Y + 2, 1, Math.Max(1, body.H - 4), palette.Skin);
            }
        }
        else
        {
            // Legs first, then torso over them, then the head on top: later parts win where
            // the measured boxes overlap, which is the order the body reads in.
            p.Fill(legs.X, legs.Y, legs.W, legs.H, palette.Trouser);
            p.Outline(legs.X, legs.Y, legs.W, legs.H, palette.TrouserDark);

            // Arms are the columns of the body the torso does not cover
            p.Fill(body.X + inset, torso.Y, body.W - (inset * 2), torso.H, palette.Skin);
            p.Fill(torso.X + 1 + inset, torso.Y, torso.W - 2 - (inset * 2), torso.H, palette.Cloth);
            p.Outline(torso.X + 1 + inset, torso.Y, torso.W - 2 - (inset * 2), torso.H, palette.ClothDark);

            p.RoundedFill(head.X, head.Y, head.W, head.H, palette.Skin, palette.SkinDark, 1);
        }

        if (preset.Has("armor"))
        {
            // Worn over the clothing, inset so the shirt still shows at the shoulders
            p.Fill(torso.X + 2, torso.Y + 1, torso.W - 4, torso.H - 2, palette.Armor);
            p.Outline(torso.X + 2, torso.Y + 1, torso.W - 4, torso.H - 2, palette.ArmorDark);
            p.Fill(legs.X + 1, legs.Y, legs.W - 2, Math.Max(1, legs.H - 2), palette.Armor);
        }

        if (sideView && !blob)
        {
            DrawSideBody(p, palette, body, torso, legs, inset);
        }

        // After the side-view body, not before it. The side-view cape and the near arm occupy
        // the same two columns on the trailing edge, so drawing the cape first left the arm
        // painted over two thirds of it and the character walked with a bare shoulder.
        if (preset.Has("cape"))
        {
            // Seen from the side a cape hangs behind the character rather than down both
            // flanks, so it becomes one band on the trailing edge
            if (sideView)
            {
                p.Fill(body.X, torso.Y, 2, torso.H + 3, palette.Accent);
                p.Fill(body.X, torso.Y, 1, torso.H + 3, palette.AccentDark);
            }
            else
            {
                p.Fill(body.X, torso.Y, 1, torso.H + 2, palette.Accent);
                p.Fill(body.X + body.W - 1, torso.Y, 1, torso.H + 2, palette.Accent);
            }
        }

        DrawHair(p, preset, palette, head, sideView);
        DrawHelm(p, preset, palette, head, sideView);
        DrawFeatures(p, preset, palette, head, body, legs, eyes, sideView);
        DrawEyes(p, preset, palette, head, eyes);
    }

    /// <summary>
    /// Turns the flat front-facing body into a side view. Measuring the game's own right-facing
    /// frames, what separates the two sides is depth: the limb on the leading edge is the far
    /// one and falls wholly into shadow, while the near limb on the trailing edge keeps the lit
    /// colour and stands a row taller. Without that the walk cycle reads as a character sliding
    /// sideways while still facing the camera.
    ///
    /// The game also stops the far foot a row short of the near one. That was tried and dropped;
    /// the reason is on the far leg below. This is not a width cue: the two arms are both two
    /// columns, and the legs differ only when the pants box measures an odd number across, which
    /// is 2 of the 13 right-facing frames.
    /// </summary>
    /// <param name="inset">
    /// The build's inset, as the front-facing pass applied it. This pass repaints the arms, so
    /// without it a slim character got a normal character's silhouette back in every side frame
    /// and the build setting did nothing there. Measured before it was passed in: slim and normal
    /// both painted 11 columns across the torso of frame 1.
    /// </param>
    private static void DrawSideBody(
        Painter p, Palette palette, PartBox body, PartBox torso, PartBox legs, int inset)
    {
        int split = legs.X + ((legs.W + 1) / 2);
        int farWidth = legs.X + legs.W - split;

        if (farWidth > 0 && legs.H > 1)
        {
            // Far leg: a flat shadow. The game also stops it a row short of the near one, but
            // its legs are separated by a gap of bare pixels; ours are one block, so cutting
            // the corner away read as damage to the sprite rather than as depth.
            p.Fill(split, legs.Y, farWidth, legs.H, palette.TrouserDark);
        }

        // Near leg keeps the lit colour it was filled with, plus a highlight down its length.
        // Derived from the same split rather than fixed at legs.X+1: for a two-column pants box
        // that column is the far leg, so the highlight repainted the shadow and the side view
        // lost its only depth cue, and for a one-column box it fell outside the box entirely.
        int highlight = Math.Min(legs.X + 1, split - 1);
        if (highlight >= legs.X && legs.H > 1)
        {
            p.Fill(highlight, legs.Y + 1, 1, legs.H - 1, palette.Trouser);
        }

        // Far arm and sleeve, tucked behind the chest on the leading edge. Both edges are taken
        // from the inset so they land on the silhouette the front-facing pass actually drew:
        // without it the shadow fell two columns short of a wide character's leading edge and
        // two columns past a slim one's, putting the slim outline back where a normal one is.
        p.Fill(body.X + body.W - inset - 2, torso.Y + 1, 2, torso.H - 1, palette.SkinDark);
        p.Fill(torso.X + torso.W - inset - 2, torso.Y + 2, 1, torso.H - 3, palette.ClothDark);

        // Near arm hangs on the trailing edge, lit and standing a row taller than the far one
        p.Fill(body.X + inset, torso.Y, 2, torso.H, palette.Skin);
        p.Fill(body.X + inset, torso.Y + 1, 1, torso.H - 1, palette.SkinDark);
    }

    private static void DrawHair(
        Painter p, PresetDefinition preset, Palette palette, PartBox head, bool sideView)
    {
        int band = Math.Max(1, head.H / 3);

        // Seen from the side the hair falls behind the head instead of framing both cheeks, and
        // the fringe stops short of the face so skin shows on the leading edge. The game's own
        // side frames carry hair all the way down the trailing side to the jaw, which is what
        // makes the head silhouette change as the character turns.
        if (sideView)
        {
            switch (preset.HairStyle)
            {
                case "short":
                    p.Fill(head.X, head.Y, head.W - 1, band, palette.Hair);
                    p.Fill(head.X, head.Y, 1, Math.Max(1, head.H - 2), palette.Hair);
                    break;

                case "long":
                    p.Fill(head.X, head.Y, head.W - 1, band, palette.Hair);
                    p.Fill(head.X - 1, head.Y + 1, 2, head.H, palette.Hair);
                    break;

                case "ponytail":
                    p.Fill(head.X, head.Y, head.W - 1, band, palette.Hair);
                    p.Fill(head.X - 1, head.Y + 1, 1, head.H - 1, palette.Hair);
                    p.Fill(head.X - 2, head.Y + 2, 1, head.H - 3, palette.Hair);
                    break;
            }

            return;
        }

        switch (preset.HairStyle)
        {
            case "short":
                p.Fill(head.X, head.Y, head.W, band, palette.Hair);
                break;

            case "long":
                p.Fill(head.X, head.Y, head.W, band, palette.Hair);
                p.Fill(head.X, head.Y, 1, head.H, palette.Hair);
                p.Fill(head.X + head.W - 1, head.Y, 1, head.H, palette.Hair);
                break;

            case "ponytail":
                p.Fill(head.X, head.Y, head.W, band, palette.Hair);
                p.Fill(head.X + head.W - 1, head.Y + 1, 1, head.H - 1, palette.Hair);
                break;
        }
    }

    private static void DrawHelm(
        Painter p, PresetDefinition preset, Palette palette, PartBox head, bool sideView)
    {
        int top = head.Y;
        int band = Math.Max(1, head.H / 3);

        // A brim, a peak and a horn all stick out forwards, so from the side they extend on
        // the leading edge only rather than symmetrically
        if (sideView)
        {
            switch (preset.HelmStyle)
            {
                case "cap":
                    p.Fill(head.X, top, head.W, band, palette.Helm);
                    p.Fill(head.X + head.W, top + band - 1, 1, 1, palette.HelmDark);
                    break;

                case "brim":
                    p.Fill(head.X, top, head.W, band, palette.Helm);
                    p.Fill(head.X, top + band, head.W + 2, 1, palette.HelmDark);
                    break;

                case "full":
                    p.Fill(head.X, top, head.W, head.H - 2, palette.Helm);
                    p.Outline(head.X, top, head.W, head.H - 2, palette.HelmDark);
                    p.Fill(head.X + head.W - 1, top + band, 2, 2, palette.HelmDark);
                    break;

                case "horned":
                    p.Fill(head.X, top, head.W, band + 1, palette.Helm);

                    // On the leading edge, like every other side-view helm cue here. Keeping
                    // the trailing horn instead pointed it backwards while walking right and
                    // forwards while walking left, which is the mirror of what was intended.
                    p.Fill(head.X + head.W, top - 1, 1, 2, palette.HelmDark);
                    break;

                case "hood":
                    p.Fill(head.X, top, head.W, head.H - 3, palette.Helm);
                    p.Fill(head.X - 1, top, 2, head.H, palette.Helm);
                    break;

                case "hat":
                    p.Fill(head.X, top + 1, head.W, band, palette.Helm);
                    p.Fill(head.X, top + 1 + band, head.W + 2, 1, palette.HelmDark);
                    p.Fill(head.X + (head.W / 2) - 1, top - 2, 3, 3, palette.Helm);
                    break;
            }

            return;
        }

        switch (preset.HelmStyle)
        {
            case "cap":
                p.Fill(head.X, top, head.W, band, palette.Helm);
                break;

            case "brim":
                p.Fill(head.X, top, head.W, band, palette.Helm);
                p.Fill(head.X - 1, top + band, head.W + 2, 1, palette.HelmDark);
                break;

            case "full":
                p.Fill(head.X, top, head.W, head.H - 2, palette.Helm);
                p.Outline(head.X, top, head.W, head.H - 2, palette.HelmDark);
                break;

            case "horned":
                p.Fill(head.X, top, head.W, band + 1, palette.Helm);
                p.Fill(head.X - 1, top - 1, 1, 2, palette.HelmDark);
                p.Fill(head.X + head.W, top - 1, 1, 2, palette.HelmDark);
                break;

            case "hood":
                p.Fill(head.X, top, head.W, head.H - 3, palette.Helm);
                p.Fill(head.X, top, 1, head.H, palette.Helm);
                p.Fill(head.X + head.W - 1, top, 1, head.H, palette.Helm);
                break;

            case "hat":
                p.Fill(head.X, top + 1, head.W, band, palette.Helm);
                p.Fill(head.X - 1, top + 1 + band, head.W + 2, 1, palette.HelmDark);
                p.Fill(head.X + (head.W / 2) - 1, top - 2, 3, 3, palette.Helm);
                break;
        }
    }

    /// <param name="eyes">
    /// Null when this frame faces away from the camera. Features that belong to the face are
    /// skipped in that case; ears, antennae and tails are drawn either way, because they read
    /// the same from behind.
    /// </param>
    private static void DrawFeatures(
        Painter p, PresetDefinition preset, Palette palette,
        PartBox head, PartBox body, PartBox legs, PartBox? eyes, bool sideView)
    {
        bool faceVisible = eyes is not null;

        if (preset.Has("ears"))
        {
            if (sideView)
            {
                // The far ear disappears behind the head; the near one is seen edge-on
                p.Fill(head.X + head.W - 3, head.Y - 2, 2, 3, palette.Hair);
                p.Fill(head.X + 1, head.Y - 1, 1, 2, palette.Hair);
            }
            else
            {
                p.Fill(head.X + 1, head.Y - 2, 2, 3, palette.Hair);
                p.Fill(head.X + head.W - 3, head.Y - 2, 2, 3, palette.Hair);
            }
        }

        if (preset.Has("antenna"))
        {
            // Kept inside the cell. The hair box sits at y=3 in almost every frame, so anchoring
            // the lit tip at head.Y-4 put it above the top edge, where Painter drops it without
            // a word: the tip was missing from 37 of the 39 frames and appeared only during the
            // swing, which read as a flicker.
            int tip = Math.Max(0, head.Y - 4);
            int stalk = tip + 1;

            p.Fill(head.X + (head.W / 2), stalk, 1, Math.Max(1, head.Y + 1 - stalk), palette.AccentDark);
            p.Fill(head.X + (head.W / 2), tip, 1, 1, palette.Accent);
        }

        if (preset.Has("tail"))
        {
            // A tail trails behind, which is the opposite edge from the way the character faces
            p.Fill(sideView ? body.X - 2 : body.X + body.W, legs.Y - 2, sideView ? 2 : 1, 3, palette.Hair);
        }

        if (preset.Has("wings"))
        {
            if (sideView)
            {
                p.Fill(body.X - 2, body.Y + (body.H / 2) - 1, 2, 5, palette.Accent);
            }
            else
            {
                p.Fill(body.X - 1, body.Y + (body.H / 2), 1, 4, palette.Accent);
                p.Fill(body.X + body.W, body.Y + (body.H / 2), 1, 4, palette.Accent);
            }
        }

        if (preset.Has("beak") && faceVisible)
        {
            // From the side a beak is in profile: it juts past the front of the face
            if (sideView)
            {
                p.Fill(head.X + head.W - 1, head.Y + ((head.H * 2) / 3), 3, 2, palette.Accent);
            }
            else
            {
                p.Fill(head.X + (head.W / 2) - 1, head.Y + ((head.H * 2) / 3), 2, 2, palette.Accent);
            }
        }

        if (preset.Has("spots"))
        {
            if (sideView)
            {
                // Only the near cheek is in view, so the far spot is not drawn
                p.Fill(head.X + head.W - 4, head.Y + 2, 2, 2, palette.Accent);
            }
            else
            {
                p.Fill(head.X + 2, head.Y + 1, 2, 2, palette.Accent);
                p.Fill(head.X + head.W - 4, head.Y + 2, 2, 1, palette.Accent);
            }
        }

        if (preset.Has("glow"))
        {
            // A single bright pixel: enough to read as a lamp, a rune or an eye light
            p.Fill(sideView ? head.X + head.W - 2 : head.X + (head.W / 2), head.Y + 1, 1, 1, palette.Accent);
        }
    }

    private static void DrawEyes(
        Painter p, PresetDefinition preset, Palette palette, PartBox head, PartBox? eyes)
    {
        // No measurement means the game draws no eyes in this frame, which is the case for
        // every frame facing away from the camera. Falling back to a default position here
        // put a face on the back of the character's head.
        if (eyes is null)
        {
            return;
        }

        // Both eyes keep their full width when the character turns: the game's own side frames
        // draw exactly the same eye pair as the front ones and rely on the measured box being
        // offset towards the leading edge to say which way the character is looking.
        if (preset.Has("eyesBig"))
        {
            p.Fill(eyes.X, eyes.Y - 1, 2, 3, palette.Eye);
            p.Fill(eyes.X + eyes.W - 2, eyes.Y - 1, 2, 3, palette.Eye);
            return;
        }

        p.Fill(eyes.X, eyes.Y, 2, 1, palette.Eye);
        p.Fill(eyes.X + eyes.W - 2, eyes.Y, 2, 1, palette.Eye);
    }

    /// <summary>The colours of one preset, with a darker shade of each for edges.</summary>
    private sealed record Palette(
        SKColor Skin, SKColor SkinDark,
        SKColor Hair,
        SKColor Cloth, SKColor ClothDark,
        SKColor Trouser, SKColor TrouserDark,
        SKColor Armor, SKColor ArmorDark,
        SKColor Helm, SKColor HelmDark,
        SKColor Accent, SKColor AccentDark,
        SKColor Eye)
    {
        public static Palette From(PresetDefinition preset)
        {
            SKColor skin = PresetDefinition.Parse(preset.Skin);
            SKColor cloth = PresetDefinition.Parse(preset.Cloth);
            SKColor trouser = PresetDefinition.Parse(preset.Trouser);
            SKColor armor = PresetDefinition.Parse(preset.Armor);
            SKColor helm = PresetDefinition.Parse(preset.Helm);
            SKColor accent = PresetDefinition.Parse(preset.Accent);

            return new Palette(
                skin, Shade(skin, 0.72),
                PresetDefinition.Parse(preset.Hair),
                cloth, Shade(cloth, 0.72),
                trouser, Shade(trouser, 0.72),
                armor, Shade(armor, 0.72),
                helm, Shade(helm, 0.70),
                accent, Shade(accent, 0.70),
                new SKColor(0x1A, 0x1A, 0x22));
        }

        /// <summary>Darkens a colour towards black, used for the one-pixel edges.</summary>
        private static SKColor Shade(SKColor c, double factor) => new(
            (byte)(c.Red * factor), (byte)(c.Green * factor), (byte)(c.Blue * factor));
    }

    /// <summary>
    /// Draws into one frame, clipping everything to that frame's rectangle so a shape drawn a
    /// pixel too wide can never appear inside the neighbouring frame.
    /// </summary>
    private readonly struct Painter(SKColor[] canvas, int sheetWidth, FrameRect frame)
    {
        public void Fill(int x, int y, int w, int h, SKColor colour)
        {
            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    Set(x + dx, y + dy, colour);
                }
            }
        }

        public void Outline(int x, int y, int w, int h, SKColor colour)
        {
            if (w <= 2 || h <= 2)
            {
                return;
            }

            for (int dx = 0; dx < w; dx++)
            {
                Set(x + dx, y, colour);
                Set(x + dx, y + h - 1, colour);
            }

            for (int dy = 0; dy < h; dy++)
            {
                Set(x, y + dy, colour);
                Set(x + w - 1, y + dy, colour);
            }
        }

        /// <summary>Fills a rectangle with its corners cut away, which reads as rounded at this size.</summary>
        public void RoundedFill(int x, int y, int w, int h, SKColor fill, SKColor edge, int corner)
        {
            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    int fromLeft = Math.Min(dx, w - 1 - dx);
                    int fromTop = Math.Min(dy, h - 1 - dy);
                    if (fromLeft + fromTop < corner)
                    {
                        continue;
                    }

                    bool onEdge = fromLeft == 0 || fromTop == 0 || fromLeft + fromTop == corner;
                    Set(x + dx, y + dy, onEdge ? edge : fill);
                }
            }
        }

        private void Set(int cellX, int cellY, SKColor colour)
        {
            if (cellX < 0 || cellY < 0 || cellX >= frame.W || cellY >= frame.H)
            {
                return;
            }

            canvas[((frame.YTopLeft + cellY) * sheetWidth) + frame.X + cellX] = colour;
        }
    }
}

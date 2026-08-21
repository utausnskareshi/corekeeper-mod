using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Sheet;

/// <summary>One region of the starter character, and the colour that identifies it.</summary>
/// <param name="Part">Name of the part in the measurements.</param>
/// <param name="Fill">Fill colour.</param>
/// <param name="Edge">Colour of the one-pixel border, which keeps neighbouring parts apart.</param>
/// <param name="OutlineOnly">
/// Draw only the border, leaving whatever is underneath visible. Used for the parts that sit on
/// top of others: filling them would be true to how the game stacks its layers, but it would
/// bury the clothing and hair completely, and the point of this sheet is to show where each part
/// goes.
/// </param>
public sealed record StarterPart(string Part, SKColor Fill, SKColor Edge, bool OutlineOnly = false);

/// <summary>
/// Builds the sheet shown before the user has opened anything of their own.
///
/// It is a working character rather than a blank canvas: every part sits exactly where the game
/// puts it, measured from the game itself, so anything painted over it lands in the right place
/// on screen. Each part has its own colour, which answers the question the frame grid alone
/// cannot - which pixels are the helmet, and which are the shirt.
///
/// No game artwork is involved. The rectangles come from <see cref="PartsLayout"/>, which stores
/// positions and sizes and nothing else.
/// </summary>
public static class StarterCharacter
{
    /// <summary>
    /// Drawn back to front, in the order the game layers them: trousers and shirt over the bare
    /// body, armour over those, then hair, then the helmet on top of the hair, and finally the
    /// eyes. A helmet covering the hair is what the game does too - it swaps in a cropped hair
    /// sprite whenever one is worn.
    /// </summary>
    private static readonly StarterPart[] Parts =
    [
        // Solid: the character itself
        new("body", new SKColor(0xE8, 0xC0, 0x9A), new SKColor(0xB2, 0x8A, 0x63)),
        new("hair", new SKColor(0x6B, 0x4A, 0x2F), new SKColor(0x45, 0x2C, 0x18)),
        new("pants", new SKColor(0x4E, 0x6B, 0xA8), new SKColor(0x2E, 0x43, 0x70)),
        new("shirt", new SKColor(0x63, 0xA9, 0x6B), new SKColor(0x3A, 0x6E, 0x41)),

        // Outlined: equipment, which covers the clothing underneath in game. Drawn as a frame so
        // both remain visible and it is obvious which area a helmet or a breastplate claims.
        new("pantsArmor", new SKColor(0xB8, 0x9A, 0xE8), new SKColor(0x7A, 0x5A, 0xC8), OutlineOnly: true),
        new("chestArmor", new SKColor(0xFF, 0xA5, 0x62), new SKColor(0xD8, 0x6E, 0x28), OutlineOnly: true),
        new("helm", new SKColor(0xFF, 0x6B, 0x6B), new SKColor(0xC8, 0x33, 0x33), OutlineOnly: true),

        new("eyes", new SKColor(0x1E, 0x1E, 0x28), new SKColor(0x1E, 0x1E, 0x28)),
    ];

    /// <summary>
    /// Rough area a held weapon or tool covers, as a fraction of the frame.
    ///
    /// Held items are drawn by a different mechanism from the character layers and are not part
    /// of the measurements, so this is an estimate rather than something read from the game. It
    /// is painted faintly and is meant to be erased once its point has been made.
    /// </summary>
    private static readonly SKColor HeldItemHint = new(0xFF, 0xD1, 0x70, 0x38);

    /// <summary>Builds the starter sheet at the layout's dimensions.</summary>
    public static SKBitmap Build(SheetLayout layout, PartsLayout parts)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(parts);

        SKBitmap sheet = PixelOps.CreateEmpty(layout.Texture.Width, layout.Texture.Height);
        SKColor[] canvas = sheet.Pixels;
        int width = layout.Texture.Width;

        foreach (FrameRect frame in layout.Frames)
        {
            foreach (StarterPart part in Parts)
            {
                PartBox? box = parts.Part(part.Part)?.For(frame.Index);
                if (box is null)
                {
                    continue;
                }

                DrawBox(canvas, width, frame, box, part);
            }

            // After the parts, which is what its "only where the frame is still empty" test is
            // written for. Drawn first, that test ran against a blank frame and was always true:
            // the hint covered the whole rectangle and was then buried. The image came out the
            // same either way only because every starter colour is fully opaque - measured, both
            // orders give sha256 A4D51E96C261893C and 1787 hint pixels - so the guard was dead
            // rather than wrong, and would have started showing through the moment any part was
            // given a translucent colour.
            DrawHeldItemHint(canvas, width, frame);
        }

        sheet.Pixels = canvas;
        return sheet;
    }

    /// <summary>
    /// Fills a rectangle, outlining it in a darker shade so two adjacent parts do not read as
    /// one block of colour. A rectangle only one or two pixels across is filled with the edge
    /// colour instead, because an outline would leave nothing inside it.
    /// </summary>
    private static void DrawBox(
        SKColor[] canvas, int width, FrameRect frame, PartBox box, StarterPart part)
    {
        bool tooSmallToOutline = box.W <= 2 || box.H <= 2;

        for (int y = 0; y < box.H; y++)
        {
            for (int x = 0; x < box.W; x++)
            {
                int cellX = box.X + x;
                int cellY = box.Y + y;

                // The measurements come from the same grid, but a layout edited by hand could
                // still put a part outside its cell, and that must never touch the neighbour.
                if (cellX < 0 || cellY < 0 || cellX >= frame.W || cellY >= frame.H)
                {
                    continue;
                }

                bool onEdge = x == 0 || y == 0 || x == box.W - 1 || y == box.H - 1;

                if (part.OutlineOnly && !onEdge && !tooSmallToOutline)
                {
                    continue;
                }

                canvas[((frame.YTopLeft + cellY) * width) + frame.X + cellX] =
                    tooSmallToOutline || onEdge ? part.Edge : part.Fill;
            }
        }
    }

    /// <summary>
    /// Marks where a held weapon or tool tends to sit, so that art drawn there is not a surprise
    /// when it disappears behind a pickaxe. Only the pixels still empty are tinted.
    /// </summary>
    private static void DrawHeldItemHint(SKColor[] canvas, int width, FrameRect frame)
    {
        // Held to one side of the body at roughly chest height. Kept narrow on purpose: a large
        // block reads as part of the character rather than as a note about it. Expressed as a
        // fraction of the frame so it follows the cell size rather than assuming 26x26.
        int left = (frame.W * 68) / 100;
        int right = (frame.W * 92) / 100;
        int top = (frame.H * 42) / 100;
        int bottom = (frame.H * 72) / 100;

        for (int y = top; y <= bottom && y < frame.H; y++)
        {
            for (int x = left; x <= right && x < frame.W; x++)
            {
                int index = ((frame.YTopLeft + y) * width) + frame.X + x;
                if (canvas[index].Alpha == 0)
                {
                    canvas[index] = HeldItemHint;
                }
            }
        }
    }
}

using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Export;

/// <summary>
/// Draws the logo mod.io asks for when a mod is registered: the character standing facing front,
/// right and away, enlarged on a plain background, 1280x720. A standing frame with nothing drawn in
/// it is left out, and with none drawn the first drawn frame of the sheet stands in, so the logo
/// always shows the character (Core Keeper's mod.io guidelines ask for images relevant to the mod).
///
/// Enlarged by a whole number with nearest-neighbour sampling, so every pixel of the art stays a
/// sharp square in this file and wherever it is shown at full size. The game's own mod browser shows
/// the 320x180 and 640x360 thumbnails mod.io makes from it (the lists and the details page), drawn
/// scaled again with Unity's default bilinear filtering, so there the edges between pixels blur a
/// little. Lining the scale and the position up with multiples of four was left out: it would shrink
/// the figures by up to a fifth (a figure the size of the game's own standing frames, 13x18, would go
/// from 23 times to 20), and the game's own scaling blurs them anyway.
///
/// No text is drawn: mod.io shows the name beside the logo, and drawing letters would mean shipping
/// a font.
/// </summary>
public static class ModIoLogo
{
    /// <summary>The idle frames shown, left to right.</summary>
    private static readonly string[] Poses = ["idle_down", "idle_right", "idle_up"];

    /// <summary>Space kept clear around the figures.</summary>
    private const int MarginX = 80;
    private const int MarginY = 90;

    /// <summary>Gap between two figures, in pixels of the art before enlarging.</summary>
    private const int GapInArtPixels = 4;

    private static readonly SKColor Top = new(0x2B, 0x3A, 0x5C);
    private static readonly SKColor Bottom = new(0x12, 0x19, 0x2B);
    private static readonly SKColor Shadow = new(0, 0, 0, 0x55);

    /// <summary>Draws the logo for a finished sheet.</summary>
    public static SKBitmap Draw(SKBitmap sheet, SheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(layout);

        // A standing frame left empty is left out rather than drawn as a shadow with nobody on it
        List<SKRectI> cells = [.. Poses
            .Select(pose => layout.Animations.TryGetValue(pose, out int[]? frames) && frames.Length > 0
                ? layout.Frames.FirstOrDefault(f => f.Index == frames[0])
                : null)
            .OfType<FrameRect>()
            .Select(Cell)
            .Where(c => ModIoExporter.AnyOpaque(sheet, c))];

        // With none of them drawn, the first frame that is (the export refuses a sheet with none),
        // so the logo still shows the character instead of an empty background
        if (cells.Count == 0)
        {
            cells.AddRange(layout.Frames
                .OrderBy(f => f.Index)
                .Select(Cell)
                .Where(c => ModIoExporter.AnyOpaque(sheet, c))
                .Take(1));
        }

        SKBitmap logo = new(new SKImageInfo(ModIoExporter.LogoWidth, ModIoExporter.LogoHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            using SKCanvas canvas = new(logo);
            using (SKPaint background = new())
            {
                // Held so the gradient is released here, like every other Skia object in this method
                using SKShader gradient = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, ModIoExporter.LogoHeight),
                    [Top, Bottom], null, SKShaderTileMode.Clamp);
                background.Shader = gradient;
                canvas.DrawRect(0, 0, ModIoExporter.LogoWidth, ModIoExporter.LogoHeight, background);
            }

            // The same box in every cell, so the three stand on one line at one size
            SKRectI? box = UnionOfDrawn(sheet, cells);
            if (cells.Count == 0 || box is not { } inner)
            {
                return logo;
            }

            int width = inner.Width;
            int height = inner.Height;
            int scale = Math.Max(1, Math.Min(
                (ModIoExporter.LogoWidth - (2 * MarginX)) / ((cells.Count * width) + ((cells.Count - 1) * GapInArtPixels)),
                (ModIoExporter.LogoHeight - (2 * MarginY)) / height));

            int figureWidth = width * scale;
            int figureHeight = height * scale;
            int gap = GapInArtPixels * scale;
            int left = (ModIoExporter.LogoWidth - ((cells.Count * figureWidth) + ((cells.Count - 1) * gap))) / 2;
            int top = (ModIoExporter.LogoHeight - figureHeight) / 2;

            using SKImage art = SKImage.FromBitmap(sheet);
            using SKPaint shadow = new() { Color = Shadow, IsAntialias = true };
            SKSamplingOptions nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

            for (int i = 0; i < cells.Count; i++)
            {
                SKRectI cell = cells[i];
                SKRect source = new(cell.Left + inner.Left, cell.Top + inner.Top, cell.Left + inner.Right, cell.Top + inner.Bottom);
                float x = left + (i * (figureWidth + gap));
                SKRect target = new(x, top, x + figureWidth, top + figureHeight);

                // A soft oval at the feet, so the figures stand on something
                float footY = top + figureHeight - (scale * 0.5f);
                canvas.DrawOval(new SKRect(x + (figureWidth * 0.18f), footY - (scale * 1.2f), x + (figureWidth * 0.82f), footY + (scale * 1.2f)), shadow);

                canvas.DrawImage(art, source, target, nearest);
            }

            canvas.Flush();
            return logo;
        }
        catch
        {
            logo.Dispose();
            throw;
        }
    }

    private static SKRectI Cell(FrameRect frame) =>
        new(frame.X, frame.YTopLeft, frame.X + frame.W, frame.YTopLeft + frame.H);

    /// <summary>
    /// The smallest box, in a cell's own coordinates, holding everything drawn in any of the cells,
    /// or null when nothing is drawn in them.
    /// </summary>
    private static SKRectI? UnionOfDrawn(SKBitmap sheet, IReadOnlyList<SKRectI> cells)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        foreach (SKRectI cell in cells)
        {
            for (int y = cell.Top; y < Math.Min(cell.Bottom, sheet.Height); y++)
            {
                for (int x = cell.Left; x < Math.Min(cell.Right, sheet.Width); x++)
                {
                    if (sheet.GetPixel(x, y).Alpha == 0)
                    {
                        continue;
                    }

                    left = Math.Min(left, x - cell.Left);
                    top = Math.Min(top, y - cell.Top);
                    right = Math.Max(right, x - cell.Left + 1);
                    bottom = Math.Max(bottom, y - cell.Top + 1);
                }
            }
        }

        return left == int.MaxValue ? null : new SKRectI(left, top, right, bottom);
    }
}

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.Editing;

/// <summary>
/// Bridges SkiaSharp pixel buffers to Avalonia's drawing surfaces.
///
/// Editing refreshes the view on every action, so encoding to PNG and back would be too slow.
/// Writing straight into <see cref="WriteableBitmap"/> memory also avoids reallocating.
/// </summary>
public static class BitmapBridge
{
    private static readonly Vector Dpi = new(96, 96);

    /// <summary>Creates a writable bitmap of the given size.</summary>
    public static WriteableBitmap Create(int width, int height) =>
        new(new PixelSize(width, height), Dpi, PixelFormat.Rgba8888, AlphaFormat.Unpremul);

    /// <summary>
    /// Builds a standalone bitmap from a pixel buffer, for images that are shown once and never
    /// written to again, such as the preset thumbnails.
    /// </summary>
    public static WriteableBitmap ToBitmap(IReadOnlyList<SKColor> pixels, int width, int height) =>
        Write(null, pixels, width, height);

    /// <summary>
    /// Writes a pixel buffer, reallocating the bitmap when the size no longer matches.
    ///
    /// Ownership of <paramref name="target"/> passes to this method: it is either written to and
    /// returned, or released and replaced. The caller must therefore always use the return value
    /// and never keep the bitmap it passed in.
    /// </summary>
    public static WriteableBitmap Write(WriteableBitmap? target, IReadOnlyList<SKColor> pixels, int width, int height)
    {
        bool reuse = target is not null
                     && target.PixelSize.Width == width
                     && target.PixelSize.Height == height;

        WriteableBitmap bitmap = reuse ? target! : Create(width, height);

        // Repacked, not copied as-is: SKColor stores its channels as 0xAARRGGBB, while the
        // bitmap is Rgba8888, which on a little-endian machine wants 0xAABBGGRR in memory.
        uint[] buffer = new uint[width * height];
        for (int i = 0; i < buffer.Length; i++)
        {
            SKColor color = pixels[i];
            buffer[i] = (uint)(color.Red | (color.Green << 8) | (color.Blue << 16) | (color.Alpha << 24));
        }

        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            CopyRows(buffer, frame, width, height);
        }

        if (!reuse)
        {
            // The old bitmap is unreachable once the caller takes the return value,
            // and holds native memory that the finalizer would only release much later.
            target?.Dispose();
        }

        return bitmap;
    }

    /// <summary>
    /// Copies row by row, honouring the framebuffer's stride.
    ///
    /// A single contiguous copy assumes RowBytes equals width * 4. That happens to hold on the
    /// usual desktop backends, but a padded stride would then shear the image diagonally,
    /// and the last row would run past the end of the buffer.
    /// </summary>
    private static void CopyRows(uint[] buffer, ILockedFramebuffer frame, int width, int height)
    {
        int[] source = (int[])(object)buffer;
        int rowBytes = width * sizeof(uint);

        if (frame.RowBytes == rowBytes)
        {
            Marshal.Copy(source, 0, frame.Address, buffer.Length);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            nint destination = frame.Address + (y * frame.RowBytes);
            Marshal.Copy(source, y * width, destination, width);
        }
    }

    /// <summary>Tones of the chequer standing in for transparency, matching the editor canvas.</summary>
    private static readonly uint CheckerDark = Pack(26, 26, 32);

    private static readonly uint CheckerLight = Pack(58, 58, 70);

    /// <summary>Side of one chequer square, in sheet pixels.</summary>
    private const int CheckerPixels = 4;

    private static uint Pack(byte r, byte g, byte b) =>
        (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));

    /// <summary>
    /// Writes a single frame, optionally mirrored, for the animation preview.
    /// </summary>
    /// <param name="checkerBackground">
    /// Fill the transparent pixels with a chequerboard. The preview sits on a panel of one flat
    /// colour, against which a dark character and an empty pixel look exactly the same; the
    /// chequer is what tells them apart.
    /// </param>
    public static WriteableBitmap WriteFrame(
        WriteableBitmap? target,
        IReadOnlyList<SKColor> sheet,
        int sheetWidth,
        Layout.FrameRect frame,
        bool mirrored,
        bool checkerBackground = false)
    {
        bool reuse = target is not null
                     && target.PixelSize.Width == frame.W
                     && target.PixelSize.Height == frame.H;

        WriteableBitmap bitmap = reuse ? target! : Create(frame.W, frame.H);

        uint[] buffer = new uint[frame.W * frame.H];
        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                int sourceX = mirrored ? frame.W - 1 - x : x;
                SKColor color = sheet[((frame.YTopLeft + y) * sheetWidth) + frame.X + sourceX];

                if (checkerBackground && color.Alpha == 0)
                {
                    bool alternate = ((x / CheckerPixels) + (y / CheckerPixels)) % 2 == 1;
                    buffer[(y * frame.W) + x] = alternate ? CheckerLight : CheckerDark;
                    continue;
                }

                buffer[(y * frame.W) + x] =
                    (uint)(color.Red | (color.Green << 8) | (color.Blue << 16) | (color.Alpha << 24));
            }
        }

        using (ILockedFramebuffer locked = bitmap.Lock())
        {
            CopyRows(buffer, locked, frame.W, frame.H);
        }

        if (!reuse)
        {
            target?.Dispose();
        }

        return bitmap;
    }
}

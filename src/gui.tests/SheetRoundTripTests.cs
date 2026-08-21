using CoreKeeperSkinTool.Editing;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// What the window saves has to be what it opens again.
///
/// Saving writes the finished 234x156 sheet; opening used to treat every file as a source
/// picture, so a saved sheet was trimmed to sixteen pixels and stamped into all thirty-nine
/// cells. Saving and reopening one's own work therefore returned something else entirely.
/// </summary>
public sealed class SheetRoundTripTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    [Fact]
    public void 保存したシートは同じ内容で開き直せる()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cks-roundtrip-{Guid.NewGuid():N}.png");

        try
        {
            // A sheet with something recognisable in it, saved the way the window saves
            using SKBitmap original = PixelOps.CreateEmpty(
                Layout.Texture.Width, Layout.Texture.Height);

            SKColor[] pixels = original.Pixels;
            for (int i = 0; i < pixels.Length; i += 7)
            {
                pixels[i] = new SKColor((byte)(i % 251), 0x40, 0x90);
            }

            original.Pixels = pixels;
            PixelOps.EncodePng(original, path);

            // Reopened. The dimensions are what tell the window this is a sheet, not a source.
            using SKBitmap reopened = PixelOps.Decode(path);

            Assert.Equal(Layout.Texture.Width, reopened.Width);
            Assert.Equal(Layout.Texture.Height, reopened.Height);

            // It goes straight into a document, with no conversion in between
            EditorDocument document = new(reopened, Layout);

            using SKBitmap result = document.ToBitmap();
            Assert.Equal(original.Pixels, result.Pixels);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void PaintedOutsideFrames_コマの中だけの絵なら偽を返す()
    {
        // What a sheet this program made looks like: fifteen of the fifty-four cells belong to
        // no frame and stay empty, along with the space around them.
        FrameRect frame = Layout.Frames[0];

        Assert.False(Layout.PaintedOutsideFrames(
            (x, y) => (byte)(x >= frame.X && x < frame.X + frame.W
                             && y >= frame.YTopLeft && y < frame.YTopLeft + frame.H ? 255 : 0)));
    }

    [Fact]
    public void PaintedOutsideFrames_使われていないセルに絵があれば真を返す()
    {
        // Somebody's own artwork that happens to be 234x156 covers the whole picture, unused
        // cells included. Opening that as a sheet cut it into thirty-nine unrelated fragments.
        Assert.True(Layout.PaintedOutsideFrames((_, _) => 255));
    }

    [Fact]
    public void PaintedOutsideFrames_1画素でもコマの外にあれば見つける()
    {
        (int X, int Y) outside = UnusedCellPoint();

        Assert.True(Layout.PaintedOutsideFrames(
            (x, y) => (byte)(x == outside.X && y == outside.Y ? 255 : 0)));
    }

    /// <summary>A point inside a cell that no frame claims.</summary>
    private static (int X, int Y) UnusedCellPoint()
    {
        HashSet<(int, int)> used = [.. Layout.Frames.Select(f => (f.X, f.YTopLeft))];

        for (int y = 0; y + Layout.Cell.Height <= Layout.Texture.Height; y += Layout.Cell.Height)
        {
            for (int x = 0; x + Layout.Cell.Width <= Layout.Texture.Width; x += Layout.Cell.Width)
            {
                if (!used.Contains((x, y)))
                {
                    return (x + 3, y + 3);
                }
            }
        }

        throw new InvalidOperationException("使われていないセルが1つも無い");
    }

    [Theory]
    [InlineData(234, 156, true)]     // 保存したシートそのもの
    [InlineData(234, 155, false)]    // 高さが1つ足りない
    [InlineData(233, 156, false)]    // 幅が1つ足りない
    [InlineData(234, 900, false)]    // 幅だけ一致する縦長の立ち絵
    [InlineData(900, 156, false)]    // 高さだけ一致する横長の絵
    [InlineData(468, 312, false)]    // ちょうど2倍
    [InlineData(1024, 1536, false)]  // ふつうの元素材
    public void 完成済みシートかどうかは寸法が両方一致するかで決まる(int width, int height, bool finished)
    {
        // Both sides have to match. An earlier version of this test wrote the rule out a second
        // time instead of calling it, so changing the rule to accept either side - which turns
        // every 234-wide picture into a sheet the editor then refuses - left the test green.
        Assert.Equal(finished, Layout.IsFinishedSheet(width, height));
    }

    [Fact]
    public void シートの寸法は234x156のままである()
    {
        // The size the rule turns on. Changing the layout without revisiting the rule should
        // show up here rather than as a picture silently converted.
        Assert.Equal(234, Layout.Texture.Width);
        Assert.Equal(156, Layout.Texture.Height);
    }
}

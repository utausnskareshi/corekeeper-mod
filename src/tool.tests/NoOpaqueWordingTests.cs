using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// What a conversion says when a picture has nothing opaque left in it.
///
/// Every case said the same thing - the background tolerance may be too high, or the alpha
/// cut-off - whichever picture was empty and whether or not the background was being removed at
/// all. A transparent --side or --back was reported as if it were the front, and the one likely
/// cause, a picture that was transparent to begin with, was never mentioned.
/// </summary>
public sealed class NoOpaqueWordingTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private static SKBitmap Solid(int width, int height, SKColor colour)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, colour);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    [Theory]
    [InlineData(true, "error.pipeline.noOpaqueRight", "右向き")]
    [InlineData(false, "error.pipeline.noOpaqueBack", "背面")]
    public void 透明な向き別の絵は向きを名指しする(bool side, string expectedKey, string facing)
    {
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap blank = PixelOps.CreateEmpty(40, 60);

        SkinSources sources = side ? new SkinSources(front, blank) : new SkinSources(front, null, blank);

        ToolException error = Assert.Throws<ToolException>(
            () => SkinPipeline.Build(sources, Layout, new SkinOptions()));

        Assert.Equal(expectedKey, error.MessageKey);
        Assert.Contains(facing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 正面が空なら向きの名前を出さず元画像が透明な可能性を言う()
    {
        using SKBitmap blank = PixelOps.CreateEmpty(40, 60);
        using SKBitmap side = Solid(40, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(
            () => SkinPipeline.Build(new SkinSources(blank, side), Layout, new SkinOptions()));

        Assert.Equal("error.pipeline.noOpaque", error.MessageKey);
        Assert.DoesNotContain("右向き", error.Message, StringComparison.Ordinal);
        Assert.Contains("完全に透明", error.Message, StringComparison.Ordinal);
    }
}

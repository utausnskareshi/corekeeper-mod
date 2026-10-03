using CoreKeeperSkinTool.Imaging;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Checks what `generate` says about the size the art was placed at.
///
/// The summary printed one pair of numbers and called the box beside it "上限". Both described
/// the front alone: the other facings are scaled to the height the front reached and nothing
/// caps their width, so a square side view beside a 40x60 front went into the sheet 19 wide
/// while the line read "配置サイズ : 13x19 (上限 16x19)". Nothing was clipped - 19 still fits
/// inside the 26-pixel cell - so the run finished quietly, having stated a limit it had just
/// broken and a size that was not in the sheet.
///
/// Driven through Program.Main rather than through the pipeline, because what was wrong was the
/// sentence, not the conversion.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class FacingSummaryTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), $"cks-facing-summary-{Guid.NewGuid():N}");

    public FacingSummaryTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the operating system clears the temp directory eventually
        }
    }

    private string Picture(string name, int width, int height, SKColor colour)
    {
        string path = Path.Combine(_work, name);

        using SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, colour);
        bitmap.Pixels = pixels;

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());

        return path;
    }

    /// <summary>Runs the tool and returns what it wrote, each stream on its own.</summary>
    private static (int Exit, string Out, string Error) Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;

        using StringWriter captured = new();
        using StringWriter capturedError = new();

        try
        {
            Console.SetOut(captured);
            Console.SetError(capturedError);
            return (Program.Main(args), captured.ToString(), capturedError.ToString());
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }

    /// <summary>The 配置サイズ line on its own, so a phrase cannot be matched elsewhere.</summary>
    private static string SizeLine(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("配置サイズ", StringComparison.Ordinal));

    [Fact]
    public void 向き別の絵を渡すと向きごとの実寸を出す()
    {
        // 40x60 front into the default 16x19 box is 13x19; the square side is scaled to that
        // height alone and comes out 19x19. Both numbers are in the sheet, and only one of them
        // used to be printed.
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string side = Picture("side.png", 64, 64, SKColors.Green);

        (int exit, string output, _) = Run(
            "generate", "-i", front, "--side", side,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static");

        Assert.Equal(0, exit);

        string line = SizeLine(output);

        Assert.Contains("正面 13x19", line, StringComparison.Ordinal);
        Assert.Contains("右向き 19x19", line, StringComparison.Ordinal);

        // The box caps the front and nothing else, so it may not be announced as a plain limit
        // while a facing stands outside it
        Assert.DoesNotContain("(上限", line, StringComparison.Ordinal);
        Assert.Contains("正面の上限 16x19", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 背面だけ渡したときは背面だけ名前が出る()
    {
        // Naming a facing that was never supplied would invent art that is not in the sheet:
        // the frames for a facing left out are filled from the front picture.
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string back = Picture("back.png", 64, 64, SKColors.Blue);

        (int exit, string output, _) = Run(
            "generate", "-i", front, "--back", back,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static");

        Assert.Equal(0, exit);

        string line = SizeLine(output);

        Assert.Contains("背面 19x19", line, StringComparison.Ordinal);
        Assert.DoesNotContain("右向き", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 正面だけなら従来どおりの1行のままにする()
    {
        // With no facing supplied the front's art fills every frame, so the single pair really
        // is the whole sheet and the box really is its limit. Naming the facings there would
        // change a line people have been reading for every run, to say nothing new.
        string front = Picture("front.png", 64, 64, SKColors.Red);

        (int exit, string output, string error) = Run(
            "generate", "-i", front,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static");

        Assert.Equal(0, exit);
        Assert.Equal("配置サイズ    : 16x16  (上限 16x19)", SizeLine(output));
        Assert.DoesNotContain("警告", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 向き別の絵が配置幅を超えたら警告する()
    {
        // The gap the clipping count cannot see. Between the box width and the cell width the
        // art is drawn outside what `cks layout` calls the safe drawing area, yet no pixel
        // leaves the cell, so the existing warning stays silent.
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string side = Picture("side.png", 64, 64, SKColors.Green);

        (int exit, _, string error) = Run(
            "generate", "-i", front, "--side", side,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static", "--quiet");

        Assert.Equal(0, exit);

        // On stderr and not silenced by --quiet, like the other warnings: --quiet is for
        // scripted runs, which is exactly where nobody is watching the sheet
        Assert.Contains("配置幅 16 を超えている", error, StringComparison.Ordinal);
        Assert.Contains("右向き 19", error, StringComparison.Ordinal);

        // Nothing was clipped, which is why this case went unreported
        Assert.DoesNotContain("切り捨てた", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 配置幅に収まる向き別の絵では警告しない()
    {
        // A side view narrower than the front is what the game itself has - its side is two
        // pixels narrower - so warning about it would fire on the ordinary case.
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string side = Picture("side.png", 20, 60, SKColors.Green);

        (int exit, _, string error) = Run(
            "generate", "-i", front, "--side", side,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("警告", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 両方の向きが超えていても説明は一度だけ出す()
    {
        // Warned per facing, handing the same wide drawing to both printed the same two
        // sentences twice under two nearly identical headings.
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string wide = Picture("wide.png", 64, 64, SKColors.Green);

        (int exit, _, string error) = Run(
            "generate", "-i", front, "--side", wide, "--back", wide,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static", "--quiet");

        Assert.Equal(0, exit);
        Assert.Contains("右向き 19 / 背面 19", error, StringComparison.Ordinal);
        Assert.Equal(1, error.Split("警告:", StringSplitOptions.None).Length - 1);
    }
}

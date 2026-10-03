using CoreKeeperSkinTool.Imaging;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// The advice given when art is clipped at the frame edges, for a wide side or back picture.
///
/// It said "--width / --height を小さくするか" whatever the cause, right after the warning that the
/// facings are not limited by the width - and with a wide side picture, a smaller --width changed
/// nothing. The old advice stays, because the clipping may still come from --offset-y or the
/// front; a line is added for the case it cannot help with.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class FacingClipAdviceTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), $"cks-facing-clip-{Guid.NewGuid():N}");

    public FacingClipAdviceTests() => Directory.CreateDirectory(_work);

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

        PixelOps.EncodePng(bitmap, path);
        return path;
    }

    private static (int Exit, string Error) Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;

        using StringWriter captured = new();
        using StringWriter capturedError = new();

        try
        {
            Console.SetOut(captured);
            Console.SetError(capturedError);
            return (Program.Main(args), capturedError.ToString());
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }

    /// <summary>The clipping warning onwards, kept apart from the width warning printed before it.</summary>
    private static string ClipWarning(string error)
    {
        int start = error.IndexOf("警告: コマからはみ出した", StringComparison.Ordinal);
        Assert.True(start >= 0, "はみ出しの警告が出ていない: " + error);
        return error[start..];
    }

    [Fact]
    public void 向き別の絵の幅ではみ出したときは_widthでは縮まないと言う()
    {
        // The 40x60 front becomes 13x19; the 120x40 side, scaled to the same height, is 57 wide
        // and runs out of the 26-pixel cell
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string wide = Picture("wide.png", 120, 40, SKColors.Green);

        (int exit, string error) = Run(
            "generate", "-i", front, "--side", wide, "--back", wide,
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static", "--quiet");

        Assert.Equal(0, exit);

        string clip = ClipWarning(error);
        Assert.Contains("--width では制限されない", clip, StringComparison.Ordinal);
        Assert.Contains("--height", clip, StringComparison.Ordinal);
    }

    [Fact]
    public void 正面だけのはみ出しでは助言を変えない()
    {
        string front = Picture("front.png", 40, 60, SKColors.Red);

        (int exit, string error) = Run(
            "generate", "-i", front, "--offset-y", "8",
            "-o", Path.Combine(_work, "sheet.png"), "--anim", "static", "--quiet");

        Assert.Equal(0, exit);

        string clip = ClipWarning(error);
        Assert.Contains("--width / --height を小さくするか、--offset-y で位置を調整すること。", clip, StringComparison.Ordinal);
        Assert.DoesNotContain("向き別", clip, StringComparison.Ordinal);
    }
}

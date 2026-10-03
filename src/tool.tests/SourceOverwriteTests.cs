using CoreKeeperSkinTool.Imaging;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Checks that converting never writes the sheet over one of the pictures it was made from.
///
/// The front picture has been guarded since before the facings existed, with a message that says
/// why: "元のファイルが失われるため、別の名前を指定すること。" When --side and --back were added
/// the guard was not extended to them, so `--side art.png -o art.png` replaced a 400x400 drawing
/// with the 234x156 sheet, said nothing, and exited 0.
///
/// Driven through Program.Main rather than a helper, because the guard is only worth anything
/// where it actually sits: between parsing the command line and writing the file.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SourceOverwriteTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), $"cks-overwrite-{Guid.NewGuid():N}");

    public SourceOverwriteTests() => Directory.CreateDirectory(_work);

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

    /// <summary>Runs the tool with the console swallowed, and returns its exit code.</summary>
    private static int Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;

        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return Program.Main(args);
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }

    [Theory]
    [InlineData("--side")]
    [InlineData("--back")]
    public void 出力が向き別の素材を上書きしようとしたら止める(string option)
    {
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string facing = Picture("facing.png", 400, 400, SKColors.Green);
        long before = new FileInfo(facing).Length;

        int exit = Run("generate", "-i", front, option, facing, "-o", facing, "--quiet");

        Assert.Equal(1, exit);
        Assert.Equal(before, new FileInfo(facing).Length);
    }

    [Fact]
    public void 出力が正面の素材を上書きしようとしたら止める()
    {
        // The guard that was already there, kept alongside the new ones so that removing any of
        // them fails here rather than in somebody's folder
        string front = Picture("front.png", 40, 60, SKColors.Red);

        Assert.Equal(1, Run("generate", "-i", front, "-o", front, "--quiet"));
    }

    [Fact]
    public void 同じ絵を正面と側面に渡すのは許す()
    {
        // Not a mistake: handing the front picture to --side is how a drawing is kept from being
        // mirrored on the right-facing frames, which is what the help says --side does. A guard
        // that refused every repeated path would take that away.
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string output = Path.Combine(_work, "sheet.png");

        Assert.Equal(0, Run("generate", "-i", front, "--side", front, "-o", output, "--quiet"));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void 同じ絵を側面と背面に渡すのは許す()
    {
        string front = Picture("front.png", 40, 60, SKColors.Red);
        string other = Picture("other.png", 40, 60, SKColors.Blue);
        string output = Path.Combine(_work, "sheet.png");

        Assert.Equal(
            0,
            Run("generate", "-i", front, "--side", other, "--back", other, "-o", output, "--quiet"));
    }
}

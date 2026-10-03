using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// What validate says about a sheet whose content is not PNG.
///
/// A WEBP or JPEG with the right dimensions was reported "問題なし", and install then put it into
/// the game, which reads PNG only: the character stayed as it was. validate is where a user asks
/// whether a sheet will work, so it has to say this one will not.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ValidateFormatTests : IDisposable
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cks-validate-format-{Guid.NewGuid():N}");

    public ValidateFormatTests() => Directory.CreateDirectory(_work);

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

    [Theory]
    [InlineData(SKEncodedImageFormat.Webp, "webp-renamed.png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "jpeg-renamed.png")]
    public void PNGでない中身のシートは問題ありと言う(SKEncodedImageFormat format, string name)
    {
        string path = Path.Combine(_work, name);
        using (SKBitmap sheet = CreateSheet())
        using (SKData data = sheet.Encode(format, 100))
        {
            File.WriteAllBytes(path, data.ToArray());
        }

        (int exit, string output) = Run("validate", "-i", path);

        Assert.Equal(1, exit);
        Assert.Contains("PNG 形式ではない", output);
        Assert.DoesNotContain("問題なし", output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(16)]
    public void 末尾が欠けたPNGは問題ありと言う(int missing)
    {
        // The decoder reports these as whole, and the game cannot read them
        string whole = Path.Combine(_work, "whole.png");
        using (SKBitmap sheet = CreateSheet())
        {
            PixelOps.EncodePng(sheet, whole);
        }

        string cut = Path.Combine(_work, "tail-cut.png");
        byte[] bytes = File.ReadAllBytes(whole);
        File.WriteAllBytes(cut, bytes[..^missing]);

        (int exit, string output) = Run("validate", "-i", cut);

        Assert.Equal(1, exit);
        Assert.Contains("IEND", output);
        Assert.DoesNotContain("問題なし", output);
    }

    [Theory]
    [InlineData(TaggedSheet.Gamma100000)]
    [InlineData(TaggedSheet.DisplayP3)]
    public void 色の記述で色が変わるPNGには注意を添えるが問題なしのまま(TaggedSheet kind)
    {
        // A note, not a problem: the sheet works, only in other colours than the ones counted here
        string path = TaggedSheets.Write(kind, Path.Combine(_work, $"{kind}.png"));

        (int exit, string output) = Run("validate", "-i", path);

        Assert.Equal(0, exit);
        Assert.Contains("色プロファイル", output);
        Assert.Contains("問題なし", output);

        // Said before the verdict, like the note about semi-transparent pixels
        Assert.True(output.IndexOf("色プロファイル", StringComparison.Ordinal) < output.IndexOf("検査結果", StringComparison.Ordinal));

        // The way round it names the window's button by the label it really has
        Assert.Contains($"「{WindowLabel("action.install")}」", output);
    }

    /// <summary>A label as the window shows it in Japanese, read from the window's own language file.</summary>
    private static string WindowLabel(string key)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "core", "Localization", "lang.ja.json")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "lang.ja.json が見つからない");
        using System.Text.Json.JsonDocument lang = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory!.FullName, "src", "core", "Localization", "lang.ja.json")));
        return lang.RootElement.GetProperty(key).GetString() ?? string.Empty;
    }

    [Theory]
    [InlineData(TaggedSheet.Plain)]
    [InlineData(TaggedSheet.SrgbWithGamma)]
    public void sRGBのPNGには色プロファイルの注意を出さない(TaggedSheet kind)
    {
        string path = TaggedSheets.Write(kind, Path.Combine(_work, $"{kind}.png"));

        (int exit, string output) = Run("validate", "-i", path);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("色プロファイル", output);
    }

    [Fact]
    public void PNGのシートはこれまでどおり問題なしと言う()
    {
        string path = Path.Combine(_work, "sheet.png");
        using (SKBitmap sheet = CreateSheet())
        {
            PixelOps.EncodePng(sheet, path);
        }

        (int exit, string output) = Run("validate", "-i", path);

        Assert.Equal(0, exit);
        Assert.Contains("問題なし", output);
        Assert.DoesNotContain("PNG 形式ではない", output);
    }

    /// <summary>A sheet with art in every frame, well inside the cells, as generate makes it.</summary>
    private static SKBitmap CreateSheet()
    {
        using SKBitmap sprite = PixelOps.CreateEmpty(6, 10);
        SKColor[] pixels = sprite.Pixels;
        Array.Fill(pixels, SKColors.Red);
        sprite.Pixels = pixels;

        return SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static)).Sheet;
    }

    private static (int Exit, string Output) Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;
        using StringWriter output = new();

        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            int exit = Program.Main(args);
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }
}

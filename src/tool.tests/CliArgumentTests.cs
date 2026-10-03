using System.Reflection;
using System.Text;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Command-line arguments that used to end in a message saying nothing useful, or in lost work.
///
/// Found by running every command and option on this machine (the test campaign of 2026-09-29):
/// a folder given as the output ended in the runtime's English "Access to the path ... is
/// denied." after the whole conversion had run; template wrote over a template the user had
/// already drawn on; an empty --side said "入力画像が見つからない: " with nothing after it; and
/// "cks version" printed a number no build ever carried.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CliArgumentTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cks-args-{Guid.NewGuid():N}");

    public CliArgumentTests() => Directory.CreateDirectory(_work);

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

    // ------------------------------------------------------------ A folder as the output

    [Theory]
    [InlineData("generate")]
    [InlineData("template")]
    public void 出力先に既存のフォルダを渡すとファイル名が要ると言う(string command)
    {
        string folder = Path.Combine(_work, "out");
        Directory.CreateDirectory(folder);

        string[] args = command == "generate"
            ? ["generate", "-i", CreatePicture("front.png"), "-o", folder, "--quiet"]
            : ["template", "-o", folder, "--quiet"];

        (int exit, _, string error) = Run(args);

        Assert.Equal(1, exit);
        Assert.Contains("ファイル名まで指定", error);
        Assert.DoesNotContain("Access to the path", error);
    }

    [Fact]
    public void 出力先の末尾が区切りならフォルダを作らずに止める()
    {
        // Written with a trailing separator, the folder was created and then the write failed
        // with "Could not find a part of the path", leaving an empty folder behind
        string folder = Path.Combine(_work, "not-yet") + Path.DirectorySeparatorChar;

        (int exit, _, string error) = Run("template", "-o", folder, "--quiet");

        Assert.Equal(1, exit);
        Assert.Contains("ファイル名まで指定", error);
        Assert.False(Directory.Exists(folder));
    }

    // ------------------------------------------------------------ template over a drawing

    [Fact]
    public void 描き込み済みのテンプレートは上書きしない()
    {
        string template = Path.Combine(_work, "template.png");
        Assert.Equal(0, Run("template", "-o", template, "--no-guide", "--quiet").Exit);

        using (SKBitmap drawn = PixelOps.Decode(template))
        {
            drawn.SetPixel(100, 50, SKColors.Red);
            PixelOps.EncodePng(drawn, template);
        }

        byte[] before = File.ReadAllBytes(template);

        (int exit, _, string error) = Run("template", "-o", template, "--no-guide", "--quiet");

        Assert.Equal(1, exit);
        Assert.Contains("空のテンプレートではない", error);
        Assert.Equal(before, File.ReadAllBytes(template));
    }

    [Fact]
    public void 空のテンプレートはこれまでどおり作り直せる()
    {
        string template = Path.Combine(_work, "template.png");
        Assert.Equal(0, Run("template", "-o", template, "--quiet").Exit);

        (int exit, _, string error) = Run("template", "-o", template, "--quiet");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void 他のアプリが開いているテンプレートは開けない理由を言う()
    {
        // Held open by another program, the blank template cannot be read, and the refusal said it
        // was "not a blank template (a drawing, or not an image)" - wrong about a blank file, and its
        // advice, delete it first, fails while it is held. The real reason was thrown away;
        // aa1990e at least said the file was in use (the test campaign of 2026-10-01).
        string template = Path.Combine(_work, "template.png");
        Assert.Equal(0, Run("template", "-o", template, "--no-guide", "--quiet").Exit);
        byte[] before = File.ReadAllBytes(template);

        int exit;
        string error;
        using (new FileStream(template, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            (exit, _, error) = Run("template", "-o", template, "--no-guide", "--quiet");
        }

        Assert.Equal(1, exit);
        Assert.Contains("他のアプリが使用中", error);
        Assert.DoesNotContain("空のテンプレートではない", error);
        Assert.Equal(before, File.ReadAllBytes(template));
    }

    // ------------------------------------------------------------ Empty picture options

    [Theory]
    [InlineData("input")]
    [InlineData("side")]
    [InlineData("back")]
    public void 画像のオプションに空の値を渡すとオプション名を挙げて止める(string option)
    {
        string front = CreatePicture("front.png");
        string output = Path.Combine(_work, "sheet.png");

        string[] args = option == "input"
            ? ["generate", "-i", "", "-o", output, "--quiet"]
            : ["generate", "-i", front, $"--{option}", " ", "-o", output, "--quiet"];

        (int exit, _, string error) = Run(args);

        Assert.Equal(1, exit);
        Assert.Contains($"--{option} に値が無い", error);
        Assert.DoesNotContain("入力画像が見つからない", error);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("install", "input")]
    [InlineData("validate", "input")]
    [InlineData("layout", "layout")]
    [InlineData("generate", "layout")]
    [InlineData("validate", "layout")]
    [InlineData("install", "layout")]
    [InlineData("template", "layout")]
    public void 入力とレイアウトに空の値を渡すとオプション名を挙げて止める(string command, string option)
    {
        // The fix for generate's picture options left the other commands, and --layout everywhere,
        // saying "入力画像が見つからない: " or "レイアウト定義が見つからない: " with nothing after it -
        // the shape a script takes when the variable holding the path was never set
        LayOutGame();
        string front = CreatePicture("front.png");
        string sheet = CreateSheet();
        string output = Path.Combine(_work, "out.png");

        string[] args = (command, option) switch
        {
            ("install", "input") => ["install", "-i", "", "--mods-dir", ModsDirectory, "--game-dir", Game, "--quiet"],
            ("validate", "input") => ["validate", "-i", ""],
            ("layout", _) => ["layout", "-l", ""],
            ("generate", _) => ["generate", "-i", front, "-o", output, "-l", "", "--quiet"],
            ("validate", _) => ["validate", "-i", sheet, "-l", ""],
            ("install", _) => ["install", "-i", sheet, "-l", "", "--mods-dir", ModsDirectory, "--game-dir", Game, "--quiet"],
            _ => ["template", "-o", output, "-l", "", "--quiet"],
        };

        (int exit, _, string error) = Run(args);

        Assert.Equal(1, exit);
        Assert.Contains($"--{option} に値が無い", error);
        Assert.DoesNotContain("入力画像が見つからない", error);
        Assert.DoesNotContain("レイアウト定義が見つからない", error);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(ModsDirectory, SheetInstaller.DefaultModFolderName)));
    }

    // ------------------------------------------------------------ Ctrl+C during generate

    [Fact]
    public void 変換中に中断を頼まれたら出力を書かずに止める()
    {
        // The first Ctrl+C only cancels the token Main hands to the run; the conversion does not
        // watch it. Measured with a real Ctrl+C: generate went on, wrote over the existing output
        // and ended with exit 0 - silently with --quiet - where v1.0.0 stopped at once and left the
        // file alone. The state is made here by handing Run a token already cancelled.
        string output = Path.Combine(_work, "skin.png");
        File.WriteAllBytes(output, [1, 2, 3, 4]);
        byte[] before = File.ReadAllBytes(output);

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        MethodInfo run = typeof(Program).GetMethod(
            "Run", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string[]), typeof(CancellationToken)])!;

        string[] args = ["generate", "-i", CreatePicture("front.png"), "-o", output, "--quiet"];

        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;
        object? returned = null;
        Exception? thrown = null;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            returned = run.Invoke(null, [args, cancelled.Token]);
        }
        catch (TargetInvocationException ex)
        {
            thrown = ex.InnerException;
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }

        Assert.True(
            thrown is OperationCanceledException,
            $"中断を頼まれた後も最後まで進んだ（戻り値 {returned ?? "なし"}、例外 {thrown?.GetType().Name ?? "なし"}）");
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    // ------------------------------------------------------------ A saves folder as --mods-dir

    [Fact]
    public void modsdirにセーブフォルダそのものを渡すと配置しない()
    {
        // The saves folder looked for beside the one given is the given folder itself, so the
        // check meant to stop a wrong --mods-dir passed, and the pictures went into
        // saves\CustomPlayerSkin\skins - reported as placed, never read by the mod
        LayOutGame();

        (int exit, _, string error) = Run(
            "install", "--input", CreateSheet(), "--mods-dir", SavesDirectory, "--game-dir", Game, "--quiet");

        Assert.Equal(1, exit);
        Assert.Contains("--mods-dir", error);
        Assert.False(
            Directory.Exists(Path.Combine(SavesDirectory, SheetInstaller.DefaultModFolderName)),
            "MOD が読まないセーブフォルダの中に配置先を作った");
    }

    [Fact]
    public void 一覧でもmodsdirにセーブフォルダそのものを渡すと止める()
    {
        // Listed, the saves folder looked like a destination - its characters "not applied" -
        // that install then turned down. This listing is where --character's slot numbers are read,
        // so it is usually the first command run against a --mods-dir.
        LayOutGame();

        (int exit, string output, string error) = Run("install", "--list", "--mods-dir", SavesDirectory);

        Assert.Equal(1, exit);
        Assert.Contains("--mods-dir にセーブフォルダが指定された", error);
        Assert.DoesNotContain(CharacterGuid, output);
    }

    [Fact]
    public void 一覧はmodsdirに正しいmodsを渡せばこれまでどおり出す()
    {
        LayOutGame();

        (int exit, string output, string error) = Run("install", "--list", "--mods-dir", ModsDirectory);

        Assert.True(exit == 0, error);
        Assert.Contains(CharacterGuid, output);
    }

    [Fact]
    public void modsdirに正しいmodsを渡せばこれまでどおり配置する()
    {
        LayOutGame();

        (int exit, _, string error) = Run(
            "install", "--input", CreateSheet(), "--mods-dir", ModsDirectory, "--game-dir", Game, "--quiet");

        Assert.True(exit == 0, error);
        Assert.True(File.Exists(Path.Combine(
            ModsDirectory, SheetInstaller.DefaultModFolderName, CharacterSkins.FolderName,
            CharacterGuid + CharacterSkins.Extension)));
    }

    // ------------------------------------------------------------ version

    [Fact]
    public void versionは製品バージョンを名乗る()
    {
        // The same value the executable's file properties show as the product version, so a
        // report can say which build it came from
        string expected = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        (int exit, string output, _) = Run("version");

        Assert.Equal(0, exit);
        Assert.Equal($"cks {expected}", output.Trim());
        Assert.DoesNotContain("0.1.0", output);
    }

    // ------------------------------------------------------------ Helpers

    private const string CharacterGuid = "3825ebc1f472f28e5b9e984c425e01d5";

    private string Game => Path.Combine(_work, "Core Keeper");

    private string UserDirectory => Path.Combine(_work, "Steam", "1");

    private string ModsDirectory => Path.Combine(UserDirectory, "mods");

    private string SavesDirectory => Path.Combine(UserDirectory, "saves");

    /// <summary>A game folder and a user folder with one character, laid out by hand inside the work folder.</summary>
    private void LayOutGame()
    {
        Directory.CreateDirectory(Path.Combine(Game, "CoreKeeper_Data"));
        File.WriteAllText(Path.Combine(Game, "CoreKeeper.exe"), "not really an executable");

        Directory.CreateDirectory(ModsDirectory);
        Directory.CreateDirectory(SavesDirectory);
        File.WriteAllText(
            Path.Combine(SavesDirectory, "0.json"),
            "{\"version\":11,\"characterGuid\":\"" + CharacterGuid + "\"}",
            new UTF8Encoding(false));
    }

    /// <summary>A finished sheet to install or validate.</summary>
    private string CreateSheet()
    {
        using SKBitmap sprite = PixelOps.CreateEmpty(6, 10);
        SKColor[] pixels = sprite.Pixels;
        Array.Fill(pixels, SKColors.Red);
        sprite.Pixels = pixels;

        ComposeResult composed = SheetComposer.Compose(
            SheetLayout.LoadEmbedded(), sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using (composed.Sheet)
        {
            string path = Path.Combine(_work, "sheet.png");
            PixelOps.EncodePng(composed.Sheet, path);
            return path;
        }
    }

    /// <summary>A small opaque picture to convert.</summary>
    private string CreatePicture(string name)
    {
        using SKBitmap picture = PixelOps.CreateEmpty(40, 60);
        SKColor[] pixels = picture.Pixels;
        Array.Fill(pixels, SKColors.SteelBlue);
        picture.Pixels = pixels;

        string path = Path.Combine(_work, name);
        PixelOps.EncodePng(picture, path);
        return path;
    }

    private static (int Exit, string Output, string Error) Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;
        using StringWriter output = new();
        using StringWriter error = new();

        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exit = Program.Main(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }
}

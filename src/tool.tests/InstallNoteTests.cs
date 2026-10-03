using System.Text;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// What install says, after placing a picture, about whether the game will show it.
///
/// "ゲームを起動したままでも数秒で反映される" was said whatever the state of the mod. With the mod not
/// in the game nothing there reads the picture at all, and a mod the game refused to load - a game
/// update is enough - reads it no more than a missing one. The window has said so since 51a9b94;
/// the command line went on promising a change within seconds.
///
/// Everything is laid out by hand in a temporary folder: a game folder of a version this build
/// supports, one user's folder with a character in it, and the game's log where the tests put it.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class InstallNoteTests : IDisposable
{
    private const string CharacterGuid = "3825ebc1f472f28e5b9e984c425e01d5";

    /// <summary>The part of the promise that must not be made when it cannot be kept.</summary>
    private const string Promise = "数秒で反映";

    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cks-note-{Guid.NewGuid():N}");

    private readonly string? _previousLog =
        Environment.GetEnvironmentVariable(GameLocator.PlayerLogOverrideVariable);

    public InstallNoteTests()
    {
        Directory.CreateDirectory(_work);

        // Pointed at a file only the tests that want a log write
        Environment.SetEnvironmentVariable(GameLocator.PlayerLogOverrideVariable, LogPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GameLocator.PlayerLogOverrideVariable, _previousLog);

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

    private string Game => Path.Combine(_work, "Core Keeper");

    private string UserDirectory => Path.Combine(_work, "Steam", "1");

    private string ModsDirectory => Path.Combine(UserDirectory, "mods");

    private string LogPath => Path.Combine(_work, "Player.log");

    private string InstalledMod => Path.Combine(
        Game, "CoreKeeper_Data", "StreamingAssets", "Mods", SheetInstaller.DefaultModFolderName);

    [Fact]
    public void MODがゲームに入っていなければ数秒で反映とは言わない()
    {
        LayOut(withMod: false);

        (int exit, string output, string error) = Install();

        Assert.Equal(0, exit);
        Assert.DoesNotContain(Promise, output + error);
        Assert.Contains("MOD がゲームに入っていない", error);
        Assert.Contains("「MOD を導入」", error);
    }

    [Fact]
    public void MODが入っていればこれまでどおり数秒で反映と言う()
    {
        LayOut(withMod: true);

        (int exit, string output, string error) = Install();

        Assert.Equal(0, exit);
        Assert.Contains(Promise, output);
        Assert.DoesNotContain("MOD がゲームに入っていない", error);
        Assert.DoesNotContain("警告", error);
    }

    [Fact]
    public void 前回の起動でMODを読み込めていなければ警告して数秒で反映とは言わない()
    {
        DateTime now = DateTime.UtcNow;
        LayOut(withMod: true, modChangedUtc: now.AddHours(-1));

        new GameLogBuilder()
            .Start(now.AddMinutes(-1), Game)
            .Discover(Game)
            .Compile()
            .RefusedByCodeCheck()
            .WriteTo(LogPath);

        (int exit, string output, string error) = Install();

        Assert.Equal(0, exit);
        Assert.DoesNotContain(Promise, output + error);
        Assert.Contains("MOD を読み込めていない", error);
        Assert.Contains("System.Reflection", error);
        Assert.Contains(LogPath, error);
    }

    [Fact]
    public void 組み込みに失敗した起動なら見た目が差し替わらないことがあると言う()
    {
        DateTime now = DateTime.UtcNow;
        LayOut(withMod: true, modChangedUtc: now.AddHours(-1));

        new GameLogBuilder()
            .Start(now.AddMinutes(-1), Game)
            .Discover(Game)
            .Compile()
            .PatchThrows()
            .WriteTo(LogPath);

        (int exit, string output, string error) = Install();

        Assert.Equal(0, exit);
        Assert.DoesNotContain(Promise, output + error);
        Assert.Contains("組み込めていない", error);
    }

    [Fact]
    public void MODを入れる前の起動の失敗は持ち出さない()
    {
        // "Update mod" pressed to fix the very failure the log records is the usual way here, and
        // repeating the failure afterwards would say the fix had not worked when it has not yet
        // been tried
        DateTime now = DateTime.UtcNow;
        LayOut(withMod: true, modChangedUtc: now.AddMinutes(-1));

        new GameLogBuilder()
            .Start(now.AddHours(-1), Game)
            .Discover(Game)
            .Compile()
            .RefusedByCodeCheck()
            .WriteTo(LogPath);

        (int exit, string output, string error) = Install();

        Assert.Equal(0, exit);
        Assert.Contains(Promise, output);
        Assert.DoesNotContain("警告", error);
    }

    [Fact]
    public void 静かにしてもMODが無いことは告げる()
    {
        // --quiet leaves out the report, not the one line that says none of it will show: the
        // version warning is given the same way
        LayOut(withMod: false);

        (int exit, string output, string error) = Install("--quiet");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(Promise, output);
        Assert.Contains("MOD がゲームに入っていない", error);
    }

    [Fact]
    public void 動作を保証できない版では対応版を照合と同じ粒度で示す()
    {
        // The check matches on the first three numbers, so "1.3.0" covers every 1.3.0 build. Listed
        // as the bare prefixes, the line read as if only those exact versions were supported; the
        // window was changed to say "1.3.0.x" and the command line with it (fix 13), and nothing
        // tested the command line's line (the test campaign of 2026-10-01).
        LayOut(withMod: true);
        File.WriteAllBytes(
            Path.Combine(Game, "CoreKeeper_Data", "globalgamemanagers"),
            Encoding.ASCII.GetBytes("\0\0\0Pugstorm\0\0\0Core Keeper\0\0\0\09.9.9.9-test\0\0\0"));

        (int exit, _, string error) = Install();

        Assert.Equal(0, exit);
        Assert.Contains("動作を保証できないバージョン", error);
        Assert.Contains("9.9.9.9-test", error);
        Assert.Contains("1.3.0.x", error);
    }

    [Theory]
    [InlineData(TaggedSheet.Gamma100000, false)]
    [InlineData(TaggedSheet.DisplayP3, false)]
    [InlineData(TaggedSheet.Gamma100000, true)]
    public void 色の記述で色が変わるシートは置いたうえで注意する(TaggedSheet kind, bool quiet)
    {
        // Placed as it is: the file is still the user's, and the game reads it. What changes is
        // only that the colours will not be the ones checked, which is said even with --quiet.
        LayOut(withMod: true);
        string sheet = TaggedSheets.Write(kind, Path.Combine(_work, $"{kind}.png"));

        (int exit, string output, string error) =
            Run(["install", "--input", sheet, "--mods-dir", ModsDirectory, "--game-dir", Game, .. (quiet ? new[] { "--quiet" } : [])]);

        Assert.Equal(0, exit);
        Assert.Contains("色プロファイル", error);
        Assert.DoesNotContain("色プロファイル", output);

        string placed = CharacterSkins.PathFor(ModsDirectory, SheetInstaller.DefaultModFolderName, CharacterGuid);
        Assert.Equal(File.ReadAllBytes(sheet), File.ReadAllBytes(placed));
    }

    [Theory]
    [InlineData(TaggedSheet.Plain)]
    [InlineData(TaggedSheet.SrgbWithGamma)]
    public void sRGBのシートには色プロファイルの注意を出さない(TaggedSheet kind)
    {
        LayOut(withMod: true);
        string sheet = TaggedSheets.Write(kind, Path.Combine(_work, $"{kind}.png"));

        (int exit, string output, string error) =
            Run(["install", "--input", sheet, "--mods-dir", ModsDirectory, "--game-dir", Game]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("色プロファイル", output + error);
    }

    // ------------------------------------------------------------ Laying out

    /// <summary>Makes the game, one user's folder with a character, and the picture to place.</summary>
    /// <param name="withMod">Whether the mod is in the game.</param>
    /// <param name="modChangedUtc">When the mod's files were written, when that matters to the test.</param>
    private void LayOut(bool withMod, DateTime? modChangedUtc = null)
    {
        string data = Path.Combine(Game, "CoreKeeper_Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(Game, "CoreKeeper.exe"), "not really an executable");

        // The header Unity writes, reduced to the product name and the version after it, so the
        // game reads as a version this build supports and no version warning gets in the way
        File.WriteAllBytes(
            Path.Combine(data, "globalgamemanagers"),
            Encoding.ASCII.GetBytes($"\0\0\0Pugstorm\0\0\0Core Keeper\0\0\0\0{GameLogBuilder.Version}\0\0\0"));

        if (withMod)
        {
            Directory.CreateDirectory(Path.Combine(InstalledMod, "Scripts"));
            File.WriteAllText(Path.Combine(InstalledMod, "ModManifest.json"), $"{{\"name\":\"{GameLogBuilder.ModName}\"}}");
            File.WriteAllText(Path.Combine(InstalledMod, "Scripts", "CustomPlayerSkinMod.cs"), "// stands in for the mod");

            if (modChangedUtc is { } changed)
            {
                SetTimes(InstalledMod, changed);
            }
        }

        Directory.CreateDirectory(ModsDirectory);
        string saves = Path.Combine(UserDirectory, "saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "0.json"), CharacterJson(CharacterGuid, "テスト"), new UTF8Encoding(false));
    }

    /// <summary>Places a picture for every character, the way a user would.</summary>
    private (int Exit, string Output, string Error) Install(params string[] extra) =>
        Run(["install", "--input", CreateSheet(), "--mods-dir", ModsDirectory, "--game-dir", Game, .. extra]);

    /// <summary>Gives a folder and everything in it one time, files first: creating files moves a folder's own time.</summary>
    private static void SetTimes(string directory, DateTime utc)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, utc);
            File.SetLastWriteTimeUtc(file, utc);
        }

        foreach (string folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Append(directory))
        {
            Directory.SetCreationTimeUtc(folder, utc);
            Directory.SetLastWriteTimeUtc(folder, utc);
        }
    }

    private string CreateSheet()
    {
        using SKBitmap sprite = PixelOps.CreateEmpty(6, 10);
        SKColor[] pixels = sprite.Pixels;
        Array.Fill(pixels, SKColors.Red);
        sprite.Pixels = pixels;

        ComposeResult composed = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using (composed.Sheet)
        {
            string path = Path.Combine(_work, "sheet.png");
            PixelOps.EncodePng(composed.Sheet, path);
            return path;
        }
    }

    /// <summary>A save file the way the game writes one, as CharacterTests does.</summary>
    private static string CharacterJson(string guid, string name)
    {
        string block = $"{{\"name\":{NameJson(name)},\"gender\":1,\"skinColor\":1}}";

        return "{\"version\":11," +
               $"\"characterGuid\":\"{guid}\"," +
               $"\"characterCustomization\":{{\"name\":{NameJson(string.Empty)}}}," +
               $"\"characterCustomizationNew\":{block}," +
               "\"coinAmount\":0}";
    }

    /// <summary>Unity's fixed-size string, serialised as one numbered field per byte.</summary>
    private static string NameJson(string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        StringBuilder json = new();

        json.Append($"{{\"utf8LengthInBytes\":{utf8.Length},\"bytes\":{{\"offset0000\":{{");
        for (int i = 0; i < 16; i++)
        {
            json.Append(i == 0 ? string.Empty : ",");
            json.Append($"\"byte{i:d4}\":{(i < utf8.Length ? utf8[i] : 0)}");
        }

        json.Append('}');

        // Past the first sixteen the fields sit beside the nested object rather than inside it
        for (int i = 16; i < 30; i++)
        {
            json.Append($",\"byte{i:d4}\":{(i < utf8.Length ? utf8[i] : 0)}");
        }

        json.Append("}}");
        return json.ToString();
    }

    private static (int Exit, string Output, string Error) Run(string[] args)
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

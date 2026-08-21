using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Install tests, focused on never handing a broken sheet to the game.
///
/// They go through CharacterSkins.Install, which is the path the CLI and the window both take.
/// They used to call SheetInstaller.Install, which nothing in the product calls, so six checks
/// of "a broken sheet is refused" were guarding a route no user could reach.
///
/// The real game folder is never touched; a temporary directory stands in for the destination.
/// </summary>
public sealed class SheetInstallerTests : IDisposable
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"cks-install-test-{Guid.NewGuid():N}");

    public SheetInstallerTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    private string Path_(string name) => Path.Combine(_workDirectory, name);


    /// <summary>Creates a sheet with correct dimensions and actual content.</summary>
    private string CreateValidSheet(string name = "sheet.png")
    {
        using SKBitmap sprite = PixelOps.CreateEmpty(6, 10);
        SKColor[] pixels = sprite.Pixels;
        Array.Fill(pixels, SKColors.Red);
        sprite.Pixels = pixels;

        ComposeResult composed = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using (composed.Sheet)
        {
            string path = Path_(name);
            PixelOps.EncodePng(composed.Sheet, path);
            return path;
        }
    }

    private string CreateSheet(int width, int height, SKColor fill, string name)
    {
        using SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = bitmap.Pixels;
        Array.Fill(pixels, fill);
        bitmap.Pixels = pixels;

        string path = Path_(name);
        PixelOps.EncodePng(bitmap, path);
        return path;
    }

    /// <summary>A character identifier of the shape the save files use.</summary>
    private const string Guid1 = "11111111111111111111111111111111";

    private string ModsDirectory => Path_("mods");

    private static string ModFolder => SheetInstaller.DefaultModFolderName;

    [Fact]
    public void Install_キャラクターの画像として配置される()
    {
        string sheet = CreateValidSheet();

        IReadOnlyList<CharacterSkinResult> results =
            CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [Guid1]);

        string destination = Assert.Single(results).Path;

        Assert.True(File.Exists(destination));
        Assert.EndsWith(Path.Combine(ModFolder, "skins", Guid1 + ".png"), destination);
    }

    [Fact]
    public void Install_配置先フォルダが無ければ作る()
    {
        string sheet = CreateValidSheet();
        Assert.False(Directory.Exists(ModsDirectory));

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [Guid1]);

        Assert.True(File.Exists(CharacterSkins.PathFor(ModsDirectory, ModFolder, Guid1)));
    }

    [Fact]
    public void Install_2回目も同じ場所を差し替える()
    {
        string sheet = CreateValidSheet();

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [Guid1]);
        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [Guid1]);

        Assert.True(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder).SetEquals([Guid1]));

        // The swap goes through a temporary file, which must not be left behind
        Assert.Empty(Directory.GetFiles(
            Path.Combine(ModsDirectory, ModFolder, "skins"), "*.new"));
    }

    [Fact]
    public void Install_寸法が違うシートは配置しない()
    {
        // Wrong dimensions shift every frame and break the look for no obvious reason in game
        string sheet = CreateSheet(100, 100, SKColors.Red, "wrong-size.png");

        ToolException ex = Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [Guid1]));

        Assert.Contains("100x100", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(ModsDirectory, ModFolder)));
    }

    [Fact]
    public void Install_完全に透明なシートは配置しない()
    {
        // Installing it would only make the character invisible, so reject it up front
        string sheet = CreateSheet(
            Layout.Texture.Width, Layout.Texture.Height, SKColors.Transparent, "blank.png");

        Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [Guid1]));
    }

    [Fact]
    public void Install_途中で切れたシートは配置しない()
    {
        // A download cut short, or a copy from a drive that went away, decodes into an image of
        // exactly the right size whose missing part is transparent. Both other checks then pass
        // and the character goes into the game with its lower half gone, with nothing said.
        string whole = CreateSheet(
            Layout.Texture.Width, Layout.Texture.Height, SKColors.Red, "whole.png");

        byte[] bytes = File.ReadAllBytes(whole);
        string cut = Path_("cut.png");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);

        ToolException ex = Assert.Throws<ToolException>(
            () => CharacterSkins.Install(cut, Layout, ModsDirectory, ModFolder, [Guid1]));

        Assert.Equal("error.sheet.truncated", ex.MessageKey);
        Assert.False(Directory.Exists(Path.Combine(ModsDirectory, ModFolder)));
    }

    [Fact]
    public void Install_入力が存在しなければ例外にする()
    {
        Assert.Throws<ToolException>(() => CharacterSkins.Install(
            Path_("no-such.png"), Layout, ModsDirectory, ModFolder, [Guid1]));
    }

    [Fact]
    public void Install_既定以外のフォルダ名は拒否される()
    {
        // The mod reads a fixed path, so writing anywhere else would report success and change
        // nothing in game. The tests this file used to hold went through SheetInstaller.Install
        // directly, which has no such check, and so fixed "MyMod" as a working folder name -
        // the opposite of what the real path does.
        string sheet = CreateValidSheet();

        Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, "MyMod", [Guid1]));
    }

    [Fact]
    public void Resolve_明示指定したフォルダをそのまま使う()
    {
        string explicitDirectory = Path_("explicit");
        Directory.CreateDirectory(explicitDirectory);

        ModConfigLocation location = GameLocator.Resolve(explicitDirectory);

        Assert.Equal(explicitDirectory, location.ModsDirectory);
    }

    [Fact]
    public void Resolve_明示指定が存在しなければ例外にする()
    {
        ToolException ex = Assert.Throws<ToolException>(() => GameLocator.Resolve(Path_("missing")));
        Assert.Contains("存在しない", ex.Message);
    }

    [Fact]
    public void Normalize_接合点を解決して同じ場所を同じ綴りにする()
    {
        // Two spellings of one folder have to compare equal, or everything keyed on the result
        // treats one place as two. GetFullPath stops at "..", case and trailing separators, so
        // a link anywhere along the path survived it - and the link is usually not the last
        // segment but one nearer the top.
        string real = Path.Combine(_workDirectory, "real", "inner");
        Directory.CreateDirectory(real);

        string link = Path.Combine(_workDirectory, "link");

        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(_workDirectory, "real")}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

        process?.WaitForExit();
        Assert.True(process?.ExitCode == 0 && Directory.Exists(link), "接合点を作成できなかった");

        try
        {
            Assert.Equal(
                PathSafety.Normalize(real),
                PathSafety.Normalize(Path.Combine(link, "inner")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}

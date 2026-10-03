using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Installing or updating the mod when its folder, or a working folder beside it, is a link.
///
/// The code base already treats a mod folder moved elsewhere with a junction left behind as a real
/// case (ModUninstaller.CountFiles). The swap moves the link itself aside, so the "backup" whose
/// contents were taken to be disposable was the user's own folder: its ModManifest.json was deleted
/// through the link. A link pointing outside the Mods folder is refused, rightly, but the refusal
/// named the link's own path as both what was checked and inside where it should be, carried no key,
/// so the English window showed it in Japanese, and said nothing of what to do
/// (the test campaign of 2026-09-30).
/// </summary>
[Collection(ChildProcessCollection.Name)]
public sealed class ModPayloadLinkTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cks-payload-link-test-{Guid.NewGuid():N}");

    public ModPayloadLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Links are taken out first, so a test that stopped short leaves nothing for the recursive
        // delete below to trip over
        foreach (string link in Directory
                     .EnumerateDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                     .Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint))
                     .ToList())
        {
            Directory.Delete(link);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateGameFolder()
    {
        string game = Path.Combine(_root, "Core Keeper");
        Directory.CreateDirectory(Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods"));
        File.WriteAllText(Path.Combine(game, "CoreKeeper.exe"), string.Empty);
        return game;
    }

    private static string ModsOf(string game) => Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods");

    /// <summary>Creates a directory junction, the link Windows makes without needing elevation.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(link);
    }

    /// <summary>A working copy of the mod as a developer might keep one: manifest and a file of their own.</summary>
    private static string CreateWorkingCopy(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ModManifest.json"), "{\"name\":\"CustomPlayerSkin\"}");
        File.WriteAllText(Path.Combine(folder, "marker.txt"), "user");
        return folder;
    }

    // ------------------------------------------------------------ link inside Mods (r2-fs-edge-1)

    [Fact]
    public void InstallTo_MODフォルダがジャンクションでもリンク先のファイルを消さない()
    {
        string game = CreateGameFolder();
        string mods = ModsOf(game);
        string work = CreateWorkingCopy(Path.Combine(mods, "_work", "CustomPlayerSkin"));
        Assert.True(TryCreateJunction(Path.Combine(mods, "CustomPlayerSkin"), work), "接合点を作成できなかった");

        ModPayload.InstallTo(game);

        Assert.True(File.Exists(Path.Combine(work, "ModManifest.json")), "リンク先の ModManifest.json が消された");
        Assert.True(File.Exists(Path.Combine(work, "marker.txt")), "リンク先のファイルが消された");
        Assert.False(Directory.Exists(Path.Combine(mods, "CustomPlayerSkin.old")), "退避用フォルダが残っている");
        Assert.True(File.Exists(Path.Combine(mods, "CustomPlayerSkin", "ModManifest.json")), "新しい MOD が置かれていない");
    }

    [Fact]
    public void InstallTo_退避フォルダがジャンクションでもリンク先のファイルを消さない()
    {
        // The recovery path: no mod folder, and a .old that is a link. It is moved into place,
        // then aside again by the swap, and removed at the end.
        string game = CreateGameFolder();
        string mods = ModsOf(game);
        string far = CreateWorkingCopy(Path.Combine(_root, "elsewhere", "CustomPlayerSkin"));
        Assert.True(TryCreateJunction(Path.Combine(mods, "CustomPlayerSkin.old"), far), "接合点を作成できなかった");

        ModPayload.InstallTo(game);

        Assert.True(File.Exists(Path.Combine(far, "ModManifest.json")), "Mods の外にある ModManifest.json が消された");
        Assert.True(File.Exists(Path.Combine(far, "marker.txt")), "Mods の外にあるファイルが消された");
    }

    [Fact]
    public void TryDelete_ジャンクションはリンクだけを消す()
    {
        string far = CreateWorkingCopy(Path.Combine(_root, "far"));
        string link = Path.Combine(_root, "CustomPlayerSkin.old");
        Assert.True(TryCreateJunction(link, far), "接合点を作成できなかった");

        ModPayload.TryDelete(link);

        Assert.False(Directory.Exists(link), "リンクが残っている");
        Assert.True(File.Exists(Path.Combine(far, "ModManifest.json")), "リンク先の ModManifest.json が消された");
    }

    [Fact]
    public void TryDelete_普通のフォルダは今までどおりマニフェストから消す()
    {
        // The guard only skips links; a real working folder still loses its manifest first
        string folder = CreateWorkingCopy(Path.Combine(_root, "CustomPlayerSkin.new"));

        ModPayload.TryDelete(folder);

        Assert.False(Directory.Exists(folder));
    }

    // ------------------------------------------------------------ link outside Mods (r2-fs-edge-3)

    [Fact]
    public void InstallTo_配置先がModsの外を指すリンクならリンクだと伝えて何も変えない()
    {
        string game = CreateGameFolder();
        string mods = ModsOf(game);
        string far = CreateWorkingCopy(Path.Combine(_root, "far", "CustomPlayerSkin"));
        string link = Path.Combine(mods, "CustomPlayerSkin");
        Assert.True(TryCreateJunction(link, far), "接合点を作成できなかった");

        ToolException ex = Assert.Throws<ToolException>(() => ModPayload.InstallTo(game));

        Assert.Equal("error.payload.destinationIsLink", ex.MessageKey);
        Assert.Contains(link, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(far, ex.Message, StringComparison.OrdinalIgnoreCase);

        // Refused before anything moved
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "リンクが置き換えられた");
        Assert.True(File.Exists(Path.Combine(far, "ModManifest.json")));
        Assert.True(File.Exists(Path.Combine(far, "marker.txt")));
        Assert.False(Directory.Exists(link + ".new"), ".new が残っている");
        Assert.False(Directory.Exists(link + ".old"), ".old が残っている");
    }

    [Fact]
    public void InstallTo_配置先が壊れたリンクでもリンクだと伝える()
    {
        string game = CreateGameFolder();
        string mods = ModsOf(game);
        string far = CreateWorkingCopy(Path.Combine(_root, "far", "CustomPlayerSkin"));
        string link = Path.Combine(mods, "CustomPlayerSkin");
        Assert.True(TryCreateJunction(link, far), "接合点を作成できなかった");
        Directory.Delete(far, recursive: true);

        ToolException ex = Assert.Throws<ToolException>(() => ModPayload.InstallTo(game));

        Assert.Equal("error.payload.destinationIsLink", ex.MessageKey);
        Assert.Contains(link, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

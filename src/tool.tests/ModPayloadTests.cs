using System.Text.Json;
using CoreKeeperSkinTool;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Bundled mod tests.
///
/// This mechanism is what lets users install the mod without Unity or .NET.
/// Confirms the payload is really embedded and that it extracts to the right place.
/// </summary>
public sealed class ModPayloadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cks-payload-test-{Guid.NewGuid():N}");

    public ModPayloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Creates a stand-in for the game's folder structure.</summary>
    private string CreateGameFolder()
    {
        string game = Path.Combine(_root, "Core Keeper");
        Directory.CreateDirectory(Path.Combine(game, @"CoreKeeper_Data\StreamingAssets"));
        File.WriteAllText(Path.Combine(game, "CoreKeeper.exe"), string.Empty);
        return game;
    }

    [Fact]
    public void 同梱データが埋め込まれている()
    {
        // Nothing is embedded if build/mod-payload is missing at build time.
        // It is mandatory for a release, so surface the problem here.
        Assert.True(
            ModPayload.IsAvailable,
            "MOD が同梱されていない。scripts/build-mod.ps1 を実行してから再ビルドすること。");
    }

    [Fact]
    public void 同梱データの情報を読める()
    {
        PayloadInfo? info = ModPayload.GetInfo();

        Assert.NotNull(info);
        Assert.Equal("CustomPlayerSkin", info!.Name);
        Assert.True(info.FileCount > 0, "同梱データが空");
    }

    [Fact]
    public void 装備表示設定をゲームが読む形式で書き出す()
    {
        // The game builds the path as <mod>\<section>-<key>.json and reads it with Unity's
        // JsonUtility, so the field names have to match its own struct exactly.
        string mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(mods);

        IReadOnlyList<string> written = ModConfigWriter.SetGearHidden(mods, "CustomPlayerSkin", hide: false);

        Assert.Equal(4, written.Count);
        Assert.All(written, path => Assert.True(File.Exists(path)));

        string armour = Path.Combine(mods, "CustomPlayerSkin", "General-hideArmor.json");
        Assert.Contains(armour, written);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(armour));
        JsonElement root = document.RootElement;

        Assert.Equal("CustomPlayerSkin", root.GetProperty("mod").GetString());
        Assert.Equal("General", root.GetProperty("section").GetString());
        Assert.Equal("hideArmor", root.GetProperty("key").GetString());
        Assert.True(root.GetProperty("defaultValue").GetBoolean());
        Assert.False(root.GetProperty("value").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
    }

    [Fact]
    public void 装備表示設定を書いた値のまま読み戻せる()
    {
        string mods = Path.Combine(_root, "mods2");
        Directory.CreateDirectory(mods);

        // Nothing written yet: the mod's own default is to hide
        Assert.True(ModConfigWriter.IsGearHidden(mods, "CustomPlayerSkin"));

        ModConfigWriter.SetGearHidden(mods, "CustomPlayerSkin", hide: false);
        Assert.False(ModConfigWriter.IsGearHidden(mods, "CustomPlayerSkin"));

        ModConfigWriter.SetGearHidden(mods, "CustomPlayerSkin", hide: true);
        Assert.True(ModConfigWriter.IsGearHidden(mods, "CustomPlayerSkin"));

        // No temporary files left behind by the swap
        Assert.Empty(Directory.GetFiles(Path.Combine(mods, "CustomPlayerSkin"), "*.new"));
    }

    [Fact]
    public void 装備表示設定の書き出しは危険なMOD名を拒否する()
    {
        string mods = Path.Combine(_root, "mods3");
        Directory.CreateDirectory(mods);

        Assert.Throws<ToolException>(() => ModConfigWriter.SetGearHidden(mods, "..", hide: true));
    }

    [Fact]
    public void InstallTo_サブフォルダを本物のフォルダとして展開する()
    {
        // The payload zip is produced on Windows, where PowerShell 5.1's Compress-Archive
        // writes entry names with a backslash. Treating those as flat names would create a
        // file literally called "Scripts\CustomPlayerSkinMod.cs" and the mod would not load.
        string game = CreateGameFolder();

        string destination = ModPayload.InstallTo(game);

        string scripts = Path.Combine(destination, "Scripts");
        Assert.True(Directory.Exists(scripts), $"Scripts がフォルダとして展開されていない: {destination}");
        Assert.NotEmpty(Directory.GetFiles(scripts, "*.cs"));
        // No entry may have been flattened into a name that still contains a separator
        Assert.DoesNotContain(
            Directory.GetFiles(destination, "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f).Contains('\\') || Path.GetFileName(f).Contains('/'));
    }

    [Fact]
    public void InstallTo_失敗しても既存のインストールを壊さない()
    {
        string game = CreateGameFolder();
        string destination = ModPayload.InstallTo(game);

        // Mark the existing install, reinstall, and confirm the marker is gone but the
        // install is intact: the swap must replace the folder wholesale, never merge into it.
        File.WriteAllText(Path.Combine(destination, "marker.txt"), "old");

        string again = ModPayload.InstallTo(game);

        Assert.Equal(destination, again);
        Assert.False(File.Exists(Path.Combine(again, "marker.txt")), "古いファイルが残っている");
        Assert.True(File.Exists(Path.Combine(again, "ModManifest.json")));
        Assert.False(Directory.Exists(destination + ".new"), "作業用フォルダが残っている");
        Assert.False(Directory.Exists(destination + ".old"), "退避用フォルダが残っている");
    }

    [Fact]
    public void InstallTo_ゲームのModsフォルダへ展開する()
    {
        string game = CreateGameFolder();

        string destination = ModPayload.InstallTo(game);

        Assert.True(Directory.Exists(destination));
        Assert.EndsWith(Path.Combine("Mods", "CustomPlayerSkin"), destination);
        Assert.True(File.Exists(Path.Combine(destination, "ModManifest.json")), "マニフェストが展開されていない");
        Assert.True(
            Directory.GetFiles(destination, "*.cs", SearchOption.AllDirectories).Length > 0,
            "スクリプトが展開されていない");
    }

    [Fact]
    public void InstallTo_既存を入れ替える()
    {
        string game = CreateGameFolder();
        string destination = ModPayload.InstallTo(game);

        // Confirm no leftovers from the previous install remain
        string stale = Path.Combine(destination, "stale-file.txt");
        File.WriteAllText(stale, "old");

        ModPayload.InstallTo(game);

        Assert.False(File.Exists(stale), "入れ替え時に古いファイルが残っている");
        Assert.True(File.Exists(Path.Combine(destination, "ModManifest.json")));
    }

    [Fact]
    public void InstallTo_ゲームの構成が違えば例外にする()
    {
        string notGame = Path.Combine(_root, "empty");
        Directory.CreateDirectory(notGame);

        Assert.Throws<ToolException>(() => ModPayload.InstallTo(notGame));
    }

    [Fact]
    public void GetInstalled_導入前後で状態が変わる()
    {
        string game = CreateGameFolder();

        Assert.False(ModPayload.GetInstalled(game, "CustomPlayerSkin").IsInstalled);

        ModPayload.InstallTo(game);

        InstalledModInfo installed = ModPayload.GetInstalled(game, "CustomPlayerSkin");
        Assert.True(installed.IsInstalled);
        Assert.Equal("CustomPlayerSkin", installed.Name);
    }

    [Fact]
    public void 導入したものは削除機能で消せる()
    {
        // Install and remove must pair up; one working alone cannot restore the original state.
        string game = CreateGameFolder();
        ModPayload.InstallTo(game);

        RemovalPlan plan = ModUninstaller.Plan("CustomPlayerSkin", [game], []);
        Assert.Single(plan.Targets);

        RemovalResult result = ModUninstaller.Execute(plan, "CustomPlayerSkin");

        Assert.Empty(result.Failures);
        Assert.False(ModPayload.GetInstalled(game, "CustomPlayerSkin").IsInstalled);
    }

    [Fact]
    public void 作業フォルダを消せなくてもMODとしては読まれなくなる()
    {
        // .new and .old sit directly in the folder the game scans and hold a complete copy of
        // the mod, manifest and guid included. A cleanup that fails — an antivirus product
        // holding one file open is enough — would otherwise leave the game able to find two
        // mods with the same identity. Taking the manifest out first makes the leftover inert.
        string leftover = Path.Combine(_root, "CustomPlayerSkin.old");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "ModManifest.json"), "{}");

        string locked = Path.Combine(leftover, "locked.bin");
        File.WriteAllText(locked, "x");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ModPayload.TryDelete(leftover);

            Assert.True(Directory.Exists(leftover), "この検査は削除が失敗する状況を前提にしている");
            Assert.False(
                File.Exists(Path.Combine(leftover, "ModManifest.json")),
                "MOD として読まれ得る残骸になっている");
        }
    }

    [Fact]
    public void 装備表示設定は書き出しに失敗しても既存の設定を書き換えない()
    {
        // The four settings only make sense together. Writing and swapping them one at a time
        // lets a failure on the third leave shirt and trousers hidden while helmet and armour
        // stay visible, and IsGearHidden answers from the first file it can read — so the
        // screen would go on claiming everything is hidden while the game draws half of it.
        string mods = Path.Combine(_root, "mods-partial");
        string folder = Path.Combine(mods, "CustomPlayerSkin");
        Directory.CreateDirectory(folder);

        ModConfigWriter.SetGearHidden(mods, "CustomPlayerSkin", hide: false);
        Assert.False(ModConfigWriter.IsGearHidden(mods, "CustomPlayerSkin"));

        // A folder standing where the third temporary file has to go: writing it cannot succeed
        Directory.CreateDirectory(Path.Combine(folder, "General-hideHelm.json.new"));

        Assert.ThrowsAny<Exception>(
            () => ModConfigWriter.SetGearHidden(mods, "CustomPlayerSkin", hide: true));

        foreach ((string key, _) in ModConfigWriter.GearKeys)
        {
            string path = ModConfigWriter.PathFor(mods, "CustomPlayerSkin", key);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            Assert.False(document.RootElement.GetProperty("value").GetBoolean(), key);
        }

        Assert.False(ModConfigWriter.IsGearHidden(mods, "CustomPlayerSkin"));
        Assert.Empty(Directory.GetFiles(folder, "*.new"));
    }
}

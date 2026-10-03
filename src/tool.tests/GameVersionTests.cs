using System.Text;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for reading the installed game's version and deciding whether this build supports it.
///
/// Unity writes the version into globalgamemanagers, just after the company and product names.
/// The awkward part is that the file also opens with Unity's own version, which is the same
/// shape, so the parsing has to be anchored rather than take the first thing that looks right.
/// </summary>
public sealed class GameVersionTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"cks-version-test-{Guid.NewGuid():N}");

    public GameVersionTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The header as the real file has it: the Unity version at the very start, then the company
    /// and product names, then the game's version, each separated by binary padding.
    /// </summary>
    private static string Header(string unityVersion, string gameVersion) =>
        $"\0\0\0\0{unityVersion}\0\0\0\0\0Pugstorm\0\0\0\0Core Keeper\0\0\0\0\0\0\0\0{gameVersion}\0\0\0\0pugstorm";

    private string CreateGame(string unityVersion, string gameVersion)
    {
        string game = Path.Combine(_workDirectory, "Core Keeper");
        string data = Path.Combine(game, "CoreKeeper_Data");
        Directory.CreateDirectory(data);

        File.WriteAllText(Path.Combine(game, "CoreKeeper.exe"), "not really an executable");
        File.WriteAllText(
            Path.Combine(data, "globalgamemanagers"),
            Header(unityVersion, gameVersion),
            new UTF8Encoding(false));

        return game;
    }

    // ------------------------------------------------------------ Parsing

    /// <summary>
    /// The Unity version sits at the very start of the file and has the same shape as the game's.
    /// Taking the first match would report the engine version as the game version.
    /// </summary>
    [Fact]
    public void FindVersion_Unityのバージョンを誤って返さない()
    {
        string? version = GameVersion.FindVersion(Header("6000.0.59f2", "1.2.1.5-8be0"));

        Assert.Equal("1.2.1.5-8be0", version);
    }

    [Theory]
    [InlineData("1.2.1.5-8be0", "1.2.1.5-8be0")]
    [InlineData("1.2.1", "1.2.1")]
    [InlineData("1.2.1.5", "1.2.1.5")]
    [InlineData("10.20.30-abc123", "10.20.30-abc123")]
    public void FindVersion_さまざまな表記を読める(string written, string expected)
    {
        Assert.Equal(expected, GameVersion.FindVersion(Header("6000.0.59f2", written)));
    }

    /// <summary>
    /// Unity writes several version fields into the same block. In the real file two of them sit
    /// between the product name and the one wanted, and they are currently passed over only
    /// because they happen to be two-component strings. A three-component one must not win.
    /// </summary>
    [Fact]
    public void FindVersion_手前にある別のバージョン欄に釣られない()
    {
        string header =
            "\0\0\0\06000.0.59f2\0\0\0\0\0Pugstorm\0\0\0\0Core Keeper\0\0\0" +
            "1.0.0.0\0\0\0public.app-category.games\0\0\0" +
            "1.2.1.5-8be0\0\0\0\0";

        Assert.Equal("1.2.1.5-8be0", GameVersion.FindVersion(header));
    }

    /// <summary>
    /// Anything found far past the anchor belongs to an unrelated part of the file. Returning it
    /// would produce a plausible wrong version, which reads as "your game is unsupported" rather
    /// than "the version could not be determined".
    /// </summary>
    [Fact]
    public void FindVersion_目印から遠すぎるものは採らない()
    {
        string header =
            "\0\0\0\06000.0.59f2\0\0\0\0\0Pugstorm\0\0\0\0Core Keeper\0" +
            new string('\0', 8192) +
            "1.2.1.5-8be0\0";

        Assert.Null(GameVersion.FindVersion(header));
    }

    [Fact]
    public void FindVersion_目印が無ければ読めないと答える()
    {
        Assert.Null(GameVersion.FindVersion("\0\0\0\0 6000.0.59f2 \0\0\0 1.2.1.5-8be0"));
    }

    // ------------------------------------------------------------ Reading a folder

    [Fact]
    public void Read_ゲームフォルダからバージョンを読む()
    {
        string game = CreateGame("6000.0.59f2", "1.2.1.5-8be0");

        Assert.Equal("1.2.1.5-8be0", GameVersion.Read(game));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Z:\\no\\such\\place")]
    public void Read_読めない場合はnullを返し例外にしない(string? directory)
    {
        Assert.Null(GameVersion.Read(directory));
    }

    [Fact]
    public void Read_globalgamemanagersが無ければnull()
    {
        string game = Path.Combine(_workDirectory, "empty");
        Directory.CreateDirectory(Path.Combine(game, "CoreKeeper_Data"));

        Assert.Null(GameVersion.Read(game));
    }

    // ------------------------------------------------------------ Support decision

    /// <summary>
    /// The build suffix moves with every patch, while the parts the mod depends on do not, so
    /// support is decided on the leading three numbers. That is also the granularity the game
    /// itself uses: Manager.versionRegex is ^(\d+)\.(\d+)\.(\d+).
    /// </summary>
    [Fact]
    public void IsSupported_ビルド接尾辞が違っても対応とみなす()
    {
        string supported = GameVersion.SupportedVersions[0];

        Assert.True(GameVersion.IsSupported(supported + ".9-ffff"));
        Assert.True(GameVersion.IsSupported(supported));
    }

    [Theory]
    [InlineData("99.0.0")]
    [InlineData("0.0.1-abc")]
    public void IsSupported_未検証のバージョンは拒否する(string version)
    {
        Assert.False(GameVersion.IsSupported(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a version")]
    [InlineData("1.2")]
    public void IsSupported_バージョンとして読めないものは拒否する(string? version)
    {
        Assert.False(GameVersion.IsSupported(version));
    }

    [Fact]
    public void Check_対応バージョンならSupportedを返す()
    {
        string game = CreateGame("6000.0.59f2", GameVersion.SupportedVersions[0] + ".5-8be0");

        GameVersionCheck check = GameVersion.Check(game);

        Assert.Equal(GameVersionState.Supported, check.State);
        Assert.NotNull(check.Version);
    }

    [Fact]
    public void Check_未対応バージョンはバージョンを添えて拒否する()
    {
        string game = CreateGame("6000.0.59f2", "99.0.0-future");

        GameVersionCheck check = GameVersion.Check(game);

        Assert.Equal(GameVersionState.Unsupported, check.State);

        // The message shows this to the user, so it has to be the version actually found
        Assert.Equal("99.0.0-future", check.Version);
    }

    /// <summary>
    /// Not the same as unsupported: the installation may be fine and only the reading broken.
    /// The two are kept apart so the user is told which of the two happened.
    /// </summary>
    [Fact]
    public void Check_読めない場合はUnknownでありUnsupportedではない()
    {
        string game = Path.Combine(_workDirectory, "unreadable");
        Directory.CreateDirectory(Path.Combine(game, "CoreKeeper_Data"));

        GameVersionCheck check = GameVersion.Check(game);

        Assert.Equal(GameVersionState.Unknown, check.State);
        Assert.Null(check.Version);
    }

    [Fact]
    public void SupportedVersions_少なくとも1件は登録されている()
    {
        Assert.NotEmpty(GameVersion.SupportedVersions);
        Assert.All(GameVersion.SupportedVersions, v => Assert.NotNull(GameVersion.MajorMinorPatch(v)));
    }

    /// <summary>
    /// The game version is recorded in two places that nothing else ties together: the layout
    /// data says which builds the measurements have been checked against, and
    /// supported-versions.json says which builds the tool will run against without warning.
    /// Updating one and not the other is the whole risk. Adding a version to the supported list
    /// without checking it is the worse direction, because it certifies as verified a build
    /// nobody looked at.
    /// </summary>
    [Fact]
    public void 対応表に載る版はすべて実測で確かめてある()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();

        Assert.All(
            layout.VerifiedOn,
            v => Assert.True(
                GameVersion.MajorMinorPatch(v) is not null,
                $"レイアウトの verifiedVersions に版番号として読めないものがある: {v}"));

        // The build the numbers were taken from has to stay in the list. Dropping it would let
        // the file claim a newer build and quietly lose the one it was actually measured on.
        Assert.Contains(layout.GameVersion, layout.VerifiedOn);

        string? measured = GameVersion.MajorMinorPatch(layout.GameVersion);

        Assert.True(
            GameVersion.IsSupported(layout.GameVersion),
            $"レイアウトは {layout.GameVersion} から実測されているのに、対応表 " +
            $"({string.Join(", ", GameVersion.SupportedVersions)}) に {measured} が無い。" +
            "data/player-body-layout.json と data/supported-versions.json のどちらかが更新漏れ。");

        // And the other way, which the comment above calls the worse direction: a version listed
        // as supported but never checked tells the user their build has been verified when it
        // has not. Widening the list means checking that build first - the measurements may well
        // come out identical, as 1.2.1 and 1.3.0.1 did, and then both belong in verifiedVersions.
        HashSet<string> verified =
            [.. layout.VerifiedOn.Select(GameVersion.MajorMinorPatch).OfType<string>()];

        string[] unverified =
            [.. GameVersion.SupportedVersions.Where(
                v => GameVersion.MajorMinorPatch(v) is not { } key || !verified.Contains(key))];

        Assert.True(
            unverified.Length == 0,
            $"実測で確かめていない版が対応表に載っている: {string.Join(", ", unverified)}。" +
            $"確かめてあるのは {string.Join(", ", layout.VerifiedOn)} のみ。対応を広げるなら、その版で " +
            "scripts/generate-layout.ps1 と generate-parts-layout.ps1 を実行して突き合わせ、" +
            "data/player-body-layout.json と data/player-parts.json の verifiedVersions に足すこと。");
    }

    /// <summary>
    /// A definition written before verifiedVersions existed still has to say which build it
    /// came from, or the check above would pass on an empty list and certify nothing.
    /// </summary>
    [Fact]
    public void VerifiedOn_verifiedVersionsが無ければ実測元だけを返す()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded() with { VerifiedVersions = [] };

        Assert.Equal([layout.GameVersion], layout.VerifiedOn);
    }

    /// <summary>
    /// The two measurement files name the builds they were checked against, and only the layout's
    /// list is read by anything. The parts file carries the same list so that it says so too, but
    /// nothing tied the two together: recording 1.3.0.2 in one and forgetting the other passed
    /// every test and left the parts file claiming a narrower check than was made.
    /// </summary>
    [Fact]
    public void 実測の記録は配置とパーツで同じ版を挙げている()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();

        using Stream? stream = typeof(CoreKeeperSkinTool.Layout.SheetLayout).Assembly
            .GetManifestResourceStream("player-parts.json");
        Assert.NotNull(stream);

        using System.Text.Json.JsonDocument parts = System.Text.Json.JsonDocument.Parse(stream);

        // Said plainly rather than left to GetProperty's KeyNotFoundException: regenerating the
        // file with scripts/generate-parts-layout.ps1 used to drop the list altogether
        Assert.True(
            parts.RootElement.TryGetProperty("verifiedVersions", out System.Text.Json.JsonElement listed),
            "data/player-parts.json に verifiedVersions が無い。生成スクリプトで作り直したときは、確かめた版の一覧を書き戻すこと。");

        string[] partsVerified = [.. listed.EnumerateArray().Select(v => v.GetString() ?? string.Empty)];

        Assert.Equal(layout.VerifiedOn, partsVerified);
    }

    /// <summary>
    /// 1.3.0.3-2aca (Steam build 25537589, 2026-09-29) was measured the way 1.3.0.2 was: the player
    /// bundles are byte-identical, all 299 part boxes and the sheet layout match, the mod compiles
    /// and passes the game's code check, and the game itself loaded it. Without the record,
    /// "cks layout" listed the builds it was checked against and this machine's was not among them.
    /// </summary>
    [Fact]
    public void 実測の記録に1_3_0_3_2acaが載っている()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();

        Assert.Contains("1.3.0.3-2aca", layout.VerifiedOn);
    }

    /// <summary>
    /// 1.3.0.4-511d (Steam build 25625027, 2026-10-02): the sprite bundles are byte-identical to
    /// 1.3.0.3, and the bundle holding the player prefab gained six objects unrelated to it - the nine
    /// layers, their drawing order, the animator, its clips and the skin colours came out identical.
    /// The code that changed is three methods of the continuous-attack state. The mod compiles and
    /// passes the game's own code check, and the game loaded it, drew with it and reloaded a picture
    /// replaced while it ran.
    /// </summary>
    [Fact]
    public void 実測の記録に1_3_0_4_511dが載っている()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();

        Assert.Contains("1.3.0.4-511d", layout.VerifiedOn);
    }

    /// <summary>
    /// The tool's version follows the game's major.minor.patch - 1.3.0 for Core Keeper 1.3.0.x - so
    /// the number says which game it is made for (the user's decision of 2026-10-02). It is set once,
    /// in src/Directory.Build.props, and has to name the newest game version the tool supports.
    /// </summary>
    [Fact]
    public void 製品の版は対応するゲームの最新の版と同じ番号()
    {
        Version product = typeof(GameVersion).Assembly.GetName().Version!;

        Version newest = GameVersion.SupportedVersions
            .Select(GameVersion.MajorMinorPatch)
            .OfType<string>()
            .Select(Version.Parse)
            .Max()!;

        Assert.Equal(newest.ToString(3), product.ToString(3));
    }

    /// <summary>
    /// The supported list is matched on major.minor.patch, so "1.3.0.1" in it stands for every
    /// 1.3.0 build. Shown as written, the warning read as if 1.3.0.2 and 1.3.0.3 were not supported.
    /// </summary>
    [Fact]
    public void 対応版の表示は照合と同じ粒度にする()
    {
        Assert.Equal("1.2.1.x, 1.3.0.x", GameVersion.DescribeSupported(["1.2.1", "1.3.0.1"]));
        Assert.Equal("1.3.0.x", GameVersion.DescribeSupported(["1.3.0.1", "1.3.0.2-182b"]));
        Assert.Equal("oddly", GameVersion.DescribeSupported(["oddly"]));
    }
}

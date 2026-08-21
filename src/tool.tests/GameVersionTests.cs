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
    /// data says which build the measurements were taken from, and supported-versions.json says
    /// which builds the tool will run against without warning. Updating one and not the other is
    /// the whole risk. Adding a version to the supported list without re-measuring is the worse
    /// direction, because it certifies as verified a build nobody checked.
    /// </summary>
    [Fact]
    public void 実測元のバージョンが対応表に載っている()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();

        string? measured = GameVersion.MajorMinorPatch(layout.GameVersion);

        Assert.True(
            measured is not null,
            $"レイアウトの gameVersion が版番号として読めない: {layout.GameVersion}");

        Assert.True(
            GameVersion.IsSupported(layout.GameVersion),
            $"レイアウトは {layout.GameVersion} から実測されているのに、対応表 " +
            $"({string.Join(", ", GameVersion.SupportedVersions)}) に {measured} が無い。" +
            "data/player-body-layout.json と data/supported-versions.json のどちらかが更新漏れ。");

        // And the other way, which the comment above calls the worse direction and which nothing
        // was actually checking: a version listed as supported but never measured tells the user
        // their build has been verified when it has not. One set of measurements ships, so the
        // list may name exactly the build it came from. Wanting to support several versions from
        // one measurement is a real decision to make, and this failing is where to make it.
        string[] unmeasured =
            [.. GameVersion.SupportedVersions.Where(v => GameVersion.MajorMinorPatch(v) != measured)];

        Assert.True(
            unmeasured.Length == 0,
            $"実測していない版が対応表に載っている: {string.Join(", ", unmeasured)}。" +
            $"実測元は {measured} のみ。対応を広げるなら、その版で " +
            "scripts/generate-layout.ps1 と generate-parts-layout.ps1 を実行して実測し直すこと。");
    }
}

using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for finding the game.
///
/// Steam installs into whichever library folder the user chose, on any drive, and records those
/// folders in libraryfolders.vdf. Reading that file correctly is the whole of the search, so it
/// is what these tests cover; the registry lookup around it cannot be exercised here.
/// </summary>
public sealed class GameLocatorTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"cks-locator-test-{Guid.NewGuid():N}");

    public GameLocatorTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    /// <summary>The format Steam writes today: one "path" per library, separators escaped twice.</summary>
    private const string CurrentFormat = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"277286956"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		""
        		"apps"
        		{
        			"1621690"		"1060980742"
        		}
        	}
        }
        """;

    /// <summary>The format older Steam versions wrote: libraries under numbered keys.</summary>
    private const string LegacyFormat = """
        "LibraryFolders"
        {
        	"TimeNextStatsReport"		"1234567890"
        	"ContentStatsID"		"-1234567890"
        	"1"		"D:\\SteamLibrary"
        	"2"		"E:\\Games\\Steam"
        }
        """;

    [Fact]
    public void ParseLibraryPaths_別ドライブのライブラリを読み取る()
    {
        IReadOnlyList<string> paths = GameLocator.ParseLibraryPaths(CurrentFormat);

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], paths);
    }

    /// <summary>
    /// The current format also has numbered keys, inside its "apps" blocks, whose values are byte
    /// counts. Reading those as folders would be nonsense, so the legacy form is only consulted
    /// when no "path" entry exists at all.
    /// </summary>
    [Fact]
    public void ParseLibraryPaths_appsブロックの数値をパスと誤認しない()
    {
        IReadOnlyList<string> paths = GameLocator.ParseLibraryPaths(CurrentFormat);

        Assert.DoesNotContain("1060980742", paths);
        Assert.DoesNotContain("277286956", paths);
    }

    [Fact]
    public void ParseLibraryPaths_古い形式も読める()
    {
        IReadOnlyList<string> paths = GameLocator.ParseLibraryPaths(LegacyFormat);

        Assert.Equal([@"D:\SteamLibrary", @"E:\Games\Steam"], paths);
    }

    [Fact]
    public void ParseLibraryPaths_同じライブラリを重複させない()
    {
        IReadOnlyList<string> paths = GameLocator.ParseLibraryPaths("""
            "path"		"D:\\SteamLibrary"
            "path"		"d:\\steamlibrary"
            """);

        Assert.Single(paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a vdf at all")]
    [InlineData("\"libraryfolders\"\n{\n}")]
    public void ParseLibraryPaths_読めない内容でも例外にしない(string text)
    {
        Assert.Empty(GameLocator.ParseLibraryPaths(text));
    }

    // ------------------------------------------------------------ Recognising the folder

    private string CreateGameFolder(string name)
    {
        string directory = Path.Combine(_workDirectory, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "CoreKeeper.exe"), "not really an executable");
        return directory;
    }

    [Fact]
    public void IsGameDirectory_実行ファイルの有無で判定する()
    {
        string game = CreateGameFolder("Core Keeper");

        Assert.True(GameLocator.IsGameDirectory(game));
    }

    [Fact]
    public void IsGameDirectory_名前だけ同じフォルダは認めない()
    {
        string lookalike = Path.Combine(_workDirectory, "Core Keeper (copy)");
        Directory.CreateDirectory(lookalike);

        Assert.False(GameLocator.IsGameDirectory(lookalike));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Z:\\no\\such\\place")]
    [InlineData("|invalid|")]
    public void IsGameDirectory_不正な指定でも例外にしない(string? directory)
    {
        Assert.False(GameLocator.IsGameDirectory(directory));
    }

    /// <summary>
    /// A folder the user pointed at has to win. Detection exists to save them the trouble, and
    /// without this an installation Steam's records do not mention could never be reached.
    /// </summary>
    [Fact]
    public void FindGameInstallations_手動指定を先頭に置く()
    {
        string game = CreateGameFolder("Moved Core Keeper");

        IReadOnlyList<string> found = GameLocator.FindGameInstallations(game);

        Assert.Equal(game, found[0]);
    }

    [Fact]
    public void FindGameInstallations_ゲームの無いフォルダの手動指定は採用しない()
    {
        string empty = Path.Combine(_workDirectory, "empty");
        Directory.CreateDirectory(empty);

        Assert.DoesNotContain(empty, GameLocator.FindGameInstallations(empty));
    }

    // ------------------------------------------------------------ Folders that cannot be listed

    /// <summary>
    /// Makes a folder that exists but cannot be listed through its ordinary path: Windows drops a
    /// trailing dot when it resolves a path, so "Epic." is looked up as "Epic", which is not there.
    /// Measured on .NET 9: listing it throws DirectoryNotFoundException, as a broken junction does.
    /// </summary>
    private static void CreateUnlistable(string root, params string[] below) =>
        Directory.CreateDirectory(@"\\?\" + Path.Combine([root, .. below]));

    /// <summary>
    /// The data root holds the game's own folders beside the platforms - Sentry and SentryNative
    /// on this machine - and one of them that could not be listed took every character with it:
    /// detection threw, and neither the window nor the command line found anyone at all.
    /// </summary>
    [Fact]
    public void 列挙できないフォルダが1つあっても他のアカウントを見つける()
    {
        string root = Path.Combine(_workDirectory, "data");
        Directory.CreateDirectory(Path.Combine(root, "Steam", "123", "saves"));
        CreateUnlistable(root, "Epic.", "456");

        IReadOnlyList<GameLocator.UserDataDirectory> found = GameLocator.FindUserDataDirectories(root);

        GameLocator.UserDataDirectory user = Assert.Single(found);
        Assert.Equal("Steam", user.Platform);
        Assert.Equal("123", user.UserId);
    }

    /// <summary>
    /// When the folder that could not be listed may be the account itself - nothing readable has
    /// saves or mods - the failure is the answer. Skipping it would report "start the game once to
    /// create it" to a player whose data is right there but unreadable.
    /// </summary>
    [Fact]
    public void アカウントが1つも読めなければこれまでどおり失敗する()
    {
        string root = Path.Combine(_workDirectory, "data");
        CreateUnlistable(root, "Steam.", "123", "saves");
        Directory.CreateDirectory(Path.Combine(root, "Sentry", "abc"));

        Assert.Throws<DirectoryNotFoundException>(() => GameLocator.FindUserDataDirectories(root));
    }

    // ------------------------------------------------------------ Several accounts

    /// <summary>Lays out one account's settings folder with the times given.</summary>
    private ModConfigLocation Account(string id, DateTime? readmeWritten, DateTime folderWritten)
    {
        string mods = Path.Combine(_workDirectory, "Steam", id, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "CustomPlayerSkin"));

        if (readmeWritten is { } written)
        {
            string readme = Path.Combine(mods, "README.txt");
            File.WriteAllText(readme, "MODS ARE NOT LOADED FROM HERE");
            File.SetLastWriteTimeUtc(readme, written);
        }

        // Set last: creating anything inside moves the folder's own time
        Directory.SetLastWriteTimeUtc(mods, folderWritten);
        return new ModConfigLocation(mods, "Steam", id);
    }

    /// <summary>
    /// "The one used last" has to mean the account the game last started with. The game rewrites
    /// modsREADME.txt every time it starts (Manager.EarlyInit, measured on 1.3.0.2), and this tool
    /// never touches that file. The folder's own time moves whenever anything directly inside it
    /// is created or removed - including this tool placing or removing CustomPlayerSkin - so
    /// ordering by it put the account the tool had just written to first, and after "Remove mod"
    /// the tool went on to use the account the player does not play on.
    /// </summary>
    [Fact]
    public void 設定フォルダはゲームが最後に起動したアカウントを先に並べる()
    {
        DateTime day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Played on "played" last; the tool has just written into "other"
        ModConfigLocation played = Account("played", readmeWritten: day.AddDays(2), folderWritten: day.AddDays(2));
        ModConfigLocation other = Account("other", readmeWritten: day.AddDays(1), folderWritten: day.AddDays(3));

        IReadOnlyList<ModConfigLocation> ordered = GameLocator.MostRecentlyUsedFirst([other, played]);

        Assert.Equal(played.ModsDirectory, ordered[0].ModsDirectory);
    }

    /// <summary>
    /// A folder the game has not written README.txt into keeps the old key, so nothing changes for
    /// an account whose mods folder was made some other way.
    /// </summary>
    [Fact]
    public void READMEが無い設定フォルダはフォルダの時刻で並べる()
    {
        DateTime day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        ModConfigLocation older = Account("older", readmeWritten: null, folderWritten: day.AddDays(1));
        ModConfigLocation newer = Account("newer", readmeWritten: null, folderWritten: day.AddDays(2));

        IReadOnlyList<ModConfigLocation> ordered = GameLocator.MostRecentlyUsedFirst([older, newer]);

        Assert.Equal(newer.ModsDirectory, ordered[0].ModsDirectory);
    }
}

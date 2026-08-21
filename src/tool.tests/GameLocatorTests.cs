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
}

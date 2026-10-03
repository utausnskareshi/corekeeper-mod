namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// What "install --list" says about a mods folder given with --mods-dir.
///
/// A folder with no saves folder beside it - the game's own StreamingAssets\Mods, or one level
/// too deep - was listed as having no characters, "ゲームで1体以上作成すること", with exit code 0.
/// Somebody who already has characters could not tell that the folder was the mistake, while
/// install on the same folder said exactly that.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class InstallListTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), $"cks-list-{Guid.NewGuid():N}");

    public InstallListTests() => Directory.CreateDirectory(_work);

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

    private static (int Exit, string Output) Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;
        using StringWriter output = new();

        try
        {
            Console.SetOut(output);
            Console.SetError(TextWriter.Null);
            int exit = Program.Main(args);
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }

    [Fact]
    public void セーブフォルダが隣に無ければキャラクターを作れとは言わない()
    {
        string mods = Path.Combine(_work, "User", "mods");
        Directory.CreateDirectory(mods);

        (int _, string output) = Run("install", "--list", "--mods-dir", mods);

        Assert.Contains("セーブフォルダが見つからない", output);
        Assert.Contains(Path.Combine(_work, "User", "saves"), output);
        Assert.DoesNotContain("ゲームで1体以上作成すること", output);
    }

    [Fact]
    public void セーブフォルダが空ならこれまでどおりキャラクターが無いと言う()
    {
        string mods = Path.Combine(_work, "User", "mods");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(_work, "User", "saves"));

        (int exit, string output) = Run("install", "--list", "--mods-dir", mods);

        Assert.Equal(0, exit);
        Assert.Contains("操作キャラクターなし（ゲームで1体以上作成すること）", output);
    }

    [Fact]
    public void 自動で見つけたフォルダにはこれまでどおりキャラクターを作れと言う()
    {
        // A detected folder is the game's own <id>\mods, made at its first start, while saves
        // appears only with the first character: a new player who has not made one yet was told
        // to fix a --mods-dir they never gave. Read off the source, because detection starts from
        // the real user profile, which no test may point elsewhere.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "tool", "Program.cs")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
        string source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "tool", "Program.cs"));

        // From the listing's heading, because install itself has the same sentence further down
        int listing = source.IndexOf("MOD 設定フォルダの候補:", StringComparison.Ordinal);
        Assert.True(listing >= 0, "install --list の一覧が見つからない");
        int message = source.IndexOf("--mods-dir には、その隣に saves フォルダがある mods フォルダを指定すること。", listing, StringComparison.Ordinal);
        Assert.True(message >= 0, "install --list の案内が見つからない");

        // The nearest condition above the message is the one that guards it
        int condition = source.LastIndexOf("if (", message, StringComparison.Ordinal);
        string guard = source[condition..source.IndexOf('\n', condition)];
        Assert.Contains("cmd.HasOption(\"mods-dir\")", guard);
        Assert.Contains("!Directory.Exists(saves)", guard);
    }
}

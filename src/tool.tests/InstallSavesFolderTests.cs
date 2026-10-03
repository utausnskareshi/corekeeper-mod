namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// What install - without --list - says when the settings folder has no saves folder beside it.
///
/// It told everyone to fix --mods-dir, a player who never gave one included. A folder found by
/// detection is the game's own &lt;id&gt;\mods, which the game makes at its first start, while saves
/// appears only with the first character: there a missing saves folder means "no character yet",
/// and advice about an option never used leads nowhere. --list was put right the same way before
/// (InstallListTests); install kept the old message.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class InstallSavesFolderTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), $"cks-saves-{Guid.NewGuid():N}");

    public InstallSavesFolderTests() => Directory.CreateDirectory(_work);

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

    [Fact]
    public void 指定したmodsdirの隣にセーブフォルダが無ければそう言う()
    {
        string game = Path.Combine(_work, "Core Keeper");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "CoreKeeper.exe"), "not really an executable");

        string mods = Path.Combine(_work, "User", "mods");
        Directory.CreateDirectory(mods);

        // The picture is never read: the missing saves folder stops the run first
        (int exit, string error) = Run(
            "install", "--input", Path.Combine(_work, "sheet.png"), "--mods-dir", mods, "--game-dir", game);

        Assert.Equal(1, exit);
        Assert.Contains("セーブフォルダが見つからない", error);
        Assert.Contains(Path.Combine(_work, "User", "saves"), error);
        Assert.Contains("--mods-dir には", error);
    }

    [Fact]
    public void 自動で見つけた設定フォルダにはmodsdirの案内を出さない()
    {
        // Read off the source, because detection starts from the real user profile, which no
        // test may point elsewhere - as InstallListTests does for --list
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "tool", "Program.cs")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
        string source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "tool", "Program.cs"));

        int install = source.IndexOf("private static void InstallSheet(", StringComparison.Ordinal);
        Assert.True(install >= 0, "InstallSheet が見つからない");
        int message = source.IndexOf("セーブフォルダが見つからないため配置できない。", install, StringComparison.Ordinal);
        Assert.True(message >= 0, "install の案内が見つからない");

        // The nearest condition above the message is the one that guards it
        int condition = source.LastIndexOf("if (", message, StringComparison.Ordinal);
        string guard = source[condition..source.IndexOf('\n', condition)];
        Assert.Contains("cmd.HasOption(\"mods-dir\")", guard);
        Assert.Contains("!Directory.Exists(saves)", guard);
    }

    private static (int Exit, string Error) Run(params string[] args)
    {
        TextWriter outWriter = Console.Out;
        TextWriter errorWriter = Console.Error;
        using StringWriter error = new();

        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(error);
            int exit = Program.Main(args);
            return (exit, error.ToString());
        }
        finally
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }
}

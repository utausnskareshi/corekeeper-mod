using CoreKeeperSkinTool.Gui.Localization;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Tests for persisting the language choice.
///
/// Everything here works inside a temporary folder, pointed at by the settings override, so the
/// real user settings are never touched.
/// </summary>
public sealed class LanguageSettingsTests : IDisposable
{
    private readonly string? _previous =
        Environment.GetEnvironmentVariable(LanguageSettings.DirectoryOverrideVariable);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cks-settings-test-{Guid.NewGuid():N}");

    public LanguageSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable(LanguageSettings.DirectoryOverrideVariable, _directory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LanguageSettings.DirectoryOverrideVariable, _previous);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void 保存した言語を読み戻せる()
    {
        LanguageSettings.Save("en");

        Assert.True(File.Exists(SettingsPath));
        Assert.Equal("en", LanguageSettings.Load());
    }

    [Fact]
    public void 設定ファイルが無ければnullを返す()
    {
        Assert.Null(LanguageSettings.Load());
    }

    [Fact]
    public void 壊れた設定ファイルでも例外を投げずnullを返す()
    {
        // A corrupt file must never prevent the application from starting
        File.WriteAllText(SettingsPath, "{ this is not json");

        Assert.Null(LanguageSettings.Load());
    }

    [Fact]
    public void 中身が空のJSONでもnullを返す()
    {
        File.WriteAllText(SettingsPath, "{}");

        Assert.Null(LanguageSettings.Load());
    }

    [Fact]
    public void 保存先フォルダが無ければ作成する()
    {
        string nested = Path.Combine(_directory, "nested", "deeper");
        Environment.SetEnvironmentVariable(LanguageSettings.DirectoryOverrideVariable, nested);

        LanguageSettings.Save("ja");

        Assert.Equal("ja", LanguageSettings.Load());
    }

    [Fact]
    public void 上書き保存で最後の値が残る()
    {
        LanguageSettings.Save("ja");
        LanguageSettings.Save("en");

        Assert.Equal("en", LanguageSettings.Load());
    }

    [Fact]
    public void 読めない設定ファイルでも保存はできる()
    {
        // Choosing the game folder is how a user recovers when detection finds nothing, and it
        // is stored through exactly this path. Refusing to write when the existing file cannot
        // be read made that button do nothing at all - no error, no change, no way forward -
        // and on a machine where detection comes up empty that leaves the application unusable.
        File.WriteAllText(SettingsPath, "{ this is not json");

        GamePathSettings.Save(@"D:\somewhere\Core Keeper");

        Assert.Equal(@"D:\somewhere\Core Keeper", GamePathSettings.Load());

        // The unreadable copy is kept rather than thrown away, so it can still be looked at
        Assert.True(File.Exists(SettingsPath + ".bad"), "壊れた設定ファイルが退避されていない");
    }

    [Fact]
    public void 読み取り専用の設定ファイルでも保存できる()
    {
        // Readable and unwritable at once. Every read succeeds, so the "put the damaged file
        // aside" path never runs, and every write fails without a word - permanently. Choosing
        // the game folder is stored through here, and it is the only way out when detection
        // finds nothing, so a save that can never land takes the last way out with it.
        GamePathSettings.Save(null);
        File.SetAttributes(SettingsPath, FileAttributes.ReadOnly);

        try
        {
            GamePathSettings.Save(@"D:\somewhere\Core Keeper");

            Assert.Equal(@"D:\somewhere\Core Keeper", GamePathSettings.Load());
        }
        finally
        {
            if (File.Exists(SettingsPath))
            {
                File.SetAttributes(SettingsPath, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void 設定ファイルと同名のフォルダがあっても保存できる()
    {
        // File.Exists is false for a folder, so this reads as an ordinary first run: nothing to
        // put aside, and a move that can never succeed. Same dead end, reached another way.
        Directory.CreateDirectory(SettingsPath);

        GamePathSettings.Save(@"D:\somewhere\Core Keeper");

        Assert.False(Directory.Exists(SettingsPath), "同名のフォルダが残っている");
        Assert.Equal(@"D:\somewhere\Core Keeper", GamePathSettings.Load());
    }

    [Fact]
    public void 壊れた設定を書き直したあとも他の項目を保てる()
    {
        File.WriteAllText(SettingsPath, "{ broken");

        GamePathSettings.Save(@"D:\game");
        LanguageSettings.Save("en");

        // Once the file is readable again, the ordinary read-modify-write applies and the
        // earlier value survives the later one
        Assert.Equal(@"D:\game", GamePathSettings.Load());
        Assert.Equal("en", LanguageSettings.Load());
    }

    [Fact]
    public void 書き込めない場所では保存が偽を返す()
    {
        // Why the return value exists at all. The startup gate re-reads the file to decide what
        // to show, so a save that cannot land hands back the identical fatal notice - and a fatal
        // notice hides the accept button, leaving the folder button as the only thing to press
        // and nothing on screen ever changing. Reporting the failure is the only way out.
        //
        // A file standing where a folder has to be created is the general case the two guards
        // inside the writer do not cover: they clear a read-only settings.json and a folder of
        // that name, both of which are about the destination, not about the folder above it.
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");
        Environment.SetEnvironmentVariable(
            LanguageSettings.DirectoryOverrideVariable,
            Path.Combine(blocker, "inner"));

        Assert.False(GamePathSettings.Save(@"D:\somewhere\Core Keeper"));
    }

    [Fact]
    public void 保存できたときは真を返す()
    {
        // The other direction, and the reason this matters: a writer that always reported failure
        // would put the cannot-save message on screen every time, including when it worked, and
        // the startup gate would stop re-checking the notices it is there to re-check.
        Assert.True(GamePathSettings.Save(@"D:\somewhere\Core Keeper"));
        Assert.Equal(@"D:\somewhere\Core Keeper", GamePathSettings.Load());

        // Clearing it is a write too, and the view model reports that one the same way
        Assert.True(GamePathSettings.Save(null));
        Assert.Null(GamePathSettings.Load());
    }
}

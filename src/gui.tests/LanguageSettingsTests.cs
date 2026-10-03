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
    public void 書き込めない場所では同意と言語の保存も偽を返す()
    {
        // Both used to throw the answer away, so the terms were asked again at every start and the
        // language went back to the default, with no reason given anywhere (the test campaign of
        // 2026-10-01). The caller needs the answer to say so.
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");
        Environment.SetEnvironmentVariable(
            LanguageSettings.DirectoryOverrideVariable,
            Path.Combine(blocker, "inner"));

        Assert.False(DisclaimerSettings.Accept("1.0.0.0"));
        Assert.False(LanguageSettings.Save("en"));
    }

    [Fact]
    public void 同意と言語は保存できたら真を返す()
    {
        Assert.True(DisclaimerSettings.Accept("1.0.0.0"));
        Assert.True(LanguageSettings.Save("en"));
    }

    [Fact]
    public void 同意を保存できなければ通知窓でそう言ってから進む()
    {
        // Read off the window's source: a real window needs a running platform
        string window = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "StartupNoticeWindow.axaml.cs"));
        int start = window.IndexOf("private void OnAcceptClicked(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnAcceptClicked が見つからない");
        string body = window[start..window.IndexOf("private async void OnFolderClicked(", start, StringComparison.Ordinal)];

        int accept = body.IndexOf("DisclaimerSettings.Accept(", StringComparison.Ordinal);
        int told = body.IndexOf("\"startup.settingsNotSaved\"", StringComparison.Ordinal);
        int next = body.IndexOf("ShowNext(", StringComparison.Ordinal);
        Assert.True(accept >= 0 && told > accept && told < next, "保存できなかったことを進む前に言っていない");
    }

    [Fact]
    public void 版の警告でフォルダの選び直しに失敗しても警告の本文を残す()
    {
        // The folder's problem replaced the messages outright. A fatal notice has nothing to accept
        // so that was harmless there, but the version warning kept its "continue" button: what it
        // warned about - an unsupported version, a look that may break, back up the saves - was
        // gone, and the user went on agreeing to something no longer on screen (the test campaign
        // of 2026-10-01). A warning keeps the text it was shown with; a fatal one is replaced.
        string window = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "StartupNoticeWindow.axaml.cs"));

        int start = window.IndexOf("private async void OnFolderClicked(", StringComparison.Ordinal);
        string folder = window[start..window.IndexOf("private void ShowNext(", start, StringComparison.Ordinal)];
        Assert.Contains("ShowFolderProblem(Loc.Instance.EveryLanguage(\"status.gamePathInvalid\"", folder, StringComparison.Ordinal);
        Assert.Contains("ShowFolderProblem(Loc.Instance.EveryLanguage(\"status.gamePathNotSaved\"", folder, StringComparison.Ordinal);

        int helper = window.IndexOf("private void ShowFolderProblem(", StringComparison.Ordinal);
        Assert.True(helper >= 0, "ShowFolderProblem が見つからない");
        string body = window[helper..];
        Assert.Contains("StartupNoticeKind.Fatal", body[..body.IndexOf('}')], StringComparison.Ordinal);
        Assert.Contains("_shownMessages", body[..body.IndexOf('}')], StringComparison.Ordinal);
    }

    [Fact]
    public void 言語を保存できなければ画面でそう言う()
    {
        string loc = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Localization", "Loc.cs"));
        Assert.Contains("LastLanguageSaved = LanguageSettings.Save(", loc, StringComparison.Ordinal);

        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "private void OnLanguageChanged()");
        Assert.Contains("Loc.Instance.LastLanguageSaved", body, StringComparison.Ordinal);
        Assert.Contains("\"status.languageNotSaved\"", body, StringComparison.Ordinal);
    }

    private static string NoticeWindow() => File.ReadAllText(Path.Combine(
        TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "StartupNoticeWindow.axaml.cs"));

    private static string LanguageChanged() => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        "private void OnLanguageChanged()");

    [Fact]
    public void 同意を保存できなくても2回目の押下で進む()
    {
        // Saying it once is what lets the next press go on. Without the flag every press failed to
        // save again and put the same paragraph over the last one, and on a folder that cannot be
        // written the terms could never be passed (the final review of 2026-10-02, X59a)
        string body = LanguageNotificationTests.MethodBody(NoticeWindow(), "private void OnAcceptClicked(");

        Assert.Matches(@"if \(_notice\.Kind == StartupNoticeKind\.Disclaimer && !_notSavedShown\)", body);
        int shown = body.IndexOf("_notSavedShown = true;", StringComparison.Ordinal);
        int told = body.IndexOf("\"startup.settingsNotSaved\"", StringComparison.Ordinal);
        Assert.True(shown >= 0 && shown < told, "知らせる前に一度言ったことを覚えていない");
    }

    [Fact]
    public void 言語を保存できなかったときだけそう言う()
    {
        // Turned round, the notice came on every switch that saved and never on one that did not
        // (the final review of 2026-10-02, X60a)
        Assert.Matches(
            @"if \(!Loc\.Instance\.LastLanguageSaved\)\s*\{[^}]*SetStatus\(""status\.languageNotSaved""",
            LanguageChanged());
    }

    [Fact]
    public void 言語を保存できた切り替えでは前の知らせを残さない()
    {
        // Written again like any other message, the notice stayed after a later switch had saved -
        // naming a place that could by then be written, and saying the language would go back at
        // the next start when it would not (the final review of 2026-10-02, L2). A switch that saves
        // puts back what the notice was written over, if nothing has been said since.
        string body = LanguageChanged();

        int drop = body.IndexOf("ReferenceEquals(_statusRecipe, _languageNotice)", StringComparison.Ordinal);
        int rewrite = body.IndexOf("if (_statusRecipe is { } compose)", StringComparison.Ordinal);
        Assert.True(drop >= 0 && drop < rewrite, "前の知らせを外してから書き直していない");
        Assert.Contains("_statusRecipe = _beforeLanguageNotice;", body[drop..rewrite], StringComparison.Ordinal);

        int notice = body.IndexOf("SetStatus(\"status.languageNotSaved\"", StringComparison.Ordinal);
        Assert.True(notice > rewrite, "知らせが書き直しより前にある");
        Assert.Contains("_beforeLanguageNotice = _statusRecipe", body[rewrite..notice], StringComparison.Ordinal);
        Assert.Contains("_languageNotice = _statusRecipe;", body[notice..], StringComparison.Ordinal);
    }

    [Fact]
    public void フォルダの問題で置き換えるのは致命的な通知だけ()
    {
        // Turned round, the version warning lost its text again, and a fatal notice kept an old one
        // under the problem (the final review of 2026-10-02, X61a)
        string body = LanguageNotificationTests.MethodBody(NoticeWindow(), "private void ShowFolderProblem(");

        Assert.Matches(
            @"_notice\.Kind == StartupNoticeKind\.Fatal\s*\?\s*problem\s*:\s*\[\.\. problem, \.\. _shownMessages\]",
            body);
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

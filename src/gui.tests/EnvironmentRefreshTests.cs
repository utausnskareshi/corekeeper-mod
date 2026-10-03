using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Two things about how the window re-reads the game and the mod, read from the source: the
/// main view model looks for the real Steam install and the real game when it is built, so it
/// cannot be built here without touching the user's own machine.
/// </summary>
public sealed class EnvironmentRefreshTests
{
    private static string ViewModelSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "gui")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "gui", "ViewModels", "MainViewModel.cs"));
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"見つからない: {signature}");

        Match next = Regex.Match(source[(start + 1)..], @"^\s{4}(public|private|internal) ", RegexOptions.Multiline);
        return next.Success ? source.Substring(start, next.Index + 1) : source[start..];
    }

    [Fact]
    public void ゲームを見失ったら更新ボタンも下ろす()
    {
        // Every branch that says the mod is not installed has to say it is not outdated either.
        // The two that return early did not, so a game folder that went missing - "自動検出に戻す"
        // with the game only findable by hand - left "MOD を更新" on screen under "ゲームが見つかりません".
        string body = Body(ViewModelSource(), "public void RefreshEnvironment()");

        int notInstalled = Regex.Matches(body, @"IsModInstalled\s*=\s*false;").Count;
        int notOutdated = Regex.Matches(body, @"IsModOutdated\s*=\s*false;").Count;

        Assert.True(notInstalled >= 3, $"分岐の数が想定と違う: {notInstalled}");
        Assert.Equal(notInstalled, notOutdated);
    }

    [Fact]
    public void 取得元のキャラクター名は今の言語で出す()
    {
        // The label above the canvas held the character's name as text made at the moment of the
        // fetch, so after a switch to English it still read "［クリエイティブ］" and "（名前なし）"
        // while the list and the status line had changed. The choice is kept, and its Display -
        // which reads the current language - is asked each time the label is shown.
        string source = ViewModelSource();

        Assert.Matches(@"private\s+CharacterChoice\?\s+_fetchedFrom;", source);
        Assert.Matches(@"_fetchedFrom\s*=\s*choice;", source);
        Assert.DoesNotMatch(@"_fetchedFrom\s*=\s*choice\.Display;", source);
    }

    [Fact]
    public void キャンバスを押してもフォーカスで表示がずれない()
    {
        // Pressing the canvas gives it focus, and the ScrollViewer around it then brought it into
        // view by its 10 px padding - between the press and the first move. Measured in a real
        // Avalonia window: the sheet jumped 10 px up and left and the first segment of the stroke
        // was drawn from the pressed pixel to one the pointer never touched, in every frame when
        // "全コマに反映" was on. With BringIntoViewOnFocusChange off the offset did not move.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "gui")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
        string axaml = File.ReadAllText(Path.Combine(directory!.FullName, "src", "gui", "Views", "MainWindow.axaml"));

        Match viewer = Regex.Match(axaml, @"<ScrollViewer\b(?<attributes>[^>]*)>\s*<ed:PixelCanvas\b");
        Assert.True(viewer.Success, "キャンバスを包む ScrollViewer が見つからない");
        Assert.Contains(@"BringIntoViewOnFocusChange=""False""", viewer.Groups["attributes"].Value);
    }

    [Fact]
    public void MODの削除が一部だけ済んだときも表示を読み直す()
    {
        // A removal that deleted the mod but not every skin returned before re-reading the game,
        // so the bar went on saying the mod was installed, with "MOD を削除" and "MOD を更新" still
        // offered and every character still marked as applied. Re-read first, then report, so a
        // failed character scan cannot overwrite the report of the partial removal.
        string body = Body(ViewModelSource(), "public void ExecuteRemoval(");

        // The report may be composed inside the message (so that a language switch translates
        // the kind names in it), which is why the call is matched in either form
        Assert.Matches(
            @"if\s*\(result\.Failures\.Count\s*>\s*0\)\s*\{[^}]*RefreshEnvironment\(\);[\s\S]*?" +
            @"SetStatus\((\(\)\s*=>\s*Loc\.Instance\.Format\(\s*)?""status\.uninstallPartial""[^;]*;\s*return;",
            body);
    }

    [Fact]
    public void 削除するものが無いときもMODの導入状態を読み直してから報告する()
    {
        // Nothing to remove means the mod went away outside this window - removed by hand, by the
        // command line or by another window - while the bar still said it was installed. That
        // branch returned without re-reading, so "MOD を削除" stayed on screen, "ゲームへ配置" left
        // out the note that the mod is missing, and nothing short of a restart or a language switch
        // brought "MOD を導入" back (measured in a sandbox with the real view model). Re-read first,
        // then report, as the partial removal does.
        string window = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "MainWindow.axaml.cs"));
        string body = Body(window, "private async Task RemoveModAsync()");

        Match branch = Regex.Match(body, @"if\s*\(plan\.IsEmpty\)\s*\{(?<inner>[^}]*)\}");
        Assert.True(branch.Success, "plan.IsEmpty の分岐が見つからない");

        Assert.Matches(
            @"Model\.RefreshEnvironment\(\);[\s\S]*Model\.SetStatus\(""status\.uninstallNothing""\);\s*return;",
            branch.Groups["inner"].Value);
    }
}

using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// The line under the environment bar that says the game failed to load the mod the last time it
/// started, as the game's own log records it.
///
/// "MOD 導入済み" was all the bar said once the files were in place, while a game update could have
/// made the game refuse the mod. The wording is tested through <see cref="ModLoadMessages"/>; the
/// wiring is read off the source, because the view model looks for the real game as it is built
/// and cannot be built here without touching the user's own machine.
/// </summary>
public sealed class ModLoadWarningTests : IDisposable
{
    private static readonly DateTime Started = new(2026, 9, 28, 1, 11, 55, DateTimeKind.Utc);

    private const string LogPath = @"C:\Users\player\AppData\LocalLow\Pugstorm\Core Keeper\Player.log";

    private readonly LanguageOption _original = Loc.Instance.Current;

    public void Dispose() => Loc.Instance.Current = _original;

    private static LanguageOption Language(string code) =>
        Loc.Instance.Languages.Single(x => x.Code == code);

    private static ModLoadVerdict Failed(ModLoadFailure failure) =>
        new(ModLoadState.Failed, LogPath, new GameLogRun(Started, "1.3.0.2-182b", null, null, failure));

    private static readonly ModLoadFailure Refused = new(
        ModLoadFailureKind.CodeSecurity, "CompileFailed", "System.Reflection",
        ["Illegal reference to disallowed namespace: System.Reflection"]);

    // ------------------------------------------------------------ Wording

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 拒否された理由と起動した時刻を告げる(string language)
    {
        Loc.Instance.Current = Language(language);

        (string line, string detail) = ModLoadMessages.Describe(Failed(Refused), updateOffered: false);

        Assert.Contains(GameLog.DescribeTime(Started), line);
        Assert.Contains("System.Reflection", line);
        Assert.Contains(Loc.Instance["env.modLoadAdvice"], line);

        // The file it came from and the lines as the game wrote them sit behind the line
        Assert.Contains(LogPath, detail);
        Assert.Contains("Illegal reference to disallowed namespace: System.Reflection", detail);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 更新ボタンが出ていればそれを押すよう告げる(string language)
    {
        Loc.Instance.Current = Language(language);

        (string line, _) = ModLoadMessages.Describe(Failed(Refused), updateOffered: true);

        Assert.Contains(Loc.Instance["env.modLoadAdviceUpdate"], line);
        Assert.Contains(Loc.Instance["action.updateMod"], line);
        Assert.DoesNotContain(Loc.Instance["env.modLoadAdvice"], line);
    }

    [Theory]
    [InlineData(ModLoadFailureKind.CodeSecurity, "env.modLoadRefused")]
    [InlineData(ModLoadFailureKind.Compile, "env.modLoadNotCompiled")]
    [InlineData(ModLoadFailureKind.LoadError, "env.modLoadError")]
    [InlineData(ModLoadFailureKind.AssetBundle, "env.modLoadBundle")]
    [InlineData(ModLoadFailureKind.Manifest, "env.modLoadManifest")]
    public void 失敗の種類ごとに理由を言い分ける(ModLoadFailureKind kind, string key)
    {
        foreach (string language in new[] { "ja", "en" })
        {
            Loc.Instance.Current = Language(language);

            (string line, _) = ModLoadMessages.Describe(
                Failed(new ModLoadFailure(kind, "CompileFailed", "summary", [])), updateOffered: false);

            // The reason's own words, less its placeholder, with the summary where it takes one
            string reason = Loc.Instance[key];
            Assert.Contains(reason.Split("{0}")[0], line);
            Assert.Equal(reason.Contains("{0}", StringComparison.Ordinal), line.Contains("summary", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 組み込みだけの失敗は読み込めなかったとは言わない(string language)
    {
        Loc.Instance.Current = Language(language);

        (string line, string detail) = ModLoadMessages.Describe(
            Failed(new ModLoadFailure(ModLoadFailureKind.Patch, null, "HarmonyLib.HarmonyException: …",
                ["HarmonyLib.HarmonyException: …"])),
            updateOffered: false);

        // The words between the time and the reason are the ones that say "could not load"
        string loadFailed = Loc.Instance["env.modLoadFailed"];
        int from = loadFailed.IndexOf("{0}", StringComparison.Ordinal) + "{0}".Length;
        int to = loadFailed.IndexOf("{1}", StringComparison.Ordinal);
        Assert.DoesNotContain(loadFailed[from..to], line);
        Assert.Equal(Loc.Instance.Format("env.modPatchFailed", GameLog.DescribeTime(Started), Loc.Instance["env.modLoadAdvice"]), line);
        Assert.Contains("HarmonyLib.HarmonyException", detail);
    }

    [Theory]
    [InlineData(ModLoadState.NotInstalled)]
    [InlineData(ModLoadState.NoLog)]
    [InlineData(ModLoadState.Undetermined)]
    [InlineData(ModLoadState.OtherInstallation)]
    [InlineData(ModLoadState.Outdated)]
    [InlineData(ModLoadState.NoProblem)]
    public void 失敗と言えないときは何も出さない(ModLoadState state)
    {
        // Even with a failure in the run: an outdated or unrelated one is exactly what must not show
        ModLoadVerdict verdict = new(state, LogPath, new GameLogRun(Started, null, null, null, Refused));

        Assert.Equal((string.Empty, string.Empty), ModLoadMessages.Describe(verdict, updateOffered: true));
    }

    // ------------------------------------------------------------ Wiring

    private static string ViewModel() =>
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

    [Fact]
    public void MODが無いと分かった分岐ではどれも警告を下ろす()
    {
        // As IsModOutdated is: a game folder that went missing must not leave a warning about the
        // mod that was in it on screen under "ゲームが見つかりません"
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public void RefreshEnvironment()");

        int notInstalled = Regex.Matches(body, @"IsModInstalled\s*=\s*false;").Count;
        int cleared = Regex.Matches(body, @"ClearModLoadWarning\(\);").Count;

        Assert.True(notInstalled >= 3, $"分岐の数が想定と違う: {notInstalled}");
        Assert.Equal(notInstalled, cleared);
    }

    [Fact]
    public void 警告は更新ボタンの要否を決めた後に組み立てる()
    {
        // What the line tells the user to press depends on whether "Update mod" is offered
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public void RefreshEnvironment()");

        int outdated = body.IndexOf("IsModOutdated = installed.IsInstalled", StringComparison.Ordinal);
        int warning = body.IndexOf("UpdateModLoadWarning(installed);", StringComparison.Ordinal);

        Assert.True(outdated >= 0, "IsModOutdated の算出が見つからない");
        Assert.True(warning > outdated, "警告の組み立てが IsModOutdated より前にある、または無い");
    }

    [Fact]
    public void 警告の失敗で環境の表示を壊さない()
    {
        // RefreshEnvironment's own catch turns the bar into "could not check the environment" and
        // switches the install button off; one line of advice is not worth that
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private void UpdateModLoadWarning(InstalledModInfo installed)");

        Assert.Matches(@"try\s*\{[\s\S]*GameLog\.CheckLastRun\([\s\S]*\}\s*catch\s*\(Exception\)", body);
    }

    [Fact]
    public void ウィンドウに戻ったらログを読み直す()
    {
        string root = TranslationCoverageTests.FindRepositoryRoot();
        string code = File.ReadAllText(Path.Combine(root, "src", "gui", "Views", "MainWindow.axaml.cs"));

        Assert.Matches(@"Activated\s*\+=\s*\(_,\s*_\)\s*=>\s*Model\.RefreshModLoadIfChanged\(\);", code);

        // Only when the log changed, so switching windows does not re-read it every time
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public void RefreshModLoadIfChanged()");
        Assert.Matches(@"if\s*\(fingerprint\s*!=\s*_modLoadFingerprint\)", body);
    }

    [Fact]
    public void 帯の下の段に警告を出す()
    {
        string root = TranslationCoverageTests.FindRepositoryRoot();
        string axaml = File.ReadAllText(Path.Combine(root, "src", "gui", "Views", "MainWindow.axaml"));

        int bar = axaml.IndexOf("x:Name=\"EnvironmentBar\"", StringComparison.Ordinal);
        Assert.True(bar >= 0, "環境の帯が見つからない");
        string section = axaml[bar..axaml.IndexOf("</Border>", bar, StringComparison.Ordinal)];

        Match line = Regex.Match(section, @"<TextBlock\b(?<attributes>[^>]*\{Binding ModLoadWarning\}[^>]*)/>");
        Assert.True(line.Success, "帯に警告の行が無い");

        string attributes = line.Groups["attributes"].Value;
        Assert.Contains("IsVisible=\"{Binding HasModLoadWarning}\"", attributes);
        Assert.Contains("ToolTip.Tip=\"{Binding ModLoadWarningDetail}\"", attributes);
        Assert.Contains("Grid.Row=\"1\"", attributes);

        // The bar paints its own navy, so everything on it states its own colour
        Assert.Contains("Foreground=", attributes);
    }
}

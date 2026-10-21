using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Export;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// The "Export for mod.io" button and its screen.
///
/// The screen's rules live in ModIoExportDialogModel and run here. The view model looks for the real
/// game as it is built, so its part - waiting for a conversion, refusing to write over a picture
/// being converted, naming a file another program holds - is read off the source, as the save and
/// gear tests do.
/// </summary>
public sealed class ModIoExportTests : IDisposable
{
    private readonly LanguageOption _original = Loc.Instance.Current;

    public void Dispose() => Loc.Instance.Current = _original;

    private static readonly GameVersionCheck Supported = new(GameVersionState.Supported, "1.3.0.4-511d", ["1.3.0.1"]);

    private static void UseLanguage(string code) =>
        Loc.Instance.Current = Loc.Instance.Languages.First(l => l.Code == code);

    // ------------------------------------------------------------ The screen

    [Fact]
    public void 書き出し画面の既定値()
    {
        ModIoExportDialogModel model = new("  忍者  ", gearShownNow: true, Supported);

        Assert.Equal("忍者", model.Name);
        Assert.Equal("1.0.0", model.Version);

        // The look the toggle shows in the game now, which is the one that has been tried
        Assert.True(model.ShowGear);
        Assert.False(model.IsUntested);

        // Nothing is written before the picture's rights are confirmed
        Assert.False(model.RightsConfirmed);
        Assert.False(model.HasProblem);
        Assert.False(model.CanExport);

        model.RightsConfirmed = true;
        Assert.True(model.CanExport);
    }

    [Fact]
    public void 長すぎる名前は既定値の時点で上限に切る()
    {
        ModIoExportDialogModel model = new(new string('a', 80), gearShownNow: false, Supported);
        Assert.Equal(ModIoExporter.MaxDisplayNameLength, model.Name.Length);
        Assert.False(model.HasProblem);
    }

    [Fact]
    public void 既定値を上限に切るときサロゲートペアの途中で切らない()
    {
        // A file name such as 49 letters and an emoji: cut at 50 UTF-16 units it would end in half a
        // pair, which the name check refuses as a broken character before anything was typed
        int max = ModIoExporter.MaxDisplayNameLength;
        string emoji = char.ConvertFromUtf32(0x1F600);

        ModIoExportDialogModel split = new(new string('a', max - 1) + emoji + new string('b', 30), gearShownNow: false, Supported);
        Assert.Equal(new string('a', max - 1), split.Name);
        Assert.False(split.HasProblem);

        // A pair that ends exactly at the limit stays whole
        ModIoExportDialogModel whole = new(new string('a', max - 2) + emoji + new string('b', 30), gearShownNow: false, Supported);
        Assert.Equal(new string('a', max - 2) + emoji, whole.Name);
    }

    [Theory]
    [InlineData("", "1.0.0", "error.modio.nameEmpty")]
    [InlineData("Ninja", "v1", "error.modio.version")]
    [InlineData("Ninja", "", "error.modio.version")]
    public void 名前や版が誤っていれば理由を出して書き出させない(string name, string version, string key)
    {
        UseLanguage("en");
        ModIoExportDialogModel model = new("Ninja", gearShownNow: false, Supported) { RightsConfirmed = true };

        model.Name = name;
        model.Version = version;

        Assert.True(model.HasProblem);
        Assert.False(model.CanExport);

        // In the window's language, from the exporter's own check
        string expected = Loc.Instance.Describe(new ToolException(key, key == "error.modio.version" ? [version.Trim()] : [], string.Empty));
        Assert.Equal(expected, model.Problem);
        Assert.DoesNotContain("error.", model.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 入力が変わるたびに点検の結果を知らせる()
    {
        // The window binds to Problem, HasProblem and CanExport; without a change notice the export
        // button stayed as it was when the screen opened
        ModIoExportDialogModel model = new("Ninja", gearShownNow: false, Supported) { RightsConfirmed = true };
        List<string> raised = [];
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        model.Name = string.Empty;
        Assert.Contains(nameof(ModIoExportDialogModel.Problem), raised);
        Assert.Contains(nameof(ModIoExportDialogModel.HasProblem), raised);
        Assert.Contains(nameof(ModIoExportDialogModel.CanExport), raised);

        raised.Clear();
        model.Version = "x";
        Assert.Contains(nameof(ModIoExportDialogModel.CanExport), raised);

        raised.Clear();
        model.RightsConfirmed = false;
        Assert.Contains(nameof(ModIoExportDialogModel.CanExport), raised);

        raised.Clear();
        model.ShowGear = true;
        Assert.Contains(nameof(ModIoExportDialogModel.HideGear), raised);
        Assert.Contains(nameof(ModIoExportDialogModel.IsUntested), raised);
    }

    [Fact]
    public void 試していない見え方を選ぶと知らせる()
    {
        ModIoExportDialogModel model = new("Ninja", gearShownNow: false, Supported);

        Assert.False(model.IsUntested);
        Assert.True(model.HideGear);

        // The second choice is bound to HideGear; choosing it sets the first one off
        model.HideGear = false;
        Assert.True(model.ShowGear);
        Assert.True(model.IsUntested);

        model.HideGear = true;
        Assert.False(model.ShowGear);
        Assert.False(model.IsUntested);
    }

    [Fact]
    public void 書き出す選択は前後の空白を除いて渡す()
    {
        ModIoExportDialogModel model = new("Ninja", gearShownNow: false, Supported)
        {
            Name = "  Ninja  ",
            Version = " 1.2.0 ",
            ShowGear = true,
            RightsConfirmed = true,
        };

        Assert.Equal(new ModIoExportOptions("Ninja", "1.2.0", true), model.Result());
    }

    [Fact]
    public void ゲームの版の案内は分かっていることに応じて変わる()
    {
        UseLanguage("ja");

        string supported = new ModIoExportDialogModel("a", false, Supported).GameVersionNote;
        Assert.Equal(Loc.Instance.Format("modio.gameVersion", "1.3.0"), supported);

        string unsupported = new ModIoExportDialogModel("a", false,
            new GameVersionCheck(GameVersionState.Unsupported, "1.4.0.1-abcd", ["1.3.0.1"])).GameVersionNote;
        Assert.Contains(Loc.Instance.Format("modio.gameVersion", "1.4.0"), unsupported, StringComparison.Ordinal);
        Assert.Contains(Loc.Instance.Format("modio.gameVersionUnsupported", "1.4.0.1-abcd"), unsupported, StringComparison.Ordinal);

        ModIoExportDialogModel unknown = new("a", false, new GameVersionCheck(GameVersionState.Unknown, null, []));
        Assert.Contains(Loc.Instance.Format("modio.gameVersionUnknown", unknown.GameVersionTag), unknown.GameVersionNote, StringComparison.Ordinal);
        Assert.Equal(ModIoExporter.GameVersionTag(null), unknown.GameVersionTag);
    }

    [Theory]
    [InlineData("忍者", "1.0.0", "忍者-1.0.0.zip")]
    [InlineData("a/b:c*d?\"e<f>g|h\\i", "1.0", "a_b_c_d__e_f_g_h_i-1.0.zip")]
    [InlineData("name...", "1", "name...-1.zip")]
    public void 保存先の名前は名前と版から作る(string name, string version, string expected)
    {
        Assert.Equal(expected, ModIoExportDialogModel.SuggestedFileName(new ModIoExportOptions(name, version, false)));
    }

    [Fact]
    public void 保存先の名前は長すぎれば切り_末尾の点と空白を落とす()
    {
        string name = ModIoExportDialogModel.SuggestedFileName(new ModIoExportOptions(new string('n', 100), "1.0.0", false));
        Assert.True(name.Length <= 84, name);
        Assert.EndsWith(".zip", name, StringComparison.Ordinal);

        // Windows drops these from the end of a name, leaving the memo and logo named after another file
        Assert.Equal("a-1.zip", ModIoExportDialogModel.SuggestedFileName(new ModIoExportOptions("a", "1. ", false)));
    }

    // ------------------------------------------------------------ The wiring

    private static string ViewModel() =>
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

    [Fact]
    public void 書き出しは変換を待ってから行い_届かなければ書き出さない()
    {
        string body = LanguageNotificationTests.MethodBody(
            ViewModel(), "public async Task ExportForModIoWhenSettledAsync(string zipPath, ModIoExportOptions options)");

        int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
        int check = body.IndexOf("NotArrivedSince(mark)", StringComparison.Ordinal);
        int export = body.IndexOf("ExportForModIo(zipPath, options);", StringComparison.Ordinal);

        Assert.True(wait >= 0 && check > wait && export > check, "待つ・届いたか確かめる・書き出す、の順になっていない");
        Assert.Contains("status.notExportedAfterWait", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 書き出しは変換中の元の絵を上書きせず_書けないファイルを名指しする()
    {
        string body = LanguageNotificationTests.MethodBody(
            ViewModel(), "public void ExportForModIo(string zipPath, ModIoExportOptions options)");

        // All three files, the logo above all: it is a PNG named after the zip
        Assert.Contains("ModIoExporter.CompanionPaths(zipPath)", body, StringComparison.Ordinal);
        Assert.Matches(@"new\[\]\s*\{\s*zipPath,\s*logoPath,\s*guidePath\s*\}", body);
        Assert.Contains("HeldSourcePaths().Any(source => PointsAtSameFile(source, target))", body, StringComparison.Ordinal);
        Assert.Contains("\"status.modioOverSource\"", body, StringComparison.Ordinal);

        Assert.Matches(@"catch\s*\(ModIoWriteException\s+ex\)\s*\{[^}]*""status\.modioWriteFailed"",\s*ex\.FilePath\)", body);
        Assert.Contains("SetStatus(\"status.modioExported\", result.ZipPath, result.LogoPath, result.GuidePath);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 置き換えの途中で止まれば_もう新しくなったファイルも知らせる()
    {
        // The zip, logo and memo are moved into place one after another. Stopped at the logo, the zip
        // is already new and no longer matches the logo and memo beside it - the plain "could not
        // write" message would leave that unsaid
        string body = LanguageNotificationTests.MethodBody(
            ViewModel(), "public void ExportForModIo(string zipPath, ModIoExportOptions options)");

        Assert.Contains("if (ex.Replaced.Count == 0)", body, StringComparison.Ordinal);
        Assert.Matches(@"""status\.modioWritePartial"",\s*ex\.FilePath,\s*string\.Join\(""\\n"",\s*ex\.Replaced\)\)", body);

        foreach (string code in new[] { "ja", "en" })
        {
            UseLanguage(code);
            string text = Loc.Instance.Format("status.modioWritePartial", "a.png", "a.zip");
            Assert.Contains("a.png", text, StringComparison.Ordinal);
            Assert.Contains("a.zip", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ボタンは絵があれば押せ_押すと書き出し画面を開く()
    {
        string root = TranslationCoverageTests.FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(root, "src", "gui", "Views", "MainWindow.axaml"));
        string code = File.ReadAllText(Path.Combine(root, "src", "gui", "Views", "MainWindow.axaml.cs"));

        Match button = Regex.Match(window, @"<Button[^>]*x:Name=""ExportModIoButton""[^>]*/>", RegexOptions.Singleline);
        Assert.True(button.Success, "ExportModIoButton が無い");
        Assert.Contains("IsEnabled=\"{Binding HasDocument}\"", button.Value, StringComparison.Ordinal);
        Assert.Contains("{loc:Tr action.exportModIo}", button.Value, StringComparison.Ordinal);

        Assert.Contains("ExportModIoButton.Click += OnExportModIoClicked;", code, StringComparison.Ordinal);

        string handler = LanguageNotificationTests.MethodBody(code, "private async void OnExportModIoClicked(object? sender, RoutedEventArgs e)");
        int screen = handler.IndexOf("new(Model.CreateModIoExportDialog())", StringComparison.Ordinal);
        int picker = handler.IndexOf("SaveFilePickerAsync", StringComparison.Ordinal);
        int export = handler.IndexOf("ExportForModIoWhenSettledAsync(path, options)", StringComparison.Ordinal);
        Assert.True(screen >= 0 && picker > screen && export > picker, "画面・保存先・書き出し、の順になっていない");
    }

    private static string ExportHandler()
    {
        string root = TranslationCoverageTests.FindRepositoryRoot();
        string code = File.ReadAllText(Path.Combine(root, "src", "gui", "Views", "MainWindow.axaml.cs"));
        return LanguageNotificationTests.MethodBody(code, "private async void OnExportModIoClicked(object? sender, RoutedEventArgs e)");
    }

    [Fact]
    public void 書き出し画面は読み込みと変換が落ち着いてから開き_絵が届かなければ開かない()
    {
        // Waited for only after the screen and the save dialog, a picture still loading at the press
        // was offered the name of the one before it, and one that failed to load while the screen
        // was open left the previous picture to be exported over the message saying why (the code
        // review of 2026-10-08, R3). The press waits first, as "Apply to game" does.
        string handler = ExportHandler();

        Match settle = Regex.Match(handler, @"if \(!await Model\.SettleBeforeModIoExportAsync\(\)\)\s*\{\s*return;\s*\}");
        int screen = handler.IndexOf("new(Model.CreateModIoExportDialog())", StringComparison.Ordinal);
        Assert.True(settle.Success, "画面を開く前に読み込みと変換が落ち着くのを待っていない");
        Assert.True(screen > settle.Index, "落ち着くのを待つ前に画面を作っている");

        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public async Task<bool> SettleBeforeModIoExportAsync()");

        int mark = body.IndexOf("int mark = _picturesNotArrived;", StringComparison.Ordinal);
        int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
        Assert.True(mark >= 0 && wait > mark, "待つ前に届かなかった数を控えていない");

        Match check = Regex.Match(body, @"if \(NotArrivedSince\(mark\) is \{ \} why\)\s*\{(?<branch>[^}]*)\}");
        Assert.True(check.Success && check.Index > wait, "待った後に届かなかったかを見ていない");

        string branch = check.Groups["branch"].Value;
        Assert.Contains("why()", branch, StringComparison.Ordinal);
        Assert.Contains("\"status.notExportedAfterWait\"", branch, StringComparison.Ordinal);
        Assert.Contains("return false;", branch, StringComparison.Ordinal);
        Assert.Contains("return true;", body[(check.Index + check.Length)..], StringComparison.Ordinal);
    }

    [Fact]
    public void 書き出しのボタンは待っている間に押し直しても画面を1つしか開かない()
    {
        // The press can wait for a picture on its way before the screen opens, and the window takes
        // clicks while it does: every press waited and then opened a screen of its own
        string handler = ExportHandler();

        Assert.Matches(@"\A\{\s*if \(_exportingForModIo\)\s*\{\s*return;\s*\}\s*_exportingForModIo = true;\s*try\s*\{", handler);

        // Down again however the press ends, or the button would do nothing from then on
        Match lastFinally = Regex.Matches(handler, @"finally\s*\{").Last();
        Assert.Contains("_exportingForModIo = false;", handler[lastFinally.Index..], StringComparison.Ordinal);
    }

    [Fact]
    public void 横のロゴと登録メモが既にあれば書き出す前に確かめ_答えなければ書き出さない()
    {
        // The save dialog asks only about the zip. The logo and the memo beside it were replaced
        // without a word, and a logo touched up by hand under that name was lost for good (the code
        // review of 2026-10-08, R1). Asked only when one is there, so a first export goes as before.
        string handler = ExportHandler();

        int picker = handler.IndexOf("SaveFilePickerAsync", StringComparison.Ordinal);
        int existing = handler.IndexOf("ModIoExporter.ExistingCompanions(path)", StringComparison.Ordinal);
        int export = handler.IndexOf("ExportForModIoWhenSettledAsync(path, options)", StringComparison.Ordinal);
        Assert.True(picker >= 0 && existing > picker && export > existing, "保存先・既にあるかの確認・書き出し、の順になっていない");

        // The files are listed under the question
        Assert.Matches(
            @"new\(\s*Loc\.Instance\[""confirm\.modioReplaceCompanions""\],\s*""confirm\.modioReplaceCompanionsOk"",\s*string\.Join\(Environment\.NewLine,\s*existing\)\)",
            handler);

        // Cancel, Escape and the close button all leave every file as it was
        Match ask = Regex.Match(handler, @"if \(await confirm\.ShowDialog<bool\?>\(this\) != true\)\s*\{\s*return;\s*\}");
        Assert.True(ask.Success, "確認に「置き換える」と答えたときだけ書き出すようになっていない");
        Assert.True(ask.Index > existing && ask.Index < export, "確かめる前に書き出している");
    }

    [Theory]
    [InlineData("ja", "元に戻せません", "別の名前")]
    [InlineData("en", "cannot be recovered", "another name")]
    public void 横のファイルを置き換える確認は戻せないことと残し方を言う(string language, string lost, string keep)
    {
        // Read from the language's own file: for a key missing in English the window shows the
        // Japanese text, which would hide the gap from Loc.Instance
        string file = Path.Combine(TranslationCoverageTests.FindRepositoryRoot(), "src", "core", "Localization", $"lang.{language}.json");
        Dictionary<string, string> table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];

        string message = table.GetValueOrDefault("confirm.modioReplaceCompanions") ?? string.Empty;
        Assert.Contains(lost, message, StringComparison.Ordinal);
        Assert.Contains(keep, message, StringComparison.Ordinal);

        // Shown through the indexer rather than Format, so a brace would be shown as it is
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace(table.GetValueOrDefault("confirm.modioReplaceCompanionsOk")), "確認のボタンの文が無い");
    }

    [Fact]
    public void 書き出し画面の入力の上限は書き出しの上限と同じ()
    {
        // The boxes repeat the exporter's limits as numbers. Changed on one side only, the screen
        // would stop typing short of what is accepted, or take what the export then refuses.
        // Avalonia's MaxLength counts UTF-16 units, as the exporter does.
        string root = TranslationCoverageTests.FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(root, "src", "gui", "Views", "ModIoExportWindow.axaml"));

        Match name = Regex.Match(window, @"<TextBox\b[^>]*Text=""\{Binding Name\}""[^>]*MaxLength=""(?<n>\d+)""");
        Match version = Regex.Match(window, @"<TextBox\b[^>]*Text=""\{Binding Version\}""[^>]*MaxLength=""(?<n>\d+)""");
        Assert.True(name.Success, "名前の欄に MaxLength が無い");
        Assert.True(version.Success, "版の欄に MaxLength が無い");
        Assert.Equal(ModIoExporter.MaxDisplayNameLength, int.Parse(name.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ModIoExporter.MaxVersionLength, int.Parse(version.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));

        // The hint under the name states the same limit
        foreach (string code in new[] { "ja", "en" })
        {
            UseLanguage(code);
            Assert.Contains(ModIoExporter.MaxDisplayNameLength.ToString(System.Globalization.CultureInfo.InvariantCulture), Loc.Instance["modio.nameHint"], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 名前の欄で半分に切れた文字があれば理由を出して書き出させない()
    {
        // The box's MaxLength counts UTF-16 units: 49 letters and an emoji keep only the emoji's first
        // half, and that went out as U+FFFD in the manifest, NOTICE.txt and the memo (the adversarial
        // verification of 2026-10-09, A1)
        UseLanguage("ja");
        ModIoExportDialogModel model = new("Ninja", gearShownNow: false, Supported) { RightsConfirmed = true };
        string emoji = char.ConvertFromUtf32(0x1F600);

        model.Name = new string('a', ModIoExporter.MaxDisplayNameLength - 1) + emoji[0];
        Assert.True(model.HasProblem);
        Assert.False(model.CanExport);
        Assert.Equal(Loc.Instance.Describe(new ToolException("error.modio.nameBrokenChar", [ModIoExporter.MaxDisplayNameLength], string.Empty)), model.Problem);
        Assert.Contains(ModIoExporter.MaxDisplayNameLength.ToString(System.Globalization.CultureInfo.InvariantCulture), model.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("error.", model.Problem, StringComparison.Ordinal);

        // The whole emoji is a character like any other
        model.Name = new string('a', ModIoExporter.MaxDisplayNameLength - 2) + emoji;
        Assert.False(model.HasProblem);
        Assert.True(model.CanExport);
    }

    [Fact]
    public void 書き出しが断る保存先なら置き換えの確認を出す前に断る()
    {
        // A picture being converted named like the logo (hero-logo.png for hero.zip) was listed as
        // about to be replaced and lost, and only after the user agreed was the export refused (the
        // adversarial verification of 2026-10-09, A5). The export keeps its own check as well.
        string handler = ExportHandler();

        int picker = handler.IndexOf("SaveFilePickerAsync", StringComparison.Ordinal);
        Match refuse = Regex.Match(handler, @"if \(Model\.RefuseModIoTarget\(path\)\)\s*\{\s*return;\s*\}");
        int existing = handler.IndexOf("ModIoExporter.ExistingCompanions(path)", StringComparison.Ordinal);
        Assert.True(refuse.Success, "保存先が元の絵と同じかを確認の前に見ていない");
        Assert.True(refuse.Index > picker && refuse.Index < existing, "保存先・断る条件・置き換えの確認、の順になっていない");

        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public bool RefuseModIoTarget(string zipPath)");
        Assert.Contains("ModIoExporter.CompanionPaths(zipPath)", body, StringComparison.Ordinal);
        Assert.Matches(@"new\[\]\s*\{\s*zipPath,\s*logoPath,\s*guidePath\s*\}", body);
        Assert.Matches(@"if \(HeldSourcePaths\(\)\.Any\(source => PointsAtSameFile\(source, target\)\)\)\s*\{\s*SetStatus\(""status\.modioOverSource"", target\);\s*return true;\s*\}", body);
        Assert.Contains("return false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 書き出し画面の前の待ちで待ちの文しか残らなければ前の文に戻す()
    {
        // A conversion superseded by a preset ends without a word, and an export screen then
        // cancelled says nothing either: "waiting for the conversion to finish" stayed with nothing
        // waiting (the adversarial verification of 2026-10-09, A6). Done here, not in the shared
        // wait, whose other callers always write a line of their own afterwards.
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "public async Task<bool> SettleBeforeModIoExportAsync()");

        int before = body.IndexOf("Func<string>? before = _statusRecipe;", StringComparison.Ordinal);
        int said = body.IndexOf("string said = Status;", StringComparison.Ordinal);
        int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
        Assert.True(before >= 0 && said >= 0 && wait > before && wait > said, "待つ前の文を控えていない");

        Match restore = Regex.Match(body, @"if \(Status != said && Status == Loc\.Instance\[""status\.waitingForConversion""\]\)\s*\{\s*SetStatus\(before \?\? \(\(\) => said\)\);\s*\}");
        int notArrived = body.IndexOf("NotArrivedSince(mark)", StringComparison.Ordinal);
        int done = body.LastIndexOf("return true;", StringComparison.Ordinal);
        Assert.True(restore.Success, "待ちの文だけが残ったときに前の文へ戻していない");
        Assert.True(restore.Index > notArrived && restore.Index < done, "届かなかったときの理由を戻した文で消すか、戻す前に返している");
    }

    [Fact]
    public void 変換中の元の絵に当たれば書き出す前に戻る()
    {
        // The existing check sees the condition and the message but not the return after them:
        // without it the picture being converted was written over by the logo (the adversarial
        // verification of 2026-10-09, A9)
        string body = LanguageNotificationTests.MethodBody(
            ViewModel(), "public void ExportForModIo(string zipPath, ModIoExportOptions options)");

        Assert.Matches(@"if \(HeldSourcePaths\(\)\.Any\(source => PointsAtSameFile\(source, target\)\)\)\s*\{\s*SetStatus\(""status\.modioOverSource"", target\);\s*return;\s*\}", body);
    }

    [Fact]
    public void 横のファイルの確認はロゴかメモの1つでもあれば出す()
    {
        // Only the logo left beside the zip (the memo moved away) is the case R1 was about; a count of
        // two or more would let it be replaced unasked (the adversarial verification of 2026-10-09, A10)
        Assert.Matches(@"if \(ModIoExporter\.ExistingCompanions\(path\) is \{ Count: > 0 \} existing\)", ExportHandler());
    }

    [Fact]
    public void 書き出しには画面の言語と今のゲームの版を渡し_画面は今の装備の見え方から始める()
    {
        // The memo follows the window's language and names the installed game's version, as the
        // screen does; the screen starts from the look the gear toggle shows in the game now, the one
        // that has been tried there (the adversarial verification of 2026-10-09, A14)
        string export = LanguageNotificationTests.MethodBody(
            ViewModel(), "public void ExportForModIo(string zipPath, ModIoExportOptions options)");
        Assert.Matches(
            @"new\s+ModIoExportRequest\(\s*options,\s*ModIoExporter\.GameVersionTag\(GameVersion\.Read\(_gamePath\)\),\s*ToolVersion,\s*Loc\.Instance\.Current\.Code\s*\)",
            export);

        string create = LanguageNotificationTests.MethodBody(ViewModel(), "public ModIoExportDialogModel CreateModIoExportDialog()");
        Assert.Contains("GameVersion.Check(_gamePath)", create, StringComparison.Ordinal);
        Assert.Contains("return new ModIoExportDialogModel(DefaultModIoName(), ShowGameGear, game);", create, StringComparison.Ordinal);
    }

    [Fact]
    public void 保存先を選んだ後の待ちも届かなかった数を待つ前に控え_届かなければ理由を残して戻る()
    {
        // The second wait, after the screen and the save dialog: the existing check sees the order of
        // wait, check and export, but neither where the mark is read nor the return in the branch
        // (the adversarial verification of 2026-10-09, A15)
        string body = LanguageNotificationTests.MethodBody(
            ViewModel(), "public async Task ExportForModIoWhenSettledAsync(string zipPath, ModIoExportOptions options)");

        int mark = body.IndexOf("int mark = _picturesNotArrived;", StringComparison.Ordinal);
        int wait = body.IndexOf("await WaitUntilSettledAsync();", StringComparison.Ordinal);
        Assert.True(mark >= 0 && wait > mark, "待つ前に届かなかった数を控えていない");

        Match check = Regex.Match(body, @"if \(NotArrivedSince\(mark\) is \{ \} why\)\s*\{(?<branch>[^}]*)\}");
        Assert.True(check.Success && check.Index > wait, "待った後に届かなかったかを見ていない");
        string branch = check.Groups["branch"].Value;
        Assert.Contains("why()", branch, StringComparison.Ordinal);
        Assert.Contains("\"status.notExportedAfterWait\"", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
        Assert.True(body.IndexOf("ExportForModIo(zipPath, options);", StringComparison.Ordinal) > check.Index + check.Length, "届かなかったときの分岐の外で書き出していない");
    }
}

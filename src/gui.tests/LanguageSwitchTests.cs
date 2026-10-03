using System.ComponentModel;
using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Gui.ViewModels;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Tests that a language switch actually reaches the view.
///
/// Even with every string translated, the view stays untranslated if the notification never arrives.
/// That was actually missed once, so the notification path is now checked mechanically.
/// </summary>
public sealed class LanguageSwitchTests : IDisposable
{
    private readonly LanguageOption _original = Loc.Instance.Current;

    /// <summary>Restores the original language so other tests are unaffected.</summary>
    public void Dispose() => Loc.Instance.Current = _original;

    private static LanguageOption Language(string code) =>
        Loc.Instance.Languages.Single(x => x.Code == code);

    [Fact]
    public void 同じキーには同じ通知源が返る()
    {
        // Separate instances would leave part of the interface un-refreshed
        Assert.Same(Loc.Instance.Get("top.open"), Loc.Instance.Get("top.open"));
    }

    [Fact]
    public void 言語を変えると文言が変わる()
    {
        LocalizedString entry = Loc.Instance.Get("top.open");

        Loc.Instance.Current = Language("ja");
        string japanese = entry.Value;

        Loc.Instance.Current = Language("en");
        string english = entry.Value;

        Assert.NotEqual(japanese, english);
        Assert.Equal("Open image…", english);
    }

    [Fact]
    public void 言語を変えると通知が飛ぶ()
    {
        // Without this notification the value changes but the view stays stale
        Loc.Instance.Current = Language("ja");
        LocalizedString entry = Loc.Instance.Get("import.title");

        List<string?> notified = [];
        entry.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Loc.Instance.Current = Language("en");

        Assert.Contains(nameof(LocalizedString.Value), notified);
    }

    [Fact]
    public void 既に作られている通知源すべてに通知が飛ぶ()
    {
        Loc.Instance.Current = Language("ja");

        string[] keys =
        [
            "top.open", "import.title", "section.background", "size.width",
            "tool.pen", "editor.color", "preview.title", "action.save",
            "action.install", "action.uninstall", "help.button",
        ];

        Dictionary<string, bool> notified = keys.ToDictionary(k => k, _ => false);
        foreach (string key in keys)
        {
            string captured = key;
            Loc.Instance.Get(key).PropertyChanged += (_, _) => notified[captured] = true;
        }

        Loc.Instance.Current = Language("en");

        string[] missed = [.. notified.Where(p => !p.Value).Select(p => p.Key)];
        Assert.True(missed.Length == 0, "通知が届かないキー: " + string.Join(", ", missed));
    }

    [Fact]
    public void 同じ言語を選び直しても余計な通知は飛ばない()
    {
        Loc.Instance.Current = Language("ja");
        LocalizedString entry = Loc.Instance.Get("top.open");

        int count = 0;
        entry.PropertyChanged += (_, _) => count++;

        Loc.Instance.Current = Language("ja");

        Assert.Equal(0, count);
    }

    [Fact]
    public void 状態行はSetStatusを通してしか書かれない()
    {
        // The status line is the one string on screen with nothing bound to it, so it follows a
        // language switch only because SetStatus keeps hold of the function that composed it. An
        // assignment straight to the property still puts the right words there and nothing looks
        // wrong at the time; the line simply stops following the switch from then on, which is
        // the failure this replaced. Sixty-three of them were moved onto SetStatus in one go, and
        // a sixty-fourth written the old way would be indistinguishable from the rest.
        string root = TranslationCoverageTests.FindRepositoryRoot();

        // Anchored on an identifier boundary so that EnvironmentStatus, which has a refresh path
        // of its own, is not mistaken for the tail of this one.
        Regex assignment = new(@"(?<![A-Za-z0-9_])Status\s*=(?!=)", RegexOptions.Compiled);

        List<string> writes = [];
        string[] sources = Directory.GetFiles(
            Path.Combine(root, "src", "gui"), "*.cs", SearchOption.AllDirectories);

        foreach (string file in sources.Where(f =>
                     !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                if (assignment.IsMatch(lines[i]))
                {
                    writes.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        // The single write inside SetStatus, and nothing besides it.
        Assert.True(
            writes.Count == 1,
            "SetStatus を通さない状態行の書き込み: " + string.Join(" / ", writes));
        Assert.EndsWith("Status = compose();", writes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 画面で使うキーがすべて訳されている()
    {
        // A missing translation puts the key itself on screen, which is easier to miss than untranslated text.
        Loc.Instance.Current = Language("en");

        string[] keys =
        [
            "app.title", "app.language", "top.open", "top.dropHint", "top.regenerate",
            "import.title", "import.hint", "section.background", "background.remove",
            "section.size", "size.trim", "size.width", "size.height",
            "section.pixelart", "pixelart.quantize", "pixelart.nearest",
            "section.outline", "outline.enable", "section.motion", "motion.enable", "motion.shiftOnly",
            "tool.pen", "tool.eraser", "tool.picker", "tool.bucket",
            "editor.color", "editor.undo", "editor.redo", "editor.allFrames",
            "editor.view", "editor.zoom", "editor.grid", "editor.guides",
            "editor.lightBackground", "editor.wholeSheet",
            "preview.title", "preview.animation", "preview.direction", "preview.play",
            "action.save", "action.install", "action.uninstall", "action.installMod",
            "help.button", "help.close",
            "confirm.title", "confirm.ok", "confirm.cancel", "confirm.uninstallOk",
        ];

        string[] untranslated = [.. keys.Where(key => Loc.Instance[key] == key)];
        Assert.True(untranslated.Length == 0, "訳が無いキー: " + string.Join(", ", untranslated));
    }

    /// <summary>
    /// The tool's version follows the game's (1.3.0 for Core Keeper 1.3.0.x, the user's decision of
    /// 2026-10-02), and the window said nowhere which one was running: the number was in the file's
    /// properties and nowhere a user looks. The title carries it, in the language of the moment.
    /// </summary>
    [Fact]
    public void 窓の題名に版を出し言語の切り替えに従う()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", MainViewModel.ProductVersion);
        Assert.Equal(
            typeof(MainViewModel).Assembly.GetName().Version!.ToString(3),
            MainViewModel.ProductVersion);

        string window = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "MainWindow.axaml"));
        Assert.Contains("Title=\"{Binding WindowTitle}\"", window, StringComparison.Ordinal);

        string model = LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());
        Assert.Matches(@"WindowTitle\s*=>\s*\$""\{Loc\.Instance\[""app\.title""\]\} v\{ProductVersion\}"";", model);
        Assert.Contains(
            "OnPropertyChanged(nameof(WindowTitle));",
            LanguageNotificationTests.MethodBody(model, "private void OnLanguageChanged()"),
            StringComparison.Ordinal);
    }
}

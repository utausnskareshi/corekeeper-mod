namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that opening or clearing a facing never calls off the front picture's load.
///
/// Loads are ordered by numbers held in the view model, and a facing has to watch the front
/// picture's number without raising it. Raising it - which is how the facings were first wired
/// into the ordering - meant that clearing a facing while a large picture was still being opened
/// threw that picture away, and silently: a superseded load returns without publishing and
/// without a message, so the previous picture's "opened" line stayed on screen over a canvas
/// that never changed. A 4000x4000 decode was measured at 270ms, and a conversion holding the
/// build gate widens the window to seconds.
///
/// Read off the repository's own source, the way <see cref="LanguageNotificationTests"/> and
/// <see cref="TranslationCoverageTests"/> already do. Exercising the view model cannot see this:
/// reproducing it needs a decode still running when the next click arrives, and the view model
/// takes no seam for slowing one down.
/// </summary>
public sealed class LoadOrderingTests
{
    private const string FacingSignature =
        "private async Task LoadFacingAsync(string? path, bool side)";

    private const string FrontSignature = "public async Task LoadImageAsync(string path)";

    private static string FacingBody() => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        FacingSignature);

    [Fact]
    public void 向きの読み込みは正面の読み込みを打ち切らない()
    {
        // The regression itself. Both spellings are checked because either one raises the number
        // every other load compares against, and the comparison is what makes a load give up.
        string body = FacingBody();

        Assert.DoesNotContain("++_loadVersion", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_loadVersion++", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 向きの読み込みは向きごとの世代で順序を付ける()
    {
        // Without a number of its own, nothing orders two loads of the same facing: two files
        // dropped on one button decode on worker threads, and the slower one lands last however
        // it was asked for. The two facings are counted apart so neither disturbs the other.
        string body = FacingBody();

        Assert.Contains("++_sideVersion", body, StringComparison.Ordinal);
        Assert.Contains("++_backVersion", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 向きの読み込みは正面の世代も照合する()
    {
        // Why the facings joined the ordering at all: opening a finished sheet frees these two
        // bitmaps under the build gate, and a facing still being decoded would otherwise take
        // the gate afterwards and put one back - onto a panel that is disabled in that state, so
        // it could not be removed again, and the next ordinary picture would be built with it.
        string body = FacingBody();

        Assert.Contains("_loadVersion", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 検査の土台として正面の読み込みは世代を進めている()
    {
        // The three checks above are worth nothing if the front picture stops raising the number:
        // the facings would then be watching something that never moves, and would happily land
        // on top of a sheet opened after them.
        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            FrontSignature);

        Assert.Contains("++_loadVersion", body, StringComparison.Ordinal);
    }
}

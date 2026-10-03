using CoreKeeperSkinTool.Gui.Localization;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// What the fill and the picker say when they did not do what the user meant.
///
/// Both acted without a word on frames or colours the user was not looking at: the fill, over
/// every frame, flooded the background of frames where the clicked spot held something else, and
/// the picker took a transparent pixel as the pen's colour, which then erased (the test campaign
/// of 2026-10-01). Read off the view model's source, which needs the real game to be built.
/// </summary>
public sealed class EditToolMessageTests : IDisposable
{
    private readonly LanguageOption _original = Loc.Instance.Current;

    public void Dispose() => Loc.Instance.Current = _original;

    private static string HandlePixel() => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        "public void HandlePixel(int x, int y, bool isStart, bool erase, bool interpolated)");

    private static string Branch(string body, string label)
    {
        int start = body.IndexOf(label, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{label} が見つからない");
        int end = body.IndexOf("case EditorTool.", start + label.Length, StringComparison.Ordinal);
        return end > start ? body[start..end] : body[start..];
    }

    [Fact]
    public void 全コマの塗りつぶしで塗らなかったコマがあればその数を言う()
    {
        string bucket = Branch(HandlePixel(), "case EditorTool.Bucket:");

        Assert.Contains("out int skippedFrames", bucket, StringComparison.Ordinal);
        Assert.Contains("\"status.fillSkippedFrames\"", bucket, StringComparison.Ordinal);

        // A fill that did nothing at all keeps its own sentence
        Assert.Contains("\"status.fillNothing\"", bucket, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "別の色")]
    [InlineData("en", "different colour")]
    public void 塗らなかったコマの数の文は理由を言う(string language, string reason)
    {
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

        string text = Loc.Instance.Format("status.fillSkippedFrames", 37);

        Assert.Contains("37", text, StringComparison.Ordinal);
        Assert.Contains(reason, text, StringComparison.Ordinal);
    }

    [Fact]
    public void スポイトで透明な画素を取ったらペンの色を変えずにそう言う()
    {
        string picker = Branch(HandlePixel(), "case EditorTool.Picker:");

        int transparent = picker.IndexOf("picked.Alpha == 0", StringComparison.Ordinal);
        int assign = picker.IndexOf("PenColor = ", StringComparison.Ordinal);
        Assert.True(transparent >= 0 && transparent < assign, "透明な画素をペンの色にする前に止めていない");
        Assert.Contains("\"status.colorPickedTransparent\"", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void 絵から作るプリセットを読み込んだら塗りつぶしの効き方を言う()
    {
        // The 18 picture presets are shaded art scaled down to 13 pixels wide, so nearly every
        // pixel is its own colour (78 to 173 colours in frame 0, areas of 1 or 2 pixels). The fill
        // and the colour replace then change one or two pixels and say nothing, while the message
        // on loading said "このまま塗り替えて使えます" (the test campaign of 2026-10-01).
        string model = LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());
        int loaded = model.IndexOf("\"status.presetLoaded\", choice.Display", StringComparison.Ordinal);
        Assert.True(loaded >= 0, "プリセットを読み込んだときの文が見つからない");

        string around = model[Math.Max(0, loaded - 400)..loaded];
        Assert.Contains("IsDrawn", around, StringComparison.Ordinal);
        Assert.Contains("\"status.presetLoadedPicture\"", around, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "狭い範囲")]
    [InlineData("en", "small area")]
    public void 絵のプリセットの文は塗りつぶしが狭くしか効かないと言う(string language, string phrase)
    {
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

        string text = Loc.Instance.Format("status.presetLoadedPicture", "NAME");

        Assert.Contains("NAME", text, StringComparison.Ordinal);
        Assert.Contains(phrase, text, StringComparison.Ordinal);
    }

    [Fact]
    public void スポイトで透明な画素を取ったらそこで止める()
    {
        // Without the return the notice was written and then the pen turned transparent all the same,
        // with "picked #00000000" over the notice (the final review of 2026-10-02, X53a)
        string picker = Branch(HandlePixel(), "case EditorTool.Picker:");

        Assert.Matches(@"if \(picked\.Alpha == 0\)\s*\{\s*SetStatus\(""status\.colorPickedTransparent""\);\s*return;\s*\}", picker);
    }

    [Fact]
    public void 絵から作るプリセットにだけ塗りつぶしの効き方を言う()
    {
        // Turned round, the drawn presets were told the fill reaches a pixel or two, and the picture
        // ones "paint over it as it is" again (the final review of 2026-10-02, X63a)
        Assert.Matches(
            @"!choice\.Definition\.IsDrawn\s*\?\s*Loc\.Instance\.Format\(""status\.presetLoadedPicture""",
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()));
    }

    [Fact]
    public void 塗らなかったコマがあるときだけその数を言う()
    {
        // With the condition turned to "< 0" the count was never said (the final review of
        // 2026-10-02, X52d); EditorDocumentTests check the count itself, not this
        string bucket = Branch(HandlePixel(), "case EditorTool.Bucket:");

        Assert.Matches(@"else if \(skippedFrames > 0\)\s*\{[^}]*SetStatus\(""status\.fillSkippedFrames"", skippedFrames\);", bucket);
    }

    [Theory]
    [InlineData("ja", "透明")]
    [InlineData("en", "transparent")]
    public void 透明な画素を取ったときの文は透明だと言う(string language, string word)
    {
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

        Assert.Contains(word, Loc.Instance["status.colorPickedTransparent"], StringComparison.Ordinal);
    }
}

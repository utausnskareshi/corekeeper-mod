using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// What the conversion summary says about the right-facing and back-facing pictures.
///
/// The summary is the line that stays: it is written again on every conversion, so anything said
/// only when a picture is loaded is gone about 140ms later, when the conversion that load asks for
/// finishes - at once, when something is drawn, because the refusal to rebuild writes over it. A
/// facing that could be read only in part was said on loading and then never again, although the
/// command line warns about it and the window does so for the front on every conversion. And a
/// clipping caused by a facing wider than the front was reported with no cause at all: the
/// numbers on the line are the front's, which fits, and the advice beside it - a picture shaped
/// closer to the box - was about the front, so following it changed nothing (the test campaign
/// of 2026-09-30).
/// </summary>
public sealed class FacingSummaryTests : IDisposable
{
    private readonly LanguageOption _original = Loc.Instance.Current;

    public void Dispose() => Loc.Instance.Current = _original;

    private static string ViewModel() =>
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

    private static void Use(string language) =>
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

    private static SKBitmap Solid(int width, int height, SKColor colour)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, colour);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    // ------------------------------------------------------------ facing read in part

    [Fact]
    public void 向き別の絵の途中切れは差し替えと同じ場所で覚える()
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private async Task LoadFacingAsync(string? path, bool side)");

        // Written with the swap, under the gate and after the second supersede check - the same
        // rule TruncatedWarningTests holds the front to: set any earlier, a load that was called
        // off would leave a warning about a picture that is not in use.
        int gate = body.IndexOf("await _buildGate.WaitAsync();", StringComparison.Ordinal);
        int recheck = body.IndexOf("if (Superseded())", gate, StringComparison.Ordinal);
        Assert.True(gate >= 0 && recheck > gate, "ゲートを取った後の世代の照合が見つからない");

        foreach ((string swap, string flag) in new[]
                 {
                     ("_sideImage = decoded;", "_sideTruncated = decoded is not null && !complete;"),
                     ("_backImage = decoded;", "_backTruncated = decoded is not null && !complete;"),
                 })
        {
            int swapAt = body.IndexOf(swap, StringComparison.Ordinal);
            int flagAt = body.IndexOf(flag, StringComparison.Ordinal);
            Assert.True(swapAt > recheck, $"{swap} が照合の後に無い");
            Assert.True(flagAt > recheck, $"{flag} が照合の後に無い");
        }

        // Taking the picture over clears the local, so the flag has to be written before that
        int handedOver = body.IndexOf("decoded = null;", recheck, StringComparison.Ordinal);
        Assert.True(handedOver > body.IndexOf("_backTruncated =", StringComparison.Ordinal), "decoded を手放した後で途中切れを判定している");
    }

    [Fact]
    public void シートとして開いたら向き別の途中切れも忘れる()
    {
        // Opening a finished sheet throws both facings away. A flag left set would put a warning
        // about a picture no longer loaded into the summary of the next picture opened.
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private async Task LoadAsSheetAsync(");

        Assert.Contains("_sideTruncated = false;", body, StringComparison.Ordinal);
        Assert.Contains("_backTruncated = false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 変換の要約は向き別の途中切れを毎回言う()
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private async Task RebuildAsync(");

        Assert.Contains("bool sideTruncated = _sideTruncated;", body, StringComparison.Ordinal);
        Assert.Contains("bool backTruncated = _backTruncated;", body, StringComparison.Ordinal);

        // Composed inside the message, as the front's warning is, so a language switch rewrites it
        int message = body.IndexOf("SetStatus(() =>", StringComparison.Ordinal);
        Assert.True(message >= 0, "要約の SetStatus が見つからない");
        Assert.Contains("\"status.sideTruncated\"", body[message..], StringComparison.Ordinal);
        Assert.Contains("\"status.backTruncated\"", body[message..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "status.sideTruncated", "右向き")]
    [InlineData("ja", "status.backTruncated", "背面")]
    [InlineData("en", "status.sideTruncated", "right-facing")]
    [InlineData("en", "status.backTruncated", "back-facing")]
    public void 向き別の途中切れの警告は向きを名指しする(string language, string key, string facing)
    {
        Use(language);

        string text = Loc.Instance[key];

        // A line of its own, like the front's warning it sits beside
        Assert.StartsWith("\n", text, StringComparison.Ordinal);
        Assert.Contains(facing, text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ facing wider than the box

    [Fact]
    public void はみ出しが向き別の絵の幅によるならそう言う()
    {
        // The case measured: a 40x60 front fits the 16-wide box at 9x19, and a 120x40 right-facing
        // picture scaled to that height is 57 wide, well past the 26-pixel frame
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap wide = Solid(120, 40, SKColors.Green);

        SkinBuildResult built = SkinPipeline.Build(new SkinSources(front, wide), SheetLayout.LoadEmbedded(), new SkinOptions());
        using (built.Sheet)
        {
            Assert.True(built.RightClippedSideways > 0, "右向きが左右にはみ出す前提が崩れた");
            Assert.True(built.RightSize?.Width > built.BoxSize.Width, "右向きが配置幅を超える前提が崩れた");

            Use("ja");
            string ja = MainViewModel.WideFacingNote(
                built.RightSize, built.RightClippedSideways, built.UpSize, built.UpClippedSideways, built.BoxSize.Width);
            Assert.Contains($"右向き {built.RightSize!.Value.Width}", ja, StringComparison.Ordinal);
            Assert.Contains($"幅 {built.BoxSize.Width} を超えて", ja, StringComparison.Ordinal);
            Assert.Contains("正面より横長にならない絵", ja, StringComparison.Ordinal);
            Assert.DoesNotContain("背面", ja, StringComparison.Ordinal);

            Use("en");
            string en = MainViewModel.WideFacingNote(
                built.RightSize, built.RightClippedSideways, built.UpSize, built.UpClippedSideways, built.BoxSize.Width);
            Assert.Contains($"right-facing {built.RightSize.Value.Width}", en, StringComparison.Ordinal);
            Assert.Contains($"width of {built.BoxSize.Width}", en, StringComparison.Ordinal);
            Assert.DoesNotContain("右向き", en, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void はみ出しが背面の絵の幅によるならそう言う()
    {
        // The case above through the back. The back's count was only ever handed to the note by
        // hand, so a conversion that counted nothing for the back, or a pipeline that passed 0 on,
        // went unseen while the back's sentence was never said again (the final review of
        // 2026-10-02, X42e and X42f)
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap wide = Solid(120, 40, SKColors.Blue);

        SkinBuildResult built = SkinPipeline.Build(new SkinSources(front, null, wide), SheetLayout.LoadEmbedded(), new SkinOptions());
        using (built.Sheet)
        {
            Assert.True(built.UpClippedSideways > 0, "背面が左右にはみ出す前提が崩れた");
            Assert.True(built.UpSize?.Width > built.BoxSize.Width, "背面が配置幅を超える前提が崩れた");
            Assert.Equal(0, built.RightClippedSideways);

            Use("ja");
            string ja = MainViewModel.WideFacingNote(
                built.RightSize, built.RightClippedSideways, built.UpSize, built.UpClippedSideways, built.BoxSize.Width);
            Assert.Contains($"背面 {built.UpSize!.Value.Width}", ja, StringComparison.Ordinal);
            Assert.DoesNotContain("右向き", ja, StringComparison.Ordinal);

            Use("en");
            string en = MainViewModel.WideFacingNote(
                built.RightSize, built.RightClippedSideways, built.UpSize, built.UpClippedSideways, built.BoxSize.Width);
            Assert.Contains($"back-facing {built.UpSize.Value.Width}", en, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(9)]
    public void 向き別の絵がコマに収まりはみ出しが位置調整によるなら向き別の絵の幅を挙げない(int offsetY)
    {
        // A right-facing picture a little wider than the 16-wide box, 17 to 25, still fits the
        // 26-pixel frame. What a vertical offset cuts off is then the offset's doing, and naming the
        // facing's width sent the user to change a picture that was not the cause - the very mistake
        // the note was added to correct, the other way round (the test campaign of 2026-10-01).
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(17, 19, SKColors.Green);

        SkinBuildResult built = SkinPipeline.Build(
            new SkinSources(front, side), SheetLayout.LoadEmbedded(), new SkinOptions(OffsetY: offsetY));
        using (built.Sheet)
        {
            Assert.True(built.ClippedPixels > 0, "位置調整ではみ出す前提が崩れた");
            Assert.True(built.RightSize?.Width > built.BoxSize.Width, "右向きが配置幅を超える前提が崩れた");
            Assert.Equal(0, built.RightClippedSideways);

            Use("ja");
            Assert.Equal(string.Empty, MainViewModel.WideFacingNote(
                built.RightSize, built.RightClippedSideways, built.UpSize, built.UpClippedSideways, built.BoxSize.Width));
        }
    }

    [Theory]
    [InlineData(30, 0, 30, 0)]      // wide facings, but nothing of them was cut off at the sides: no new sentence
    [InlineData(16, 120, 12, 40)]   // cut off at the sides, but no facing is wider than the box: the cause lies elsewhere
    [InlineData(-1, 0, -1, 0)]      // no facing given at all
    public void はみ出しの原因が向き別の絵の幅でなければ何も足さない(int rightWidth, int rightSideways, int upWidth, int upSideways)
    {
        Use("ja");

        (int Width, int Height)? right = rightWidth < 0 ? null : (rightWidth, 19);
        (int Width, int Height)? up = upWidth < 0 ? null : (upWidth, 19);

        Assert.Equal(string.Empty, MainViewModel.WideFacingNote(right, rightSideways, up, upSideways, 16));
    }

    [Fact]
    public void 両方の向きが広ければ一文にまとめて両方を挙げる()
    {
        Use("ja");

        string text = MainViewModel.WideFacingNote((41, 19), 270, (30, 19), 40, 16);

        Assert.Contains("右向き 41", text, StringComparison.Ordinal);
        Assert.Contains("背面 30", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), line => line.Contains("幅の設定", StringComparison.Ordinal));
    }

    [Fact]
    public void 片方だけが左右にはみ出すならその向きだけを挙げる()
    {
        Use("ja");

        string text = MainViewModel.WideFacingNote((41, 19), 270, (20, 19), 0, 16);

        Assert.Contains("右向き 41", text, StringComparison.Ordinal);
        Assert.DoesNotContain("背面", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 変換の要約は向き別の絵の幅を写して要約の中で組み立てる()
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private async Task RebuildAsync(");

        // Copied out of the result like every other value there, since the result's sheet is
        // disposed before the message is written again after a language switch
        int message = body.IndexOf("SetStatus(() =>", StringComparison.Ordinal);
        int right = body.IndexOf("= built.RightSize;", StringComparison.Ordinal);
        int up = body.IndexOf("= built.UpSize;", StringComparison.Ordinal);
        Assert.True(right >= 0 && right < message, "built.RightSize を要約の前で写していない");
        Assert.True(up >= 0 && up < message, "built.UpSize を要約の前で写していない");
        int rightSideways = body.IndexOf("= built.RightClippedSideways;", StringComparison.Ordinal);
        int upSideways = body.IndexOf("= built.UpClippedSideways;", StringComparison.Ordinal);
        Assert.True(rightSideways >= 0 && rightSideways < message, "built.RightClippedSideways を要約の前で写していない");
        Assert.True(upSideways >= 0 && upSideways < message, "built.UpClippedSideways を要約の前で写していない");
        Assert.Contains("WideFacingNote(", body[message..], StringComparison.Ordinal);
        Assert.DoesNotContain("built.", body[message..], StringComparison.Ordinal);
    }
}

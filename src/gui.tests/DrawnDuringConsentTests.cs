using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Whether strokes drawn while a conversion or load runs survive it, when the user had agreed to
/// lose the drawing as it stood before.
///
/// That agreement covers the canvas as it was when the user asked, and the comments promise as
/// much. The landing check looked only at the document being swapped and at "unedited at the
/// start, edited now". When the document was already edited at the start - which is exactly when
/// agreement is asked for - drawing more on it left IsModified true as before, and the conversion
/// replaced it with no confirmation and no undo. Measured in a sandbox with the real view model:
/// one pixel drawn during "rebuild from settings", during opening a picture, and while a sheet
/// waited for the gate, was gone on landing each time with CanUndo false. A document unedited at
/// the start was protected.
///
/// Read from the source, as LoadBaselineTests is: the view model has no seam to hold a conversion
/// back.
/// </summary>
public sealed class DrawnDuringConsentTests
{
    private static string Body(string signature) => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        signature);

    [Fact]
    public void 同意のある変換は開始時に編集済みでも変換中の筆を見分ける()
    {
        string body = Body("private async Task RebuildAsync(");

        // The landing check compares the revision taken at the start with the current one
        Match check = Regex.Match(
            body, @"bool\s+editedSinceStart\s*=(?<expr>[^;]*);", RegexOptions.Singleline);
        Assert.True(check.Success, "RebuildAsync に editedSinceStart が見つからない");
        Assert.Contains("Revision", check.Groups["expr"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 完成シートの読み込みも開始時に編集済みで門を待つ間の筆を見分ける()
    {
        string body = Body("private async Task LoadAsSheetAsync(");

        // The check inside the gate that calls the load off looks at the revision too
        int gate = body.IndexOf("await _buildGate.WaitAsync();", StringComparison.Ordinal);
        int cancelled = body.IndexOf("SetStatus(\"status.loadCancelledByEdits\");", StringComparison.Ordinal);
        Assert.True(gate >= 0 && cancelled > gate, "LoadAsSheetAsync の門か取りやめの知らせが見つからない");
        Assert.Contains("Revision", body[gate..cancelled], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public async Task LoadImageAsync(string path)")]
    [InlineData("private async Task FetchFromGame(")]
    public void 読み込みは基準と同じ時点で変更回数も控える(string signature)
    {
        string body = Body(signature);

        // Taken where the baseline is taken. Taken later, whatever was drawn in between would count
        // as agreed to.
        int baseline = body.IndexOf("baseline = (Document, WouldDiscardEdits);", StringComparison.Ordinal);
        Assert.True(baseline >= 0, $"{signature} に基準の取得が見つからない");

        Match revision = Regex.Match(body, @"long\s+revisionAtStart\s*=\s*Document\?\.Revision\s*\?\?\s*0;");
        Assert.True(revision.Success, $"{signature} に変更回数の取得が無い");
        Assert.InRange(revision.Index, baseline - 400, baseline + 400);
    }
}

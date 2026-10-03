using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that a load takes its "nothing drawn" reading again when the only thing that replaced
/// the document was a conversion of the previous picture.
///
/// A load reads the document before the decode, so that anything drawn while the file is read is
/// protected. A settings change a moment earlier leaves a conversion of the previous picture
/// running, and when that one published while the new file waited for the gate, the new file's
/// conversion saw a different document, counted it as replaced and threw itself away: the new
/// file's name over the old picture, and a message naming a rebuild button that was not there.
/// Measured with the real view model on the Avalonia dispatcher (an 8000-pixel picture converting
/// while a small one is dropped): three times out of three before, never after, and a drawing made
/// on either document is still protected.
///
/// The first version of the retake also required that nothing had been drawn when the file was
/// chosen, and so missed the same race after a drawing the user had agreed to lose: draw, open a
/// large picture and accept, then open another while it converts and accept again - the new file's
/// name over the large picture's conversion, 3 times out of 3. The only conversion that can publish
/// over a drawing is one whose loss was agreed to, so the drawing at the start is not asked about.
///
/// Read off the source like <see cref="LoadOrderingTests"/>, for the same reason: reproducing it
/// needs a conversion still running when the next file arrives, and the view model takes no seam
/// for slowing one down.
/// </summary>
public sealed class LoadBaselineTests
{
    private static string Body(string signature) => LanguageNotificationTests.MethodBody(
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
        signature);

    [Fact]
    public void 画像の読み込みは変換だけで差し替わった文書を基準に取り直す()
    {
        string body = Body("public async Task LoadImageAsync(string path)");

        // Only when nothing is drawn on the document now showing, and inside the gate before the
        // picture is swapped in, so no other conversion can publish between this and the rebuild
        Match retake = Regex.Match(
            body,
            @"if\s*\(\s*!WouldDiscardEdits\s*&&\s*!ReferenceEquals\(Document,\s*baseline\.Document\)\s*\)\s*\{\s*" +
            @"baseline\s*=\s*\(Document,\s*false\);");
        Assert.True(retake.Success, "LoadImageAsync に基準の取り直しが無い");

        int gate = body.IndexOf("await _buildGate.WaitAsync();", StringComparison.Ordinal);
        int swap = body.IndexOf("_sourceImage = decoded;", StringComparison.Ordinal);
        Assert.True(gate >= 0 && swap >= 0, "LoadImageAsync の門か差し替えが見つからない");
        Assert.InRange(retake.Index, gate, swap);
    }

    [Fact]
    public void 完成シートの読み込みも取り直してから進行中の変換を打ち切る()
    {
        string body = Body("private async Task LoadAsSheetAsync(");

        Match retake = Regex.Match(
            body,
            @"if\s*\(\s*!WouldDiscardEdits\s*&&\s*!ReferenceEquals\(Document,\s*documentAtStart\)\s*\)\s*\{\s*" +
            @"documentAtStart\s*=\s*Document;\s*editedAtStart\s*=\s*false;");
        Assert.True(retake.Success, "LoadAsSheetAsync に基準の取り直しが無い");

        // Before the supersede: after it no conversion can publish, so only drawing is left to
        // change the document, which is what the check under the gate is there to catch
        int supersede = body.IndexOf("SupersedePendingBuild();", StringComparison.Ordinal);
        Assert.True(supersede > retake.Index, "取り直しが SupersedePendingBuild より後にある");
    }
}

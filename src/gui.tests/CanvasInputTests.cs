using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks two things about how the canvas starts and holds a stroke.
///
/// Read from the source, as the other checks on the window are: the canvas is an Avalonia
/// control and needs a live platform to exist, and what these pin down is the order of
/// statements inside two input handlers, which nothing driving it from outside could see.
/// </summary>
public sealed class CanvasInputTests
{
    private static string CanvasSource()
    {
        string path = Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(),
            "src", "gui", "Editing", "PixelCanvas.cs");

        Assert.True(File.Exists(path), $"PixelCanvas.cs が無い: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The body of one method, from its signature to the next member's signature.</summary>
    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} が見つからない");

        Match next = Regex.Match(
            source[(start + signature.Length)..],
            @"^\s{4}(protected|private|public|internal) ",
            RegexOptions.Multiline);

        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    [Fact]
    public void 画素に当たらない押下では描画を始めない()
    {
        // The hit test includes the right and bottom edges, so a press on the last grid line
        // arrives though it maps to no pixel. Starting the stroke there meant no start event was
        // raised, and the line and rectangle tools, which take their origin from it, painted
        // freehand along the drag instead. The check has to come before the stroke is begun.
        string pressed = Method(CanvasSource(), "protected override void OnPointerPressed(");

        int check = pressed.IndexOf("ToPixel(", StringComparison.Ordinal);
        int begin = pressed.IndexOf("_painting = true", StringComparison.Ordinal);

        Assert.True(check >= 0, "押下時に画素へ当たるかを確かめていない");
        Assert.True(begin >= 0, "描画を始める箇所が見つからない");
        Assert.True(check < begin, "画素へ当たるかを確かめる前に描画を始めている");
    }

    [Fact]
    public void どちらのボタンで描き始めてもキャンバスにフォーカスを移す()
    {
        // Avalonia moves focus on a left press only. After erasing with the right button - "right
        // button erases with any tool", as the help says - the keyboard stayed in the colour box the
        // user had last typed into: Ctrl+Z undid the colour box instead of the stroke, and a tool's
        // letter was typed into the colour, which then stopped painting or, with #F00 + "b" read as
        // #bbff0000, painted half transparent without a word (the test campaign of 2026-10-01).
        string pressed = Method(CanvasSource(), "protected override void OnPointerPressed(");

        int focus = pressed.IndexOf("Focus(NavigationMethod.Pointer)", StringComparison.Ordinal);
        int begin = pressed.IndexOf("_painting = true", StringComparison.Ordinal);
        int check = pressed.IndexOf("ToPixel(", StringComparison.Ordinal);

        Assert.True(focus >= 0, "押下でキャンバスにフォーカスを移していない");
        Assert.True(focus > check && focus < begin, "フォーカスを移すのが筆を始める押下だけになっていない");
    }

    [Fact]
    public void 直線や矩形のドラッグ中はEscで取りやめられる()
    {
        // There was no way to call a shape off once its preview showed it in the wrong place: the
        // canvas swallows the other button during a stroke (a stroke belongs to the button that
        // began it), Escape did nothing, and letting go wrote it - over every frame by default
        // (the test campaign of 2026-10-01). Escape is now taken while the button is down, before
        // the rule that ignores every other key then.
        string window = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "MainWindow.axaml.cs"));
        string keys = Method(window, "private void OnKeyDown(");

        int escape = keys.IndexOf("Key.Escape", StringComparison.Ordinal);
        int ignore = keys.IndexOf("if (Canvas.IsPainting)", StringComparison.Ordinal);
        Assert.True(escape >= 0 && escape < ignore, "描いている間の Esc を、他のキーを無視する前に受けていない");
        Assert.Contains("Model.CancelShapeInProgress()", keys[escape..ignore], StringComparison.Ordinal);

        string model = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "public bool CancelShapeInProgress()");
        Assert.Contains("_dragOrigin is null", model, StringComparison.Ordinal);
        Assert.Contains("CancelShape();", model, StringComparison.Ordinal);
        Assert.Contains("_shapeCalledOff = true;", model, StringComparison.Ordinal);

        // The rest of that stroke does nothing: with the origin gone, the shape tools painted
        // freehand along the remaining drag (measured: 6 pixels written after the shape was called
        // off). Forgotten at the next press and when the stroke ends.
        string pixel = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "public void HandlePixel(int x, int y, bool isStart, bool erase, bool interpolated)");
        int calledOff = pixel.IndexOf("else if (_shapeCalledOff)", StringComparison.Ordinal);
        int firstWrite = pixel.IndexOf("document.Paint(", StringComparison.Ordinal);
        Assert.True(calledOff >= 0 && calledOff < firstWrite, "取りやめた後の筆を、描く前に止めていない");
        Assert.Contains("_shapeCalledOff = false;", LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()), "public void EndStroke()"),
            StringComparison.Ordinal);

        // The other button calls a shape off as well, as the view model's comment had always said:
        // the canvas still keeps the stroke to the button that began it, and only says it was pressed
        string pressed = Method(CanvasSource(), "protected override void OnPointerPressed(");
        int guard = pressed.IndexOf("if (_painting)", StringComparison.Ordinal);
        int told = pressed.IndexOf("OtherButtonPressed?.Invoke(", StringComparison.Ordinal);
        Assert.True(guard >= 0 && told > guard && told < pressed.IndexOf("return;", guard, StringComparison.Ordinal),
            "描いている間のもう一方のボタンを知らせていない");
        Assert.Contains("Canvas.OtherButtonPressed += (_, _) => Model.CancelShapeInProgress();", window, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "Esc")]
    [InlineData("en", "Escape")]
    public void ヘルプは図形の取りやめ方を書いている(string language, string key)
    {
        string help = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "core", "Localization", $"help.{language}.json"));
        int line = help.IndexOf(language == "ja" ? "\"線（L）\"" : "\"Line (L)\"", StringComparison.Ordinal);
        Assert.True(line >= 0, "線の項目が見つからない");
        string entry = help[line..help.IndexOf('}', line)];
        Assert.Contains(key, entry, StringComparison.Ordinal);
    }

    [Fact]
    public void 描画中は表示をスクロールするキーを握りつぶす()
    {
        // The wheel is held still during a stroke; the keys were not. PageUp and PageDown are the
        // two the ScrollViewer scrolls on, and each joined the last sample to the next across the
        // whole distance scrolled - a line through the character, once per frame.
        string keyDown = Method(CanvasSource(), "protected override void OnKeyDown(");

        Assert.Contains("_painting", keyDown, StringComparison.Ordinal);
        Assert.Contains("Key.PageUp", keyDown, StringComparison.Ordinal);
        Assert.Contains("Key.PageDown", keyDown, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", keyDown, StringComparison.Ordinal);
    }
}

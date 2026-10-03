namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Whether the sheet a conversion publishes can be left disagreeing with the import settings on
/// screen. Read from the source, as the other view model checks are: the view model looks for the
/// real game when it is built.
///
/// While "rebuild from settings", or opening a picture, runs over a drawing the user agreed to
/// lose, a settings change goes to RequestRebuild, which refuses it because the old document is
/// still edited, raises the "settings changed" flag and says a rebuild is needed. The conversion
/// then landed a fresh, unedited sheet built from the settings read when it started:
/// NeedsRegenerate went false, the summary replaced the message, and the sheet silently did not
/// match the settings shown (measured in a sandbox with the real view model: 2765 pixels off after
/// toggling the outline, 6211 after moving the offset). Saving or applying it then wrote the old
/// settings out.
///
/// The other way round, a picture opened after such a refusal is converted with the current
/// settings, yet the flag stayed raised: one pixel drawn on it brought back "rebuild from
/// settings" for settings already applied, and pressing it threw the drawing away to produce the
/// very same sheet.
///
/// Both come down to comparing, once the sheet is published, the settings it was built from with
/// the ones on screen now.
/// </summary>
public sealed class PublishedSettingsTests
{
    private const string Publish = "Document = new EditorDocument(built.Sheet, _layout);";

    /// <summary>The part of RebuildAsync from publishing the sheet to announcing NeedsRegenerate.</summary>
    private static string AfterPublish()
    {
        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "private async Task RebuildAsync(");

        int publish = body.IndexOf(Publish, StringComparison.Ordinal);
        Assert.True(publish >= 0, "RebuildAsync で表を公開する箇所が見つからない");

        int notify = body.IndexOf("OnPropertyChanged(nameof(NeedsRegenerate));", publish, StringComparison.Ordinal);
        Assert.True(notify > publish, "公開の後で NeedsRegenerate を知らせていない");

        return body[(publish + Publish.Length)..notify];
    }

    [Fact]
    public void 公開した表の設定と今の設定を比べる()
    {
        // The options handed to the conversion against the settings as they stand at publication
        Assert.Matches(@"BuildOptions\(\)\s*[=!]=\s*options|options\s*[=!]=\s*BuildOptions\(\)", AfterPublish());
    }

    [Fact]
    public void 今の設定で作った表を公開したら作り直しの印を下ろす()
    {
        // A sheet built from the current settings leaves nothing outstanding, whatever was refused before
        Assert.Contains("_settingsChangedSinceBuild = false;", AfterPublish(), StringComparison.Ordinal);
    }

    [Fact]
    public void 変換の間に変わった設定は公開の後で作り直しに回す()
    {
        // A change refused during the conversion is asked for again over the unedited sheet just
        // published. Without it the sheet keeps the old settings and both the message and the
        // button disappear.
        Assert.Contains("RequestRebuild();", AfterPublish(), StringComparison.Ordinal);
    }
}

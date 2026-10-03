using CoreKeeperSkinTool.Gui.Localization;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// What the window says when "Remove mod" deletes only part of what it planned to.
///
/// Both targets are called CustomPlayerSkin - the mod in the game folder and its settings and
/// pictures in the user's data - and the report named each by its last segment only, so it could
/// not say which one was left, where, or what to do next; the install button had meanwhile turned
/// into "Install mod", so pressing it again was no way to finish. Read off the source, because the
/// view model looks for the real game as it is built.
/// </summary>
public sealed class RemovalReportTests : IDisposable
{
    private readonly LanguageOption _original = Loc.Instance.Current;

    public void Dispose() => Loc.Instance.Current = _original;

    [Fact]
    public void 一部を削除できなかったときは種類名とフルパスで示す()
    {
        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "public void ExecuteRemoval(RemovalPlan plan)");

        // The same names the confirmation dialog uses for the two kinds of target, each for its own
        Assert.Contains("\"uninstall.kindMod\"", body, StringComparison.Ordinal);
        Assert.Contains("\"uninstall.kindConfig\"", body, StringComparison.Ordinal);
        Assert.Matches(@"RemovalKind\.ModInstall\s*\?\s*""uninstall\.kindMod""", body);
        Assert.DoesNotContain("Path.GetFileName(f.Path)}: {f.Reason}", body, StringComparison.Ordinal);

        // The whole path, not its last segment
        Assert.Contains("{f.Path}: {f.Reason}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 一部を削除できなかったときの種類名は言語を切り替えると訳し直す()
    {
        // Resolved where the reasons were put together and passed in as a finished string, the kind
        // stayed in the language of the moment of removal while the sentence around it changed:
        // "Removed 1 location(s), but some could not be deleted: [MOD 本体] ..." (the test campaign of
        // 2026-10-01). Composed inside the message, as every other summary here is.
        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "public void ExecuteRemoval(RemovalPlan plan)");

        int partial = body.IndexOf("\"status.uninstallPartial\"", StringComparison.Ordinal);
        Assert.True(partial >= 0, "status.uninstallPartial が見つからない");

        int message = body.LastIndexOf("SetStatus(() =>", partial, StringComparison.Ordinal);
        Assert.True(message >= 0, "一部失敗の報告が SetStatus(() => の中で組み立てられていない");

        // Only as far as the return that ends the partial report: the full report further down
        // looks up a text of its own, which must not count
        int end = body.IndexOf("return;", partial, StringComparison.Ordinal);
        Assert.True(end > partial, "一部失敗の報告の後の return が見つからない");
        Assert.Contains("Loc.Instance[f.KindKey]", body[message..end], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "エクスプローラー", "手動で削除")]
    [InlineData("en", "Explorer", "by hand")]
    public void 一部を削除できなかったときは次にすることを言う(string language, string program, string byHand)
    {
        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == language);

        string text = Loc.Instance.Format("status.uninstallPartial", 1, "REASONS");

        Assert.Contains("1", text, StringComparison.Ordinal);
        Assert.Contains("REASONS", text, StringComparison.Ordinal);
        Assert.Contains(program, text, StringComparison.Ordinal);
        Assert.Contains(byHand, text, StringComparison.Ordinal);
    }
}

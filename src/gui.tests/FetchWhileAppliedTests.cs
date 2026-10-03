namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that fetching a character with a picture applied says the look it brings is old.
///
/// The mod never captures a character it is drawing over, so once a picture is applied the
/// captured file stops moving. Fetching such a character still brought back that file - how the
/// character looked just before the picture went on, possibly long before - and said "took the
/// in-game appearance", while the same character with no captured file was refused as "cannot be
/// taken". The picture read is unchanged; only what the status line says about it is.
///
/// Read off the source because the fetch reads the game's own settings folder, which is found from
/// the user profile and has no seam a test could point elsewhere.
/// </summary>
public sealed class FetchWhileAppliedTests
{
    [Fact]
    public void 適用中のキャラクターの取得は古い見た目だと告げる()
    {
        string body = LanguageNotificationTests.MethodBody(
            LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource()),
            "private async Task FetchFromGame(CharacterChoice? choice)");

        int loaded = body.IndexOf("if (SourcePath == path)", StringComparison.Ordinal);
        Assert.True(loaded >= 0, "読み込みが済んだ後の分岐が見つからない");
        string after = body[loaded..];

        // Asked again from the folder after the load, as the refusal above asks it
        Assert.Matches(@"CharacterSkins\s*\.InstalledGuids\([^;]*\.Contains\(choice\.Character\.Guid\)", after);
        Assert.Contains("\"status.fetchedWhileApplied\"", after, StringComparison.Ordinal);
        Assert.Contains("\"status.fetched\"", after, StringComparison.Ordinal);
    }
}

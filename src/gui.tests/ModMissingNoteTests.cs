namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that placing a picture and turning the gear toggle say so when the mod is not in the game.
///
/// Both said "if the game is running it updates within a few seconds" whatever the state of the
/// mod, and the row turned to "applied". With the mod not installed nothing in the game reads the
/// file at all, and on 1.3.0.2 the loader scans the Mods folder only once, as the game starts, so
/// installing it afterwards still needs a restart. The note is added only when the game was found
/// and the mod can be installed: IsModInstalled alone is also false when the game was not found or
/// its state could not be read, and saying "the mod is missing" then would be a guess.
///
/// Read off the source because both write to the game's own settings folder, which is found from
/// the user profile and has no seam a test could point elsewhere.
/// </summary>
public sealed class ModMissingNoteTests
{
    private static string ViewModel() =>
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

    [Fact]
    public void 注記はゲームが見つかりMODを導入できて未導入のときだけ出す()
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "private Func<string> ModMissingNote()");

        Assert.Matches(@"IsGameFound\s*&&\s*CanInstallMod\s*&&\s*!IsModInstalled", body);
        Assert.Contains("\"status.modMissingNote\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public void InstallToGame()")]
    [InlineData("partial void OnShowGameGearChanged(bool value)")]
    public void 反映を告げる表示に注記を添える(string signature)
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), signature);

        // Taken before the message is set, and appended inside it, so a language switch rewrites
        // the same message rather than one that forgot the note
        Assert.Matches(@"Func<string>\s+missing\s*=\s*ModMissingNote\(\);", body);
        Assert.Matches(@"SetStatus\(\(\)\s*=>[^;]*\+\s*note\(\)\s*\+\s*missing\(\)\);", body);
    }
}

using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that the gear toggle goes back when writing the setting fails.
///
/// It stayed on the new value: before the game had made its mods folder the pill said the gear
/// showed while the game hid it, and after a write that stopped part way the "run it again"
/// the message gives asked for the opposite on the next press.
///
/// Read off the source because the write goes to the game's own settings folder, which is found
/// from the user profile and has no seam a test could point elsewhere. How the revert has to be
/// made was measured on Avalonia 12 with the same binding and the same toolkit setter: put back
/// inside the change handler, the view model went back and the pill stayed pressed, and the next
/// click wrote nothing; posted to the dispatcher, pill and view model agreed at every step.
/// </summary>
public sealed class GearToggleTests
{
    private static string ViewModel() =>
        LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

    [Fact]
    public void 書き込みに失敗したら防具トグルを戻す()
    {
        string body = LanguageNotificationTests.MethodBody(ViewModel(), "partial void OnShowGameGearChanged(bool value)");

        // Both catches, each after its message
        Assert.Matches(@"catch\s*\(ToolException\s+ex\)\s*\{\s*SetStatus\(ex\);\s*RevertGearToggle\(value\);\s*\}", body);
        Assert.Matches(@"catch\s*\(Exception\s+ex\)\s*\{\s*SetStatus\(""status\.gearFailed"",\s*ex\);\s*RevertGearToggle\(value\);\s*\}", body);
    }

    [Fact]
    public void 防具トグルは後から戻しディスクから読み直さない()
    {
        string source = ViewModel();
        int at = source.IndexOf("private void RevertGearToggle(bool attempted)", StringComparison.Ordinal);
        Assert.True(at >= 0, "RevertGearToggle が見つからない");

        int end = source.IndexOf("});", at, StringComparison.Ordinal);
        Assert.True(end > at, "RevertGearToggle の終わりが見つからない");
        string body = source[at..end];

        Assert.Contains("Dispatcher.UIThread.Post(", body, StringComparison.Ordinal);

        // Put back without writing again, and the flag always lowered, or no later press would write
        Assert.Matches(
            @"_syncingGearSetting\s*=\s*true;\s*try\s*\{\s*ShowGameGear\s*=\s*!attempted;\s*\}\s*" +
            @"finally\s*\{\s*_syncingGearSetting\s*=\s*false;\s*\}",
            body);

        // A mixed state reads as "shown", so syncing from disk would make the retry flip the wrong way
        Assert.DoesNotContain("SyncGearSetting", body, StringComparison.Ordinal);
    }
}

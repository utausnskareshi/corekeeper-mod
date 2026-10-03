using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Whether the character list follows the account the window applies to.
///
/// The list is read through the most recently used mods folder as it stood when the list was
/// built, while "ゲームへ配置", "ゲームから削除", fetching and the gear toggle choose that folder
/// again when pressed. Which one comes first follows the README.txt the game rewrites at every
/// start, and the list was rebuilt only when the save folders changed. With two Steam accounts on
/// one Windows user, starting the game with the other account and coming back without saving left
/// the list showing the first account while "ゲームへ配置" wrote its characters' pictures into the
/// second account's folder and marked them applied (measured in a sandbox with the real view
/// model). A mods folder appearing after start-up, when the list had been read from every account,
/// was missed the same way.
/// </summary>
public sealed class CharacterListAccountTests
{
    private static ModConfigLocation Account(string id) =>
        new(Path.Combine(@"C:\Users\player\AppData\LocalLow\Pugstorm\Core Keeper\Steam", id, "mods"), "Steam", id);

    [Fact]
    public void 一覧の指紋は元にした設定フォルダが変われば変わる()
    {
        const string saves = "saves|1|2";

        Assert.NotEqual(
            MainViewModel.CharactersFingerprint(Account("111"), saves),
            MainViewModel.CharactersFingerprint(Account("222"), saves));

        // No mods folder yet, and then one: the list read from every account has to be read again
        Assert.NotEqual(
            MainViewModel.CharactersFingerprint(null, saves),
            MainViewModel.CharactersFingerprint(Account("111"), saves));

        // Nothing changed, nothing to rebuild - it is compared on every activation of the window
        Assert.Equal(
            MainViewModel.CharactersFingerprint(Account("111"), saves),
            MainViewModel.CharactersFingerprint(Account("111"), saves));

        // The save folders still count on their own
        Assert.NotEqual(
            MainViewModel.CharactersFingerprint(Account("111"), saves),
            MainViewModel.CharactersFingerprint(Account("111"), "saves|1|3"));
    }

    [Fact]
    public void 一覧を作ったときと比べるときに同じ指紋を使う()
    {
        string source = LanguageNotificationTests.ViewModelClass(LanguageNotificationTests.ViewModelSource());

        // Compared on activation against the folder that comes first now...
        string check = LanguageNotificationTests.MethodBody(source, "public void RefreshCharactersIfChanged()");
        Assert.Contains("GameLocator.FindModConfigLocations()", check, StringComparison.Ordinal);
        Assert.Matches(@"fingerprint\s*=\s*CharactersFingerprint\(", check);

        // ...and recorded with the folder the list was actually read through
        string build = LanguageNotificationTests.MethodBody(source, "public void RefreshCharacters()");
        Assert.Matches(@"_charactersFingerprint\s*=\s*CharactersFingerprint\(\s*location\s*,", build);
    }
}

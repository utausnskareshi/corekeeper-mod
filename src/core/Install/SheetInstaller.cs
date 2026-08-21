using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Install;

/// <summary>Installs a generated sprite sheet where the mod reads it.</summary>
public static class SheetInstaller
{
    /// <summary>Folder name used by the mod. Must match the mod's <c>RootDirectory</c>.</summary>
    public const string DefaultModFolderName = "CustomPlayerSkin";

    // DefaultSkinFileName ("Skin.png") went with the removed Install. It described the single
    // shared image of an earlier design; each character now has its own file named after that
    // character, under CustomPlayerSkin/skins/, and nothing read the constant any more.

    // Installing is CharacterSkins.Install's job: every character has its own picture, so the
    // destination depends on which character it is for. A whole-mod Install and its atomic copy
    // used to live here too, but nothing in the product called either of them - the only callers
    // were tests, which meant six checks of "a broken sheet never reaches the game" were
    // guarding a route no user could take, while the route users do take checks the mod folder
    // name and this one did not. What remains is Verify, which CharacterSkins calls.

    /// <summary>
    /// Checks the two things that would otherwise fail silently in game: the sheet has the
    /// dimensions the layout expects, and it is not blank. Individual frames are not inspected;
    /// an empty frame is a legitimate choice, an empty sheet is an invisible character.
    /// </summary>
    public static void Verify(string sheetPath, SheetLayout layout)
    {
        using SKBitmap sheet = PixelOps.Decode(sheetPath, out bool complete);

        // A file that stopped part-way through - a download cut short, a copy from a disconnected
        // drive - decodes into an image of the right size whose missing part is transparent. Both
        // checks below then pass, and the character goes into the game with its lower half gone.
        // Nothing later can tell the difference, so it has to be refused here.
        if (!complete)
        {
            throw new ToolException(
                "error.sheet.truncated", [sheetPath],
                $"シートが途中で切れているため配置しない: {sheetPath}" + Environment.NewLine +
                "  ファイルが最後まで保存されていない可能性がある。作り直すこと。");
        }

        if (sheet.Width != layout.Texture.Width || sheet.Height != layout.Texture.Height)
        {
            throw new ToolException(
                "error.sheet.badSize",
                [sheet.Width, sheet.Height, layout.Texture.Width, layout.Texture.Height],
                $"シートの寸法が違うため配置しない: {sheet.Width}x{sheet.Height} " +
                $"(期待値 {layout.Texture.Width}x{layout.Texture.Height})" + Environment.NewLine +
                "  cks generate で作り直すこと。");
        }

        SKColor[] pixels = sheet.Pixels;
        if (pixels.All(p => p.Alpha == 0))
        {
            throw new ToolException(
                "error.sheet.transparent", [],
                "シートが完全に透明なため配置しない。キャラクターが見えなくなる。");
        }
    }
}

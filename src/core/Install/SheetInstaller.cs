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

        // The file is copied into the game byte for byte as <guid>.png, and the game reads PNG only:
        // its player has no WEBP or BMP decoder, and a JPEG carries no transparency. Such a file
        // decodes here - SkiaSharp reads them all - so every check below passed, the character was
        // reported as applied, and in the game it stayed as it was with a warning in the log.
        if (!HasPngSignature(sheetPath))
        {
            throw new ToolException(
                "error.sheet.notPng", [sheetPath],
                $"シートが PNG 形式ではないため配置しない（ゲームは PNG しか正しく読めない）: {sheetPath}" +
                Environment.NewLine + "  画像編集ソフトで PNG 形式で保存し直すこと。");
        }

        // A file that stopped part-way through - a download cut short, a copy from a disconnected
        // drive - decodes into an image of the right size whose missing part is transparent. Both
        // checks below then pass, and the character goes into the game with its lower half gone.
        // Nothing later can tell the difference, so it has to be refused here. A file missing only
        // its last few bytes decodes whole, and the game still cannot read it; the message fits it
        // as it stands, so the same key says it.
        if (!complete || PngEndIsMissing(sheetPath))
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

    /// <summary>The eight bytes every PNG file starts with.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Whether a file is a PNG by its content, not its name: a picture saved as WEBP or JPEG and
    /// then renamed .png is not one.
    /// </summary>
    public static bool HasPngSignature(string path)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[8];
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
               && head.SequenceEqual(PngSignature);
    }

    /// <summary>IEND as every PNG ends: length 0, the type, and the CRC of that type.</summary>
    private static readonly byte[] CanonicalIend = [0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    /// <summary>
    /// Whether a PNG lacks its proper end: the chunks, walked from the signature, run out before an
    /// IEND, or the IEND there is not the one every PNG carries.
    ///
    /// Checked apart from the decode, which cannot see it. SkiaSharp reports a PNG as whole once
    /// the last row is out and reads nothing after, so a file missing its last twenty-odd bytes -
    /// the end of the compressed data, its checksums and IEND - decodes with every pixel intact.
    /// The game's Texture2D.LoadImage refuses such a file, and one whose IEND has a wrong CRC as
    /// well (measured with the game's own UnityPlayer.dll on 1.3.0.3). Anything after IEND is not
    /// looked at: the game reads those files. Only meant for a file with the PNG signature.
    /// </summary>
    public static bool PngEndIsMissing(string path)
    {
        byte[] data;
        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }

        // Every chunk is a 4-byte length, a 4-byte type, the data and a 4-byte CRC. Counted in
        // long, so a corrupt length cannot wrap round to a position that looks valid.
        long position = PngSignature.Length;
        while (position + 12 <= data.Length)
        {
            ReadOnlySpan<byte> chunk = data.AsSpan((int)position);
            if (chunk[4..8].SequenceEqual("IEND"u8))
            {
                return !chunk[..12].SequenceEqual(CanonicalIend);
            }

            long length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(chunk);
            position += 12 + length;
        }

        return true;
    }
}

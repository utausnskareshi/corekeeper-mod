using System.Text.Json;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Install;

/// <summary>
/// Reads the appearance the mod wrote out from inside the running game.
///
/// The game keeps no picture of a character anywhere. A save file records which parts and colours
/// were chosen - as small indices in the older format, as data block identifiers in the newer one -
/// and the pixels themselves live in the game's asset bundles, tinted by a shader as they are
/// drawn. The finished appearance therefore exists only on screen, which is why the mod is the one
/// that writes it out: it draws each layer through the game's own material, so the colours come
/// out resolved, and stacks the results into one image with a band per layer.
///
/// The bands are kept apart rather than flattened in the game because the choice of what to
/// include belongs here. Equipment is worn, not part of how a character looks, and someone
/// starting a drawing from their character rarely wants a helmet baked into it. Flattening in the
/// mod would have made that choice cost a restart of the game.
/// </summary>
public static class CapturedSkins
{
    /// <summary>Folder the mod writes into, inside the mod's own settings folder.</summary>
    public const string FolderName = "captured";

    /// <summary>Extension of the files the mod writes.</summary>
    public const string Extension = ".png";

    /// <summary>Name of the file recording how the bands were written.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// The band layout this tool understands.
    ///
    /// Checked rather than assumed. A mod writing bands in another order or of another kind would
    /// otherwise be composited into a picture that is wrong in a way nothing reports: the result
    /// is still a valid sheet of the right size, just with the wrong layers on top.
    /// </summary>
    public const int SupportedFormat = 1;

    /// <summary>
    /// The bands, in the order the game draws them. Earlier ones sit underneath.
    /// </summary>
    public static readonly string[] Layers =
    [
        "body", "hair", "hairShade", "eyes", "shirt", "pants", "helm", "breastArmor", "pantsArmor",
    ];

    /// <summary>
    /// The bands that show equipment rather than the character's own appearance.
    /// These are the ones left out unless the caller asks for them.
    /// </summary>
    public static readonly string[] EquipmentLayers = ["helm", "breastArmor", "pantsArmor"];

    /// <summary>The folder holding every captured appearance.</summary>
    public static string DirectoryFor(string modsDirectory, string modFolderName)
    {
        CharacterSkins.EnsureModReadsThisFolder(modFolderName);

        return Path.Combine(modsDirectory, modFolderName, FolderName);
    }

    /// <summary>The captured appearance of one character.</summary>
    public static string PathFor(string modsDirectory, string modFolderName, string guid)
    {
        // The identifier reaches a file path, so it is checked rather than trusted, exactly as it
        // is on the way in. A save file is not something the user edits, but it is still input.
        if (!CharacterLocator.IsValidGuid(guid))
        {
            throw new ToolException(
                "error.character.badGuid", [guid], $"キャラクターの識別子が不正: {guid}");
        }

        return Path.Combine(DirectoryFor(modsDirectory, modFolderName), guid.ToLowerInvariant() + Extension);
    }

    /// <summary>The manifest describing how the bands in this folder were written.</summary>
    public static string ManifestPathFor(string modsDirectory, string modFolderName) =>
        Path.Combine(DirectoryFor(modsDirectory, modFolderName), ManifestFileName);

    /// <summary>
    /// The characters the mod has written an appearance for.
    ///
    /// Read from the folder rather than from the character list, so a capture belonging to a
    /// character since deleted is simply not matched by anything.
    ///
    /// Its one caller uses it to tell two reasons apart when there is nothing to fetch: nothing
    /// captured at all usually means the mod has not run yet, while captures for other characters
    /// mean it has and this character has not been played since. Failure gives back an empty set,
    /// which lands on the first of those - the more useful advice of the two when unsure.
    /// </summary>
    public static IReadOnlySet<string> CapturedGuids(string modsDirectory, string modFolderName)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        string directory;
        try
        {
            directory = DirectoryFor(modsDirectory, modFolderName);
        }
        catch (Exception)
        {
            return found;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return found;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*" + Extension))
            {
                string guid = Path.GetFileNameWithoutExtension(file);
                if (CharacterLocator.IsValidGuid(guid))
                {
                    found.Add(guid.ToLowerInvariant());
                }
            }
        }
        catch (Exception)
        {
            // Nothing to offer is the honest answer for a folder that cannot be listed
        }

        return found;
    }

    /// <summary>
    /// Checks that the folder was written by a mod this tool agrees with.
    ///
    /// Called before the image is read, so a mod whose bands mean something else is turned away
    /// with an answer the user can act on rather than a picture that is quietly wrong.
    /// </summary>
    public static void EnsureFormatUnderstood(string modsDirectory, string modFolderName)
    {
        string path = ManifestPathFor(modsDirectory, modFolderName);

        // Not there and unreadable need different advice. Updating the mod is what fixes the
        // first; it cannot touch a folder the user has no permission to read, or a file another
        // program is holding open, so saying it for those would send them after the wrong thing.
        if (!File.Exists(path))
        {
            throw new ToolException(
                "error.capture.manifestMissing",
                [path],
                $"取り込みの書式ファイルが無い: {path}" + Environment.NewLine +
                "  MOD を更新してから、ゲームでそのキャラクターを一度読み込むこと。");
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ToolException(
                "error.capture.manifestFailed",
                [path, ex.Message],
                $"取り込みの書式ファイルを読めない: {path}" + Environment.NewLine +
                $"  {ex.Message}");
        }

        CaptureManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CaptureManifest>(text, ManifestFormat);
        }
        catch (JsonException ex)
        {
            throw new ToolException(
                "error.capture.manifestUnreadable",
                [path],
                $"取り込みの書式ファイルが壊れている: {path}（{ex.Message}）");
        }

        if (manifest is null || manifest.Format != SupportedFormat)
        {
            throw new ToolException(
                "error.capture.formatMismatch",
                [manifest?.Format ?? 0, SupportedFormat],
                $"取り込みの書式が違う: {manifest?.Format ?? 0}（このアプリが読めるのは {SupportedFormat}）" +
                Environment.NewLine + "  MOD とアプリの版を揃えること。");
        }

        // The band order decides which layer ends up on top, so a different one is refused
        // outright. Compositing it anyway produces a sheet of the right size and the wrong picture.
        if (manifest.Layers is null || !manifest.Layers.SequenceEqual(Layers))
        {
            throw new ToolException(
                "error.capture.layersMismatch",
                [string.Join(", ", manifest.Layers ?? []), string.Join(", ", Layers)],
                "取り込みのレイヤー構成が違う。" + Environment.NewLine +
                $"  MOD: {string.Join(", ", manifest.Layers ?? [])}" + Environment.NewLine +
                $"  アプリ: {string.Join(", ", Layers)}");
        }
    }

    /// <summary>
    /// Builds one sheet from a captured file.
    /// </summary>
    /// <param name="path">The captured image, one band per layer stacked downwards.</param>
    /// <param name="layout">Geometry of a single sheet, which every band matches.</param>
    /// <param name="includeEquipment">
    /// Whether to draw the helmet and armour bands. False leaves the character as they look
    /// underneath what they happen to be wearing.
    /// </param>
    public static SKBitmap Compose(string path, SheetLayout layout, bool includeEquipment)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Texture is not { Width: > 0, Height: > 0 })
        {
            throw new ToolException("レイアウトにシートの寸法が無い。");
        }

        int width = layout.Texture.Width;
        int height = layout.Texture.Height;

        using SKBitmap stacked = PixelOps.Decode(path, out bool complete);

        // A file read while the game is still writing it comes back short, with the missing part
        // transparent. That is worth saying rather than compositing: the result would be a
        // character with layers silently absent, which looks like a deliberate drawing.
        if (!complete)
        {
            throw new ToolException(
                "error.capture.truncated",
                [path],
                $"取り込んだ画像が途中で切れている: {path}" + Environment.NewLine +
                "  ゲームが書き込んでいる最中の可能性がある。少し待ってからもう一度試すこと。");
        }

        int expectedHeight = height * Layers.Length;
        if (stacked.Width != width || stacked.Height != expectedHeight)
        {
            throw new ToolException(
                "error.capture.badSize",
                [stacked.Width, stacked.Height, width, expectedHeight],
                $"取り込んだ画像の寸法が違う: {stacked.Width}x{stacked.Height}" +
                $"（期待値 {width}x{expectedHeight}）");
        }

        SKBitmap result = PixelOps.CreateEmpty(width, height);

        SKColor[] source = stacked.Pixels;
        SKColor[] target = result.Pixels;

        for (int band = 0; band < Layers.Length; band++)
        {
            if (!includeEquipment && EquipmentLayers.Contains(Layers[band]))
            {
                continue;
            }

            int offset = band * height * width;

            for (int i = 0; i < target.Length; i++)
            {
                target[i] = Over(source[offset + i], target[i]);
            }
        }

        result.Pixels = target;
        return result;
    }

    /// <summary>
    /// Lays one pixel over another, the way the game stacks the layers.
    ///
    /// Written out rather than left to a canvas because the working bitmaps hold unpremultiplied
    /// colour: handing those to a drawing call asks it to guess, and a wrong guess shows up as a
    /// dark fringe around every semi-transparent edge - which is most of the hair.
    /// </summary>
    private static SKColor Over(SKColor top, SKColor bottom)
    {
        if (top.Alpha == byte.MaxValue || bottom.Alpha == 0)
        {
            return top.Alpha == 0 ? bottom : top;
        }

        if (top.Alpha == 0)
        {
            return bottom;
        }

        // Source-over on unpremultiplied values. Scaled to 255 * 255 so the whole of it stays in
        // integer arithmetic, with the divide done once at the end.
        int topAlpha = top.Alpha;
        int bottomShare = bottom.Alpha * (255 - topAlpha) / 255;
        int outAlpha = topAlpha + bottomShare;

        if (outAlpha == 0)
        {
            return SKColors.Transparent;
        }

        byte Mix(byte topChannel, byte bottomChannel) =>
            (byte)(((topChannel * topAlpha) + (bottomChannel * bottomShare) + (outAlpha / 2)) / outAlpha);

        return new SKColor(
            Mix(top.Red, bottom.Red),
            Mix(top.Green, bottom.Green),
            Mix(top.Blue, bottom.Blue),
            (byte)outAlpha);
    }

    /// <summary>
    /// How the manifest is read. Case-insensitive on purpose: the mod writes camelCase, which is
    /// what a JSON file should look like, and the default reader matches names exactly - so
    /// "format" would have quietly left the version at zero and reported a mismatch that was not.
    /// </summary>
    private static readonly JsonSerializerOptions ManifestFormat =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>What the mod recorded about the way it wrote the bands.</summary>
    private sealed record CaptureManifest(int Format, string[]? Layers);
}

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Export;

/// <summary>What the person exporting chose on the export screen.</summary>
/// <param name="DisplayName">The mod's name, as it is also entered on mod.io.</param>
/// <param name="Version">Version of this file, as it is also entered on mod.io.</param>
/// <param name="ShowGear">Whether the game's clothes and armour are drawn over the picture.</param>
public sealed record ModIoExportOptions(string DisplayName, string Version, bool ShowGear);

/// <summary>The names a written mod goes by inside the game.</summary>
/// <param name="Name">ModManifest name: <see cref="ModIoExporter.NamePrefix"/> and twelve hex digits.</param>
/// <param name="Guid">ModManifest guid: thirty-two lowercase hex digits.</param>
public sealed record ModIoIdentity(string Name, string Guid);

/// <summary>Everything one export needs besides the picture.</summary>
/// <param name="Options">What was chosen on the export screen.</param>
/// <param name="GameVersionTag">The mod.io Game Version tag to ask for, such as 1.3.0.</param>
/// <param name="ToolVersion">This tool's version, recorded in the mod's settings.</param>
/// <param name="LanguageCode">Language of the registration memo, such as ja or en.</param>
public sealed record ModIoExportRequest(
    ModIoExportOptions Options, string GameVersionTag, string ToolVersion, string LanguageCode);

/// <summary>The files one export wrote, and the names the mod goes by.</summary>
public sealed record ModIoExportResult(
    string ZipPath, string LogoPath, string GuidePath, ModIoIdentity Identity);

/// <summary>
/// One of the export's files could not be written, and which one.
///
/// A destination another program holds open fails the final move with "access denied" rather than
/// a sharing violation (measured on Windows 11 with File.Move over a file opened FileShare.None), the
/// same as a read-only file or a folder that refuses writing - so the cause is not told apart, only
/// the file is named.
/// </summary>
public sealed class ModIoWriteException : IOException
{
    public ModIoWriteException(string path, Exception inner)
        : this(path, inner, [])
    {
    }

    public ModIoWriteException(string path, Exception inner, IReadOnlyList<string> replaced)
        : base(inner.Message, inner)
    {
        ArgumentNullException.ThrowIfNull(replaced);

        FilePath = path;
        Replaced = replaced;
        HResult = inner.HResult;
    }

    /// <summary>The file that could not be written.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Files of this export already moved into place before the failure, in the order they were
    /// moved; empty when nothing had been replaced. Each is complete on its own, but they no longer
    /// match the files that were left as they were.
    /// </summary>
    public IReadOnlyList<string> Replaced { get; }
}

/// <summary>
/// Writes a skin as a mod that can be uploaded to mod.io, so that players can turn it on from the
/// game's own mod menu instead of having this tool place it.
///
/// What the game needs from the zip was read out of its own loader (PugMod.Platform's ModIOLoader
/// and PugMod.Loader's Loader, 1.3.0.2, unchanged in 1.3.0.4): ModManifest.json at the root of the
/// zip, and every script named in its files list. The scripts are compiled by the game when it
/// starts, so no Unity and no asset bundle is involved. The picture travels as a file of its own
/// and the scripts read it back through LoadedMod.GetFile.
///
/// Each export gets a name and guid of its own, made up on the spot. The loader keeps only one of
/// any mods sharing a name, silently, so two zips written by this tool must never share one: a
/// second skin put on mod.io as a page of its own then installs and loads beside the first. An
/// upload that replaces the file on an existing page still works with a new name - mod.io and the
/// game follow the page, not the name. What does not carry over is what the game keeps by guid: the
/// load order a player arranged and their "Load anyway" choice start again for the new file (the
/// registration memo says so).
///
/// Uploading is left to the person exporting, on the mod.io website. This writes the files only.
/// </summary>
public static class ModIoExporter
{
    /// <summary>
    /// How every name starts. The scripts look for it to tell the skins written by this tool from
    /// other mods (CksSkinMod.NamePrefix in src/mod-export says the same).
    /// </summary>
    public const string NamePrefix = "CksSkin_";

    /// <summary>Longest name accepted; mod.io shows the name in lists, where a long one is cut off anyway.</summary>
    public const int MaxDisplayNameLength = 50;

    /// <summary>Longest version accepted.</summary>
    public const int MaxVersionLength = 20;

    /// <summary>The version offered for a first upload.</summary>
    public const string DefaultVersion = "1.0.0";

    /// <summary>Longest name NTFS takes for one file, in UTF-16 units.</summary>
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// Size of the logo. mod.io asks for at least 512x288 at 16:9 and makes 1280x720 its largest
    /// thumbnail, so this is shown as it is on the website. The game's own mod browser shows the
    /// 320x180 and 640x360 thumbnails instead (see <see cref="ModIoLogo"/>).
    /// </summary>
    public const int LogoWidth = 1280;
    public const int LogoHeight = 720;

    /// <summary>Folder the scripts sit in, inside the zip.</summary>
    public const string ScriptsFolder = "Scripts/";

    public const string ManifestEntry = "ModManifest.json";
    public const string PictureEntry = "Skin/skin.png";
    public const string SettingsEntry = "Skin/settings.txt";
    public const string NoticeEntry = "NOTICE.txt";

    /// <summary>Prefix of the scripts among this assembly's resources.</summary>
    private const string ScriptResourcePrefix = "mod-export/";

    private const string LicenseResource = "LICENSE.txt";

    /// <summary>Where the tool lives, named in the notice that goes with the scripts.</summary>
    public const string ProjectUrl = "https://github.com/utausnskareshi/corekeeper-mod";

    /// <summary>
    /// The time stamped on every entry of the zip.
    ///
    /// Fixed rather than the moment of writing. A zip records local time, so the real moment would
    /// have put the time of day and the time zone of the person exporting into every file they
    /// publish; fixed, the zip depends only on what goes into it.
    /// </summary>
    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Digits written [0-9] rather than \d, which in .NET also matches full-width and other scripts'
    /// digits: "１.０" passed as a version until a test tried it.
    /// </summary>
    private static readonly Regex VersionPattern = new(@"^[0-9]{1,9}(\.[0-9]{1,9}){0,3}$", RegexOptions.CultureInvariant);

    /// <summary>A name and guid no other export has.</summary>
    public static ModIoIdentity NewIdentity()
    {
        string guid = System.Guid.NewGuid().ToString("N");
        return new ModIoIdentity(NamePrefix + guid[..12], guid);
    }

    /// <summary>
    /// The name as it will be written, or a <see cref="ToolException"/> saying what is wrong with it.
    /// </summary>
    public static string CheckDisplayName(string? displayName)
    {
        string name = (displayName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            throw new ToolException("error.modio.nameEmpty", [], "MOD の名前を入れること。");
        }

        if (name.Length > MaxDisplayNameLength)
        {
            throw new ToolException(
                "error.modio.nameTooLong", [MaxDisplayNameLength, name.Length],
                $"MOD の名前が長すぎる（{MaxDisplayNameLength} 文字まで。今は {name.Length} 文字）。");
        }

        if (name.Any(char.IsControl))
        {
            throw new ToolException("error.modio.nameControl", [], "MOD の名前に改行やタブは使えない。");
        }

        // A lone surrogate is half a character. The name box counts UTF-16 units and cuts an emoji
        // typed or pasted at the limit in two; written out, the half became U+FFFD in the manifest,
        // NOTICE.txt and the memo (the adversarial verification of 2026-10-09)
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsSurrogatePair(name, i))
            {
                i++;
            }
            else if (char.IsSurrogate(name[i]))
            {
                throw new ToolException(
                    "error.modio.nameBrokenChar", [MaxDisplayNameLength],
                    "MOD の名前に途中で切れた文字がある（欄の文字数の上限で絵文字などが半分に切れた）。その文字を消して入れ直すこと。");
            }
        }

        return name;
    }

    /// <summary>
    /// The version as it will be written, or a <see cref="ToolException"/> saying what is wrong with it.
    /// Digits separated by dots, as mod.io recommends (1.0.0).
    /// </summary>
    public static string CheckVersion(string? version)
    {
        string text = (version ?? string.Empty).Trim();

        if (text.Length == 0 || text.Length > MaxVersionLength || !VersionPattern.IsMatch(text))
        {
            throw new ToolException(
                "error.modio.version", [text],
                $"版は 1.0.0 のように、数字をピリオドで区切って入れること（入力: {text}）。");
        }

        return text;
    }

    /// <summary>
    /// Refuses a picture that would not make a working skin: one of another size, whose frames would
    /// not line up with the game's, or one with nothing drawn in any frame.
    /// </summary>
    public static void CheckSheet(SKBitmap sheet, SheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Texture is not { } texture || sheet.Width != texture.Width || sheet.Height != texture.Height)
        {
            throw new ToolException(
                "error.modio.sheetSize", [sheet.Width, sheet.Height, layout.Texture?.Width, layout.Texture?.Height],
                $"絵の寸法が {sheet.Width}x{sheet.Height} で、ゲームの {layout.Texture?.Width}x{layout.Texture?.Height} と違う。");
        }

        foreach (FrameRect frame in layout.Frames)
        {
            if (AnyOpaque(sheet, new SKRectI(frame.X, frame.YTopLeft, frame.X + frame.W, frame.YTopLeft + frame.H)))
            {
                return;
            }
        }

        throw new ToolException("error.modio.sheetEmpty", [], "絵がどのコマにも描かれていない。");
    }

    /// <summary>
    /// The mod.io Game Version tag for a game version, such as 1.3.0 for 1.3.0.4-511d. Unless one
    /// of a mod.io mod's tags carries the same first three numbers, the game warns at start-up and
    /// loads it only if the player chooses "Load anyway".
    ///
    /// Falls back to the newest version this build has been checked against when the game's own
    /// version is unknown.
    /// </summary>
    public static string GameVersionTag(string? installedVersion)
    {
        if (GameVersion.MajorMinorPatch(installedVersion) is { } installed)
        {
            return installed;
        }

        return GameVersion.SupportedVersions
            .Select(GameVersion.MajorMinorPatch)
            .OfType<string>()
            .Select(v => (Text: v, Parsed: Version.Parse(v)))
            .OrderByDescending(v => v.Parsed)
            .Select(v => v.Text)
            .First();
    }

    /// <summary>The scripts the zip carries, by file name, in the order they are written.</summary>
    public static IReadOnlyList<(string FileName, string Text)> TemplateScripts()
    {
        Assembly assembly = typeof(ModIoExporter).Assembly;
        List<(string, string)> scripts = [];

        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(r => r.StartsWith(ScriptResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(r => r, StringComparer.Ordinal))
        {
            scripts.Add((resource[ScriptResourcePrefix.Length..], NormalizeNewlines(ReadResource(assembly, resource))));
        }

        if (scripts.Count == 0)
        {
            throw new ToolException(
                "error.modio.templateMissing", [],
                "書き出す MOD のスクリプトが同梱されていない。配布物が壊れている可能性がある。");
        }

        return scripts;
    }

    /// <summary>
    /// The zip to upload, in memory. The same picture, choices, identity and tool version always
    /// give the same bytes.
    /// </summary>
    public static byte[] BuildModZip(SKBitmap sheet, ModIoExportOptions options, ModIoIdentity identity, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);

        string name = CheckDisplayName(options.DisplayName);
        string version = CheckVersion(options.Version);

        List<(string Path, byte[] Bytes)> content = [];
        foreach ((string fileName, string text) in TemplateScripts())
        {
            content.Add((ScriptsFolder + fileName, Utf8(text)));
        }

        content.Add((PictureEntry, EncodePng(sheet)));
        content.Add((SettingsEntry, Utf8(SettingsText(options.ShowGear, version, toolVersion))));
        content.Add((NoticeEntry, Utf8(NoticeText(name))));

        byte[] manifest = Utf8(ManifestJson(identity, name, content.Select(c => c.Path)));

        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, ManifestEntry, manifest);
            foreach ((string path, byte[] bytes) in content)
            {
                Add(archive, path, bytes);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The manifest, field for field what the Mod SDK writes (JsonUtility, ModMetadata), which is
    /// what the game reads it back with.
    ///
    /// requiredOn 0: the skin is drawn on this player's own screen only, so it is required on neither
    /// side, and a host never asks the people joining to install it. accessesExtraAssemblies: the
    /// scripts use the game's own types (PlayerController, Manager), as the tool's own mod does.
    /// Safety checks stay on - the scripts pass the game's code check, and on mod.io they are forced
    /// on anyway unless the mod carries the elevated access tag.
    /// </summary>
    internal static string ManifestJson(ModIoIdentity identity, string displayName, IEnumerable<string> files)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   IndentSize = 4,
                   NewLine = "\n",

                   // The name as it was typed, not escaped into \u sequences; quotes and control
                   // characters are still escaped
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            json.WriteStartObject();
            json.WriteString("guid", identity.Guid);
            json.WriteString("name", identity.Name);
            json.WriteString("displayName", displayName);
            json.WriteBoolean("skipSafetyChecks", false);
            json.WriteBoolean("disableScripts", false);
            json.WriteBoolean("accessesExtraAssemblies", true);
            json.WriteBoolean("disableHarmonyPatching", false);
            json.WriteNumber("requiredOn", 0);

            json.WriteStartArray("files");
            foreach (string file in files)
            {
                json.WriteStartObject();
                json.WriteString("path", file);
                json.WriteString("guid", string.Empty);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("dependencies");
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Skin/settings.txt: what the scripts read to decide whether the gear shows.</summary>
    internal static string SettingsText(bool showGear, string version, string toolVersion) =>
        $"# Written by Core Keeper Skin Maker (cks) {toolVersion} for a mod.io upload.\n" +
        "# showGear: true draws the game's clothes and armour over the picture, false hides them.\n" +
        $"showGear={(showGear ? "true" : "false")}\n" +
        $"version={version}\n";

    /// <summary>NOTICE.txt: who made what in the zip, and the licence the scripts come under.</summary>
    internal static string NoticeText(string displayName) =>
        $"{displayName}\n" +
        "A player skin for Core Keeper, made with Core Keeper Skin Maker (cks).\n" +
        $"{ProjectUrl}\n" +
        "\n" +
        "Skin/     The picture and its settings, supplied by the author of this mod.\n" +
        "          A picture made from a cks preset comes from Core Keeper Skin Maker, under the MIT License below.\n" +
        "          A look fetched from the game contains Core Keeper art, which belongs to Pugstorm.\n" +
        "Scripts/  From Core Keeper Skin Maker, under the MIT License below.\n" +
        "\n" +
        "-----\n" +
        "\n" +
        NormalizeNewlines(ReadResource(typeof(ModIoExporter).Assembly, LicenseResource)).TrimEnd('\n') + "\n";

    /// <summary>
    /// Writes the zip, the logo and the registration memo. The logo and the memo go beside the zip,
    /// named after it.
    ///
    /// Everything is made in memory first and each file is written next to its final name before any
    /// of them is moved into place, so a failure while writing leaves the files that were there
    /// before. A failure while moving them into place (zip, then logo, then memo) leaves the ones
    /// already moved new and the rest old - each complete on its own, and named in
    /// <see cref="ModIoWriteException.Replaced"/> so the caller can say so.
    /// </summary>
    public static ModIoExportResult Export(
        string zipPath, SKBitmap sheet, SheetLayout layout, ModIoExportRequest request, ModIoIdentity? identity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(request);

        string fullZip = Path.GetFullPath(zipPath);
        if (!IsZipName(fullZip))
        {
            throw new ToolException(
                "error.modio.notZip", [fullZip],
                $"保存先のファイル名は .zip で終わること: {fullZip}");
        }

        // mod.io's upload form compares what follows the name's last '.' with "zip" case-sensitively
        // and refuses "knight.ZIP" as not a zip, so the extension is written in lower case. Moving
        // over an existing "knight.ZIP" leaves the new spelling (measured on NTFS).
        fullZip = Path.ChangeExtension(fullZip, ".zip");

        string name = CheckDisplayName(request.Options.DisplayName);
        string version = CheckVersion(request.Options.Version);
        CheckSheet(sheet, layout);

        ModIoIdentity id = identity ?? NewIdentity();
        (string logoPath, string guidePath) = CompanionPaths(fullZip);

        // NTFS takes 255 characters for one name, and the memo's is the zip's with 7 more. Checked
        // before anything is written: under their short temporary names every file is written, and
        // only the move into place failed - after the zip, and perhaps the logo, had been replaced
        // (the adversarial verification of 2026-10-09)
        int longest = LongestName(fullZip, logoPath, guidePath);
        if (longest > MaxFileNameLength)
        {
            int length = Path.GetFileName(fullZip).Length;
            int limit = length - (longest - MaxFileNameLength);
            throw new ToolException(
                "error.modio.fileNameTooLong", [limit, length],
                $"zip の名前が長すぎる（横のロゴと登録メモの名前がもっと長くなるため、拡張子を含めて {limit} 文字まで。今は {length} 文字）。");
        }

        byte[] zip = BuildModZip(sheet, request.Options with { DisplayName = name, Version = version }, id, request.ToolVersion);

        byte[] logo;
        using (SKBitmap image = ModIoLogo.Draw(sheet, layout))
        {
            logo = EncodePng(image);
        }

        string guide = ModIoGuide.Render(request.LanguageCode, new ModIoGuideValues(
            Name: name,
            Version: version,
            ShowGear: request.Options.ShowGear,
            GameVersionTag: request.GameVersionTag,
            ZipFile: Path.GetFileName(fullZip),
            LogoFile: Path.GetFileName(logoPath),
            Identity: id));

        WriteTogether([(fullZip, zip), (logoPath, logo), (guidePath, Encoding.UTF8.GetPreamble().Concat(Utf8(guide)).ToArray())]);

        return new ModIoExportResult(fullZip, logoPath, guidePath, id);
    }

    /// <summary>Where the logo and the memo go for a zip: beside it, named after it.</summary>
    public static (string LogoPath, string GuidePath) CompanionPaths(string zipPath)
    {
        string full = Path.GetFullPath(zipPath);
        string folder = Path.GetDirectoryName(full) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(full);

        return (Path.Combine(folder, stem + "-logo.png"), Path.Combine(folder, stem + "-mod.io.txt"));
    }

    /// <summary>
    /// Of the logo and the memo an export to <paramref name="zipPath"/> writes beside it, the ones
    /// already there, in that order. The export replaces them as it does the zip, but the save dialog
    /// asks only about the zip: a logo touched up by hand under that name was lost for good (the code
    /// review of 2026-10-08), so the window asks about these first. Only looks; writes nothing.
    /// </summary>
    public static IReadOnlyList<string> ExistingCompanions(string zipPath)
    {
        // Nothing is replaced under a name the export refuses - one not ending in .zip, or one that
        // makes the logo's or the memo's name too long - so nothing is listed as about to be either:
        // the window asked about replacing files that were then left alone (the adversarial
        // verification of 2026-10-09)
        string full = Path.GetFullPath(zipPath);
        (string logoPath, string guidePath) = CompanionPaths(full);
        if (!IsZipName(full) || LongestName(full, logoPath, guidePath) > MaxFileNameLength)
        {
            return [];
        }

        return [.. new[] { logoPath, guidePath }.Where(File.Exists)];
    }

    /// <summary>Whether the export takes this as the zip's name (any case of ".zip").</summary>
    private static bool IsZipName(string fullPath) =>
        string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>The length of the longest of the file names, the folders left out.</summary>
    private static int LongestName(params string[] paths) => paths.Max(path => Path.GetFileName(path).Length);

    /// <summary>
    /// Writes each file under a temporary name beside its destination, then moves them all into
    /// place. A temporary file left by a failure is removed; files already moved stay, since each
    /// is complete on its own. A failure names the file it happened to (<see cref="ModIoWriteException"/>):
    /// with three files written at once, "being used by another process" alone does not say which
    /// one to close. It also lists the files already moved, which no longer match the others.
    /// </summary>
    private static void WriteTogether(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        // A folder under one of the names makes its move fail. It is there to be seen before
        // anything is written, rather than after the files before it have been replaced
        foreach ((string path, _) in files)
        {
            if (Directory.Exists(path))
            {
                throw new ModIoWriteException(path, new IOException($"同じ名前のフォルダーがある: {path}"));
            }
        }

        List<(string Final, string Staging)> staged = [];
        List<string> replaced = [];

        try
        {
            foreach ((string path, byte[] bytes) in files)
            {
                string folder = Path.GetDirectoryName(path) ?? string.Empty;
                // A name of fixed length, not the destination's with more added: that one could pass
                // the 255 characters NTFS allows for a name when the destination itself does not
                string staging = Path.Combine(folder, $".cks-{System.Guid.NewGuid():N}.tmp");

                try
                {
                    Directory.CreateDirectory(folder);
                    staged.Add((path, staging));
                    File.WriteAllBytes(staging, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ModIoWriteException(path, ex);
                }
            }

            foreach ((string final, string staging) in staged)
            {
                try
                {
                    // A destination marked read-only, by hand or by a backup or sync tool, refuses
                    // the move. The user has agreed to replace it (the window asks about the logo
                    // and the memo, the save dialog about the zip), so the attribute is cleared once
                    // the move has objected, as CharacterSkins and ModConfigWriter do
                    try
                    {
                        File.Move(staging, final, overwrite: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        PathSafety.ClearReadOnly(final);
                        File.Move(staging, final, overwrite: true);
                    }

                    replaced.Add(final);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ModIoWriteException(final, ex, [.. replaced]);
                }
            }
        }
        finally
        {
            foreach ((_, string staging) in staged)
            {
                try
                {
                    if (File.Exists(staging))
                    {
                        File.Delete(staging);
                    }
                }
                catch (Exception)
                {
                    // Cleanup only; the failure that brought us here is the one worth reporting
                }
            }
        }
    }

    private static void Add(ZipArchive archive, string path, byte[] bytes)
    {
        // Compressed, into BuildModZip's MemoryStream: the game's mod.io plugin unpacks downloads with
        // SharpZipLib's ZipInputStream, which cannot read an entry stored uncompressed and followed by
        // a data descriptor - what ZipArchive writes for NoCompression to a stream that cannot seek
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;

        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Whether any pixel inside the rectangle is not fully transparent.</summary>
    internal static bool AnyOpaque(SKBitmap sheet, SKRectI rect)
    {
        for (int y = Math.Max(0, rect.Top); y < Math.Min(sheet.Height, rect.Bottom); y++)
        {
            for (int x = Math.Max(0, rect.Left); x < Math.Min(sheet.Width, rect.Right); x++)
            {
                if (sheet.GetPixel(x, y).Alpha > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static byte[] EncodePng(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ToolException("error.modio.encodeFailed", [], "PNG のエンコードに失敗した。");
        return data.ToArray();
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new ToolException(
                "error.modio.templateMissing", [],
                $"書き出す MOD の部品 ({name}) が同梱されていない。配布物が壊れている可能性がある。");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The files come out of the repository with whatever line ends git gave them on the machine that
    /// built this, so they are made LF here: the same export then gives the same bytes everywhere.
    /// </summary>
    internal static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
}

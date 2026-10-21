using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Export;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// The zip written for mod.io, the logo and the registration memo beside it.
///
/// What the game wants from the zip was read out of its own loader (ModIOLoader and Loader, 1.3.0.2,
/// unchanged in 1.3.0.4): ModManifest.json at the root, the scripts named in its files list, and a
/// name no other mod has - the loader keeps only one of the mods that share a name, saying nothing.
/// </summary>
public sealed class ModIoExporterTests : IDisposable
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cks-modio-test-{Guid.NewGuid():N}");

    public ModIoExporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SKBitmap Sheet() => StarterCharacter.Build(Layout, PartsLayout.LoadEmbedded());

    private static ModIoExportOptions Options(bool showGear = false, string name = "忍者 Ninja", string version = "1.0.0") =>
        new(name, version, showGear);

    private static readonly ModIoIdentity FixedIdentity = new("CksSkin_0123456789ab", "0123456789abcdef0123456789abcdef");

    private static ZipArchive Open(byte[] zip) => new(new MemoryStream(zip), ZipArchiveMode.Read);

    private static string ReadText(ZipArchive archive, string entry)
    {
        using StreamReader reader = new(archive.GetEntry(entry)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(ZipArchive archive, string entry)
    {
        using MemoryStream copy = new();
        archive.GetEntry(entry)!.Open().CopyTo(copy);
        return copy.ToArray();
    }

    // ------------------------------------------------------------ The zip

    [Fact]
    public void zipの直下にマニフェストがあり決まったファイルだけが入る()
    {
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0"));

        string[] names = [.. archive.Entries.Select(e => e.FullName)];

        // The loader reads <install folder>\ModManifest.json, which is the root of the zip
        Assert.Equal(ModIoExporter.ManifestEntry, names[0]);
        Assert.Equal(
            new[]
            {
                "ModManifest.json",
                "Scripts/CksSkinApplier.cs",
                "Scripts/CksSkinMod.cs",
                "Scripts/CksSkinPatch.cs",
                "Skin/skin.png",
                "Skin/settings.txt",
                "NOTICE.txt",
            },
            names);

        // Forward slashes only, no folder entries, nothing that climbs out of the install folder
        Assert.All(names, n => Assert.DoesNotContain('\\', n));
        Assert.All(names, n => Assert.False(n.EndsWith('/'), n));
        Assert.All(names, n => Assert.False(n.StartsWith('/') || n.Contains(".."), n));
    }

    [Fact]
    public void マニフェストはゲームが読む形で書かれる()
    {
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(name: "  忍者 \"Ninja\"  "), FixedIdentity, "1.3.0"));

        using JsonDocument manifest = JsonDocument.Parse(ReadText(archive, "ModManifest.json"));
        JsonElement root = manifest.RootElement;

        // Field for field what the Mod SDK writes (ModMetadata through JsonUtility)
        Assert.Equal(
            new[]
            {
                "guid", "name", "displayName", "skipSafetyChecks", "disableScripts", "accessesExtraAssemblies",
                "disableHarmonyPatching", "requiredOn", "files", "dependencies",
            },
            root.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(FixedIdentity.Guid, root.GetProperty("guid").GetString());
        Assert.Equal(FixedIdentity.Name, root.GetProperty("name").GetString());
        Assert.Equal("忍者 \"Ninja\"", root.GetProperty("displayName").GetString());
        Assert.False(root.GetProperty("skipSafetyChecks").GetBoolean());
        Assert.False(root.GetProperty("disableScripts").GetBoolean());
        Assert.True(root.GetProperty("accessesExtraAssemblies").GetBoolean());
        Assert.False(root.GetProperty("disableHarmonyPatching").GetBoolean());

        // Required on neither side: a host never asks the people joining to install a skin
        Assert.Equal(0, root.GetProperty("requiredOn").GetInt32());
        Assert.Equal(0, root.GetProperty("dependencies").GetArrayLength());

        // Every entry but the manifest itself is listed
        string[] listed = [.. root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        string[] entries = [.. archive.Entries.Select(e => e.FullName).Where(n => n != "ModManifest.json")];
        Assert.Equal(entries, listed);

        // Written as UTF-8 without a byte order mark, the name unescaped
        byte[] raw = ReadBytes(archive, "ModManifest.json");
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
        Assert.Contains("忍者", Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void ゲームのローダーと同じ選び方でスクリプトが揃う()
    {
        // Loader.Load takes the scripts, libraries and bundles from the files list by extension and
        // opens each at <folder>/<path>. Every script listed has to be in the zip, and nothing listed
        // may need what this mod does not have: a library needs accessesExtraAssemblies (it is set,
        // but there are none), and a bundle would need Unity.
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0"));
        using JsonDocument manifest = JsonDocument.Parse(ReadText(archive, "ModManifest.json"));

        string[] listed = [.. manifest.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];

        string[] scripts = [.. listed.Where(p => p.EndsWith(".cs", StringComparison.Ordinal))];
        Assert.Equal(3, scripts.Length);
        Assert.All(scripts, s => Assert.NotNull(archive.GetEntry(s)));
        Assert.DoesNotContain(listed, p => p.EndsWith(".dll", StringComparison.Ordinal));
        Assert.DoesNotContain(listed, p => p.EndsWith(".assetbundle", StringComparison.Ordinal));
    }

    [Fact]
    public void zipのスクリプトはsrcのmod_exportと同じ()
    {
        string folder = Path.Combine(FindRepositoryRoot(), "src", "mod-export");
        string[] files = [.. Directory.GetFiles(folder, "*.cs").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)!];

        IReadOnlyList<(string FileName, string Text)> embedded = ModIoExporter.TemplateScripts();
        Assert.Equal(files, embedded.Select(s => s.FileName).ToArray());

        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0"));

        foreach (string file in files)
        {
            string expected = ModIoExporter.NormalizeNewlines(File.ReadAllText(Path.Combine(folder, file)));
            Assert.Equal(expected, ReadText(archive, "Scripts/" + file));
        }
    }

    [Fact]
    public void 絵はシートと同じ画素で入る()
    {
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0"));
        using SKBitmap picture = DecodeUnpremul(ReadBytes(archive, "Skin/skin.png"));

        Assert.Equal(sheet.Width, picture.Width);
        Assert.Equal(sheet.Height, picture.Height);

        for (int y = 0; y < sheet.Height; y++)
        {
            for (int x = 0; x < sheet.Width; x++)
            {
                SKColor expected = sheet.GetPixel(x, y);
                SKColor actual = picture.GetPixel(x, y);
                Assert.Equal(expected.Alpha, actual.Alpha);
                if (expected.Alpha > 0)
                {
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Theory]
    [InlineData(true, "showGear=true")]
    [InlineData(false, "showGear=false")]
    public void 装備の見え方と版が設定に書かれる(bool showGear, string line)
    {
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(showGear, version: "2.10.3"), FixedIdentity, "1.3.0"));

        string[] lines = ReadText(archive, "Skin/settings.txt").Split('\n');

        Assert.Contains(line, lines);
        Assert.Contains("version=2.10.3", lines);
        Assert.Single(lines, l => l.StartsWith("showGear=", StringComparison.Ordinal));
    }

    [Fact]
    public void スクリプトの使用条件として_MITの表示が入る()
    {
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(name: "Ninja"), FixedIdentity, "1.3.0"));

        string notice = ReadText(archive, "NOTICE.txt");
        string license = ModIoExporter.NormalizeNewlines(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "LICENSE"))).TrimEnd('\n');

        Assert.StartsWith("Ninja\n", notice, StringComparison.Ordinal);
        Assert.Contains(license, notice, StringComparison.Ordinal);
        Assert.Contains("MIT License", notice, StringComparison.Ordinal);
        Assert.Contains(ModIoExporter.ProjectUrl, notice, StringComparison.Ordinal);
    }

    [Fact]
    public void NOTICEは絵の出どころごとの権利者を書く()
    {
        // The picture is not always the uploader's own: one made from a preset is the cks project's
        // (MIT), and a look fetched from the game holds Pugstorm's art. The notice travels with every
        // copy of the zip, so it must not name the uploader as the owner of either.
        using SKBitmap sheet = Sheet();
        using ZipArchive archive = Open(ModIoExporter.BuildModZip(sheet, Options(name: "Ninja"), FixedIdentity, "1.3.0"));

        string[] skin = [.. ReadText(archive, "NOTICE.txt").Split('\n')
            .SkipWhile(l => !l.StartsWith("Skin/", StringComparison.Ordinal))
            .TakeWhile(l => !l.StartsWith("Scripts/", StringComparison.Ordinal))];

        Assert.NotEmpty(skin);
        Assert.Contains(skin, l => l.Contains("preset", StringComparison.Ordinal) && l.Contains("MIT", StringComparison.Ordinal));
        Assert.Contains(skin, l => l.Contains("Pugstorm", StringComparison.Ordinal));
    }

    [Fact]
    public void 識別子は書き出すたびに新しく_形が決まっている()
    {
        // The loader keeps only one of any mods sharing a name, silently, so two exports must never
        // share one - however alike their pictures and names are.
        HashSet<string> names = [];
        HashSet<string> guids = [];

        for (int i = 0; i < 2000; i++)
        {
            ModIoIdentity id = ModIoExporter.NewIdentity();

            Assert.Matches("^CksSkin_[0-9a-f]{12}$", id.Name);

            // Read as Unity.Entities.Hash128 by the game's multiplayer mod list: 32 hex digits
            Assert.Matches("^[0-9a-f]{32}$", id.Guid);
            Assert.NotEqual(SheetInstaller.DefaultModFolderName, id.Name);

            Assert.True(names.Add(id.Name), "名前が重なった: " + id.Name);
            Assert.True(guids.Add(id.Guid), "guid が重なった: " + id.Guid);
        }
    }

    [Fact]
    public void 同じ絵と名前でも書き出しごとに別のMODになる()
    {
        using SKBitmap sheet = Sheet();
        ModIoExportRequest request = new(Options(), "1.3.0", "1.3.0", "ja");

        ModIoExportResult first = ModIoExporter.Export(Path.Combine(_root, "a.zip"), sheet, Layout, request);
        ModIoExportResult second = ModIoExporter.Export(Path.Combine(_root, "b.zip"), sheet, Layout, request);

        Assert.NotEqual(first.Identity.Name, second.Identity.Name);
        Assert.NotEqual(first.Identity.Guid, second.Identity.Guid);

        using ZipArchive a = ZipFile.OpenRead(first.ZipPath);
        using ZipArchive b = ZipFile.OpenRead(second.ZipPath);
        Assert.NotEqual(
            JsonDocument.Parse(ReadText(a, "ModManifest.json")).RootElement.GetProperty("name").GetString(),
            JsonDocument.Parse(ReadText(b, "ModManifest.json")).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void 同じ識別子なら同じバイト列になり_時刻を持ち込まない()
    {
        using SKBitmap sheet = Sheet();

        byte[] first = ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0");
        byte[] second = ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0");
        Assert.Equal(first, second);

        // A zip records local time; the moment of writing would carry the exporter's time zone
        using ZipArchive archive = Open(first);
        Assert.All(archive.Entries, e => Assert.Equal(new DateTime(2026, 1, 1), e.LastWriteTime.DateTime));
    }

    [Fact]
    public void zipはゲームのmodioプラグインが前から順に読める形で書かれる()
    {
        // The game's mod.io plugin unpacks a download with SharpZipLib's ZipInputStream (modio.UnityPlugin,
        // ExtractOperation), which reads the local headers one after another rather than the central
        // directory. Read out of the game's copy, it takes only what is checked here: stored or
        // deflated, not encrypted, a "version needed" that ZipEntry.CanDecompress lists, no stored
        // entry followed by a data descriptor, a data descriptor that starts with its signature, and
        // right sizes in a local header without one. All of it holds today; this keeps it so - writing
        // to a stream that cannot seek with NoCompression, for one, gives stored entries with descriptors.
        using SKBitmap sheet = Sheet();
        byte[] zip = ModIoExporter.BuildModZip(sheet, Options(), FixedIdentity, "1.3.0");
        using ZipArchive archive = Open(zip);

        const uint LocalHeader = 0x04034B50;
        const uint DataDescriptor = 0x08074B50;
        const uint CentralDirectory = 0x02014B50;
        int[] readableVersions = [10, 11, 20, 45, 46, 51];

        int offset = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string at = $"{entry.FullName}（{offset} バイト目）";
            Assert.True(U32(zip, offset) == LocalHeader, "前のエントリーのすぐ後にローカルヘッダーが無い: " + at);

            int versionNeeded = U16(zip, offset + 4);
            int flags = U16(zip, offset + 6);
            int method = U16(zip, offset + 8);
            uint compressedSize = U32(zip, offset + 18);
            uint size = U32(zip, offset + 22);
            int nameLength = U16(zip, offset + 26);
            int extraLength = U16(zip, offset + 28);
            bool descriptor = (flags & 8) != 0;

            // The local headers come in the order of the entries, so the walk meets each of them once
            Assert.Equal(entry.FullName, Encoding.UTF8.GetString(zip, offset + 30, nameLength));

            Assert.True(method is 0 or 8, $"方式が Stored(0) でも Deflated(8) でもない（{method}）: {at}");
            Assert.True((flags & 1) == 0, "暗号化されている: " + at);
            Assert.True(readableVersions.Contains(versionNeeded), $"version needed が読めない値（{versionNeeded}）: {at}");
            Assert.False(method == 0 && descriptor, "Stored なのにデータ記述子が付いている: " + at);

            if (!descriptor)
            {
                // Without a descriptor ZipInputStream takes the sizes from the local header and holds
                // the data to them
                Assert.Equal(entry.CompressedLength, (long)compressedSize);
                Assert.Equal(entry.Length, (long)size);
            }

            offset += 30 + nameLength + extraLength + (int)entry.CompressedLength;

            if (descriptor)
            {
                // ZipInputStream insists on the signature, which the zip format leaves optional
                Assert.True(U32(zip, offset) == DataDescriptor, "データ記述子に署名が無い: " + at);
                offset += 16;
            }
        }

        // The central directory follows the last entry; ZipInputStream stops there
        Assert.True(U32(zip, offset) == CentralDirectory, $"最後のエントリーの後に中央ディレクトリが無い（{offset} バイト目）");

        static int U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));

        static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    }

    // ------------------------------------------------------------ What is refused

    [Theory]
    [InlineData("", "error.modio.nameEmpty")]
    [InlineData("   ", "error.modio.nameEmpty")]
    [InlineData(null, "error.modio.nameEmpty")]
    [InlineData("a\tb", "error.modio.nameControl")]
    [InlineData("a\nb", "error.modio.nameControl")]
    [InlineData("123456789012345678901234567890123456789012345678901", "error.modio.nameTooLong")]
    public void 名前の誤りを断る(string? name, string key)
    {
        ToolException ex = Assert.Throws<ToolException>(() => ModIoExporter.CheckDisplayName(name));
        Assert.Equal(key, ex.MessageKey);
    }

    [Fact]
    public void 名前は前後の空白を除いて50文字まで受け付ける()
    {
        Assert.Equal("Ninja", ModIoExporter.CheckDisplayName("  Ninja  "));
        string fifty = new('あ', 50);
        Assert.Equal(fifty, ModIoExporter.CheckDisplayName(fifty));
    }

    [Fact]
    public void 名前の対の無いサロゲートを断る()
    {
        // The name box counts UTF-16 units, so 49 letters and an emoji keep only the emoji's first
        // half. The check let that through, and the manifest, NOTICE.txt and the memo got U+FFFD
        // (the adversarial verification of 2026-10-09, A1)
        int max = ModIoExporter.MaxDisplayNameLength;
        string pair = char.ConvertFromUtf32(0x20BB7);

        foreach (string broken in new[] { new string('a', max - 1) + pair[0], pair[1] + "abc", "a" + pair[0] + "b" })
        {
            ToolException ex = Assert.Throws<ToolException>(() => ModIoExporter.CheckDisplayName(broken));
            Assert.Equal("error.modio.nameBrokenChar", ex.MessageKey);
        }

        // A whole pair is a character, right up to the limit
        string whole = new string('a', max - 2) + pair;
        Assert.Equal(whole, ModIoExporter.CheckDisplayName(whole));
        string emoji = char.ConvertFromUtf32(0x1F600) + "忍者";
        Assert.Equal(emoji, ModIoExporter.CheckDisplayName(emoji));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0")]
    [InlineData(" 1.2.3.4 ")]
    [InlineData("10.20.30")]
    [InlineData("12345678.123456789.1")] // exactly MaxVersionLength (20) characters
    public void 版は数字をピリオドで区切った形を受け付ける(string version)
    {
        Assert.Equal(version.Trim(), ModIoExporter.CheckVersion(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1.0")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1..0")]
    [InlineData("1.0.0.0.0")]
    [InlineData("1.0-beta")]
    [InlineData("１.０")]
    [InlineData("1234567890.1.1")]
    [InlineData("123456789.123456789.1")] // fits the pattern; refused only by MaxVersionLength (21 characters)
    public void 版の誤りを断る(string? version)
    {
        ToolException ex = Assert.Throws<ToolException>(() => ModIoExporter.CheckVersion(version));
        Assert.Equal("error.modio.version", ex.MessageKey);
    }

    [Fact]
    public void 寸法の違う絵は書き出さない()
    {
        using SKBitmap sheet = new(new SKImageInfo(233, 156, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        ToolException ex = Assert.Throws<ToolException>(() => ModIoExporter.CheckSheet(sheet, Layout));
        Assert.Equal("error.modio.sheetSize", ex.MessageKey);
    }

    [Fact]
    public void どのコマにも何も描かれていない絵は書き出さない()
    {
        using SKBitmap sheet = new(new SKImageInfo(Layout.Texture.Width, Layout.Texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        sheet.Erase(SKColors.Transparent);

        ToolException ex = Assert.Throws<ToolException>(() => ModIoExporter.CheckSheet(sheet, Layout));
        Assert.Equal("error.modio.sheetEmpty", ex.MessageKey);

        // One pixel in one frame is enough
        FrameRect last = Layout.Frames[^1];
        sheet.SetPixel(last.X + (last.W / 2), last.YTopLeft + (last.H / 2), SKColors.Red);
        ModIoExporter.CheckSheet(sheet, Layout);
    }

    [Fact]
    public void zip以外の名前には書き出さない()
    {
        using SKBitmap sheet = Sheet();
        ModIoExportRequest request = new(Options(), "1.3.0", "1.3.0", "ja");

        ToolException ex = Assert.Throws<ToolException>(
            () => ModIoExporter.Export(Path.Combine(_root, "skin.png"), sheet, Layout, request));

        Assert.Equal("error.modio.notZip", ex.MessageKey);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    // ------------------------------------------------------------ Writing the files

    [Fact]
    public void zipとロゴと登録メモを並べて書き_前のものを置き換える()
    {
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, "out", "忍者-1.0.0.zip");
        (string logo, string guide) = ModIoExporter.CompanionPaths(zip);

        Assert.Equal(Path.Combine(_root, "out", "忍者-1.0.0-logo.png"), logo);
        Assert.Equal(Path.Combine(_root, "out", "忍者-1.0.0-mod.io.txt"), guide);

        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.WriteAllText(zip, "old");
        File.WriteAllText(logo, "old");
        File.WriteAllText(guide, "old");

        ModIoExportResult result = ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "en"));

        Assert.Equal(zip, result.ZipPath);
        Assert.Equal(logo, result.LogoPath);
        Assert.Equal(guide, result.GuidePath);

        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            Assert.NotNull(archive.GetEntry("ModManifest.json"));
        }

        using (SKBitmap image = SKBitmap.Decode(logo))
        {
            Assert.Equal(1280, image.Width);
        }

        Assert.Contains(result.Identity.Name, File.ReadAllText(guide), StringComparison.Ordinal);

        // Nothing left over from writing beside the final names
        Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(zip)!).Length);
    }

    [Fact]
    public void 横のロゴと登録メモのうち既にあるものを書き出す前に返す()
    {
        // The save dialog asks only about the zip, and the export replaces the logo and the memo
        // beside it as well: a logo touched up by hand under that name was lost for good. The window
        // asks first when any is there (the code review of 2026-10-08, R1).
        string zip = Path.Combine(_root, "忍者-1.0.0.zip");
        (string logo, string guide) = ModIoExporter.CompanionPaths(zip);

        Assert.Empty(ModIoExporter.ExistingCompanions(zip));

        // Neither the zip itself, which the save dialog has asked about, nor files merely named alike
        File.WriteAllText(zip, "old");
        File.WriteAllText(Path.Combine(_root, "忍者-1.0.0.png"), "other");
        File.WriteAllText(logo + ".bak", "other");
        Assert.Empty(ModIoExporter.ExistingCompanions(zip));

        File.WriteAllText(guide, "old");
        Assert.Equal(new[] { guide }, ModIoExporter.ExistingCompanions(zip));

        File.WriteAllText(logo, "touched up");
        Assert.Equal(new[] { logo, guide }, ModIoExporter.ExistingCompanions(zip));

        // The export writes the same two names whatever the case of the extension chosen
        Assert.Equal(new[] { logo, guide }, ModIoExporter.ExistingCompanions(Path.ChangeExtension(zip, ".ZIP")));

        // Only looked at
        Assert.Equal("touched up", File.ReadAllText(logo));
        Assert.Equal(5, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void zip以外の名前では横のファイルを置き換えるとは言わない()
    {
        // The export refuses a name that does not end in .zip, so nothing beside it is replaced; the
        // save dialog can hand back "knight.png" typed by hand, and the window then asked about
        // replacing files it was not going to touch (the adversarial verification of 2026-10-09, A5)
        string chosen = Path.Combine(_root, "knight.png");
        (string logo, string guide) = ModIoExporter.CompanionPaths(chosen);
        File.WriteAllText(logo, "touched up");
        File.WriteAllText(guide, "old");

        Assert.Empty(ModIoExporter.ExistingCompanions(chosen));

        using SKBitmap sheet = Sheet();
        ToolException ex = Assert.Throws<ToolException>(
            () => ModIoExporter.Export(chosen, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja")));
        Assert.Equal("error.modio.notZip", ex.MessageKey);
        Assert.Equal("touched up", File.ReadAllText(logo));

        // A zip in capitals is still a zip, and its companions are still asked about
        Assert.Equal(new[] { logo, guide }, ModIoExporter.ExistingCompanions(Path.Combine(_root, "knight.ZIP")));
    }

    [Fact]
    public void 書き出しは登録メモにロゴの名前とゲームの版と選んだ装備の見え方を渡す()
    {
        // Every other export in these tests passes the same game and tool version and hides the
        // gear, so handing the memo the wrong one of them went unseen (the adversarial
        // verification of 2026-10-09, A13)
        using SKBitmap sheet = Sheet();
        ModIoExportResult result = ModIoExporter.Export(
            Path.Combine(_root, "values.zip"), sheet, Layout, new ModIoExportRequest(Options(showGear: true), "9.8.7", "1.3.0", "en"));

        string memo = File.ReadAllText(result.GuidePath);
        (IReadOnlyDictionary<string, string> header, _) = ModIoGuide.Load("en");

        Assert.Contains(Path.GetFileName(result.LogoPath), memo, StringComparison.Ordinal);
        Assert.Matches(@"Game Version\s+9\.8\.7", memo);
        Assert.DoesNotMatch(@"Game [Vv]ersion\s+1\.3\.0", memo);
        Assert.Contains(header["gearShown"], memo, StringComparison.Ordinal);
        Assert.DoesNotContain(header["gearHidden"], memo, StringComparison.Ordinal);
    }

    [Fact]
    public void 他のプログラムが開いているファイルは名指しで知らせ_一時ファイルを残さない()
    {
        // Three files are written at once, and the runtime's sentence alone does not say which one
        // to close. Moving over a file held open fails as "access denied" on Windows, not as a
        // sharing violation (measured here), so the failure is caught whichever it is.
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, "locked.zip");
        (string logo, _) = ModIoExporter.CompanionPaths(zip);
        File.WriteAllText(logo, "held");

        using (FileStream held = new(logo, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ModIoWriteException ex = Assert.Throws<ModIoWriteException>(
                () => ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja")));

            Assert.Equal(logo, ex.FilePath);
            Assert.True(ex.InnerException is IOException or UnauthorizedAccessException, ex.InnerException?.GetType().FullName);
        }

        Assert.DoesNotContain(Directory.GetFiles(_root), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal("held", File.ReadAllText(logo));
    }

    [Fact]
    public void 書き込めない場所も名指しで知らせる()
    {
        // A folder where the zip cannot be created at all: here a file stands where the folder
        // would have to be
        using SKBitmap sheet = Sheet();
        string blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        string zip = Path.Combine(blocker, "skin.zip");

        ModIoWriteException ex = Assert.Throws<ModIoWriteException>(
            () => ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "en")));

        Assert.Equal(zip, ex.FilePath);
    }

    [Fact]
    public void 置き換えの途中で止まれば_もう置き換えたファイルを知らせる()
    {
        // The zip is moved into place first; a logo held open then stops the rest, leaving a new zip
        // beside the old logo and memo. Each file is whole, but the exception has to say which are new.
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, "partial.zip");
        (string logo, string guide) = ModIoExporter.CompanionPaths(zip);
        File.WriteAllText(zip, "old");
        File.WriteAllText(logo, "held");
        File.WriteAllText(guide, "old");

        using (FileStream held = new(logo, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ModIoWriteException ex = Assert.Throws<ModIoWriteException>(
                () => ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja")));

            Assert.Equal(logo, ex.FilePath);
            Assert.Equal(new[] { zip }, ex.Replaced);
        }

        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            Assert.NotNull(archive.GetEntry("ModManifest.json"));
        }

        Assert.Equal("held", File.ReadAllText(logo));
        Assert.Equal("old", File.ReadAllText(guide));
        Assert.Equal(3, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void 最初のファイルで止まれば_置き換えたファイルは無い()
    {
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, "first.zip");
        (string logo, string guide) = ModIoExporter.CompanionPaths(zip);
        File.WriteAllText(zip, "held");

        using (FileStream held = new(zip, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ModIoWriteException ex = Assert.Throws<ModIoWriteException>(
                () => ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja")));

            Assert.Equal(zip, ex.FilePath);
            Assert.Empty(ex.Replaced);
        }

        Assert.Equal("held", File.ReadAllText(zip));
        Assert.False(File.Exists(logo));
        Assert.False(File.Exists(guide));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("knight.ZIP")]
    [InlineData("knight.Zip")]
    public void 拡張子が大文字でも_小文字のzipで書く(string chosen)
    {
        // mod.io's upload form compares what follows the name's last '.' with "zip" case-sensitively,
        // and refuses "knight.ZIP" as not a zip. A file already there under the other spelling is
        // replaced, and the new spelling is what remains.
        using SKBitmap sheet = Sheet();
        File.WriteAllText(Path.Combine(_root, chosen), "old");

        ModIoExportResult result = ModIoExporter.Export(
            Path.Combine(_root, chosen), sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "en"));

        Assert.Equal(Path.Combine(_root, "knight.zip"), result.ZipPath);

        string[] names = [.. Directory.GetFiles(_root).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { "knight-logo.png", "knight-mod.io.txt", "knight.zip" }, names);

        string memo = File.ReadAllText(result.GuidePath);
        Assert.Contains("knight.zip", memo, StringComparison.Ordinal);
        Assert.DoesNotContain(chosen, memo, StringComparison.Ordinal);

        using ZipArchive archive = ZipFile.OpenRead(result.ZipPath);
        Assert.NotNull(archive.GetEntry("ModManifest.json"));
    }

    [Fact]
    public void zipの名前が長くても一時ファイルの名前で失敗しない()
    {
        // NTFS takes up to 255 characters for one name. Named after the file it stands in for, the
        // memo's temporary file was 45 characters longer than the zip's name, so a zip name of 211 or
        // more characters failed - and was reported as a file in use - although every final name fitted
        // (the code review of 2026-10-08). Here the zip's name is 214 and the memo's 221 characters.
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, new string('a', 210) + ".zip");

        ModIoExportResult result = ModIoExporter.Export(
            zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja"));

        Assert.True(File.Exists(result.ZipPath));
        Assert.True(File.Exists(result.LogoPath));
        Assert.True(File.Exists(result.GuidePath));
        Assert.Equal(3, Directory.GetFiles(_root).Length);
    }

    [Theory]
    [InlineData(245)]
    [InlineData(247)]
    public void zipの名前が長すぎて横のファイルの名前が255文字を超えるときは何も書かずに断る(int stem)
    {
        // NTFS takes up to 255 characters for one name, and the memo is named after the zip with 7
        // more (a zip of 249: logo 254, memo 256; of 251: logo 256). Since the temporary names
        // became fixed in length every write went through, and the zip (and the logo) were moved
        // into place before the next one failed, reported as a file in use - on every try (the
        // adversarial verification of 2026-10-09, A2)
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, new string('a', stem) + ".zip");
        File.WriteAllText(zip, "old");

        ToolException ex = Assert.Throws<ToolException>(
            () => ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja")));

        Assert.Equal("error.modio.fileNameTooLong", ex.MessageKey);
        Assert.Equal("old", File.ReadAllText(zip));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void zipの名前が248文字なら登録メモの名前がちょうど255文字で3つとも書ける()
    {
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, new string('a', 244) + ".zip");

        ModIoExportResult result = ModIoExporter.Export(
            zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja"));

        Assert.Equal(255, Path.GetFileName(result.GuidePath).Length);
        Assert.True(File.Exists(result.ZipPath));
        Assert.True(File.Exists(result.LogoPath));
        Assert.True(File.Exists(result.GuidePath));
        Assert.Equal(3, Directory.GetFiles(_root).Length);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("logo")]
    [InlineData("guide")]
    public void 読み取り専用のファイルも置き換えて3つをそろえる(string which)
    {
        // The window asks before replacing a logo or memo that is there (the save dialog for the
        // zip). Marked read-only by hand or by a backup or sync tool, the move refused it: only the
        // files before it were replaced, on every try. Cleared once the move objects, as
        // CharacterSkins and ModConfigWriter do (the adversarial verification of 2026-10-09, A3)
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, "ro.zip");
        (string logo, string guide) = ModIoExporter.CompanionPaths(zip);
        string[] files = [zip, logo, guide];
        foreach (string file in files)
        {
            File.WriteAllText(file, "old");
        }

        string held = which switch { "logo" => logo, "guide" => guide, _ => zip };
        File.SetAttributes(held, FileAttributes.ReadOnly);

        try
        {
            ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja"));

            foreach (string file in files)
            {
                Assert.False(File.ReadAllBytes(file).AsSpan().SequenceEqual("old"u8), $"置き換わっていない: {Path.GetFileName(file)}");
            }

            Assert.False(File.GetAttributes(held).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(3, Directory.GetFiles(_root).Length);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(_root))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    [Theory]
    [InlineData("logo")]
    [InlineData("guide")]
    public void 横のファイルの名前のフォルダーがあれば何も置き換えずに知らせる(string which)
    {
        // A folder under the logo's or memo's name is there to be seen before anything moves;
        // stopping only after the zip had been replaced left it out of step with the files beside
        // it for no reason (the adversarial verification of 2026-10-09, A3)
        using SKBitmap sheet = Sheet();
        string zip = Path.Combine(_root, "dir.zip");
        (string logo, string guide) = ModIoExporter.CompanionPaths(zip);
        File.WriteAllText(zip, "old");
        string folder = which == "logo" ? logo : guide;
        Directory.CreateDirectory(folder);

        ModIoWriteException ex = Assert.Throws<ModIoWriteException>(
            () => ModIoExporter.Export(zip, sheet, Layout, new ModIoExportRequest(Options(), "1.3.0", "1.3.0", "ja")));

        Assert.Equal(folder, ex.FilePath);
        Assert.Empty(ex.Replaced);
        Assert.Equal("old", File.ReadAllText(zip));
        Assert.Single(Directory.GetFiles(_root));
        Assert.True(Directory.Exists(folder));
    }

    // ------------------------------------------------------------ The logo

    [Fact]
    public void ロゴは1280x720で_立ち姿の色がそのまま拡大される()
    {
        using SKBitmap sheet = Sheet();
        using SKBitmap logo = ModIoLogo.Draw(sheet, Layout);

        Assert.Equal(1280, logo.Width);
        Assert.Equal(720, logo.Height);

        // 16:9, and at least what mod.io asks for (taken from the logo itself, so a change of size
        // made together with the two lines above is still held to 16:9)
        Assert.Equal(16 * logo.Height, 9 * logo.Width);
        Assert.True(logo.Width >= 512 && logo.Height >= 288);

        // Every opaque colour of the three standing frames shows up unchanged: nearest-neighbour
        // enlargement keeps the art's pixels as they are
        HashSet<uint> art = [];
        foreach (string pose in new[] { "idle_down", "idle_right", "idle_up" })
        {
            FrameRect frame = Layout.Frames.First(f => f.Index == Layout.Animations[pose][0]);
            for (int y = frame.YTopLeft; y < frame.YTopLeft + frame.H; y++)
            {
                for (int x = frame.X; x < frame.X + frame.W; x++)
                {
                    SKColor c = sheet.GetPixel(x, y);
                    if (c.Alpha == 255)
                    {
                        art.Add((uint)c);
                    }
                }
            }
        }

        Assert.NotEmpty(art);

        HashSet<uint> shown = [];
        for (int y = 0; y < logo.Height; y++)
        {
            for (int x = 0; x < logo.Width; x++)
            {
                shown.Add((uint)logo.GetPixel(x, y));
            }
        }

        Assert.DoesNotContain(art, c => !shown.Contains(c));

        // Opaque all over, so mod.io has no transparency to fill in
        Assert.Equal(255, logo.GetPixel(0, 0).Alpha);
        Assert.Equal(255, logo.GetPixel(1279, 719).Alpha);
    }

    [Fact]
    public void 立ち姿のコマが空ならロゴから外し_影だけの枠を描かない()
    {
        // Front and back drawn, the right-facing frame left empty: two figures, not two figures and
        // a shadow with nobody standing on it
        using SKBitmap sheet = BlankSheet();
        Fill(sheet, FirstFrameOf("idle_down"), SKColors.Red);
        Fill(sheet, FirstFrameOf("idle_up"), SKColors.Red);

        using SKBitmap logo = ModIoLogo.Draw(sheet, Layout);

        Assert.Equal(2, ColumnRuns(logo, (c, _) => c == SKColors.Red));
        Assert.Equal(2, ColumnRuns(logo, (c, y) => DiffersFrom(c, logo.GetPixel(0, y))));
    }

    [Fact]
    public void 立ち姿が3つとも空なら_描かれた別のコマをロゴに使う()
    {
        // The export accepts a sheet with any frame drawn; a logo of background only would show
        // nothing of the mod, which Core Keeper's mod.io guidelines ask the images to show
        using SKBitmap sheet = BlankSheet();
        HashSet<int> standing = [.. new[] { "idle_down", "idle_right", "idle_up" }.Select(p => FirstFrameOf(p).Index)];
        FrameRect other = Layout.Frames.Last(f => !standing.Contains(f.Index));
        Fill(sheet, other, SKColors.Blue);

        using SKBitmap logo = ModIoLogo.Draw(sheet, Layout);

        Assert.Equal(1, ColumnRuns(logo, (c, _) => c == SKColors.Blue));
    }

    private static FrameRect FirstFrameOf(string pose) =>
        Layout.Frames.First(f => f.Index == Layout.Animations[pose][0]);

    private static SKBitmap BlankSheet()
    {
        SKBitmap sheet = new(new SKImageInfo(Layout.Texture.Width, Layout.Texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        sheet.Erase(SKColors.Transparent);
        return sheet;
    }

    /// <summary>Paints the middle of a frame, a block half its size.</summary>
    private static void Fill(SKBitmap sheet, FrameRect frame, SKColor color)
    {
        for (int y = frame.YTopLeft + (frame.H / 4); y < frame.YTopLeft + (frame.H * 3 / 4); y++)
        {
            for (int x = frame.X + (frame.W / 4); x < frame.X + (frame.W * 3 / 4); x++)
            {
                sheet.SetPixel(x, y, color);
            }
        }
    }

    /// <summary>How many separate runs of columns hold a pixel the test marks.</summary>
    private static int ColumnRuns(SKBitmap image, Func<SKColor, int, bool> marks)
    {
        int runs = 0;
        bool inRun = false;
        for (int x = 0; x < image.Width; x++)
        {
            bool marked = false;
            for (int y = 0; y < image.Height && !marked; y++)
            {
                marked = marks(image.GetPixel(x, y), y);
            }

            if (marked && !inRun)
            {
                runs++;
            }

            inRun = marked;
        }

        return runs;
    }

    /// <summary>
    /// Whether a pixel is something drawn over the background. The background only changes from top
    /// to bottom, so its left edge, inside the margin, is the background of that row.
    /// </summary>
    private static bool DiffersFrom(SKColor pixel, SKColor background) =>
        Math.Abs(pixel.Red - background.Red) > 8
        || Math.Abs(pixel.Green - background.Green) > 8
        || Math.Abs(pixel.Blue - background.Blue) > 8;

    // ------------------------------------------------------------ The memo

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 登録メモに選ぶタグとファイル名と識別子が入る(string language)
    {
        string memo = ModIoGuide.Render(language, new ModIoGuideValues(
            "Ninja", "1.2.0", ShowGear: true, "1.3.0", "Ninja-1.2.0.zip", "Ninja-1.2.0-logo.png", FixedIdentity));

        Assert.DoesNotContain("{{", memo, StringComparison.Ordinal);
        Assert.Contains("Ninja-1.2.0.zip", memo, StringComparison.Ordinal);
        Assert.Contains("Ninja-1.2.0-logo.png", memo, StringComparison.Ordinal);
        Assert.Contains(FixedIdentity.Name, memo, StringComparison.Ordinal);
        Assert.Contains(FixedIdentity.Guid, memo, StringComparison.Ordinal);
        Assert.Contains("https://mod.io/g/corekeeper", memo, StringComparison.Ordinal);

        // The four tags: without the game version the game warns at start-up and loads the mod
        // only if the player chooses "Load anyway", and with Access Type "Asset" it loads none of
        // the scripts
        Assert.Matches(@"Game Version\s+1\.3\.0", memo);
        Assert.Matches(@"Access Type\s+Script\b", memo);
        Assert.Matches(@"Application Type\s+Client\b", memo);
        Assert.Matches(@"Type\s+Visual\b", memo);
    }

    [Fact]
    public void 登録メモは装備の見え方を言語ごとに書き分ける()
    {
        ModIoGuideValues values = new("Ninja", "1.0.0", ShowGear: false, "1.3.0", "a.zip", "a-logo.png", FixedIdentity);

        foreach (string language in ModIoGuide.Languages)
        {
            (IReadOnlyDictionary<string, string> header, _) = ModIoGuide.Load(language);

            Assert.Contains(header["gearHidden"], ModIoGuide.Render(language, values), StringComparison.Ordinal);
            Assert.Contains(header["gearShown"], ModIoGuide.Render(language, values with { ShowGear = true }), StringComparison.Ordinal);
            Assert.NotEqual(header["gearShown"], header["gearHidden"]);
        }
    }

    [Fact]
    public void 登録メモのひな形はどの言語もすべての項目を使う()
    {
        Assert.Equal(new[] { "en", "ja" }, ModIoGuide.Languages);

        foreach (string language in ModIoGuide.Languages)
        {
            (IReadOnlyDictionary<string, string> header, string body) = ModIoGuide.Load(language);

            Assert.All(ModIoGuide.HeaderKeys, k => Assert.True(header.ContainsKey(k), $"{language}: #{k} が無い"));

            HashSet<string> used = [.. Regex.Matches(body, @"\{\{([A-Za-z]+)\}\}").Select(m => m.Groups[1].Value)];
            Assert.Equal(ModIoGuide.Tokens.OrderBy(t => t, StringComparer.Ordinal), used.OrderBy(t => t, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void 要約の例は名前が50文字でも250文字に収まる()
    {
        // mod.io's summary takes 250 characters. The examples are the lines that end the sentence
        // with the tool's name.
        string name = new('名', ModIoExporter.MaxDisplayNameLength);

        foreach (string language in ModIoGuide.Languages)
        {
            string memo = ModIoGuide.Render(language, new ModIoGuideValues(name, "1.0.0", false, "1.3.0", "a.zip", "a-logo.png", FixedIdentity));
            string[] examples = [.. memo.Split(Environment.NewLine)
                .Select(l => l.Trim())
                .Where(l => l.Contains(name, StringComparison.Ordinal) && (l.EndsWith("(cks).", StringComparison.Ordinal) || l.EndsWith("作りました。", StringComparison.Ordinal)))];

            Assert.NotEmpty(examples);
            Assert.All(examples, l => Assert.True(l.Length <= 250, $"{language}: {l.Length} 文字: {l}"));
        }
    }

    [Fact]
    public void 要約の例が2行あれば_どちらか1行だけを貼るよう書く()
    {
        // mod.io joins the pasted lines into one paragraph. Both Japanese examples together pass the
        // 250 characters once the name has 38 or more, so the memo has to say to paste one of them.
        string name = new('名', ModIoExporter.MaxDisplayNameLength);

        foreach (string language in ModIoGuide.Languages)
        {
            string memo = ModIoGuide.Render(language, new ModIoGuideValues(name, "1.0.0", false, "1.3.0", "a.zip", "a-logo.png", FixedIdentity));
            string[] lines = memo.Split(Environment.NewLine);
            int examples = lines
                .Select(l => l.Trim())
                .Count(l => l.Contains(name, StringComparison.Ordinal) && (l.EndsWith("(cks).", StringComparison.Ordinal) || l.EndsWith("作りました。", StringComparison.Ordinal)));

            if (examples > 1)
            {
                string heading = lines.First(l => l.Contains("Summary", StringComparison.Ordinal));
                Assert.Matches("1 行|one line", heading);
            }
        }
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 登録メモは公開の設定をファイルより先に選ばせ_公開するボタンを示す(string language)
    {
        // The visibility is a field of mod.io's first stage, Public unless changed, and Core Keeper's
        // mod.io approves nothing first: the mod is public as soon as its first file is published.
        // A visibility step after the upload comes too late, and a file kept as a draft never
        // reaches the game.
        string memo = ModIoGuide.Render(language, new ModIoGuideValues(
            "Ninja", "1.2.0", ShowGear: true, "1.3.0", "Ninja-1.2.0.zip", "Ninja-1.2.0-logo.png", FixedIdentity));

        int visibility = memo.IndexOf("Mod visibility", StringComparison.Ordinal);
        int create = memo.IndexOf("Create Mod", StringComparison.Ordinal);
        int fileManager = memo.IndexOf("File manager", StringComparison.Ordinal);
        int publish = memo.IndexOf("Upload & publish", StringComparison.Ordinal);

        Assert.True(visibility >= 0, "Mod visibility が無い");
        Assert.True(create > visibility, "Create Mod が公開の設定の後に無い");
        Assert.True(fileManager > create, "ファイルを上げる手順が Create Mod の後に無い");
        Assert.True(publish > fileManager, "Upload & publish がファイルの手順に無い");

        Assert.Contains("I agree to the terms", memo, StringComparison.Ordinal);
        Assert.Contains("Upload & keep as draft", memo, StringComparison.Ordinal);
        Assert.Contains("https://mod.io/g/corekeeper/m/add", memo, StringComparison.Ordinal);
        Assert.Contains("Profile URL", memo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja", "「無効にする」")]
    [InlineData("en", "\"Disable\"")]
    public void 登録メモは版の警告で無効にした人には有効にし直すまで読まれないと書く(string language, string disable)
    {
        // The game's warning for a mod without the game's version disables it for that player unless
        // "Load anyway" is chosen (ModIOUnity.DisableMod), and adding the tag later does not undo that.
        // The warning names the mod by its identifier, not by the name entered on mod.io.
        string memo = ModIoGuide.Render(language, new ModIoGuideValues(
            "Ninja", "1.0.0", false, "1.3.0", "a.zip", "a-logo.png", FixedIdentity));

        Assert.Contains(disable, memo, StringComparison.Ordinal);
        Assert.True(Regex.Matches(memo, Regex.Escape(FixedIdentity.Name)).Count >= 2, "警告に出る識別子が書かれていない");
    }

    [Fact]
    public void 名前に波括弧があっても登録メモはそのまま書く()
    {
        string memo = ModIoGuide.Render("ja", new ModIoGuideValues(
            "{{version}} {{id}}", "9.9.9", false, "1.3.0", "a.zip", "a-logo.png", FixedIdentity));

        Assert.Contains("{{version}} {{id}}", memo, StringComparison.Ordinal);
    }

    [Fact]
    public void 無い言語の登録メモは日本語で書く()
    {
        ModIoGuideValues values = new("Ninja", "1.0.0", false, "1.3.0", "a.zip", "a-logo.png", FixedIdentity);
        Assert.Equal(ModIoGuide.Render("ja", values), ModIoGuide.Render("xx", values));
    }

    // ------------------------------------------------------------ The game version tag

    [Fact]
    public void ゲームの版から先頭3つのタグを作る()
    {
        Assert.Equal("1.3.0", ModIoExporter.GameVersionTag("1.3.0.4-511d"));
        Assert.Equal("1.2.1", ModIoExporter.GameVersionTag("1.2.1.5-8be0"));
    }

    [Fact]
    public void ゲームの版が分からなければ確認済みの最新の版にする()
    {
        string tag = ModIoExporter.GameVersionTag(null);

        Version[] families = [.. GameVersion.SupportedVersions
            .Select(GameVersion.MajorMinorPatch)
            .OfType<string>()
            .Select(Version.Parse)];

        Assert.Contains(Version.Parse(tag), families);
        Assert.All(families, f => Assert.True(f <= Version.Parse(tag)));
        Assert.Equal(tag, ModIoExporter.GameVersionTag("not a version"));
    }

    // ------------------------------------------------------------ Helpers

    /// <summary>
    /// Decodes a PNG as it was stored. Skia's default decode premultiplies, and reading a pixel back
    /// then rounds every semi-transparent one; the sheet itself is held unpremultiplied.
    /// </summary>
    private static SKBitmap DecodeUnpremul(byte[] png)
    {
        using SKCodec codec = SKCodec.Create(new MemoryStream(png));
        SKImageInfo info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
        return SKBitmap.Decode(codec, info);
    }

    /// <summary>Walks up from the test binaries until the source folders appear.</summary>
    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "mod-export"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "core", "Export")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
    }
}

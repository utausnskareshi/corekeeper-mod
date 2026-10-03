using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for reading back the appearance the mod writes out from inside the game.
///
/// The real game folder is never touched, and no game artwork is used. The captured files are
/// written here by hand in the shape the mod writes them: one band per layer, stacked downwards
/// in the order the game draws them, with a manifest saying which order that was.
///
/// Nearly all of them run against a six-by-four sheet rather than the real 234x156 one. Composing
/// reads nothing from the layout but the texture size, so the small one exercises the same code,
/// and it keeps this class from writing a dozen full-size images into the temporary folder while
/// the rest of the suite is using it.
/// </summary>
public sealed class CapturedSkinTests : IDisposable
{
    private static readonly SheetLayout RealLayout = SheetLayout.LoadEmbedded();

    /// <summary>The same layout shrunk to a few pixels, which is all composing looks at.</summary>
    private static readonly SheetLayout Layout = RealLayout with { Texture = new SizeSpec(6, 4) };

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"cks-capture-test-{Guid.NewGuid():N}");

    private const string ModFolder = "CustomPlayerSkin";

    private const string Guid1 = "0123456789abcdef0123456789abcdef";

    public CapturedSkinTests() => Directory.CreateDirectory(CapturedDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    private string ModsDirectory => Path.Combine(_workDirectory, "mods");

    private string CapturedDirectory =>
        Path.Combine(ModsDirectory, ModFolder, CapturedSkins.FolderName);

    /// <summary>Writes the manifest the mod writes beside the captures.</summary>
    private void WriteManifest(int format = CapturedSkins.SupportedFormat, string[]? layers = null)
    {
        string names = string.Join(", ", (layers ?? CapturedSkins.Layers).Select(l => $"\"{l}\""));
        File.WriteAllText(
            Path.Combine(CapturedDirectory, CapturedSkins.ManifestFileName),
            $"{{ \"format\": {format}, \"layers\": [{names}] }}");
    }

    /// <summary>
    /// Writes a capture whose bands are filled with the given colours, one per layer.
    /// A null entry leaves that band transparent, which is what an unequipped slot looks like.
    /// </summary>
    private string WriteCapture(SheetLayout layout, params SKColor?[] bands)
    {
        int width = layout.Texture!.Width;
        int height = layout.Texture.Height;
        int count = CapturedSkins.Layers.Length;

        using SKBitmap stacked = PixelOps.CreateEmpty(width, height * count);
        SKColor[] pixels = stacked.Pixels;

        for (int band = 0; band < count && band < bands.Length; band++)
        {
            if (bands[band] is not { } colour)
            {
                continue;
            }

            int offset = band * height * width;
            for (int i = 0; i < height * width; i++)
            {
                pixels[offset + i] = colour;
            }
        }

        stacked.Pixels = pixels;

        string path = Path.Combine(CapturedDirectory, Guid1 + CapturedSkins.Extension);
        PixelOps.EncodePng(stacked, path);
        return path;
    }

    /// <summary>Every band opaque and distinguishable, so an ordering mistake is visible.</summary>
    private static SKColor?[] DistinctBands() =>
    [
        new SKColor(10, 0, 0), new SKColor(20, 0, 0), new SKColor(30, 0, 0),
        new SKColor(40, 0, 0), new SKColor(50, 0, 0), new SKColor(60, 0, 0),
        new SKColor(70, 0, 0), new SKColor(80, 0, 0), new SKColor(90, 0, 0),
    ];

    [Fact]
    public void 帯の枚数と描画順が固定されている()
    {
        // The mod writes the bands in this order and the tool stacks them in it. Changing either
        // side alone silently produces a sheet of the right size with the wrong layer on top.
        Assert.Equal(9, CapturedSkins.Layers.Length);
        Assert.Equal("body", CapturedSkins.Layers[0]);
        Assert.Equal(new[] { "helm", "breastArmor", "pantsArmor" }, CapturedSkins.Layers[^3..]);
        Assert.Equal(CapturedSkins.EquipmentLayers, CapturedSkins.Layers[^3..]);
    }

    [Fact]
    public void すべて不透明なら兜が一番上に見える()
    {
        WriteManifest();
        string path = WriteCapture(Layout, DistinctBands());

        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment: true);

        // Every band is opaque, so the layer the game draws last - the helmet, band 6, not the
        // last band in the file - is the only one that can be seen
        Assert.Equal(Layout.Texture!.Width, composed.Width);
        Assert.Equal(Layout.Texture.Height, composed.Height);
        Assert.Equal(new SKColor(70, 0, 0), composed.GetPixel(0, 0));
    }

    [Fact]
    public void 装備を含めなければ装備の帯は無視される()
    {
        WriteManifest();
        string path = WriteCapture(Layout, DistinctBands());

        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment: false);

        // The shirt is drawn last of the layers that are not equipment (over the trousers)
        Assert.Equal(new SKColor(50, 0, 0), composed.GetPixel(0, 0));
    }

    [Fact]
    public void 装備の帯だけが塗られていれば装備なしでは透明になる()
    {
        WriteManifest();

        SKColor?[] bands = new SKColor?[CapturedSkins.Layers.Length];
        bands[6] = new SKColor(1, 2, 3);
        bands[7] = new SKColor(4, 5, 6);
        bands[8] = new SKColor(7, 8, 9);

        string path = WriteCapture(Layout, bands);

        using SKBitmap withEquipment = CapturedSkins.Compose(path, Layout, includeEquipment: true);
        using SKBitmap without = CapturedSkins.Compose(path, Layout, includeEquipment: false);

        // The helmet is drawn over both kinds of armour
        Assert.Equal(new SKColor(1, 2, 3), withEquipment.GetPixel(0, 0));
        Assert.Equal(0, without.GetPixel(0, 0).Alpha);
    }

    /// <summary>
    /// Paints only the named bands, each opaque in its own colour, and composites them.
    /// </summary>
    private SKColor TopOf(bool includeEquipment, params (string Layer, SKColor Colour)[] painted)
    {
        WriteManifest();

        SKColor?[] bands = new SKColor?[CapturedSkins.Layers.Length];
        foreach ((string layer, SKColor colour) in painted)
        {
            bands[Array.IndexOf(CapturedSkins.Layers, layer)] = colour;
        }

        string path = WriteCapture(Layout, bands);
        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment);
        return composed.GetPixel(0, 0);
    }

    /// <summary>
    /// The bands are stacked in the order the game draws them, not in the order they sit in the
    /// file. Every layer of the 1.3.0.2 Player prefab shares one sorting layer, and its sorting
    /// orders are body 0, hairShade 1, eyes 2, hair 3, pants 4, shirt 5, pantsArmor 6, breastArmor 7,
    /// helm 10 (the z positions agree). Stacking in file order put the trousers over the shirt, the
    /// hair shading and eyes over the hair, leg armour over chest armour, and the helmet under both.
    /// </summary>
    [Fact]
    public void シャツはズボンの上に描く()
    {
        SKColor shirt = new(50, 0, 0), pants = new(60, 0, 0);

        Assert.Equal(shirt, TopOf(false, ("pants", pants), ("shirt", shirt)));
    }

    [Fact]
    public void 髪は髪の影と目の上に描く()
    {
        SKColor hair = new(20, 0, 0), shade = new(30, 0, 0), eyes = new(40, 0, 0);

        Assert.Equal(hair, TopOf(false, ("hair", hair), ("hairShade", shade), ("eyes", eyes)));
        Assert.Equal(eyes, TopOf(false, ("hairShade", shade), ("eyes", eyes)));
    }

    [Fact]
    public void 兜は胴防具と脚防具の上に描き胴防具は脚防具の上に描く()
    {
        SKColor helm = new(1, 2, 3), chest = new(4, 5, 6), legs = new(7, 8, 9);

        Assert.Equal(helm, TopOf(true, ("helm", helm), ("breastArmor", chest), ("pantsArmor", legs)));
        Assert.Equal(chest, TopOf(true, ("breastArmor", chest), ("pantsArmor", legs)));
    }

    [Fact]
    public void 描く順は帯と同じ層を1回ずつ並べたもの()
    {
        // A name missing from either list would drop a band or index past the file
        Assert.Equal(CapturedSkins.Layers.Order(), CapturedSkins.DrawOrder.Order());
        Assert.Equal("body", CapturedSkins.DrawOrder[0]);
    }

    [Fact]
    public void 透明な帯は下の帯を隠さない()
    {
        WriteManifest();

        // Only body is painted; every layer above it is transparent, as it is for a character
        // with no hair style, no shirt and nothing equipped.
        string path = WriteCapture(Layout, new SKColor(11, 22, 33));

        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment: true);

        Assert.Equal(new SKColor(11, 22, 33), composed.GetPixel(0, 0));
    }

    [Fact]
    public void 半透明の帯は下と混ざる()
    {
        WriteManifest();

        SKColor?[] bands = new SKColor?[CapturedSkins.Layers.Length];
        bands[0] = new SKColor(0, 0, 0);
        bands[1] = new SKColor(255, 255, 255, 128);

        string path = WriteCapture(Layout, bands);

        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment: true);
        SKColor pixel = composed.GetPixel(0, 0);

        // Half of white over black lands in the middle, and the result is fully opaque because the
        // layer underneath was. A wrong premultiplication shows up here as a dark fringe instead.
        Assert.Equal(byte.MaxValue, pixel.Alpha);
        Assert.InRange(pixel.Red, 126, 130);
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Red, pixel.Blue);
    }

    [Fact]
    public void 半透明どうしを重ねると不透明度が上がる()
    {
        WriteManifest();

        SKColor?[] bands = new SKColor?[CapturedSkins.Layers.Length];
        bands[0] = new SKColor(255, 0, 0, 128);
        bands[1] = new SKColor(255, 0, 0, 128);

        string path = WriteCapture(Layout, bands);

        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment: true);
        SKColor pixel = composed.GetPixel(0, 0);

        // Neither layer is opaque, so the result must not be either - and it must not stay at 128
        Assert.InRange(pixel.Alpha, 185, 195);
        Assert.Equal(255, pixel.Red);
    }

    [Fact]
    public void 何も塗られていない取り込みは透明なシートになる()
    {
        WriteManifest();
        string path = WriteCapture(Layout);

        using SKBitmap composed = CapturedSkins.Compose(path, Layout, includeEquipment: true);

        Assert.Equal(0, composed.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void 段数が足りない画像は寸法違いとして断られる()
    {
        WriteManifest();

        // Six bands instead of nine, which is what a mod flattening the appearance would write
        string path = Path.Combine(CapturedDirectory, Guid1 + CapturedSkins.Extension);
        using (SKBitmap wrong = PixelOps.CreateEmpty(Layout.Texture!.Width, Layout.Texture.Height * 6))
        {
            PixelOps.EncodePng(wrong, path);
        }

        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.Compose(path, Layout, includeEquipment: true));

        Assert.Equal("error.capture.badSize", error.MessageKey);
    }

    [Fact]
    public void 幅が違う画像は寸法違いとして断られる()
    {
        WriteManifest();

        string path = Path.Combine(CapturedDirectory, Guid1 + CapturedSkins.Extension);
        int height = Layout.Texture!.Height * CapturedSkins.Layers.Length;
        using (SKBitmap wrong = PixelOps.CreateEmpty(Layout.Texture.Width + 1, height))
        {
            PixelOps.EncodePng(wrong, path);
        }

        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.Compose(path, Layout, includeEquipment: true));

        Assert.Equal("error.capture.badSize", error.MessageKey);
    }

    [Fact]
    public void 書式ファイルが無ければ更新を促す()
    {
        // No manifest at all, which is what an older mod leaves behind
        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder));

        Assert.Equal("error.capture.manifestMissing", error.MessageKey);
    }

    [Fact]
    public void 書式の版が違えば断られる()
    {
        WriteManifest(format: CapturedSkins.SupportedFormat + 1);

        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder));

        Assert.Equal("error.capture.formatMismatch", error.MessageKey);
    }

    [Fact]
    public void 帯の並びが違えば断られる()
    {
        // The same nine names with two of them swapped. The file is the right size, so nothing
        // else would notice: hair would be composited where the shirt belongs.
        string[] swapped = [.. CapturedSkins.Layers];
        (swapped[1], swapped[4]) = (swapped[4], swapped[1]);
        WriteManifest(layers: swapped);

        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder));

        Assert.Equal("error.capture.layersMismatch", error.MessageKey);
    }

    [Fact]
    public void 帯が1枚足りない書式も断られる()
    {
        WriteManifest(layers: [.. CapturedSkins.Layers[..^1]]);

        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder));

        Assert.Equal("error.capture.layersMismatch", error.MessageKey);
    }

    [Fact]
    public void 壊れた書式ファイルは断られる()
    {
        File.WriteAllText(
            Path.Combine(CapturedDirectory, CapturedSkins.ManifestFileName), "{ \"format\": ");

        ToolException error = Assert.Throws<ToolException>(
            () => CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder));

        Assert.Equal("error.capture.manifestUnreadable", error.MessageKey);
    }

    [Fact]
    public void 書式ファイルの鍵は大文字小文字を問わない()
    {
        // The mod writes camelCase, which is what a JSON file should look like. A reader that
        // matched names exactly would leave the version at zero and report a mismatch that is not.
        File.WriteAllText(
            Path.Combine(CapturedDirectory, CapturedSkins.ManifestFileName),
            $"{{ \"Format\": {CapturedSkins.SupportedFormat}, \"Layers\": [" +
            string.Join(", ", CapturedSkins.Layers.Select(l => $"\"{l}\"")) + "] }");

        CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder);
    }

    [Fact]
    public void 正しい書式ファイルは通る()
    {
        WriteManifest();

        CapturedSkins.EnsureFormatUnderstood(ModsDirectory, ModFolder);
    }

    [Fact]
    public void 取り込み済みのキャラクターを一覧できる()
    {
        WriteManifest();
        WriteCapture(Layout, new SKColor(1, 2, 3));

        IReadOnlySet<string> found = CapturedSkins.CapturedGuids(ModsDirectory, ModFolder);

        Assert.Single(found);
        Assert.Contains(Guid1, found);
    }

    [Fact]
    public void 識別子でないファイル名は一覧に入らない()
    {
        WriteManifest();
        File.WriteAllText(Path.Combine(CapturedDirectory, "notaguid.png"), "x");
        File.WriteAllText(Path.Combine(CapturedDirectory, "readme.txt"), "x");

        Assert.Empty(CapturedSkins.CapturedGuids(ModsDirectory, ModFolder));
    }

    [Fact]
    public void 取り込みフォルダが無くても一覧は空で返る()
    {
        Directory.Delete(CapturedDirectory, recursive: true);

        Assert.Empty(CapturedSkins.CapturedGuids(ModsDirectory, ModFolder));
    }

    [Fact]
    public void MODが読まないフォルダ名は断られる()
    {
        // The mod's own path is a constant, so a capture filed anywhere else is never written
        Assert.Throws<ToolException>(
            () => CapturedSkins.DirectoryFor(ModsDirectory, "SomethingElse"));
    }

    [Fact]
    public void 識別子として不正なものはパスにしない()
    {
        Assert.Throws<ToolException>(
            () => CapturedSkins.PathFor(ModsDirectory, ModFolder, "../escape"));
    }

    [Fact]
    public void 大文字の識別子でも同じパスになる()
    {
        // The mod files a capture under the identifier as the game writes it; the save file and
        // this tool must agree on the name whichever case each of them happens to use.
        string lower = CapturedSkins.PathFor(ModsDirectory, ModFolder, Guid1);
        string upper = CapturedSkins.PathFor(ModsDirectory, ModFolder, Guid1.ToUpperInvariant());

        Assert.Equal(lower, upper);
    }

    // ------------------------------------------------------------ The contract with the mod

    /// <summary>
    /// Finds the repository root by walking up until the source folders appear.
    /// The mod is not part of any assembly these tests reference, so it is read as text.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "mod"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "core", "Install")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
    }

    private static string ReadModSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "mod", fileName));

    [Fact]
    public void 帯の並びがMOD側と一致する()
    {
        // The mod writes the bands and this side stacks them. Both hold the order as a literal,
        // because the mod is compiled by Unity against the game and cannot reference this code.
        // Until this test existed, a drift between them was found only by building the mod,
        // playing the game and pressing the button - and only when the tool happened to refuse
        // the result. Bands of the right shape and the wrong meaning composite silently into a
        // sheet of exactly the right size with the wrong layer on top.
        string source = ReadModSource("SkinCapture.cs");

        // The lookbehind matters: EquipmentLayerNames ends in this name, so without it the match
        // depends on which declaration happens to come first in the file.
        Match declaration = Regex.Match(
            source,
            @"(?<![A-Za-z])LayerNames\s*=\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        Assert.True(declaration.Success, "MOD 側の LayerNames の宣言が見つからない");

        string[] modLayers =
        [
            .. Regex.Matches(declaration.Groups["body"].Value, "\"(?<name>[^\"]+)\"")
                .Select(m => m.Groups["name"].Value),
        ];

        Assert.Equal(CapturedSkins.Layers, modLayers);
    }

    [Fact]
    public void 装備の帯がMOD側と一致する()
    {
        // The two sides treat these bands differently, and the difference has to agree on which
        // bands they are. The mod counts equipment as present only while the game is drawing it,
        // because a switched-off armour layer still holds the prefab's default armour; a base
        // layer counts as soon as its art has loaded, so that a picture without equipment still
        // has the shirt that the armour was covering. Drift either way is silent: a base layer
        // would vanish whenever something covered it, or armour nobody was wearing would appear.
        string source = ReadModSource("SkinCapture.cs");

        Match declaration = Regex.Match(
            source,
            @"EquipmentLayerNames\s*=\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        Assert.True(declaration.Success, "MOD 側の EquipmentLayerNames の宣言が見つからない");

        string[] modEquipment =
        [
            .. Regex.Matches(declaration.Groups["body"].Value, "\"(?<name>[^\"]+)\"")
                .Select(m => m.Groups["name"].Value),
        ];

        Assert.Equal(CapturedSkins.EquipmentLayers, modEquipment);

        // Each one also has to be a band that actually exists, or the mod would be classifying a
        // layer nothing stacks
        Assert.All(modEquipment, name => Assert.Contains(name, CapturedSkins.Layers));
    }

    [Fact]
    public void 書式の版がMOD側と一致する()
    {
        string source = ReadModSource("SkinCapture.cs");

        Match declaration = Regex.Match(source, @"const\s+int\s+Format\s*=\s*(?<value>\d+)\s*;");
        Assert.True(declaration.Success, "MOD 側の Format 定数が見つからない");

        Assert.Equal(
            CapturedSkins.SupportedFormat,
            int.Parse(declaration.Groups["value"].Value));
    }

    [Fact]
    public void MOD側のシート寸法がレイアウトと一致する()
    {
        // The mod measures every layer it captures against these two numbers and refuses anything
        // else, so a layout whose texture changed size would silently stop capturing rather than
        // capture the wrong thing. Worth knowing at build time either way.
        string source = ReadModSource("SkinApplier.cs");

        Match width = Regex.Match(source, @"ExpectedWidth\s*=\s*(?<value>\d+)\s*;");
        Match height = Regex.Match(source, @"ExpectedHeight\s*=\s*(?<value>\d+)\s*;");

        Assert.True(width.Success && height.Success, "MOD 側のシート寸法の定数が見つからない");

        Assert.Equal(RealLayout.Texture!.Width, int.Parse(width.Groups["value"].Value));
        Assert.Equal(RealLayout.Texture.Height, int.Parse(height.Groups["value"].Value));
    }

    [Fact]
    public void 取り込み先のフォルダ名がMOD側と一致する()
    {
        // The mod writes into this folder and the tool reads from it. Both spell it out.
        string source = ReadModSource("SkinCapture.cs");

        Assert.Contains(
            $"RootDirectory + \"{CapturedSkins.FolderName}/\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 配置先の名前と設定の区分がMOD側と一致する()
    {
        // The other direction of the same contract, and the one everything else rests on: the tool
        // files each picture under these names and the mod looks for it under its own spelling of
        // them. Nothing else compared the two, so renaming either side alone passed every test and
        // shipped a tool that reports "applied" for pictures the mod never reads - or, for the
        // section, settings toggles that the mod never sees.
        string mod = ReadModSource("CustomPlayerSkinMod.cs");

        Assert.Contains(
            $"ModName = \"{SheetInstaller.DefaultModFolderName}\"",
            mod,
            StringComparison.Ordinal);

        // The root the mod reads under is spelled from the name, not written out a second time
        Assert.Contains("RootDirectory = ModName + \"/\"", mod, StringComparison.Ordinal);

        Assert.Contains(
            $"SkinsDirectory = RootDirectory + \"{CharacterSkins.FolderName}/\"",
            mod,
            StringComparison.Ordinal);
        Assert.Contains(
            $"SkinsDirectory + guid + \"{CharacterSkins.Extension}\"",
            mod,
            StringComparison.Ordinal);

        Assert.Contains(
            $"Section = \"{ModConfigWriter.Section}\"",
            ReadModSource("ConfigValues.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MOD側がリフレクションを参照していない()
    {
        // The game compiles the mod's source at startup and then puts the result through
        // Trivial.CodeSecurity, which disallows System.Reflection. Naming it rejects the whole
        // assembly, so the mod does not load at all: the player gets a box saying the mod failed
        // to compile, with the real reason only in Player.log. Nothing catches it earlier - the
        // source compiles cleanly against the game's own assemblies, so building the mod, and
        // compiling it against the installed game, both stay silent.
        //
        // Measured on 1.3.0.1-90f1: a MethodBase parameter and a MemberInfo.Name call were each
        // rejected by name. Whether a local of such a type alone would be is not known, so the
        // type names are refused here rather than tried in the game.
        //
        // This looks for names, so reflection reached through var and an inferred type would
        // slip past. What Harmony needs in order to be handed a method - a TargetMethods
        // returning IEnumerable<MethodBase>, a MethodBase parameter - all name a type, and that
        // is what makes the check worth having.
        string[] forbidden =
        [
            "System.Reflection",
            "MethodBase",
            "MethodInfo",
            "MemberInfo",
            "FieldInfo",
            "PropertyInfo",
            "BindingFlags",
        ];

        List<string> offences = [];

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(FindRepositoryRoot(), "src", "mod"),
            "*.cs",
            SearchOption.AllDirectories))
        {
            int number = 0;

            foreach (string line in File.ReadLines(file))
            {
                number++;
                string trimmed = line.TrimStart();

                // A whole line of comment is prose, and the reason for this rule is worth
                // writing down beside the code that obeys it. A comment trailing a line of code
                // is not excused: moving it onto its own line is the cheaper fix.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                offences.AddRange(
                    forbidden
                        .Where(name => line.Contains(name, StringComparison.Ordinal))
                        .Select(name => $"{Path.GetFileName(file)}:{number} {name}"));
            }
        }

        Assert.True(
            offences.Count == 0,
            "MOD のソースがリフレクションを参照している。ゲームのコード検証に弾かれ、MOD ごと"
                + $"読み込まれなくなる:{Environment.NewLine}  "
                + string.Join($"{Environment.NewLine}  ", offences));
    }

    [Fact]
    public void MOD側がゲームのコード検査で拒否される名前を参照していない()
    {
        // The reflection check above covers one line of the game's rules. The rules themselves,
        // read from the RoslynCSharpSettings in 1.3.0.2-182b's resources.assets, deny the
        // namespaces System.IO, System.Diagnostics, System.Net, System.Runtime.InteropServices,
        // System.Reflection, RoslynCSharp and Pug.Platform, the type System.AppDomain, and fifteen
        // HarmonyLib types including AccessTools, Traverse and Harmony itself. Each of them rejects
        // the whole mod at startup, exactly as reflection does, and each compiles cleanly against
        // the game's assemblies. Measured against the game's own Trivial.CodeSecurity: a
        // Stopwatch call, Traverse.Create, AccessTools.DeclaredMethod and new Harmony(...) were all
        // rejected, while the reflection check passed every one of them.
        //
        // System.Diagnostics.CodeAnalysis is left out: its attributes are not examined by the game
        // (measured), and nullable annotations are a normal thing to write. The Harmony type is
        // looked for as a whole word in the code with its string literals taken out, because the
        // word is also part of this mod's attribute names, which the game allows, and of log text.
        // What is not covered: SetterHandler and Traverse exist in the game's Harmony only as
        // generics, which the game lets through (measured), and a bare Patch, Patches or Code
        // reached through "using HarmonyLib;" cannot be told apart from this mod's own
        // CustomPlayerSkin.Patches namespace by text, so only their qualified forms are refused.
        string[] forbidden =
        [
            "System.IO",
            "System.Diagnostics",
            "System.Net",
            "System.Runtime.InteropServices",
            "RoslynCSharp",
            "Pug.Platform",
            "AppDomain",
            "AccessTools",
            "Traverse",
            "FastAccess",
            "MethodInvoker",
            "PatchProcessor",
            "PatchClassProcessor",
            "ReversePatcher",
            "FastInvokeHandler",
            "PatchInfo",
            "Transpilers",
            "new Harmony(",
            "Harmony.",
            "DllImport",
        ];

        List<string> offences = [];

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(FindRepositoryRoot(), "src", "mod"),
            "*.cs",
            SearchOption.AllDirectories))
        {
            int number = 0;

            foreach (string line in File.ReadLines(file))
            {
                number++;
                string trimmed = line.TrimStart();

                // Whole lines of comment are prose, as in the reflection check above.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                string code = line.Replace("System.Diagnostics.CodeAnalysis", string.Empty, StringComparison.Ordinal);

                offences.AddRange(
                    forbidden
                        .Where(name => code.Contains(name, StringComparison.Ordinal))
                        .Select(name => $"{Path.GetFileName(file)}:{number} {name}"));

                // The Harmony type as a word, and the qualified HarmonyLib types, outside strings
                string bare = System.Text.RegularExpressions.Regex.Replace(code, @"""(?:[^""\\]|\\.)*""", "\"\"");
                foreach (string pattern in new[] { @"\bHarmony\b", @"\bHarmonyLib\.(Patch|Patches|Code)\b" })
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(bare, pattern))
                    {
                        offences.Add($"{Path.GetFileName(file)}:{number} {pattern}");
                    }
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "MOD のソースが、ゲームのコード検査で拒否される名前を参照している。MOD ごと"
                + $"読み込まれなくなる:{Environment.NewLine}  "
                + string.Join($"{Environment.NewLine}  ", offences));
    }

    [Fact]
    public void 取り込んだシートはそのまま編集できる()
    {
        // The one test at the real size: the whole point of the feature is that what comes out
        // can be loaded as a finished sheet, and that is decided by the real layout.
        WriteManifest();
        string path = WriteCapture(RealLayout, DistinctBands());

        using SKBitmap composed = CapturedSkins.Compose(path, RealLayout, includeEquipment: false);

        Editing.EditorDocument document = new(composed, RealLayout);

        Assert.Equal(RealLayout.Texture!.Width, document.Width);
        Assert.Equal(RealLayout.Texture.Height, document.Height);
    }
}

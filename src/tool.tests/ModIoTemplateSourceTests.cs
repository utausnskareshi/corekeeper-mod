using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Export;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// The scripts every mod.io export carries (src/mod-export) against the things they have to agree
/// with: the tool that writes them, and the tool's own mod they have to make way for.
///
/// Those scripts are compiled by the game, never by this repository, so nothing else notices when
/// one side changes a name and the other does not. Each test reads the source as text.
/// </summary>
public sealed class ModIoTemplateSourceTests
{
    private static string Root => ModIoExporterTests.FindRepositoryRoot();

    private static string Export(string file) => File.ReadAllText(Path.Combine(Root, "src", "mod-export", file));

    private static string Local(string file) => File.ReadAllText(Path.Combine(Root, "src", "mod", file));

    /// <summary>The value of <c>const string Name = "...";</c> in a source file.</summary>
    private static string Constant(string source, string name)
    {
        Match match = Regex.Match(source, $@"const\s+string\s+{name}\s*=\s*""([^""]*)""\s*;");
        Assert.True(match.Success, $"{name} が見つからない");
        return match.Groups[1].Value;
    }

    [Fact]
    public void 公開する型はIModの1つだけ()
    {
        // The loader creates every public IMod it finds, and each later mod is compiled against
        // the earlier ones. A public type with the same name in two exports is only a warning
        // there (CS0436, measured against the game's compiler settings), so the fewer the better.
        string[] publicTypes = [.. Directory.GetFiles(Path.Combine(Root, "src", "mod-export"), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"^\s*public\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|struct|interface|enum|record)\s+(\w+)", RegexOptions.Multiline))
            .Select(m => m.Groups[1].Value)];

        Assert.Equal(new[] { "CksSkinMod" }, publicTypes);
        Assert.Matches(@"public\s+class\s+CksSkinMod\s*:\s*IMod\b", Export("CksSkinMod.cs"));
    }

    [Fact]
    public void 名前の頭が書き出す側と同じ()
    {
        // How the skins tell each other apart, to use only the first in the load order
        Assert.Equal(ModIoExporter.NamePrefix, Constant(Export("CksSkinMod.cs"), "NamePrefix"));
    }

    [Fact]
    public void ツールのMODの名前と絵の置き場所がローカルのMODと同じ()
    {
        // A character the tool has given a picture keeps it; the export looks for that picture
        // where the tool's own mod keeps it
        string export = Export("CksSkinMod.cs");
        string local = Local("CustomPlayerSkinMod.cs");

        Assert.Equal(SheetInstaller.DefaultModFolderName, Constant(export, "ToolModName"));
        Assert.Equal(SheetInstaller.DefaultModFolderName, Constant(local, "ModName"));

        Assert.Contains("RootDirectory = ModName + \"/\";", local, StringComparison.Ordinal);
        Assert.Contains("SkinsDirectory = RootDirectory + \"skins/\";", local, StringComparison.Ordinal);
        Assert.Contains("PathFor(string guid) => SkinsDirectory + guid + \".png\";", local, StringComparison.Ordinal);
        Assert.Contains("CksSkinMod.ToolModName + \"/skins/\" + guid + \".png\"", Export("CksSkinApplier.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void フックする場所がローカルのMODと同じ()
    {
        // Both have to land on the same two methods, and on the player's own OnSpawn - a game update
        // that moves one is fixed in both places at once
        string export = Export("CksSkinPatch.cs");
        string local = Local(Path.Combine("Patches", "PlayerControllerPatch.cs"));

        foreach (string source in new[] { export, local })
        {
            Assert.Contains("[HarmonyPatch(typeof(PlayerController), nameof(PlayerController.RefreshCustomization))]", source, StringComparison.Ordinal);
            Assert.Contains("[HarmonyPatch(typeof(PlayerController), OnSpawnMethod)]", source, StringComparison.Ordinal);
            Assert.Equal("OnSpawn", Constant(source, "OnSpawnMethod"));
        }
    }

    [Fact]
    public void ローカルのMODは書き出したMODの絵が掛かっている間は取得用に記録しない()
    {
        // The tool's own mod tells an exported skin by the name of the texture the body is drawing.
        // Both sides hold the prefix as a literal, so a test holds them to each other: the export
        // names its picture after the mod (CksSkin_...) and its hiding texture CksSkin_Transparent.
        string capture = Local("SkinCapture.cs");
        Assert.Equal(ModIoExporter.NamePrefix, Constant(capture, "ExportedSkinPrefix"));

        Assert.Contains("texture.name = LogName;", Export("CksSkinMod.cs"), StringComparison.Ordinal);
        Assert.Contains("LogName = self.Metadata.name ?? LogName;", Export("CksSkinMod.cs"), StringComparison.Ordinal);

        // The picture takes the mod's name (CksSkin_...) only when LogName is set before the picture
        // is loaded; loaded first, it would be named "CksSkin" and the tool's own mod would record it
        string mod = Export("CksSkinMod.cs");
        int named = mod.IndexOf("LogName = self.Metadata.name ?? LogName;", StringComparison.Ordinal);
        int loaded = mod.IndexOf("LoadPicture(self)", StringComparison.Ordinal);
        Assert.True(named >= 0 && loaded > named, "絵を読む前に MOD の名前を LogName に入れていない");
        Assert.Contains("name = \"CksSkin_Transparent\",", Export("CksSkinApplier.cs"), StringComparison.Ordinal);
        Assert.StartsWith(ModIoExporter.NamePrefix, "CksSkin_Transparent", StringComparison.Ordinal);

        // Checked in Capture, after the guard for the tool's own picture and before anything is read
        string body = Regex.Match(capture, @"public static void Capture\(PlayerController player, bool rebuilt = false\)\s*\{(?<body>.*?)\n        \}\r?\n", RegexOptions.Singleline).Groups["body"].Value;
        int own = body.IndexOf("CustomPlayerSkinMod.IsReplacing(guid)", StringComparison.Ordinal);
        int exported = body.IndexOf("DrawnByExportedSkin(player)", StringComparison.Ordinal);
        int capture2 = body.IndexOf("CaptureNow(player, guid);", StringComparison.Ordinal);
        Assert.True(own >= 0 && exported > own && capture2 > exported, "書き出した MOD の絵の判定が、記録の前に無い");
        Assert.Matches(@"if\s*\(DrawnByExportedSkin\(player\)\)\s*\{\s*NextAttempt\[guid\]\s*=\s*now\s*\+\s*SettledIntervalSeconds;\s*NoteExportedSkin\(\);\s*return;\s*\}", body);

        // Told by what the shader is sampling now, the same way a capture reads it
        string drawn = Regex.Match(capture, @"private static bool DrawnByExportedSkin\(PlayerController player\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("DrawnTextureFor(body, renderer, material, out string source)", drawn, StringComparison.Ordinal);
        Assert.Contains("source == \"replacement\"", drawn, StringComparison.Ordinal);
        Assert.Contains("drawn.name.StartsWith(ExportedSkinPrefix, StringComparison.Ordinal)", drawn, StringComparison.Ordinal);
    }

    [Fact]
    public void 書き出したMODのフックはローカルのMODより後に動く()
    {
        // Harmony runs postfixes by priority before load order, so the tool's own mod (normal
        // priority) applies first whatever the order in the game's mod menu, and the export only
        // makes way for it by doing nothing
        string export = Export("CksSkinPatch.cs");

        Assert.Equal(2, Regex.Matches(export, @"\[HarmonyPostfix\]").Count);
        Assert.Equal(2, Regex.Matches(export, @"\[HarmonyPriority\(Priority\.Low\)\]").Count);
        Assert.DoesNotContain("HarmonyPriority", Local(Path.Combine("Patches", "PlayerControllerPatch.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void 絵と設定の場所と鍵が書き出す側と同じ()
    {
        string export = Export("CksSkinMod.cs");

        Assert.Equal(ModIoExporter.PictureEntry, Constant(export, "PicturePath"));
        Assert.Equal(ModIoExporter.SettingsEntry, Constant(export, "SettingsPath"));

        string settings = ModIoExporter.SettingsText(showGear: true, "1.0.0", "1.3.0");
        Assert.Contains("showGear=true\n", settings, StringComparison.Ordinal);
        Assert.Contains("version=1.0.0\n", settings, StringComparison.Ordinal);
        Assert.Contains("key == \"showGear\"", export, StringComparison.Ordinal);
        Assert.Contains("key == \"version\"", export, StringComparison.Ordinal);
        Assert.Contains("value == \"true\"", export, StringComparison.Ordinal);
    }

    [Fact]
    public void 想定する絵の寸法がゲームのシートと同じ()
    {
        SheetLayout layout = SheetLayout.LoadEmbedded();
        string applier = Export("CksSkinApplier.cs");

        Assert.Matches($@"ExpectedWidth\s*=\s*{layout.Texture.Width}\s*;", applier);
        Assert.Matches($@"ExpectedHeight\s*=\s*{layout.Texture.Height}\s*;", applier);
    }

    [Fact]
    public void 自分のキャラクターにだけ掛ける()
    {
        // Spec: whichever character the player plays, and nobody else on screen. The patches call
        // Apply for every PlayerController, so the guard at the top of Apply is the only filter:
        // held there, not merely somewhere in the file (a comment would have done before)
        string apply = Regex.Match(Export("CksSkinApplier.cs"), @"public static void Apply\(PlayerController player\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;

        Assert.Matches(@"if\s*\([^)]*!player\.isLocal[^)]*\)\s*\{\s*return;\s*\}", apply);
        int guard = apply.IndexOf("!player.isLocal", StringComparison.Ordinal);
        int draw = apply.IndexOf("SetLayer(player.bodySkin", StringComparison.Ordinal);
        Assert.True(guard >= 0 && draw > guard, "絵を掛ける前に自分のキャラクターかを確かめていない");
    }

    [Fact]
    public void ツールが絵を置いたキャラクターでは何もしない()
    {
        // Spec: a character the tool has given a picture keeps it. Checked before anything is drawn
        string apply = Regex.Match(Export("CksSkinApplier.cs"), @"public static void Apply\(PlayerController player\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;

        int wins = apply.IndexOf("ToolPictureWins()", StringComparison.Ordinal);
        int draw = apply.IndexOf("SetLayer(player.bodySkin", StringComparison.Ordinal);
        Assert.True(wins >= 0 && draw > wins, "絵を掛ける前にツールの絵を確かめていない");
        Assert.Matches(@"if\s*\(picture\s*==\s*null\s*\|\|\s*ToolPictureWins\(\)\)\s*\{\s*return;\s*\}", apply);
    }

    [Fact]
    public void 書き出したMODが複数あれば読み込み順で最初の1つだけが掛ける()
    {
        // Spec: every skin from the tool aims at the same character, so all of them agree on one -
        // the first among the loaded mods (the load order) whose name has the tool's prefix
        string mod = Export("CksSkinMod.cs");

        Assert.Contains("foreach (LoadedMod mod in API.ModLoader.LoadedMods)", mod, StringComparison.Ordinal);
        Assert.Matches(@"if\s*\(firstSkin\s*==\s*null\s*&&\s*name\.StartsWith\(NamePrefix,\s*StringComparison\.Ordinal\)\)", mod);
        // The ones that stand aside return before the settings and the picture are read, so they
        // hold no texture and never draw
        int rule = mod.IndexOf("if (firstSkin != null && firstSkin != self)", StringComparison.Ordinal);
        int settings = mod.IndexOf("Settings settings = ReadSettings(self);", StringComparison.Ordinal);
        int picture = mod.IndexOf("LoadPicture(self)", StringComparison.Ordinal);
        Assert.True(rule >= 0 && settings > rule && picture > settings, "読み込み順の判定が絵を読むより先に無い");
        Assert.Contains("return;", mod[rule..settings], StringComparison.Ordinal);
    }

    [Fact]
    public void ツールのMODが無ければ書き出したMODの絵を掛け_あればツールの絵を確かめる()
    {
        // Most people install an export from mod.io without the tool's own mod, and for them the
        // tool's picture can never win; where that mod is loaded, a picture it placed does. Neither
        // half was held: the early return could say true and the flag could stay down, and every
        // test passed (the adversarial verification of 2026-10-09, A8)
        string wins = Regex.Match(Export("CksSkinApplier.cs"), @"private static bool ToolPictureWins\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Matches(@"^\s*if\s*\(!CksSkinMod\.ToolModLoaded\)\s*\{\s*return false;\s*\}", wins);

        string mod = Export("CksSkinMod.cs");
        Assert.Matches(@"if\s*\(name\s*==\s*ToolModName\)\s*\{\s*ToolModLoaded\s*=\s*true;\s*\}", mod);
        int cleared = mod.IndexOf("ToolModLoaded = false;", StringComparison.Ordinal);
        int loop = mod.IndexOf("foreach (LoadedMod mod in API.ModLoader.LoadedMods)", StringComparison.Ordinal);
        Assert.True(cleared >= 0 && loop > cleared, "読み込んだ MOD を見る前にツールの MOD の印を下ろしていない");
    }

    [Fact]
    public void ローカルのMODが書き出したMODの絵と見なすのは4つの条件がそろったときだけ()
    {
        // Joined by || the tool's mod would stop recording every character, and the status line would
        // then tell the player to disable an exported mod they do not have. The existing test sees the
        // three parts but not how they are joined (the adversarial verification of 2026-10-09, A11)
        string drawn = Regex.Match(Local("SkinCapture.cs"), @"private static bool DrawnByExportedSkin\(PlayerController player\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Matches(
            @"return\s+source\s*==\s*""replacement""\s*&&\s*drawn\s*!=\s*null\s*&&\s*drawn\.name\s*!=\s*null\s*&&\s*drawn\.name\.StartsWith\(ExportedSkinPrefix,\s*StringComparison\.Ordinal\)\s*;",
            drawn);
    }

    [Fact]
    public void 装備の見え方の選択が絵を掛けるところまで届く()
    {
        // The export screen's choice travels as showGear=true/false in Skin/settings.txt; these three
        // places carry it into the game, and none of them runs in a test, so each is held by its
        // text: reading "true", copying it into the static switch, and hiding the five clothes and
        // armour layers only when the switch is off (the adversarial verification of 2026-10-09, A12)
        string mod = Export("CksSkinMod.cs");
        Assert.Matches(@"if\s*\(\s*value\s*==\s*""true""\s*\)\s*\{\s*settings\.ShowGear\s*=\s*true\s*;", mod);
        Assert.Matches(@"(?<![\w.])ShowGear\s*=\s*settings\.ShowGear\s*;", mod);

        string apply = Regex.Match(Export("CksSkinApplier.cs"), @"public static void Apply\(PlayerController player\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Matches(
            @"if\s*\(\s*!CksSkinMod\.ShowGear\s*\)\s*\{\s*" +
            @"Hide\(player\.shirtSkin\);\s*Hide\(player\.pantsSkin\);\s*Hide\(player\.helmSkin\);\s*" +
            @"Hide\(player\.breastArmorSkin\);\s*Hide\(player\.pantsArmorSkin\);\s*\}",
            apply);

        // Each of them hidden only there, so none is hidden when the gear is to be shown
        foreach (string layer in new[] { "shirtSkin", "pantsSkin", "helmSkin", "breastArmorSkin", "pantsArmorSkin" })
        {
            Assert.Single(Regex.Matches(apply, $@"Hide\(player\.{layer}\)"));
        }
    }
}

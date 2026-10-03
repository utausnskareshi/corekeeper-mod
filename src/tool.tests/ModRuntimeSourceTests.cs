using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Guards on how the mod behaves inside the game, read off its source.
///
/// The mod is compiled by the game against the game's own assemblies and cannot run outside it,
/// so what these pin down was measured in the real game (1.3.0.2-182b: its IL, its logs, its
/// settings API) and is held here as the shape of the code that follows from it. Each one failed
/// against the source before the change it guards.
/// </summary>
public sealed class ModRuntimeSourceTests
{
    /// <summary>Finds the repository root by walking up until the source folders appear.</summary>
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

    /// <summary>The source with whole lines of comment taken out, so prose cannot satisfy a check.</summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source.Split('\n').Where(line =>
        {
            string trimmed = line.TrimStart();
            return !trimmed.StartsWith("//", StringComparison.Ordinal)
                   && !trimmed.StartsWith("*", StringComparison.Ordinal)
                   && !trimmed.StartsWith("/*", StringComparison.Ordinal);
        }));

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{signature} が見つからない");

        int open = source.IndexOf('{', at);
        Assert.True(open >= 0, $"{signature} の本体が見つからない");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[open..i]; }
        }

        throw new InvalidOperationException($"{signature} の本体が閉じていない");
    }

    /// <summary>
    /// The cap on log lines for an image that keeps failing covers the reasons too. Only the mod's
    /// own two lines were capped; SkinStore's reason ("PNG として読み込めなかった" and the like) was
    /// written on every attempt, so a file rewritten and still broken on every poll went on adding a
    /// line per poll - 62 of the 68 warnings in a simulated run (the test campaign of 2026-09-29).
    /// A picture removed and put back starts the count again, so a new bad file is still reported.
    /// </summary>
    [Fact]
    public void 読み込みの連続失敗が上限を超えたら理由の警告も止める()
    {
        string reload = CodeOnly(MethodBody(
            ReadModSource("CustomPlayerSkinMod.cs"),
            "private static void Reload(string guid, SkinEntry entry, bool logWhenMissing)"));

        Assert.Matches(
            new Regex(@"SkinStore\.LoadTexture\(path,\s*logFailures:\s*entry\.ConsecutiveFailures\s*<\s*MaxTotalLoadFailures\)"),
            reload);

        int missing = reload.IndexOf("if (state == FileState.Missing)", StringComparison.Ordinal);
        int load = reload.IndexOf("SkinStore.LoadTexture(", StringComparison.Ordinal);
        Assert.True(missing >= 0 && load > missing, "Missing の分岐か読み込みの行が見つからない");
        Assert.Contains("entry.ConsecutiveFailures = 0;", reload[missing..load], StringComparison.Ordinal);

        // The reset has to be reachable. For a character whose picture never loaded the recorded
        // timestamp stays 0 while a rewritten bad file keeps failing, and a removed file also reads
        // 0, so the poll saw no change and never reached the Missing branch: the count stayed past
        // the cap and the next bad picture said nothing at all (aa1990e still gave its reasons).
        // A failing version whose file has gone is sent through Reload as well, and Missing forgets
        // the failing version so it is not sent again on every poll.
        string poll = CodeOnly(MethodBody(ReadModSource("CustomPlayerSkinMod.cs"), "private static bool ReloadChangedSkins()"));
        Assert.Matches(
            new Regex(@"pair\.Value\.FailedTimestamp\s*!=\s*0\s*&&\s*SkinStore\.GetTimestamp\(PathFor\(pair\.Key\)\)\s*==\s*0"),
            poll);
        Assert.Contains("entry.FailedTimestamp = 0;", reload[missing..load], StringComparison.Ordinal);

        string loadTexture = CodeOnly(MethodBody(
            ReadModSource("SkinStore.cs"), "public static Texture2D? LoadTexture(string path, bool logFailures = true)"));

        int all = Regex.Matches(loadTexture, @"CustomPlayerSkinMod\.LogWarning\(").Count;
        int guarded = Regex.Matches(loadTexture, @"if\s*\(logFailures\)\s*\{?\s*CustomPlayerSkinMod\.LogWarning\(").Count;
        Assert.Equal(4, all);
        Assert.Equal(all, guarded);
    }

    /// <summary>
    /// The two ways into a reload are alternatives: the file changed, or a failing version's file
    /// has gone. Joined with "and" only a file that did both was reloaded, which never happens, so
    /// hot reloading stopped altogether - and the check above still passed on the second half
    /// alone (the final review of 2026-10-02).
    /// </summary>
    [Fact]
    public void 変わったファイルと失敗中に消えたファイルはどちらでも読み直す()
    {
        string poll = CodeOnly(MethodBody(ReadModSource("CustomPlayerSkinMod.cs"), "private static bool ReloadChangedSkins()"));

        Assert.Matches(
            new Regex(@"if\s*\(SkinStore\.HasChangedSince\(PathFor\(pair\.Key\),\s*pair\.Value\.Timestamp\)\s*\|\|\s*\(pair\.Value\.FailedTimestamp\s*!=\s*0\s*&&\s*SkinStore\.GetTimestamp\(PathFor\(pair\.Key\)\)\s*==\s*0\)\)"),
            poll);
    }

    /// <summary>
    /// Another player's character is identified by PlayerGhost.playerGuid. The joining client fills
    /// it with its own SaveManager.GetCharacterGuid() - the save's characterGuid - and the game
    /// itself tells players apart by it. CharacterGuidCD, which was read before, is the identifier
    /// the game gives merchants and other NPCs (every writer of it is NPC code on 1.3.0.2), so with
    /// localPlayerOnly turned off no other player was ever matched. Turned back into
    /// UnityEngine.Hash128 before ToString, because Unity.Entities.Hash128 prints its digits in a
    /// different order, and the local player's identifier comes from UnityEngine.Hash128.
    /// </summary>
    [Fact]
    public void 他プレイヤーの識別子はPlayerGhostのplayerGuidから読む()
    {
        string body = CodeOnly(MethodBody(ReadModSource("CharacterIdentity.cs"), "private static string? RemoteGuid(PlayerController player)"));

        Assert.DoesNotContain("CharacterGuidCD", body, StringComparison.Ordinal);
        Assert.Contains("GetComponentData<PlayerGhost>(entity).playerGuid", body, StringComparison.Ordinal);
        Assert.Matches(@"UnityEngine\.Hash128\s+\w+\s*=\s*id;", body);
    }

    /// <summary>
    /// An entry whose character is still on screen is kept when it expires. LastSeen moves only when
    /// the game rebuilds a look, which a character standing in the same equipment never gets, so the
    /// one being played went "unseen" every five minutes while drawn the whole time: every player was
    /// rebuilt, the same image re-read and re-decoded (ten times in one real session), and a character
    /// with no image logged "no image" again each time.
    /// </summary>
    [Fact]
    public void 期限切れでも画面上のキャラクターの項目は残す()
    {
        string body = CodeOnly(MethodBody(ReadModSource("CustomPlayerSkinMod.cs"), "private static bool ReloadChangedSkins()"));

        Assert.Contains("FindObjectsByType<PlayerController>", body, StringComparison.Ordinal);
        Assert.Contains("CharacterIdentity.For(player)", body, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"onScreen\s*!=\s*null\s*&&\s*onScreen\.Contains\(guid\)\)\s*\{\s*entry\.LastSeen\s*=\s*now;\s*continue;\s*\}"),
            body);
    }

    /// <summary>
    /// With its settings file gone, the reload interval reads as 0 on 1.3.0.2: the game's get_Value
    /// returns default(T), not the registered default. That is what "Remove mod" leaves while the
    /// game runs, and 0 means "reloading is off", so the removed look stayed on screen until the game
    /// was restarted. Only a missing file falls back; a file saying 0 still switches reloading off.
    /// </summary>
    [Fact]
    public void 設定ファイルが無いときは既定の間隔で画像の確認を続ける()
    {
        string mod = CodeOnly(ReadModSource("CustomPlayerSkinMod.cs"));

        Assert.Contains("int interval = ReloadInterval();", MethodBody(mod, "public void Update()"), StringComparison.Ordinal);

        string interval = MethodBody(mod, "private int ReloadInterval()");
        Assert.Matches(
            new Regex(@"interval\s*<=\s*0\s*&&\s*SkinStore\.Exists\(ConfigValues\.ReloadIntervalFile\)\s*==\s*FileState\.Missing"),
            interval);
        Assert.Contains("return ConfigValues.DefaultReloadIntervalSeconds;", interval, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file looked for has to be the one the game writes, or the fallback would take over for
    /// everyone and a user's 0 would stop working. The game names each setting's file
    /// &lt;mod&gt;/&lt;section&gt;-&lt;key&gt;.json (General-hideArmor.json in the real folder), so the name is
    /// built from the same key and section the setting is registered with.
    /// </summary>
    [Fact]
    public void 再読み込み間隔の設定ファイル名は登録と同じ鍵から作る()
    {
        string config = CodeOnly(ReadModSource("ConfigValues.cs"));

        Assert.Matches(
            new Regex(@"ReloadIntervalFile\s*=\s*CustomPlayerSkinMod\.RootDirectory\s*\+\s*Section\s*\+\s*""-""\s*\+\s*ReloadIntervalKey\s*\+\s*""\.json"";"),
            config);
        Assert.Matches(new Regex(@"ReloadIntervalKey\s*,\s*DefaultReloadIntervalSeconds\s*\)"), config);
        Assert.Contains(@"const string ReloadIntervalKey = ""reloadIntervalSeconds"";", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// The body layer's colour table is made an identity once a picture is on it. The table maps the
    /// game's skin palette (#EBC3BB #D29A7C #B57A47 #915B26 on 1.3.0.2) onto the character's skin
    /// tone, and the shader applies it to the replacement texture as well, so pixels of a picture in
    /// those colours came out in the skin tone. A fetched look always has them - the game's own hair
    /// and clothes use them - and 814 to 2335 of its pixels changed colour for three of the four
    /// skin tones. Set after the picture, since the game sets the real table in the same rebuild.
    /// </summary>
    [Fact]
    public void 絵を当てたら体の層の色置換を恒等にする()
    {
        string body = CodeOnly(MethodBody(ReadModSource("SkinApplier.cs"), "public static void Apply(PlayerController player)"));

        int picture = body.IndexOf("SetLayer(player.bodySkin, skin);", StringComparison.Ordinal);
        int table = body.IndexOf("player.skinColorReplacer.SetColorReplacement(IdentityColours, IdentityColours);", StringComparison.Ordinal);

        Assert.True(picture >= 0, "体の層に絵を当てる行が見つからない");
        Assert.True(table > picture, "絵を当てた後で、体の層の色置換を恒等にしていない");
    }

    /// <summary>
    /// A hide setting whose file is gone reads false on 1.3.0.2 rather than its registered true - what
    /// "Remove mod" leaves while the game runs - and a picture placed again in the same session was
    /// drawn with the game's hair, eyes and clothes over it. Each of the six is judged by its own file:
    /// a false one is believed only while that file is there. Judging all six by the reload setting's
    /// file instead also overrode the four the tool's gear toggle writes back, so "show gear" did
    /// nothing after that. The poll compares the same values, or a toggle would not be noticed.
    /// </summary>
    [Fact]
    public void 隠す設定は項目ごとに自分のファイルが無ければ既定値で扱う()
    {
        string[] layers = ["Hair", "Eyes", "Shirt", "Pants", "Helm", "Armor"];

        string config = CodeOnly(ReadModSource("ConfigValues.cs"));
        Assert.Matches(
            new Regex(@"static bool Hidden\(IConfigEntry<bool> entry, string key\)\s*=>\s*entry\.Value\s*\|\|\s*SkinStore\.Exists\(CustomPlayerSkinMod\.RootDirectory\s*\+\s*Section\s*\+\s*""-""\s*\+\s*key\s*\+\s*""\.json""\)\s*==\s*FileState\.Missing;"),
            config);

        foreach (string layer in layers)
        {
            // Registered and judged under one key, so the file looked for is the file the game writes
            Assert.Matches(new Regex($@"const string Hide{layer}Key = ""hide{layer}"";"), config);
            Assert.Matches(new Regex($@"Hide{layer}Key,\s*true\)"), config);
            Assert.Matches(new Regex($@"public bool {layer}Hidden\s*=>\s*Hidden\(Hide{layer},\s*Hide{layer}Key\);"), config);
        }

        string apply = CodeOnly(MethodBody(ReadModSource("SkinApplier.cs"), "public static void Apply(PlayerController player)"));
        string poll = CodeOnly(MethodBody(ReadModSource("CustomPlayerSkinMod.cs"), "private bool HaveSettingsChanged()"));

        foreach (string body in new[] { apply, poll })
        {
            Assert.DoesNotMatch(new Regex(@"\.Hide(Hair|Eyes|Shirt|Pants|Helm|Armor)\.Value"), body);
            foreach (string layer in layers)
            {
                Assert.Contains($"{layer}Hidden", body, StringComparison.Ordinal);
            }
        }

        // Not for the capture setting: read as registered, it would have the running game make the
        // removed folder again
        Assert.Contains("CustomPlayerSkinMod.Config.CaptureAppearance.Value", CodeOnly(ReadModSource("SkinCapture.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A settings file the game cannot parse costs only that one setting. The game's Register reads
    /// an existing file and parses it with no handler (IL of 1.3.0.3-2aca), and the loader catches
    /// what EarlyInit throws but keeps the mod: one General-*.json saved with a BOM (PowerShell 5.1's
    /// Set-Content -Encoding UTF8) or a trailing comma left the whole settings object unset, so no
    /// picture was ever applied and nothing was captured, on every start, while the log still said
    /// "読み込み完了" and the tool reported no problem (the test campaign of 2026-09-30).
    /// </summary>
    [Fact]
    public void 設定の登録は1件ずつ失敗を受け止めて既定値で続ける()
    {
        string config = CodeOnly(ReadModSource("ConfigValues.cs"));

        // The game's Register is called in one place only, inside the guarded helper
        Assert.Single(Regex.Matches(config, @"API\.Config\.Register\("));

        string helper = MethodBody(config, "private static IConfigEntry<T> RegisterOrDefault<T>(");
        Assert.Contains("API.Config.Register(", helper, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"catch\s*\(\s*(System\.)?Exception\s+\w+\s*\)"), helper);
        Assert.Contains("CustomPlayerSkinMod.LogWarning(", helper, StringComparison.Ordinal);
        Assert.Contains("new FixedEntry<T>(defaultValue)", helper, StringComparison.Ordinal);

        // All nine settings go through it
        Assert.Equal(9, Regex.Matches(MethodBody(config, "public static ConfigValues Register()"), @"RegisterOrDefault\(").Count);
    }

    /// <summary>
    /// A captured look that cannot be written is not tried again for as long as a settled one waits.
    /// A refused write (a read-only file) is refused again five seconds later, and nothing moved the
    /// next attempt on after a failure: every five or six seconds the layers were read back from the
    /// GPU, encoded and written again, with a warning each time - some 720 lines an hour.
    /// </summary>
    [Fact]
    public void 取り込みの書き込みに失敗したら落ち着いたときと同じ間隔を空ける()
    {
        string body = CodeOnly(MethodBody(ReadModSource("SkinCapture.cs"), "private static void CaptureNow(PlayerController player, string guid)"));

        foreach (string failure in new[] { "if (!WriteManifest())", "if (!SkinStore.Write(CapturedDirectory + guid + \".png\", png))" })
        {
            int at = body.IndexOf(failure, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{failure} が見つからない");

            int end = body.IndexOf("return;", at, StringComparison.Ordinal);
            Assert.True(end > at, $"{failure} の後の return が見つからない");

            Assert.Contains("NextAttempt[guid] = Time.unscaledTime + SettledIntervalSeconds;", body[at..end], StringComparison.Ordinal);
        }
    }
}

using System.Text;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Install;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for reading the game's characters and for giving each of them their own image.
///
/// The real game folder is never touched. Save files are written by hand into a temporary
/// folder, in the same shape the game writes them, including the parts of that shape that are
/// awkward: the byte-per-field name encoding, two customization formats, and a tail of the file
/// that is not valid JSON at all.
/// </summary>
[Collection(ChildProcessCollection.Name)]
public sealed class CharacterTests : IDisposable
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"cks-character-test-{Guid.NewGuid():N}");

    public CharacterTests()
    {
        Directory.CreateDirectory(SavesDirectory);
        Directory.CreateDirectory(ModsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    /// <summary>Stands in for one platform-and-user folder of the game's data.</summary>
    private string UserDirectory => Path.Combine(_workDirectory, "Steam", "1");

    private string SavesDirectory => Path.Combine(UserDirectory, "saves");

    private string ModsDirectory => Path.Combine(UserDirectory, "mods");

    private ModConfigLocation Location => new(ModsDirectory, "Test", "1");

    /// <summary>
    /// Writes a save file the way the game does.
    /// </summary>
    /// <param name="newFormat">
    /// Which customization block carries the name. The game moved the name from one to the other,
    /// and both still occur across a player's characters.
    /// </param>
    /// <param name="badTail">
    /// Appends a property holding a bare <c>Infinity</c>, which the game really does write and
    /// which no JSON parser accepts. A character with one must still be listed.
    /// </param>
    private void WriteCharacter(int slot, string guid, string name, bool newFormat = true, bool badTail = false)
    {
        string block = $"{{\"name\":{NameJson(name)},\"gender\":1,\"skinColor\":1}}";

        StringBuilder json = new();
        json.Append("{\"version\":11,");
        json.Append($"\"characterGuid\":\"{guid}\",");
        json.Append($"\"characterCustomization\":{(newFormat ? $"{{\"name\":{NameJson(string.Empty)}}}" : block)},");
        json.Append($"\"characterCustomizationNew\":{(newFormat ? block : $"{{\"name\":{NameJson(string.Empty)}}}")},");
        json.Append("\"coinAmount\":0");

        if (badTail)
        {
            json.Append(",\"someFloat\":Infinity");
        }

        json.Append('}');

        File.WriteAllText(Path.Combine(SavesDirectory, $"{slot}.json"), json.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Unity's fixed-size string, serialised as one numbered field per byte.</summary>
    private static string NameJson(string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        StringBuilder json = new();

        json.Append($"{{\"utf8LengthInBytes\":{utf8.Length},\"bytes\":{{\"offset0000\":{{");
        for (int i = 0; i < 16; i++)
        {
            json.Append(i == 0 ? string.Empty : ",");
            json.Append($"\"byte{i:d4}\":{(i < utf8.Length ? utf8[i] : 0)}");
        }

        json.Append("}");

        // Past the first sixteen the fields sit beside the nested object rather than inside it
        for (int i = 16; i < 30; i++)
        {
            json.Append($",\"byte{i:d4}\":{(i < utf8.Length ? utf8[i] : 0)}");
        }

        json.Append("}}");
        return json.ToString();
    }

    private string CreateValidSheet() => CreateValidSheet(SKColors.Red, "sheet.png");

    private string CreateValidSheet(SKColor colour, string fileName)
    {
        using SKBitmap sprite = PixelOps.CreateEmpty(6, 10);
        SKColor[] pixels = sprite.Pixels;
        Array.Fill(pixels, colour);
        sprite.Pixels = pixels;

        ComposeResult composed = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using (composed.Sheet)
        {
            string path = Path.Combine(_workDirectory, fileName);
            PixelOps.EncodePng(composed.Sheet, path);
            return path;
        }
    }

    /// <summary>
    /// The only folder name the mod reads. It is a compile-time constant on the mod side, so
    /// images filed under any other name are never looked at.
    /// </summary>
    private static string ModFolder => SheetInstaller.DefaultModFolderName;

    private const string GuidA = "3825ebc1f472f28e5b9e984c425e01d5";
    private const string GuidB = "41e7dabc86c6294dd77ce4221a453eb6";
    private const string GuidC = "2dd37bbd1ee73adaff410301961b5574";

    // ------------------------------------------------------------ Reading characters

    [Fact]
    public void Find_スロット順に読み出す()
    {
        WriteCharacter(0, GuidA, "いちばん");
        WriteCharacter(2, GuidC, "さんばん");
        WriteCharacter(1, GuidB, "にばん");

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(Location);

        Assert.Equal([0, 1, 2], characters.Select(c => c.SlotIndex));
        Assert.Equal([GuidA, GuidB, GuidC], characters.Select(c => c.Guid));
    }

    [Fact]
    public void Find_新旧どちらの形式でも名前を読める()
    {
        WriteCharacter(0, GuidA, "旧形式", newFormat: false);
        WriteCharacter(1, GuidB, "新形式", newFormat: true);

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(Location);

        Assert.Equal("旧形式", characters[0].Name);
        Assert.Equal("新形式", characters[1].Name);
    }

    /// <summary>
    /// The game writes a bare Infinity into older saves, which is not JSON. Parsing the whole
    /// file therefore fails, and a real character silently vanished from the list because of it.
    /// </summary>
    [Fact]
    public void Find_末尾がJSONとして壊れていても読める()
    {
        WriteCharacter(0, GuidA, "壊れた末尾", newFormat: false, badTail: true);

        GameCharacter character = Assert.Single(CharacterLocator.Find(Location));

        Assert.Equal(GuidA, character.Guid);
        Assert.Equal("壊れた末尾", character.Name);
    }

    /// <summary>
    /// The two customization blocks need not sit next to each other, and the one written first
    /// is exactly the one the game leaves empty. Stopping at the first of them lost the name.
    /// </summary>
    [Fact]
    public void Find_2つの設定ブロックが離れていても名前を読める()
    {
        string block = $"{{\"name\":{NameJson("はなれた")},\"gender\":1}}";
        string empty = $"{{\"name\":{NameJson(string.Empty)}}}";

        File.WriteAllText(
            Path.Combine(SavesDirectory, "0.json"),
            "{\"version\":11," +
            $"\"characterGuid\":\"{GuidA}\"," +
            $"\"characterCustomization\":{empty}," +
            "\"coinAmount\":0,\"maxHealth\":100," +
            $"\"characterCustomizationNew\":{block}}}",
            new UTF8Encoding(false));

        Assert.Equal("はなれた", Assert.Single(CharacterLocator.Find(Location)).Name);
    }

    /// <summary>
    /// The bare Infinity the game writes can sit anywhere, not only at the end. Hitting it must
    /// cost the name at worst, never the whole character.
    /// </summary>
    [Fact]
    public void Find_前方に不正なリテラルがあってもキャラクターは残る()
    {
        File.WriteAllText(
            Path.Combine(SavesDirectory, "0.json"),
            "{\"version\":11," +
            $"\"characterGuid\":\"{GuidA}\"," +
            "\"lastPosition\":{\"x\":Infinity,\"y\":0}," +
            $"\"characterCustomizationNew\":{{\"name\":{NameJson("とどかない")}}}}}",
            new UTF8Encoding(false));

        GameCharacter character = Assert.Single(CharacterLocator.Find(Location));

        Assert.Equal(GuidA, character.Guid);
    }

    /// <summary>
    /// The declared length becomes an allocation size, so a corrupt file could ask for gigabytes.
    /// The resulting failure also escaped the per-file catch and killed the whole scan.
    /// </summary>
    [Fact]
    public void Find_名前の長さが異常でも他のキャラクターを巻き込まない()
    {
        File.WriteAllText(
            Path.Combine(SavesDirectory, "0.json"),
            "{\"version\":11," +
            $"\"characterGuid\":\"{GuidA}\"," +
            "\"characterCustomizationNew\":{\"name\":{\"utf8LengthInBytes\":2000000000," +
            "\"bytes\":{\"offset0000\":{\"byte0000\":65}}}}}",
            new UTF8Encoding(false));

        WriteCharacter(1, GuidB, "無事なほう");

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(Location);

        Assert.Equal(2, characters.Count);
        Assert.Equal(string.Empty, characters[0].Name);
        Assert.Equal("無事なほう", characters[1].Name);
    }

    [Fact]
    public void Find_同名でも識別子で区別される()
    {
        WriteCharacter(0, GuidA, "同じ名前");
        WriteCharacter(1, GuidB, "同じ名前");

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(Location);

        Assert.Equal(2, characters.Count);
        Assert.Equal(characters[0].Name, characters[1].Name);
        Assert.NotEqual(characters[0].Guid, characters[1].Guid);
    }

    [Fact]
    public void Find_バックアップや無関係なファイルは読まない()
    {
        WriteCharacter(0, GuidA, "本物");
        File.Copy(
            Path.Combine(SavesDirectory, "0.json"),
            Path.Combine(SavesDirectory, "0.json.pugbackup"));
        File.WriteAllText(Path.Combine(SavesDirectory, "notes.json"), "{}");

        Assert.Single(CharacterLocator.Find(Location));
    }

    [Fact]
    public void Find_名前が空でもキャラクターとして扱う()
    {
        WriteCharacter(0, GuidA, string.Empty);

        GameCharacter character = Assert.Single(CharacterLocator.Find(Location));

        Assert.Equal(string.Empty, character.Name);
        Assert.Contains("#1", character.Describe("(名前なし)"));
    }

    [Fact]
    public void Find_セーブフォルダが無ければ空になる()
    {
        Directory.Delete(SavesDirectory, recursive: true);

        Assert.Empty(CharacterLocator.Find(Location));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3825ebc1")]
    [InlineData("3825ebc1f472f28e5b9e984c425e01d")]
    [InlineData("3825ebc1f472f28e5b9e984c425e01d5a")]
    [InlineData("3825ebc1f472f28e5b9e984c425e01g5")]
    [InlineData("../../../etc/passwd")]
    public void IsValidGuid_想定外の識別子を弾く(string? guid)
    {
        Assert.False(CharacterLocator.IsValidGuid(guid));
    }

    // ------------------------------------------------------------ Per-character images

    /// <summary>The folder the mod reads each character's picture from.</summary>
    private string SkinsDirectory => Path.Combine(ModsDirectory, ModFolder, "skins");

    /// <summary>A staging file of the shape an interrupted run leaves behind.</summary>
    private string WriteAbandonedStaging(string guid, int owner)
    {
        Directory.CreateDirectory(SkinsDirectory);
        string path = Path.Combine(SkinsDirectory, $"{guid}.png.{owner}.new");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    /// <summary>
    /// A process id that is certainly not in use: one belonging to a process that has just ended.
    ///
    /// Made rather than invented. Measured: id 0 comes back as the idle process and id 4 as the
    /// system one, so neither stands in for a run that is gone, and an id picked out of the air
    /// would be a guess about what the machine happens to be running.
    /// </summary>
    private static int FinishedProcessId()
    {
        using System.Diagnostics.Process finished = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;

        finished.WaitForExit();
        return finished.Id;
    }

    [Fact]
    public void Install_死んだ実行が残した作業ファイルを片付ける()
    {
        // Ctrl+C does not unwind, so the copy made beside a character and not yet swapped in stays
        // there: a full-sized file the mod never reads, that install --list never shows because it
        // only looks at *.png, and that nothing cleared short of removing the settings folder. The
        // delete on the way in only clears the name this process would use, and the name carries
        // the process id.
        if (!OperatingSystem.IsWindows())
        {
            // The stand-in for a finished process is started with cmd.exe
            return;
        }

        string abandoned = WriteAbandonedStaging(GuidB, FinishedProcessId());
        string sheet = CreateValidSheet();

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA]);

        Assert.False(File.Exists(abandoned), "死んだ実行の作業ファイルが残っている");
    }

    [Fact]
    public void Install_動いている実行の作業ファイルには手を出さない()
    {
        // Two copies of the tool may install at the same time - that is what the process id in the
        // name is for - so a staging file whose process is still alive belongs to a run that is
        // about to move it. Deleting it would make that run fail and report a partial install it
        // never had. This process is the live one here.
        string live = WriteAbandonedStaging(GuidB, Environment.ProcessId);
        string sheet = CreateValidSheet();

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA]);

        Assert.True(File.Exists(live), "動いている実行の作業ファイルが消された");
    }

    [Fact]
    public void Install_開けないプロセスの番号を持つ作業ファイルがあっても導入できる()
    {
        // A staging file outlives its run, and its id can later be handed to a process this user
        // cannot open - a service, an elevated program. Asked whether it has exited, such a
        // process does not answer: HasExited throws Win32Exception (access denied). That was not
        // among the exceptions the sweep caught, so it left the sweep and took the whole install
        // with it, whichever characters were chosen, until the id was freed - for a service, not
        // before a reboot - and all the user was told was that access was denied.
        //
        // Id 4 is the system process, as FinishedProcessId's note records: running, and closed
        // to an ordinary user.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string unreadable = WriteAbandonedStaging(GuidB, 4);
        string sheet = CreateValidSheet();

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA]);

        // Counted as still running, which is the way the sweep leans when it cannot tell: the file
        // is left for a later sweep, and the install goes ahead.
        Assert.True(File.Exists(unreadable), "開けないプロセスの作業ファイルが消された");
        Assert.True(File.Exists(Path.Combine(SkinsDirectory, $"{GuidA}.png")), "導入が行われていない");
    }

    [Fact]
    public void Install_中断すると1体も入れ替えずに作業ファイルも残さない()
    {
        // The token is read while the copies are made and not afterwards, so an interrupted run
        // leaves every character with the picture it had. What it must not leave is the copy.
        string sheet = CreateValidSheet();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => CharacterSkins.Install(
            sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB], cancelled.Token));

        Assert.Empty(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder));
        Assert.Empty(Directory.EnumerateFiles(SkinsDirectory));
    }

    [Fact]
    public void Install_選んだキャラクターにだけ配置される()
    {
        string sheet = CreateValidSheet();

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidC]);

        IReadOnlySet<string> installed = CharacterSkins.InstalledGuids(ModsDirectory, ModFolder);

        Assert.True(installed.SetEquals([GuidA, GuidC]));
        Assert.True(File.Exists(Path.Combine(ModsDirectory, ModFolder, "skins", GuidA + ".png")));
        Assert.False(File.Exists(Path.Combine(ModsDirectory, ModFolder, "skins", GuidB + ".png")));
    }

    [Fact]
    public void Install_キャラクターごとに別の画像を持てる()
    {
        // Two different pictures, not the same one twice. Installing one sheet to both slots
        // only showed that two files exist; it said nothing about each character keeping its
        // own picture, so an implementation that pointed every character at the last sheet
        // installed would have passed.
        string red = CreateValidSheet(SKColors.Red, "red.png");
        string blue = CreateValidSheet(SKColors.Blue, "blue.png");

        CharacterSkins.Install(red, Layout, ModsDirectory, ModFolder, [GuidA]);
        CharacterSkins.Install(blue, Layout, ModsDirectory, ModFolder, [GuidB]);

        // Both exist independently: applying to one must never disturb the other
        Assert.True(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder).SetEquals([GuidA, GuidB]));

        // And each holds the picture it was given, byte for byte
        Assert.Equal(
            File.ReadAllBytes(red),
            File.ReadAllBytes(CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidA)));
        Assert.Equal(
            File.ReadAllBytes(blue),
            File.ReadAllBytes(CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidB)));

        Assert.NotEqual(File.ReadAllBytes(red), File.ReadAllBytes(blue));
    }

    [Fact]
    public void Install_同じ更新日時の別の画像に差し替えても更新として見える()
    {
        // The mod reloads a skin when the file's last-write time moves. File.Copy carries the
        // source's time over, so two pictures with the same time - a folder of skins unpacked
        // from one zip, all stamped with the commit time - replaced each other on disk while the
        // running game kept showing the first, with the tool saying it would appear in seconds.
        string knight = CreateValidSheet(SKColors.Red, "knight.png");
        string wizard = CreateValidSheet(SKColors.Blue, "wizard.png");

        DateTime shared = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(knight, shared);
        File.SetLastWriteTimeUtc(wizard, shared);

        string placed = CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidA);

        CharacterSkins.Install(knight, Layout, ModsDirectory, ModFolder, [GuidA]);
        DateTime first = File.GetLastWriteTimeUtc(placed);

        CharacterSkins.Install(wizard, Layout, ModsDirectory, ModFolder, [GuidA]);
        DateTime second = File.GetLastWriteTimeUtc(placed);

        Assert.Equal(File.ReadAllBytes(wizard), File.ReadAllBytes(placed));
        Assert.NotEqual(shared, first);
        Assert.True(second >= first, "差し替え後の更新日時が前の配置より古い");
        Assert.NotEqual(shared, second);
    }

    [Fact]
    public void Install_差し替えに失敗しても元の画像は残る()
    {
        // The replacement used to be made by deleting the destination and then moving the new
        // file over it. Everything that can make the move fail - the running game holding the
        // picture open, a backup product marking it read-only - happens inside exactly the
        // window that opens between those two steps, and the character was then left with no
        // picture at all rather than the one it had.
        string red = CreateValidSheet(SKColors.Red, "red.png");
        string blue = CreateValidSheet(SKColors.Blue, "blue.png");

        CharacterSkins.Install(red, Layout, ModsDirectory, ModFolder, [GuidA]);

        string destination = CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidA);
        byte[] before = File.ReadAllBytes(destination);

        // Held the way the running game holds it
        using (FileStream held = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<ToolException>(
                () => CharacterSkins.Install(blue, Layout, ModsDirectory, ModFolder, [GuidA]));
        }

        Assert.True(File.Exists(destination), "差し替えに失敗して元の画像が消えている");
        Assert.Equal(before, File.ReadAllBytes(destination));
    }

    [Fact]
    public void Install_退避ファイルの名前はプロセスごとに違う()
    {
        // A fixed staging name is one name shared by every copy of the program running at once.
        // Two of them installing to the same character meant the second deleted the first's
        // staged copy from under it, and with the destination already gone the character was
        // left with nothing. The name has to be this process's alone.
        string sheet = CreateValidSheet();
        string destination = CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidA);
        string skins = Path.GetDirectoryName(destination)!;

        Directory.CreateDirectory(skins);

        // A leftover from another process, which this run must not touch
        // Named the way the old fixed scheme named every process's staging file. That is the
        // file another copy of the program would be holding, and deleting it is what left the
        // character with nothing.
        string foreign = destination + ".new";
        File.WriteAllText(foreign, "別プロセスの退避ファイル");

        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA]);

        Assert.True(File.Exists(foreign), "他プロセスの退避ファイルを消している");
        Assert.Equal("別プロセスの退避ファイル", File.ReadAllText(foreign));

        // And this run leaves none of its own behind
        Assert.Empty(Directory.GetFiles(skins, $"*.{Environment.ProcessId}.new"));
    }

    [Fact]
    public void Remove_選んだキャラクターの画像だけ消える()
    {
        string sheet = CreateValidSheet();
        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB, GuidC]);

        IReadOnlyList<CharacterSkinResult> results =
            CharacterSkins.Remove(ModsDirectory, ModFolder, [GuidB]);

        Assert.True(Assert.Single(results).Changed);
        Assert.True(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder).SetEquals([GuidA, GuidC]));
    }

    [Fact]
    public void Remove_元から無いキャラクターは変更なしとして扱う()
    {
        IReadOnlyList<CharacterSkinResult> results =
            CharacterSkins.Remove(ModsDirectory, ModFolder, [GuidA]);

        Assert.False(Assert.Single(results).Changed);
    }

    [Fact]
    public void Install_壊れたシートは配置しない()
    {
        using SKBitmap wrong = PixelOps.CreateEmpty(10, 10);
        string path = Path.Combine(_workDirectory, "wrong.png");
        PixelOps.EncodePng(wrong, path);

        Assert.Throws<ToolException>(
            () => CharacterSkins.Install(path, Layout, ModsDirectory, ModFolder, [GuidA]));

        Assert.Empty(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder));
    }

    [Fact]
    public void Install_キャラクターを選ばなければ拒否する()
    {
        string sheet = CreateValidSheet();

        Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, []));
    }

    /// <summary>
    /// The identifier reaches a file path, so a value that is not one must be refused outright
    /// rather than resolved into somewhere else on disk.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../../evil")]
    [InlineData(@"..\..\evil")]
    [InlineData("C:/windows/system32/evil")]
    [InlineData("")]
    public void PathFor_識別子として不正なものを拒否する(string guid)
    {
        Assert.Throws<ToolException>(() => CharacterSkins.PathFor(ModsDirectory, ModFolder, guid));
    }

    [Fact]
    public void Install_識別子が不正なら1件も書き込まない()
    {
        string sheet = CreateValidSheet();

        Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, ".."]));

        // The valid entry came first, so writing as it went would have left a half-applied state
        Assert.Empty(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder));
    }

    [Fact]
    public void OrphanedGuids_ゲームから消えたキャラクターの画像を見つける()
    {
        string sheet = CreateValidSheet();
        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB]);

        WriteCharacter(0, GuidA, "残っている");

        IReadOnlyList<GameCharacter> alive = CharacterLocator.Find(Location);

        Assert.Equal([GuidB], CharacterSkins.OrphanedGuids(ModsDirectory, ModFolder, alive));
    }

    /// <summary>
    /// The mod reads a fixed folder, so filing images anywhere else is a silent no-op. Accepting
    /// the name made the tool report those characters as applied while the game showed the
    /// original appearance.
    /// </summary>
    [Fact]
    public void MODが読まないフォルダ名は拒否する()
    {
        string sheet = CreateValidSheet();

        Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, "SomethingElse", [GuidA]));
        Assert.Throws<ToolException>(
            () => CharacterSkins.DirectoryFor(ModsDirectory, "SomethingElse"));

        // Nor may it claim anything is installed there
        Assert.Empty(CharacterSkins.InstalledGuids(ModsDirectory, "SomethingElse"));
    }

    /// <summary>
    /// Writing straight to each destination in turn left the earlier characters replaced when a
    /// later one failed, while the caller was told only that the whole operation failed.
    /// </summary>
    [Fact]
    public void Install_途中で失敗しても既存の画像を書き換えない()
    {
        string sheet = CreateValidSheet();
        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA]);

        string first = CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidA);
        byte[] before = File.ReadAllBytes(first);

        // A directory where the second image should go: the swap for it cannot succeed
        Directory.CreateDirectory(CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidB));

        Assert.ThrowsAny<Exception>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB]));

        Assert.Equal(before, File.ReadAllBytes(first));
    }

    /// <summary>
    /// The characters already swapped in really have changed, as the comment at the throw says,
    /// but the command line was told only "2 of 4" - never which two, nor which file stopped it.
    /// The identifiers are what install --list prints beside each slot and name.
    /// </summary>
    [Fact]
    public void Install_途中で失敗したら適用済みの識別子と失敗したファイルを文面に含める()
    {
        string sheet = CreateValidSheet();
        string blocked = CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidB);

        // A folder where B's picture should go: A is swapped in, then B fails
        Directory.CreateDirectory(blocked);

        ToolException error = Assert.Throws<ToolException>(
            () => CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB, GuidC]));

        Assert.Equal("error.character.partial", error.MessageKey);
        Assert.Equal([1, 3], error.MessageArguments.Cast<int>());
        Assert.Contains(GuidA, error.Message, StringComparison.Ordinal);
        Assert.Contains(blocked, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(GuidC, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_識別子が不正なら1件も消さない()
    {
        string sheet = CreateValidSheet();
        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB]);

        Assert.Throws<ToolException>(
            () => CharacterSkins.Remove(ModsDirectory, ModFolder, [GuidA, ".."]));

        // The valid entry came first, so deleting as it went would already have removed it
        Assert.True(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder).SetEquals([GuidA, GuidB]));
    }

    [Fact]
    public void InstalledGuids_フォルダが無くても空を返す()
    {
        Assert.Empty(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder));
    }

    /// <summary>
    /// The game allows 30 normal characters, 30 creative ones and a debug slot, so nothing here
    /// may assume a handful. Anything that capped the list would silently hide characters.
    /// </summary>
    [Fact]
    public void Find_ゲームが許す61スロットすべてを読める()
    {
        for (int slot = 0; slot < 61; slot++)
        {
            WriteCharacter(slot, $"{slot:x32}", $"キャラ{slot}");
        }

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(Location);

        Assert.Equal(61, characters.Count);
        Assert.Equal(Enumerable.Range(0, 61), characters.Select(c => c.SlotIndex));
    }

    [Fact]
    public void Find_クリエイティブのキャラクターは番号を1から数え直す()
    {
        WriteCharacter(0, GuidA, "通常");
        WriteCharacter(30, GuidB, "クリエイティブ");

        IReadOnlyList<GameCharacter> characters = CharacterLocator.Find(Location);

        Assert.False(characters[0].IsCreative);
        Assert.Equal(1, characters[0].DisplayNumber);

        // Slot 30 is the first creative slot, so it is shown as 1 rather than 31
        Assert.True(characters[1].IsCreative);
        Assert.Equal(1, characters[1].DisplayNumber);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void IsCreative_ゲームと同じ範囲で判定する(int slot, bool expected)
    {
        GameCharacter character = new(slot, GuidA, "x", DateTime.UtcNow);

        Assert.Equal(expected, character.IsCreative);
    }

    [Fact]
    public void Fingerprint_キャラクターの増減で変わる()
    {
        string[] folders = [SavesDirectory];
        string before = CharacterLocator.Fingerprint(folders);

        WriteCharacter(0, GuidA, "追加された");

        // The fingerprint is what decides whether the window rebuilds its list, so a character
        // appearing while the application is open has to change it.
        Assert.NotEqual(before, CharacterLocator.Fingerprint(folders));
    }

    [Fact]
    public void SavesDirectoryFor_MODフォルダの隣を指す()
    {
        Assert.Equal(SavesDirectory, CharacterLocator.SavesDirectoryFor(Location));
    }

    [Fact]
    public void Install_同じ識別子を2件渡しても失敗しない()
    {
        // Two save slots can carry the same character identifier - copying a save file to
        // duplicate a character is all it takes - and the locator does not merge them, so the
        // same identifier reaches Install twice. Staging every file and then swapping them in
        // meant the second move went looking for a staged file the first had already moved,
        // and the whole install died with a FileNotFoundException naming a .png.new path after
        // some characters had already been replaced, with no copy of what they had before.
        string sheet = CreateValidSheet();

        IReadOnlyList<CharacterSkinResult> results =
            CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidA, GuidB]);

        Assert.Equal(2, results.Count);
        Assert.True(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder).SetEquals([GuidA, GuidB]));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(ModsDirectory, ModFolder, "skins"), "*.new"));
    }

    [Fact]
    public void Install_大文字と小文字だけ違う識別子も同じ宛先として扱う()
    {
        // Windows file names do not distinguish case, so these two land on one file. Treating
        // them as separate destinations produced the same collision as the duplicate above.
        string sheet = CreateValidSheet();

        IReadOnlyList<CharacterSkinResult> results = CharacterSkins.Install(
            sheet, Layout, ModsDirectory, ModFolder, [GuidA.ToLowerInvariant(), GuidA.ToUpperInvariant()]);

        Assert.Single(results);
    }

    [Fact]
    public void Remove_1件が消せなくても残りを消して結果を返す()
    {
        // The running game holds an image open while it reads it. Letting the IO failure out
        // ended the whole removal at the first bad file and threw away the results for every
        // character already restored: the caller was told nothing had happened when some of it
        // had, and the window had no way to say which characters still wear the replacement.
        string sheet = CreateValidSheet();
        CharacterSkins.Install(sheet, Layout, ModsDirectory, ModFolder, [GuidA, GuidB, GuidC]);

        string locked = CharacterSkins.PathFor(ModsDirectory, ModFolder, GuidB);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            IReadOnlyList<CharacterSkinResult> results =
                CharacterSkins.Remove(ModsDirectory, ModFolder, [GuidA, GuidB, GuidC]);

            Assert.Equal(3, results.Count);

            CharacterSkinResult failed = Assert.Single(results, r => r.Error is not null);
            Assert.Equal(GuidB, failed.Guid);
            Assert.False(failed.Changed);

            // The other two were still dealt with, and the caller can see that they were
            Assert.Equal(2, results.Count(r => r.Changed));
            Assert.True(CharacterSkins.InstalledGuids(ModsDirectory, ModFolder).SetEquals([GuidB]));
        }
    }
}

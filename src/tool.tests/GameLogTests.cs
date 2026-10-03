using System.Globalization;
using System.Text;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for reading the game's log to tell whether it loaded the mod.
///
/// The logs are written by <see cref="GameLogBuilder"/>, in the shape the game writes them. The
/// real player's log is never read: <see cref="TestEnvironment"/> points the lookup at a file that
/// does not exist, and the tests here hand their own path in.
/// </summary>
public sealed class GameLogTests : IDisposable
{
    private const string Mod = GameLogBuilder.ModName;

    private static readonly DateTime Started = new(2026, 9, 28, 1, 11, 55, DateTimeKind.Utc);

    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cks-gamelog-{Guid.NewGuid():N}");

    public GameLogTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the operating system clears the temp directory eventually
        }
    }

    /// <summary>A game folder named in the logs. Parsing never looks at it, so it need not exist.</summary>
    private const string Game = @"C:\Games\Core Keeper";

    private static string ModDirectory(string game) =>
        Path.Combine(game, "CoreKeeper_Data", "StreamingAssets", "Mods", Mod);

    private static GameLogRun Parse(GameLogBuilder log) => GameLog.Parse(log.Lines, Mod);

    // ------------------------------------------------------------ What a start says

    [Fact]
    public void 読み込めた起動からは失敗を読み取らない()
    {
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().Loaded());

        Assert.Null(run.Failure);
        Assert.Equal(Started, run.StartedUtc);
        Assert.Equal(DateTimeKind.Utc, run.StartedUtc!.Value.Kind);
        Assert.Equal(GameLogBuilder.Version, run.GameVersion);
        Assert.Equal(Path.Combine(Game, "CoreKeeper.exe"), run.GameExecutable);
        Assert.Equal("C:/Games/Core Keeper/CoreKeeper_Data/StreamingAssets/Mods\\" + Mod, run.ModDirectory);
    }

    [Fact]
    public void コード検査で拒否されたことと拒否された名前空間を読み取る()
    {
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().RefusedByCodeCheck());

        ModLoadFailure failure = Assert.IsType<ModLoadFailure>(run.Failure);
        Assert.Equal(ModLoadFailureKind.CodeSecurity, failure.Kind);
        Assert.Equal("CompileFailed", failure.Reason);
        Assert.Equal("System.Reflection", failure.Summary);
        Assert.Contains("Illegal reference to disallowed namespace: System.Reflection", failure.Details);

        // The places it was used are one tab-indented line each, and there can be hundreds
        Assert.DoesNotContain(failure.Details, d => d.Contains("Illegal usage", StringComparison.Ordinal));
    }

    [Fact]
    public void コンパイルできなかったことと最初のエラーを読み取る()
    {
        GameLogRun run = Parse(new GameLogBuilder()
            .Start(Started, Game)
            .Discover(Game)
            .Compile()
            .DoesNotCompile(Mod,
                ("SkinApplier.cs", 42, "CS0117", "'PlayerController' does not contain a definition for 'bodySkin'"),
                ("SkinApplier.cs", 57, "CS0117", "'PlayerController' does not contain a definition for 'hairSkin'")));

        ModLoadFailure failure = Assert.IsType<ModLoadFailure>(run.Failure);
        Assert.Equal(ModLoadFailureKind.Compile, failure.Kind);
        Assert.Equal("CompileFailed", failure.Reason);
        Assert.Equal("CS0117: 'PlayerController' does not contain a definition for 'bodySkin'", failure.Summary);

        // The loader's working copy sits under the user's profile; the file name is what says where
        Assert.Equal(
            [
                "SkinApplier.cs(42,17): error CS0117: 'PlayerController' does not contain a definition for 'bodySkin'",
                "SkinApplier.cs(57,17): error CS0117: 'PlayerController' does not contain a definition for 'hairSkin'",
            ],
            failure.Details);
    }

    [Fact]
    public void 理由だけのエラーはその理由を読み取る()
    {
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().LoadError("FileAccess"));

        ModLoadFailure failure = Assert.IsType<ModLoadFailure>(run.Failure);
        Assert.Equal(ModLoadFailureKind.LoadError, failure.Kind);
        Assert.Equal("FileAccess", failure.Reason);
        Assert.Equal("FileAccess", failure.Summary);
    }

    [Fact]
    public void 組み込みで例外が出たら読み込めていても失敗とする()
    {
        // The mod's own greeting still appears: it loaded, and only the hooks that make it do
        // anything are missing
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().PatchThrows());

        ModLoadFailure failure = Assert.IsType<ModLoadFailure>(run.Failure);
        Assert.Equal(ModLoadFailureKind.Patch, failure.Kind);
        Assert.Contains(failure.Details, d => d.StartsWith("HarmonyLib.HarmonyException", StringComparison.Ordinal));
    }

    [Fact]
    public void 保護された型への組み込みを断られたら失敗とする()
    {
        GameLogRun run = Parse(new GameLogBuilder()
            .Start(Started, Game).Discover(Game).Compile().PatchRefused("SaveManager"));

        ModLoadFailure failure = Assert.IsType<ModLoadFailure>(run.Failure);
        Assert.Equal(ModLoadFailureKind.Patch, failure.Kind);
        Assert.Contains("Trying to patch disallowed type SaveManager", failure.Details);
    }

    [Fact]
    public void アセットバンドルを読めなかったら失敗とする()
    {
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().BundleFails());

        Assert.Equal(ModLoadFailureKind.AssetBundle, run.Failure?.Kind);
    }

    [Fact]
    public void マニフェストを読めなかったら失敗とする()
    {
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).ManifestUnreadable(Game));

        Assert.Equal(ModLoadFailureKind.Manifest, run.Failure?.Kind);
        Assert.Equal("C:/Games/Core Keeper/CoreKeeper_Data/StreamingAssets/Mods\\" + Mod, run.ModDirectory);
    }

    [Fact]
    public void 他のMODの失敗は拾わない()
    {
        // The loader's "got null" names no mod, and the code check's list names no assembly: both
        // have to be placed by what came before them. Another mod failing either way, before and
        // after this one loads, must leave this one clean.
        GameLogBuilder log = new GameLogBuilder()
            .Start(Started, Game)
            .Discover(Game, "Other")
            .Discover(Game)
            .Compile("Other")
            .RefusedByCodeCheck("Other")
            .Compile()
            .Loaded()
            .Compile("Other")
            .DoesNotCompile("Other", ("Other.cs", 3, "CS0103", "The name 'x' does not exist in the current context"))
            .Compile("CustomPlayerSkinExtra")
            .RefusedByCodeCheck("CustomPlayerSkinExtra")
            .LoadError("InternalError", "Other")
            .BundleFails("Other");

        Assert.Null(Parse(log).Failure);
    }

    [Fact]
    public void 読み込み直しでは最後の結果をとる()
    {
        // The loader compiles every mod again whenever the set of mods changes during a session
        GameLogRun failedThenLoaded = Parse(new GameLogBuilder()
            .Start(Started, Game).Discover(Game).Compile().RefusedByCodeCheck().Compile().Loaded());

        GameLogRun loadedThenFailed = Parse(new GameLogBuilder()
            .Start(Started, Game).Discover(Game).Compile().Loaded().Compile().RefusedByCodeCheck());

        Assert.Null(failedThenLoaded.Failure);
        Assert.Equal(ModLoadFailureKind.CodeSecurity, loadedThenFailed.Failure?.Kind);
    }

    [Fact]
    public void まだ読み込み中の起動は失敗としない()
    {
        GameLogRun run = Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile());

        Assert.Null(run.Failure);
    }

    [Fact]
    public void 見出しの無いログは起動時刻を持たない()
    {
        GameLogRun run = GameLog.Parse(["loaded mod CustomPlayerSkin at C:/Games/Core Keeper/Mods\\CustomPlayerSkin"], Mod);

        Assert.Null(run.StartedUtc);
        Assert.Null(run.GameVersion);
        Assert.Null(run.GameExecutable);
    }

    // ------------------------------------------------------------ Whether it says anything about now

    private static GameLogRun FailedRun(string game) => GameLog.Parse(
        new GameLogBuilder().Start(Started, game).Discover(game).Compile().RefusedByCodeCheck().Lines, Mod);

    private static ModLoadVerdict Evaluate(
        GameLogRun? run, DateTime? modChangedUtc, string? gameVersion = GameLogBuilder.Version, string game = Game) =>
        GameLog.Evaluate(run, "Player.log", null, game, ModDirectory(game), modChangedUtc, gameVersion);

    [Fact]
    public void 今のMODとゲームで起きた失敗は失敗と答える()
    {
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddHours(-1));

        Assert.Equal(ModLoadState.Failed, verdict.State);
        Assert.Equal(ModLoadFailureKind.CodeSecurity, verdict.Failure?.Kind);
    }

    [Fact]
    public void 失敗の無い起動は問題なしと答える()
    {
        GameLogRun run = GameLog.Parse(new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().Loaded().Lines, Mod);

        ModLoadVerdict verdict = Evaluate(run, Started.AddHours(-1));

        Assert.Equal(ModLoadState.NoProblem, verdict.State);
        Assert.Null(verdict.Failure);
    }

    [Fact]
    public void 起動より後にMODを入れ直していたら今のことは言えない()
    {
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddMinutes(1));

        Assert.Equal(ModLoadState.Outdated, verdict.State);
        Assert.Null(verdict.Failure);
    }

    [Fact]
    public void ゲームの版が変わっていたら今のことは言えない()
    {
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddHours(-1), gameVersion: "1.3.0.3-1a2b");

        Assert.Equal(ModLoadState.Outdated, verdict.State);
    }

    [Theory]
    // The two readers - the log's header and the game's own files - gave the same string on every
    // build measured. One giving less of it must not make every start look like another build's,
    // which would hide every failure from then on.
    [InlineData("1.3.0.2")]
    [InlineData("1.3.0.2-182B")]
    [InlineData(" 1.3.0.2-182b ")]
    public void 版の書き方が違っても同じ版なら失敗を伝える(string gameVersion)
    {
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddHours(-1), gameVersion: gameVersion);

        Assert.Equal(ModLoadState.Failed, verdict.State);
    }

    [Theory]
    [InlineData("1.3.0.2-19cc")]
    [InlineData("1.3.1")]
    [InlineData("1.4.0.0-0000")]
    public void 番号か組み直しが違う版なら今のことは言えない(string gameVersion)
    {
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddHours(-1), gameVersion: gameVersion);

        Assert.Equal(ModLoadState.Outdated, verdict.State);
    }

    [Fact]
    public void ゲームの版が読めなくても失敗は伝える()
    {
        // The version only rules a run out when both sides are known; not knowing it now is no
        // reason to hide what the log says
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddHours(-1), gameVersion: null);

        Assert.Equal(ModLoadState.Failed, verdict.State);
    }

    [Fact]
    public void 別の場所のゲームの起動は今のゲームのことを言わない()
    {
        // Every copy of the game writes the same log
        ModLoadVerdict verdict = Evaluate(FailedRun(@"D:\Other Library\Core Keeper"), Started.AddHours(-1));

        Assert.Equal(ModLoadState.OtherInstallation, verdict.State);
    }

    [Fact]
    public void 同じ場所なら区切りや大文字小文字の違いは問わない()
    {
        // The loader writes forward slashes and backslashes in one path
        ModLoadVerdict verdict = Evaluate(FailedRun(Game), Started.AddHours(-1), game: @"c:\games\core keeper\");

        Assert.Equal(ModLoadState.Failed, verdict.State);
    }

    [Fact]
    public void MODの見つかった場所が違えば今のMODのことを言わない()
    {
        GameLogRun run = FailedRun(Game) with { ModDirectory = @"C:\Games\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods\Copy" };

        Assert.Equal(ModLoadState.OtherInstallation, Evaluate(run, Started.AddHours(-1)).State);
    }

    [Theory]
    // Unity writes args[0] as it was typed. Measured with a player of the game's engine version:
    // started from cmd or a batch file by its bare name it is "CoreKeeper.exe" (or "CoreKeeper"),
    // and ".\CoreKeeper.exe" when written so. Such a name says nothing about which folder it was,
    // and taken as one it ruled every such run out as another installation - a real failure of the
    // mod in it was never reported. The same run's "loaded mod ... at" line still names the folder.
    [InlineData("CoreKeeper.exe")]
    [InlineData(@".\CoreKeeper.exe")]
    [InlineData("CoreKeeper")]
    public void 相対の名前で起動したログでもMODの場所が今のゲームなら失敗を伝える(string executable)
    {
        GameLogRun run = FailedRun(Game) with { GameExecutable = executable };

        ModLoadVerdict verdict = Evaluate(run, Started.AddHours(-1));

        Assert.Equal(ModLoadState.Failed, verdict.State);
        Assert.Equal(ModLoadFailureKind.CodeSecurity, verdict.Failure?.Kind);
    }

    [Theory]
    [InlineData("CoreKeeper.exe")]
    [InlineData(@".\CoreKeeper.exe")]
    public void 相対の名前で起動したログでもMODの場所が別のゲームなら今のゲームのことを言わない(string executable)
    {
        // Another installation is still told apart by where the mod was found
        GameLogRun run = FailedRun(@"D:\Other Library\Core Keeper") with { GameExecutable = executable };

        Assert.Equal(ModLoadState.OtherInstallation, Evaluate(run, Started.AddHours(-1)).State);
    }

    [Fact]
    public void 起動時刻かMODの時刻が分からなければ判断しない()
    {
        GameLogRun undated = FailedRun(Game) with { StartedUtc = null };

        Assert.Equal(ModLoadState.Undetermined, Evaluate(undated, Started.AddHours(-1)).State);
        Assert.Equal(ModLoadState.Undetermined, Evaluate(FailedRun(Game), modChangedUtc: null).State);
    }

    [Fact]
    public void 読めなかったログはその理由とともに返す()
    {
        ModLoadVerdict verdict = GameLog.Evaluate(null, "Player.log", "denied", Game, ModDirectory(Game), Started, null);

        Assert.Equal(ModLoadState.NoLog, verdict.State);
        Assert.Equal("denied", verdict.Problem);
    }

    // ------------------------------------------------------------ Reading the file

    [Fact]
    public void ゲームが書き込み中のログも読める()
    {
        // The game keeps its log open for writing for as long as it runs, sharing it for reading
        // and for being moved aside when it next starts
        string path = Path.Combine(_work, "Player.log");
        new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().RefusedByCodeCheck().WriteTo(path);

        using FileStream writer = new(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes("still running\r\n"));
        writer.Flush();

        (GameLogRun? run, string? problem) = GameLog.Read(path, Mod);

        Assert.Null(problem);
        Assert.Equal(ModLoadFailureKind.CodeSecurity, run?.Failure?.Kind);

        // Reading took nothing from the writer: the game can still write and still move the file
        writer.Write(Encoding.UTF8.GetBytes("and still writing\r\n"));
        writer.Flush();
        File.Move(path, Path.Combine(_work, "Player-prev.log"));
    }

    [Fact]
    public void 無いログは何も返さない()
    {
        (GameLogRun? run, string? problem) = GameLog.Read(Path.Combine(_work, "Player.log"), Mod);

        Assert.Null(run);
        Assert.Null(problem);
    }

    [Fact]
    public void 改行がLFだけのログも読める()
    {
        string path = Path.Combine(_work, "Player.log");
        string[] lines = [.. new GameLogBuilder().Start(Started, Game).Discover(Game).Compile().RefusedByCodeCheck().Lines];
        File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(true));

        (GameLogRun? run, _) = GameLog.Read(path, Mod);

        Assert.Equal(Started, run?.StartedUtc);
        Assert.Equal(ModLoadFailureKind.CodeSecurity, run?.Failure?.Kind);
    }

    [Fact]
    public void 長いログは先頭の決まった量だけ読む()
    {
        // A failure only past the limit is not seen; the loader runs in the first seconds of a
        // start, and a long session is not read through each time the window comes back
        string path = Path.Combine(_work, "Player.log");
        StringBuilder text = new();
        foreach (string line in new GameLogBuilder().Start(Started, Game).Discover(Game).Lines)
        {
            text.Append(line).Append("\r\n");
        }

        string filler = new string('x', 1023) + "\n";
        while (text.Length < GameLog.MaxBytes + (256 * 1024))
        {
            text.Append(filler);
        }

        foreach (string line in new GameLogBuilder().Compile().RefusedByCodeCheck().Lines)
        {
            text.Append(line).Append("\r\n");
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));

        (GameLogRun? run, _) = GameLog.Read(path, Mod);

        Assert.Equal(Started, run?.StartedUtc);
        Assert.Null(run?.Failure);
    }

    [Fact]
    public void ログの場所を渡して判定する()
    {
        string game = Path.Combine(_work, "Core Keeper");
        string mod = ModDirectory(game);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "ModManifest.json"), $"{{\"name\":\"{Mod}\"}}");
        SetTimes(mod, Started.AddHours(-1));

        string log = Path.Combine(_work, "Player.log");
        new GameLogBuilder().Start(Started, game).Discover(game).Compile().RefusedByCodeCheck().WriteTo(log);

        ModLoadVerdict verdict = GameLog.CheckLastRun(game, new InstalledModInfo(mod, Mod), log);

        Assert.Equal(ModLoadState.Failed, verdict.State);
        Assert.Equal(log, verdict.LogPath);
        Assert.Equal(ModLoadState.NotInstalled, GameLog.CheckLastRun(game, new InstalledModInfo(null, null), log).State);

        // A manifest with no name, or one that could not be read, leaves the folder's name, which
        // the installer makes the same
        Assert.Equal(ModLoadState.Failed, GameLog.CheckLastRun(game, new InstalledModInfo(mod, null), log).State);
        Assert.Equal(ModLoadState.Failed, GameLog.CheckLastRun(game, new InstalledModInfo(mod, " "), log).State);
    }

    // ------------------------------------------------------------ The mod's own times

    [Fact]
    public void MODの時刻は作成と書き込みの遅い方をとる()
    {
        // Extracting from a zip sets a file's write time to the one stored in the archive, the
        // build time; its creation time is when it was written, which is the install
        string mod = Path.Combine(_work, "Mod");
        Directory.CreateDirectory(Path.Combine(mod, "Scripts"));
        string file = Path.Combine(mod, "Scripts", "Mod.cs");
        File.WriteAllText(file, "// mod");

        DateTime built = new(2026, 8, 15, 8, 7, 32, DateTimeKind.Utc);
        DateTime installed = new(2026, 9, 28, 0, 43, 28, DateTimeKind.Utc);

        SetTimes(mod, built);
        File.SetCreationTimeUtc(file, installed);

        Assert.Equal(installed, GameLog.LastChangedUtc(mod));
    }

    [Fact]
    public void 無いMODのフォルダの時刻は分からない()
    {
        Assert.Null(GameLog.LastChangedUtc(Path.Combine(_work, "missing")));
    }

    // ------------------------------------------------------------ Small helpers

    [Fact]
    public void 和暦の設定でも西暦で時刻を出す()
    {
        // A Windows set to the Japanese calendar formats "yyyy" as the year of the era: 2026 as 08
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo japanese = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone();
        japanese.DateTimeFormat.Calendar = new JapaneseCalendar();

        try
        {
            CultureInfo.CurrentCulture = japanese;

            string text = GameLog.DescribeTime(Started);

            Assert.Equal(Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), text);
            Assert.StartsWith("2026-", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ログが書き足されたら指紋が変わる()
    {
        string path = Path.Combine(_work, "Player.log");
        Assert.Equal(string.Empty, GameLog.Fingerprint(path));

        File.WriteAllText(path, "one\r\n");
        string first = GameLog.Fingerprint(path);
        File.AppendAllText(path, "two\r\n");

        Assert.NotEqual(string.Empty, first);
        Assert.NotEqual(first, GameLog.Fingerprint(path));
    }

    /// <summary>Gives a folder and everything in it one time, files first: creating files moves a folder's own time.</summary>
    private static void SetTimes(string directory, DateTime utc)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, utc);
            File.SetLastWriteTimeUtc(file, utc);
        }

        foreach (string folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Append(directory))
        {
            Directory.SetCreationTimeUtc(folder, utc);
            Directory.SetLastWriteTimeUtc(folder, utc);
        }
    }
}

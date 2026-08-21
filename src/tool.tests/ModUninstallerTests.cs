using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Removal tests.
///
/// Deleting the wrong thing cannot be undone, so the focus is on removing only what is safe.
/// The real game folder is never touched; the same structure is recreated in a temporary directory.
/// </summary>
public sealed class ModUninstallerTests : IDisposable
{
    private const string ModName = "CustomPlayerSkin";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cks-uninstall-test-{Guid.NewGuid():N}");

    public ModUninstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Creates a stand-in for the game-side mod folder.</summary>
    private string CreateGameInstall(string modFolderName = ModName, int files = 3)
    {
        string game = Path.Combine(_root, "game");
        string mod = Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods", modFolderName);
        Directory.CreateDirectory(Path.Combine(mod, "Scripts"));

        for (int i = 0; i < files; i++)
        {
            File.WriteAllText(Path.Combine(mod, $"file{i}.txt"), "x");
        }

        return game;
    }

    /// <summary>Creates a stand-in for the settings-side folder.</summary>
    private string CreateModConfig(string modFolderName = ModName, int files = 2)
    {
        string mods = Path.Combine(_root, "config", "mods");
        string folder = Path.Combine(mods, modFolderName);
        Directory.CreateDirectory(folder);

        for (int i = 0; i < files; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"setting{i}.json"), "{}");
        }

        return mods;
    }

    [Fact]
    public void Plan_実在するものだけを挙げる()
    {
        string game = CreateGameInstall();
        string mods = CreateModConfig();

        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);

        Assert.Equal(2, plan.Targets.Count);
        Assert.Contains(plan.Targets, t => t.Kind == RemovalKind.ModInstall);
        Assert.Contains(plan.Targets, t => t.Kind == RemovalKind.ModConfig);
        Assert.Equal(5, plan.TotalFiles);
    }

    [Fact]
    public void Plan_何も入っていなければ空になる()
    {
        RemovalPlan plan = ModUninstaller.Plan(ModName, [Path.Combine(_root, "nowhere")], []);

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.TotalFiles);
    }

    [Fact]
    public void Execute_対象をすべて削除する()
    {
        string game = CreateGameInstall();
        string mods = CreateModConfig();
        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);

        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Removed.Count);
        Assert.All(plan.Targets, t => Assert.False(Directory.Exists(t.Path)));
    }

    [Fact]
    public void Execute_設定側を先に消す()
    {
        // Deleting the image first lets a running game notice and revert the appearance
        string game = CreateGameInstall();
        string mods = CreateModConfig();
        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);

        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.StartsWith(Path.Combine(mods, ModName), result.Removed[0]);
    }

    [Fact]
    public void Execute_親フォルダが想定外なら削除しない()
    {
        // A folder of the same name elsewhere must survive even if it is put into the plan
        string stray = Path.Combine(_root, "SomewhereElse", ModName);
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "important.txt"), "keep me");

        RemovalPlan plan = new([new RemovalTarget(stray, RemovalKind.ModConfig, 1)]);
        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Empty(result.Removed);
        Assert.Single(result.Failures);
        Assert.True(Directory.Exists(stray), "想定外の場所のフォルダが削除された");
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"..\..")]
    public void Plan_相対セグメントのMOD名を拒否する(string modFolderName)
    {
        // Regression: these names used to satisfy both guards. Path.Combine(mods, "..") gives
        // "...\Mods\..", whose GetFileName is ".." (equal to the supplied name) and whose parent
        // segment is "Mods", so Directory.Delete removed StreamingAssets instead of one mod.
        string game = CreateGameInstall();
        string mods = CreateModConfig();

        Assert.Throws<ToolException>(() => ModUninstaller.Plan(modFolderName, [game], [mods]));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public void Execute_相対セグメントのMOD名では親フォルダを消さない(string modFolderName)
    {
        // Guards the blast radius directly: even if a caller hand-builds the plan,
        // nothing outside the mod folder may be touched.
        string mods = CreateModConfig();
        string otherMod = Path.Combine(mods, "SomeOtherMod");
        Directory.CreateDirectory(otherMod);
        File.WriteAllText(Path.Combine(otherMod, "important.txt"), "keep me");

        string target = Path.Combine(mods, modFolderName);
        RemovalPlan plan = new([new RemovalTarget(target, RemovalKind.ModConfig, 1)]);

        Assert.Throws<ToolException>(() => ModUninstaller.Execute(plan, modFolderName));

        Assert.True(Directory.Exists(mods), "mods フォルダごと削除された");
        Assert.True(Directory.Exists(otherMod), "他の MOD が巻き添えで削除された");
        Assert.True(File.Exists(Path.Combine(otherMod, "important.txt")), "他の MOD のファイルが削除された");
        Assert.True(Directory.Exists(Path.Combine(mods, ModName)), "対象の MOD フォルダが削除された");
    }

    [Fact]
    public void Plan_中断したインストールの残骸も削除対象に含める()
    {
        // ModPayload extracts into "<mod>.new" and moves the old copy to "<mod>.old" while
        // swapping. If an install is interrupted, those folders stay inside the game's Mods
        // folder, and removal has to reach them or the original state is not restored.
        string game = CreateGameInstall();
        string mods = Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods");
        Directory.CreateDirectory(Path.Combine(mods, ModName + ".new"));
        File.WriteAllText(Path.Combine(mods, ModName + ".new", "half.txt"), "x");
        Directory.CreateDirectory(Path.Combine(mods, ModName + ".old"));

        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], []);
        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(Path.Combine(mods, ModName)), "MOD フォルダが残っている");
        Assert.False(Directory.Exists(Path.Combine(mods, ModName + ".new")), "作業用フォルダが残っている");
        Assert.False(Directory.Exists(Path.Combine(mods, ModName + ".old")), "退避用フォルダが残っている");
    }

    [Fact]
    public void Execute_名前が似ているだけの他フォルダは削除しない()
    {
        // Only the exact mod name and its two working suffixes qualify
        string mods = CreateModConfig();
        foreach (string name in new[] { ModName + "2", ModName + ".bak", ModName + "-old" })
        {
            string folder = Path.Combine(mods, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep me");

            RemovalPlan plan = new([new RemovalTarget(folder, RemovalKind.ModConfig, 1)]);
            RemovalResult result = ModUninstaller.Execute(plan, ModName);

            Assert.Single(result.Failures);
            Assert.True(Directory.Exists(folder), $"名前が似ているだけの {name} が削除された");
        }
    }

    [Fact]
    public void RemovalPlan_数えられないフォルダは0件ではなく不明として集計される()
    {
        // A folder that cannot be enumerated must not be presented as "0 files"
        // immediately before a recursive delete.
        RemovalTarget unknown = new(Path.Combine(_root, "x"), RemovalKind.ModConfig, RemovalTarget.UnknownFileCount);
        RemovalTarget known = new(Path.Combine(_root, "y"), RemovalKind.ModConfig, 4);
        RemovalPlan plan = new([unknown, known]);

        Assert.True(unknown.IsFileCountUnknown);
        Assert.True(plan.HasUnknownFileCount);
        Assert.Equal(4, plan.TotalFiles);
    }

    [Fact]
    public void Execute_フォルダ名が違えば削除しない()
    {
        // Even directly under Mods, other mods' folders must be left alone
        string otherMod = Path.Combine(_root, "config", "mods", "SomeOtherMod");
        Directory.CreateDirectory(otherMod);
        File.WriteAllText(Path.Combine(otherMod, "data.txt"), "keep me");

        RemovalPlan plan = new([new RemovalTarget(otherMod, RemovalKind.ModConfig, 1)]);
        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Empty(result.Removed);
        Assert.Single(result.Failures);
        Assert.True(Directory.Exists(otherMod), "他の MOD のフォルダが削除された");
    }

    [Fact]
    public void Execute_既に消えていても失敗として扱い他を止めない()
    {
        string game = CreateGameInstall();
        string mods = CreateModConfig();
        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);

        // One target disappeared between planning and execution
        Directory.Delete(Path.Combine(mods, ModName), recursive: true);

        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Single(result.Removed);
        Assert.Single(result.Failures);
        Assert.False(Directory.Exists(Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods", ModName)));
    }

    [Fact]
    public void Plan_MOD名が空なら例外にする()
    {
        Assert.Throws<ToolException>(() => ModUninstaller.Plan(" ", [], []));
    }

    [Fact]
    public void Plan_他のMODは対象にしない()
    {
        CreateGameInstall("SomeOtherMod");
        string game = Path.Combine(_root, "game");

        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], []);

        Assert.True(plan.IsEmpty, "他の MOD が削除対象に含まれている");
    }

    [Fact]
    public void Plan_同じフォルダを別の綴りで渡しても1件にまとめる()
    {
        // Two spellings of one game folder reach Plan whenever the user has pointed at the game
        // by hand and detection finds the same place. Listing it twice doubles the file count on
        // the confirmation screen, and Execute deletes it once and then reports the second entry
        // as a failure, telling the user that a removal which in fact worked did not.
        CreateGameInstall();
        string game = Path.Combine(_root, "game");

        RemovalPlan plan = ModUninstaller.Plan(
            ModName, [game, game + Path.DirectorySeparatorChar], []);

        Assert.Single(plan.Targets);
        Assert.Equal(3, plan.TotalFiles);

        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Empty(result.Failures);
        Assert.Single(result.Removed);
    }

    /// <summary>
    /// Creates a directory junction, the link Windows makes without needing elevation.
    /// Returns false when the platform will not make one.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Plan_接合点ごしに同じフォルダを渡しても1件にまとめる()
    {
        // A game folder reached through a junction is the same folder reached directly, but
        // GetFullPath resolves "..", case and trailing separators and stops there - so the two
        // spellings came out as different keys, the plan listed one folder twice, counted its
        // files twice, and Execute reported the second deletion as a failure because the first
        // had already removed it. Junctions are ordinary here: moving a Steam library and
        // leaving a link behind is exactly how one appears.
        CreateGameInstall();
        string real = Path.Combine(_root, "game");
        string link = Path.Combine(_root, "linked");

        Assert.True(TryCreateJunction(link, real), "接合点を作成できなかった");

        try
        {
            RemovalPlan plan = ModUninstaller.Plan(ModName, [real, link], []);

            Assert.Single(plan.Targets);
            Assert.Equal(3, plan.TotalFiles);

            RemovalResult result = ModUninstaller.Execute(plan, ModName);

            Assert.Empty(result.Failures);
            Assert.Single(result.Removed);
        }
        finally
        {
            // Taken out here rather than left to Dispose: a recursive delete of a tree holding
            // a junction fails outright, which would turn every run of this test into a failure
            // in the tear-down. Deleting the junction itself leaves what it points at alone.
            Directory.Delete(link);
        }
    }
}

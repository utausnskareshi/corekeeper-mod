using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Removal tests.
///
/// Deleting the wrong thing cannot be undone, so the focus is on removing only what is safe.
/// The real game folder is never touched; the same structure is recreated in a temporary directory.
/// </summary>
[Collection(ChildProcessCollection.Name)]
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
    public void Execute_計画の後に消えていた対象は失敗に数えず他を止めない()
    {
        // Counted as a failure until 2026-09-30, which made the window say "some could not be
        // deleted: 既に存在しない" about a folder that was already in the state asked for - and the
        // reason stayed in Japanese on an English screen. It is neither removed by this run nor a
        // failure, so it is left out of both.
        string game = CreateGameInstall();
        string mods = CreateModConfig();
        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);

        // One target disappeared between planning and execution
        Directory.Delete(Path.Combine(mods, ModName), recursive: true);

        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Single(result.Removed);
        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods", ModName)));
    }

    [Fact]
    public void Execute_すべて消えていれば削除も失敗も無い()
    {
        // The window then says "nothing to remove, already back to the original state"
        string game = CreateGameInstall();
        string mods = CreateModConfig();
        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);

        Directory.Delete(Path.Combine(mods, ModName), recursive: true);
        Directory.Delete(Path.Combine(game, @"CoreKeeper_Data\StreamingAssets\Mods", ModName), recursive: true);

        RemovalResult result = ModUninstaller.Execute(plan, ModName);

        Assert.Empty(result.Removed);
        Assert.Empty(result.Failures);
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

    /// <summary>
    /// A folder holding a junction goes in one call, and what the junction points at stays.
    ///
    /// .NET's recursive delete removes a junction inside as a link, but first tries
    /// DeleteVolumeMountPoint on it, which an ordinary user is refused, and throws that refusal
    /// once the link is already gone (.NET 9.0.4, Windows 11, not elevated): an
    /// UnauthorizedAccessException, which the catch for IOException did not take. "Remove mod"
    /// then reported "Access to the path 'skins' is denied." for a folder that was by then empty,
    /// and left it there (the test campaign of 2026-09-30).
    /// </summary>
    [Fact]
    public void DeleteDirectory_中に接合点があっても一度で消えリンク先は残る()
    {
        string folder = Path.Combine(_root, "config", "mods", ModName);
        string far = Path.Combine(_root, "far");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(far);
        File.WriteAllText(Path.Combine(folder, "General-hideHelm.json"), "{}");
        File.WriteAllText(Path.Combine(far, "skin.png"), "png");
        string link = Path.Combine(folder, "skins");
        Assert.True(TryCreateJunction(link, far), "接合点を作成できなかった");

        try
        {
            PathSafety.DeleteDirectory(folder);

            Assert.False(Directory.Exists(folder), "フォルダが空のまま残っている");
            Assert.True(File.Exists(Path.Combine(far, "skin.png")), "リンク先のファイルが消された");
        }
        finally
        {
            // Should the delete stop short, only the link is taken out, so Dispose can go on
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void Execute_設定フォルダの中の接合点は失敗にせずリンク先も残す()
    {
        // The case as "Remove mod" meets it: the pictures folder of the settings side kept in a
        // synced folder and linked back in
        string mods = CreateModConfig();
        string folder = Path.Combine(mods, ModName);
        string far = Path.Combine(_root, "sync", "skins");
        Directory.CreateDirectory(far);
        File.WriteAllText(Path.Combine(far, "72f9f8f64b557452fab4bd526350feab.png"), "png");
        string link = Path.Combine(folder, "skins");
        Assert.True(TryCreateJunction(link, far), "接合点を作成できなかった");

        try
        {
            RemovalPlan plan = ModUninstaller.Plan(ModName, [], [mods]);
            RemovalResult result = ModUninstaller.Execute(plan, ModName);

            Assert.Empty(result.Failures);
            Assert.Single(result.Removed);
            Assert.False(Directory.Exists(folder), "設定フォルダが残っている");
            Assert.True(File.Exists(Path.Combine(far, "72f9f8f64b557452fab4bd526350feab.png")), "リンク先の画像が消された");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void DeleteDirectory_開いているファイルがあれば今までどおり失敗しその名前を示す()
    {
        // The retry must not swallow a real failure: a file held open is still in the way the
        // second time, and that is what the message names
        string folder = Path.Combine(_root, "config", "mods", ModName);
        Directory.CreateDirectory(folder);
        string locked = Path.Combine(folder, "General-hideHelm.json");
        File.WriteAllText(locked, "{}");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            IOException ex = Assert.Throws<IOException>(() => PathSafety.DeleteDirectory(folder));
            Assert.Contains("General-hideHelm.json", ex.Message, StringComparison.Ordinal);
        }

        Assert.True(Directory.Exists(folder));
    }

    /// <summary>
    /// One target that cannot be deleted does not stop the others. The test that went through this
    /// at aa1990e was the one whose expectation changed with fix 15 (a target gone since the plan is
    /// no longer a failure), which left nothing that fails a target and then goes on: stopping at
    /// the first failure passed every test (the test campaign of 2026-10-01). Locked on each side in
    /// turn, so whichever is handled first, the other comes after a failure.
    /// </summary>
    [Theory]
    [InlineData(RemovalKind.ModConfig)]
    [InlineData(RemovalKind.ModInstall)]
    public void Execute_1件を消せなくても残りの対象は消す(RemovalKind lockedKind)
    {
        string game = CreateGameInstall();
        string mods = CreateModConfig();

        RemovalPlan plan = ModUninstaller.Plan(ModName, [game], [mods]);
        Assert.Equal(2, plan.Targets.Count);

        RemovalTarget locked = plan.Targets.Single(t => t.Kind == lockedKind);
        RemovalTarget other = plan.Targets.Single(t => t.Kind != lockedKind);
        string lockedFile = Directory.EnumerateFiles(locked.Path, "*", SearchOption.AllDirectories).First();

        RemovalResult result;
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = ModUninstaller.Execute(plan, ModName);
        }

        (string Path, string Reason) failure = Assert.Single(result.Failures);
        Assert.Equal(locked.Path, failure.Path);
        Assert.Contains(Path.GetFileName(lockedFile), failure.Reason, StringComparison.Ordinal);

        Assert.Equal(other.Path, Assert.Single(result.Removed));
        Assert.False(Directory.Exists(other.Path), "失敗のあとの対象が消されていない");
    }

    [Theory]
    [InlineData("General-hideHelm.json")]
    [InlineData("zz-General-hideHelm.json")]
    public void DeleteDirectory_接合点と開いているファイルがあれば開いているファイルを示す(string lockedName)
    {
        // With both, the first pass reported the link; what is really in the way is the open file.
        // Named both sides of the link: .NET reports the first failure it meets, so a file listed
        // before "skins" was named by the first pass anyway and proved nothing about the second.
        string folder = Path.Combine(_root, "config", "mods", ModName);
        string far = Path.Combine(_root, "far");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(far);
        File.WriteAllText(Path.Combine(far, "skin.png"), "png");
        string link = Path.Combine(folder, "skins");
        Assert.True(TryCreateJunction(link, far), "接合点を作成できなかった");
        string locked = Path.Combine(folder, lockedName);
        File.WriteAllText(locked, "{}");

        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                IOException ex = Assert.Throws<IOException>(() => PathSafety.DeleteDirectory(folder));
                Assert.Contains(lockedName, ex.Message, StringComparison.Ordinal);
            }

            Assert.True(File.Exists(Path.Combine(far, "skin.png")), "リンク先のファイルが消された");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }
}

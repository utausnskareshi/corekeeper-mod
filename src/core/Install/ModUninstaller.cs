namespace CoreKeeperSkinTool.Install;

/// <summary>Something to delete.</summary>
/// <param name="Path">Absolute path of the target folder.</param>
/// <param name="Kind">Which kind of location this is.</param>
/// <param name="FileCount">
/// Number of files it contains, or <see cref="UnknownFileCount"/> when it could not be counted.
/// </param>
public sealed record RemovalTarget(string Path, RemovalKind Kind, int FileCount)
{
    /// <summary>Sentinel for "the folder could not be enumerated".</summary>
    public const int UnknownFileCount = -1;

    /// <summary>Whether the file count is unknown rather than genuinely zero.</summary>
    public bool IsFileCountUnknown => FileCount == UnknownFileCount;
}

/// <summary>Kind of removal target.</summary>
public enum RemovalKind
{
    /// <summary>The mod files installed into the game.</summary>
    ModInstall,

    /// <summary>Where the settings and image live.</summary>
    ModConfig,
}

/// <summary>The removal plan, shown to the user before it runs.</summary>
/// <param name="Targets">What will be deleted.</param>
public sealed record RemovalPlan(IReadOnlyList<RemovalTarget> Targets)
{
    public bool IsEmpty => Targets.Count == 0;

    /// <summary>Total of the counts that are known. Unknown ones are excluded, not counted as zero.</summary>
    public int TotalFiles => Targets.Where(t => !t.IsFileCountUnknown).Sum(t => t.FileCount);

    /// <summary>Whether at least one target could not be counted, making <see cref="TotalFiles"/> a lower bound.</summary>
    public bool HasUnknownFileCount => Targets.Any(t => t.IsFileCountUnknown);
}

/// <summary>Result of a removal.</summary>
/// <param name="Removed">Folders that were successfully deleted.</param>
/// <param name="Failures">Folders that could not be deleted, with the reason.</param>
public sealed record RemovalResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<(string Path, string Reason)> Failures);

/// <summary>
/// Removes the files this tool installed, restoring the state before installation.
///
/// To avoid deleting an unrelated folder, only targets meeting all of the following qualify.
///   - it sits directly inside a known location (the game's Mods folder or the mod settings folder)
///   - the folder name matches the mod name exactly
/// The same conditions are re-checked at execution time before anything is deleted.
/// </summary>
public static class ModUninstaller
{
    /// <summary>
    /// Where mods live inside the game, relative to the game folder.
    /// Built with the platform separator rather than written with backslashes.
    /// </summary>
    private static readonly string ModsRelativePath =
        Path.Combine("CoreKeeper_Data", "StreamingAssets", "Mods");

    /// <summary>
    /// Builds the removal plan, listing only what actually exists.
    /// </summary>
    /// <param name="modFolderName">Name of the mod folder.</param>
    /// <param name="gamePaths">Game installations; null means auto-detect.</param>
    /// <param name="modsDirectories">Mod settings folders; null means auto-detect.</param>
    public static RemovalPlan Plan(
        string modFolderName,
        IReadOnlyList<string>? gamePaths = null,
        IReadOnlyList<string>? modsDirectories = null)
    {
        // A name like ".." would make the target resolve to the parent, so that the whole
        // Mods folder, or even StreamingAssets, gets deleted instead of one mod.
        PathSafety.EnsureSingleSegment(modFolderName, "MOD フォルダ名");

        List<RemovalTarget> targets = [];

        // The same folder must not enter the plan twice, however it was reached. Two spellings
        // of one game folder, or a caller passing overlapping lists, would otherwise double the
        // file count shown before deletion and make Execute report a failure for the second
        // copy, which by then is already gone.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddTarget(string path, RemovalKind kind)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            string key;
            try
            {
                key = PathSafety.Normalize(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException or System.Security.SecurityException)
            {
                // Unresolvable paths cannot be compared. Keeping the entry is the safe choice:
                // Execute re-checks it and refuses anything it cannot resolve.
                key = path;
            }

            if (seen.Add(key))
            {
                targets.Add(new RemovalTarget(path, kind, CountFiles(path)));
            }
        }

        foreach (string game in gamePaths ?? GameLocator.FindGameInstallations())
        {
            string modsRoot = Path.Combine(game, ModsRelativePath);

            // The mod folder itself, plus the working folders an interrupted install can leave
            // behind. Skipping those would leave files in the game that removal cannot reach,
            // so the state would not really be restored.
            foreach (string name in AcceptedFolderNames(modFolderName))
            {
                AddTarget(Path.Combine(modsRoot, name), RemovalKind.ModInstall);
            }
        }

        IEnumerable<string> configRoots = modsDirectories
            ?? [.. GameLocator.FindModConfigLocations().Select(x => x.ModsDirectory)];

        foreach (string root in configRoots)
        {
            AddTarget(Path.Combine(root, modFolderName), RemovalKind.ModConfig);
        }

        return new RemovalPlan(targets);
    }

    /// <summary>
    /// Executes the plan.
    ///
    /// The settings side, holding the image, is removed first. A running game then
    /// notices within its reload interval and restores the original look. That holds for the
    /// mod as built since 2026-09-27: the game reads a setting whose file is gone as 0 rather
    /// than as its registered default (measured on 1.3.0.2), removing the settings side takes
    /// every setting with it, and an earlier mod therefore read its reload interval as "off"
    /// and kept the removed look until the game was restarted.
    /// </summary>
    public static RemovalResult Execute(RemovalPlan plan, string modFolderName)
    {
        // Re-checked here as well: Execute is public and a caller could reach it without Plan.
        PathSafety.EnsureSingleSegment(modFolderName, "MOD フォルダ名");

        List<string> removed = [];
        List<(string, string)> failures = [];

        IEnumerable<RemovalTarget> ordered = plan.Targets
            .OrderBy(t => t.Kind == RemovalKind.ModConfig ? 0 : 1);

        foreach (RemovalTarget target in ordered)
        {
            // Gone since the plan was made - removed from another window or in Explorer - is the
            // state asked for. Reporting it as "could not be deleted" said the opposite, and in
            // Japanese on an English screen, so it counts as neither removed nor failed.
            if (!Directory.Exists(target.Path))
            {
                continue;
            }

            if (!IsSafeToRemove(target.Path, modFolderName, out string reason))
            {
                failures.Add((target.Path, reason));
                continue;
            }

            try
            {
                // Read-only files are cleared first: one of them is enough to abort the whole
                // recursive delete and leave the mod half-removed.
                PathSafety.DeleteDirectory(target.Path);
                removed.Add(target.Path);
            }
            catch (Exception ex)
            {
                // A recursive delete stops at the first file it cannot remove, having already
                // deleted everything before it, so "failed" on its own is misleading: the mod
                // folder is usually most of the way gone. Saying how much is left lets the user
                // see that closing the game and trying again will finish the job.
                failures.Add((target.Path, $"{ex.Message}{DescribeRemains(target.Path)}"));
            }
        }

        return new RemovalResult(removed, failures);
    }

    /// <summary>
    /// How much of a folder survived a failed delete, as a phrase to append to the reason.
    /// Empty when the folder is gone after all, or cannot be counted.
    /// </summary>
    private static string DescribeRemains(string path)
    {
        if (!Directory.Exists(path))
        {
            return string.Empty;
        }

        int left = CountFiles(path);
        return left switch
        {
            RemovalTarget.UnknownFileCount => string.Empty,
            0 => "（フォルダだけが残っている）",
            _ => $"（ファイル {left} 件が残っている）",
        };
    }

    /// <summary>
    /// Re-checks immediately before deletion that the target is safe to remove.
    /// The situation may have changed between planning and execution.
    /// </summary>
    private static bool IsSafeToRemove(string path, string modFolderName, out string reason)
    {
        if (!Directory.Exists(path))
        {
            reason = "既に存在しない";
            return false;
        }

        // Canonicalise first. Judging "..\" or ".\" by their spelling passes every check below
        // while Directory.Delete resolves them and removes the parent instead.
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            reason = $"パスを解決できない: {ex.Message}";
            return false;
        }

        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!AcceptedFolderNames(modFolderName).Contains(name, StringComparer.Ordinal))
        {
            reason = $"フォルダ名が MOD 名と一致しない（{name}）";
            return false;
        }

        // Final check against deleting somewhere unexpected.
        // The game side uses "Mods" and the settings side "mods", so the comparison ignores case.
        string parent = Path.GetFileName(
            Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty);
        if (!string.Equals(parent, "Mods", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"想定外の場所にある（親フォルダ: {parent}）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The folder names this uninstaller is allowed to delete: the mod folder itself and the
    /// two working folders <see cref="ModPayload"/> uses while swapping in a new copy.
    /// Nothing else is ever a candidate.
    /// </summary>
    private static IReadOnlyList<string> AcceptedFolderNames(string modFolderName) =>
    [
        modFolderName,
        modFolderName + ModPayload.StagingSuffix,
        modFolderName + ModPayload.BackupSuffix,
    ];

    /// <summary>
    /// Counts the files under a folder. Returns <see cref="RemovalTarget.UnknownFileCount"/>
    /// when the folder cannot be enumerated at all, so the confirmation screen can say
    /// "unknown" rather than claim the folder is empty right before deleting it.
    /// </summary>
    private static int CountFiles(string path)
    {
        try
        {
            // The folder itself being a link is the case AttributesToSkip below cannot cover: it
            // applies to what is found inside, not to where the walk starts. Someone who moved the
            // mod folder elsewhere and left a junction behind was quoted the file count of the far
            // side, while the removal takes only the link - a number, and a promise, that did not
            // match what happened.
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return 0;
            }

            // Inaccessible sub-folders are skipped rather than aborting the whole count,
            // so a single unreadable folder does not turn the total into "unknown".
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,

                // A junction inside the mod folder is not part of it: its contents live
                // somewhere else and are not this program's to count or to remove. Followed, the
                // total included every file on the far side of the link, and the number quoted
                // to the user before they agreed to a removal was not the number of files it
                // would touch.
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            return Directory.EnumerateFiles(path, "*", options).Count();
        }
        catch (Exception)
        {
            return RemovalTarget.UnknownFileCount;
        }
    }
}

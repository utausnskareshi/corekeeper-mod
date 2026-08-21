using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace CoreKeeperSkinTool.Install;

/// <summary>Information about the bundled mod.</summary>
/// <param name="Name">Mod name, which is also the folder name.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="FileCount">Number of files it contains.</param>
public sealed record PayloadInfo(string Name, string DisplayName, int FileCount);

/// <summary>State of the installed mod.</summary>
/// <param name="Path">Where it is installed; null when nothing is installed.</param>
/// <param name="Name">Name of the installed mod; null when nothing is installed.</param>
public sealed record InstalledModInfo(string? Path, string? Name)
{
    public bool IsInstalled => Path is not null;
}

/// <summary>
/// Bundles a prebuilt mod with the application and extracts it into the game.
///
/// Core Keeper compiles a mod's C# itself at run time, so neither Unity nor .NET is needed
/// on the user's machine. Extracting the prebuilt files here completes the installation.
///
/// The payload is build/mod-payload/CustomPlayerSkin.zip, produced at build time and embedded.
/// A build made without it, such as during development, reports <see cref="IsAvailable"/> as false.
/// </summary>
public static class ModPayload
{
    private const string ResourceName = "mod-payload.zip";
    private const string ManifestName = "ModManifest.json";

    /// <summary>
    /// Suffix of the folder a new copy is extracted into before it is swapped in.
    /// <see cref="ModUninstaller"/> knows about it so that a leftover from an interrupted
    /// install can still be removed; without that, removal could not restore the original state.
    /// </summary>
    public const string StagingSuffix = ".new";

    /// <summary>Suffix of the folder the previous install is moved aside to during a swap.</summary>
    public const string BackupSuffix = ".old";

    /// <summary>
    /// Where mods live inside the game, relative to the game folder.
    /// Built with the platform separator rather than written with backslashes.
    /// </summary>
    private static readonly string ModsRelativePath =
        Path.Combine("CoreKeeper_Data", "StreamingAssets", "Mods");

    /// <summary>Whether this build bundles the mod.</summary>
    public static bool IsAvailable =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(ResourceName);

    /// <summary>Reads information about the bundled mod, or null when none is bundled.</summary>
    public static PayloadInfo? GetInfo()
    {
        using Stream? stream = OpenPayload();
        if (stream is null)
        {
            return null;
        }

        using ZipArchive archive = new(stream, ZipArchiveMode.Read);

        ZipArchiveEntry? manifest = archive.GetEntry(ManifestName);
        if (manifest is null)
        {
            throw new ToolException(
                "error.payload.noManifest", [ManifestName],
                $"同梱データに {ManifestName} が無い。配布物が壊れている可能性がある。");
        }

        using Stream manifestStream = manifest.Open();
        using JsonDocument document = JsonDocument.Parse(manifestStream);

        string name = document.RootElement.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;

        string displayName = document.RootElement.TryGetProperty("displayName", out JsonElement displayElement)
            ? displayElement.GetString() ?? name
            : name;

        if (string.IsNullOrEmpty(name))
        {
            throw new ToolException(
                "error.payload.noName", [ManifestName], $"同梱データの {ManifestName} に名前が無い。");
        }

        // The name becomes a folder under Mods and is passed to a recursive delete,
        // so it has to be a plain folder name and nothing that can point elsewhere.
        PathSafety.EnsureSingleSegment(name, $"同梱データの {ManifestName} の name");

        return new PayloadInfo(name, displayName, archive.Entries.Count(e => !IsDirectoryEntry(e)));
    }

    /// <summary>
    /// Extracts the mod into the given game, replacing any existing install.
    /// </summary>
    /// <param name="gamePath">Where the game is installed.</param>
    /// <returns>Path the payload was extracted to.</returns>
    public static string InstallTo(string gamePath)
    {
        PayloadInfo info = GetInfo()
            ?? throw new ToolException(
                "error.payload.missing", [],
                "この配布物には MOD が同梱されていない。" + Environment.NewLine +
                "  配布物を取得し直すか、開発環境なら scripts/build-mod.ps1 を実行すること。");

        string modsDirectory = Path.Combine(gamePath, ModsRelativePath);
        if (!Directory.Exists(Path.GetDirectoryName(modsDirectory)))
        {
            throw new ToolException(
                "error.payload.badGameFolder", [gamePath],
                $"ゲームのフォルダ構成が想定と違う: {gamePath}" + Environment.NewLine +
                "  Core Keeper のインストール先を確認すること。");
        }

        string destination = PathSafety.EnsureInside(
            modsDirectory, Path.Combine(modsDirectory, info.Name), "MOD の配置先");

        // Extract into a sibling folder first and only swap it in once it is complete.
        // Deleting the old install up front would leave nothing usable behind if the
        // extraction then failed, for example because the game is running and holds a file open.
        string staging = destination + StagingSuffix;
        string backup = destination + BackupSuffix;

        // An interrupted previous run can leave the only copy of the install in the backup
        // folder while the destination is missing or empty. Recover it before cleaning up,
        // otherwise the cleanup below would delete the very files being recovered.
        if (Directory.Exists(backup) && IsMissingOrEmpty(destination))
        {
            try
            {
                if (Directory.Exists(destination))
                {
                    PathSafety.DeleteDirectory(destination);
                }

                Directory.Move(backup, destination);
            }
            catch (Exception)
            {
                // Recovery is best effort. The install below replaces the folder anyway; the
                // only thing lost is whatever the user had added inside it by hand.
            }
        }

        // Leftovers from an interrupted run must go before anything else, and a failure here
        // has to explain itself rather than surfacing as a bare UnauthorizedAccessException.
        foreach (string leftover in new[] { staging, backup })
        {
            // A file of the same name counts too. Only folders were looked for, so a stray file
            // called CustomPlayerSkin.new - which CreateDirectory below then cannot create a
            // folder over - failed every install from then on, and the uninstaller could not see
            // it either, because that only ever looks for folders.
            bool isFile = File.Exists(leftover);
            if (!isFile && !Directory.Exists(leftover))
            {
                continue;
            }

            try
            {
                if (isFile)
                {
                    PathSafety.DeleteFile(leftover);
                }
                else
                {
                    PathSafety.DeleteDirectory(leftover);
                }
            }
            catch (Exception ex)
            {
                throw new ToolException(
                    "error.payload.leftoverLocked", [leftover, ex.Message],
                    $"前回の作業フォルダを削除できない: {leftover}" + Environment.NewLine +
                    $"  {ex.Message}" + Environment.NewLine +
                    "  ゲームを終了し、このフォルダを手動で削除してから、もう一度実行すること。");
            }
        }

        Directory.CreateDirectory(staging);

        try
        {
            using (Stream stream = OpenPayload()!)
            using (ZipArchive archive = new(stream, ZipArchiveMode.Read))
            {
                ExtractSafely(archive, staging);
            }
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        // From here the new copy is complete, so replacing the old one is quick and low-risk.
        bool movedAside = false;
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedAside = true;
            }

            Directory.Move(staging, destination);
        }
        catch (Exception ex)
        {
            // Put the previous install back so the user is not left with nothing. The check is
            // deliberately not "the destination is missing": something can recreate an empty
            // folder there between the two moves, and refusing to restore because of it would
            // leave the user with an empty mod folder and their real files stranded in .old.
            if (movedAside && Directory.Exists(backup))
            {
                try
                {
                    if (Directory.Exists(destination))
                    {
                        PathSafety.DeleteDirectory(destination);
                    }

                    Directory.Move(backup, destination);
                }
                catch (Exception)
                {
                    // Reported through the outer message below; nothing more can be done here.
                }
            }

            TryDelete(staging);

            // Name the backup folder when the previous install is still sitting in it, so the
            // user can find their files instead of concluding that they are gone.
            bool leftInBackup = movedAside && Directory.Exists(backup);
            string stranded = leftInBackup
                ? Environment.NewLine + $"  以前の MOD は {backup} に残っている。もう一度実行すると復旧を試みる。"
                : string.Empty;

            // Two whole keys rather than one plus a translated tail. A sentence glued together
            // from fragments cannot be worded naturally in every language, and this one has to
            // read well: it is what the user sees when an install has failed halfway.
            throw new ToolException(
                leftInBackup ? "error.payload.swapFailedStranded" : "error.payload.swapFailed",
                leftInBackup ? [ex.Message, backup] : [ex.Message],
                $"MOD の差し替えに失敗した: {ex.Message}" + Environment.NewLine +
                "  ゲームを終了してから、もう一度実行すること。" + stranded);
        }

        TryDelete(backup);
        return destination;
    }

    /// <summary>Whether a folder is absent, or present but holds nothing.</summary>
    private static bool IsMissingOrEmpty(string path) =>
        !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

    /// <summary>
    /// Deletes a working folder if present, ignoring failures. Used for .new and .old only.
    ///
    /// The manifest is removed first. Both working folders hold a complete copy of the mod,
    /// manifest and guid included, and they sit directly in the folder the game scans, so a
    /// leftover is not merely clutter: the game would find a second mod carrying the same
    /// identity as the real one. Taking the manifest out first means that a delete which fails
    /// part way through — an antivirus product holding a file open is enough — still leaves
    /// behind something the game no longer recognises as a mod.
    ///
    /// Only ever called on a folder whose contents are already known to be disposable: the
    /// staging copy after a failed extraction, and the backup after the swap succeeded. The
    /// recovery path reads the backup before any of this runs.
    /// </summary>
    internal static void TryDelete(string? path)
    {
        if (path is null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            PathSafety.DeleteFile(Path.Combine(path, ManifestName));
        }
        catch (Exception)
        {
            // Best effort. The recursive delete below is still worth attempting.
        }

        try
        {
            PathSafety.DeleteDirectory(path);
        }
        catch (Exception)
        {
            // A leftover working folder is not fatal here: the next install removes it up front,
            // and the uninstaller knows about the .new and .old names.
        }
    }

    /// <summary>
    /// Whether the installed mod is the same as the one this application carries.
    ///
    /// Compared by content rather than by version number, because the manifest has no version to
    /// compare: it lists the files, and those file names do not change from one build to the next.
    /// The payload is a couple of dozen kilobytes, so reading it through is cheaper than the
    /// bookkeeping any other answer would need.
    ///
    /// True when it cannot be told. Reporting a difference on a folder that merely could not be
    /// read would offer an update nobody needs, every time, for as long as the read keeps failing.
    /// </summary>
    public static bool InstalledMatchesPayload(string gamePath, string modFolderName)
    {
        PathSafety.EnsureSingleSegment(modFolderName, "MOD フォルダ名");

        try
        {
            using Stream? payload = OpenPayload();
            if (payload is null)
            {
                return true;
            }

            string installed = Path.Combine(gamePath, ModsRelativePath, modFolderName);
            if (!Directory.Exists(installed))
            {
                return true;
            }

            using ZipArchive archive = new(payload, ZipArchiveMode.Read);

            HashSet<string> carried = new(StringComparer.OrdinalIgnoreCase);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // Folder entries carry no content of their own
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                string target = PathSafety.EnsureInside(
                    installed, Path.Combine(installed, entry.FullName), "MOD の配置先");

                carried.Add(Path.GetRelativePath(installed, target));

                FileInfo file = new(target);
                if (!file.Exists || file.Length != entry.Length)
                {
                    return false;
                }

                using Stream expected = entry.Open();
                using FileStream actual = File.OpenRead(target);
                if (!SameBytes(expected, actual))
                {
                    return false;
                }
            }

            // Everything carried is present and identical. The other direction counts too: a file
            // the install has and this application does not is a leftover from an older mod, and
            // the mod ships as C# source that the game compiles when it loads. A leftover compiles
            // with the rest - a class that has since been renamed arrives twice and the mod stops
            // loading - so it has to count as a difference. Nothing above would have noticed,
            // because every entry the loop walks was accounted for.
            foreach (string existing in Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories))
            {
                if (!carried.Contains(Path.GetRelativePath(installed, existing)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Whether two streams hold the same bytes, read a block at a time.</summary>
    private static bool SameBytes(Stream left, Stream right)
    {
        byte[] leftBuffer = new byte[8192];
        byte[] rightBuffer = new byte[8192];

        while (true)
        {
            int leftRead = left.ReadAtLeast(leftBuffer, leftBuffer.Length, throwOnEndOfStream: false);
            int rightRead = right.ReadAtLeast(rightBuffer, rightBuffer.Length, throwOnEndOfStream: false);

            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    /// <summary>Checks whether the mod is installed in the given game.</summary>
    public static InstalledModInfo GetInstalled(string gamePath, string modFolderName)
    {
        PathSafety.EnsureSingleSegment(modFolderName, "MOD フォルダ名");

        string path = Path.Combine(gamePath, ModsRelativePath, modFolderName);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, ManifestName)))
        {
            return new InstalledModInfo(null, null);
        }

        try
        {
            using FileStream stream = File.OpenRead(Path.Combine(path, ManifestName));
            using JsonDocument document = JsonDocument.Parse(stream);
            string? name = document.RootElement.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString()
                : null;

            return new InstalledModInfo(path, name);
        }
        catch (Exception)
        {
            // Even unreadable, we still know something is installed, so report that much
            return new InstalledModInfo(path, null);
        }
    }

    private static Stream? OpenPayload() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);

    /// <summary>
    /// Extracts entries while verifying that nothing is written outside the destination.
    /// This guards against Zip Slip, where an entry name containing .. writes outside the target.
    /// </summary>
    internal static void ExtractSafely(ZipArchive archive, string destination)
    {
        // The manifest goes last. It is what makes a folder a mod as far as the game is
        // concerned, and it happens to be the first entry in the payload, so a run interrupted
        // during extraction left a CustomPlayerSkin.new sitting in the folder the game scans,
        // carrying the same guid as the real mod. Writing it only once everything else is in
        // place puts this the same way round as the cleanup, which removes it first.
        IEnumerable<ZipArchiveEntry> ordered = archive.Entries
            .OrderBy(e => string.Equals(
                NormalizeEntryName(e.FullName), ManifestName, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0);

        foreach (ZipArchiveEntry entry in ordered)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            string relative = NormalizeEntryName(entry.FullName);

            // Every segment has to survive Windows resolving it. Trailing dots and spaces are
            // stripped on the way to the file system, so "Scripts/Mod.cs." quietly became
            // "Mod.cs" and overwrote a different file from the one the payload named - and a
            // segment that resolves to something else cannot be checked by its spelling.
            foreach (string segment in relative.Split(
                         Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment != segment.TrimEnd('.', ' '))
                {
                    throw new ToolException($"同梱データに不正な項目がある: {entry.FullName}");
                }
            }

            if (relative.Length == 0)
            {
                throw new ToolException(
                    "error.payload.unnamedEntry", [],
                    "同梱データに名前の無い項目がある。配布物が壊れている可能性がある。");
            }

            // Canonicalise, then confirm the result really is under the destination.
            // Combine alone is not enough: an entry named "..\x" or "C:\x" escapes it.
            //
            // "Under" has to mean strictly under. An entry resolving to the destination folder
            // itself, which "Scripts/.." does, passes a plain containment test and then reaches
            // ExtractToFile with a folder as its target, where it fails as a permission error
            // that says nothing about the payload being wrong.
            string target = Path.Combine(destination, relative);
            if (!PathSafety.IsWithin(destination, target)
                || string.Equals(
                    PathSafety.Normalize(destination),
                    PathSafety.Normalize(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(
                    "error.payload.badEntry", [entry.FullName],
                    $"同梱データに不正な項目がある: {entry.FullName}");
            }

            target = Path.GetFullPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Whether the entry denotes a folder rather than a file.
    ///
    /// The zip format specifies "/" as the separator, but archives produced by
    /// PowerShell 5.1's Compress-Archive use "\", so both are accepted.
    /// </summary>
    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/')
        || entry.FullName.EndsWith('\\')
        || string.IsNullOrEmpty(entry.Name);

    /// <summary>Converts an entry name to this platform's separator.</summary>
    private static string NormalizeEntryName(string entryName) =>
        entryName.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar).Trim();
}

using CoreKeeperSkinTool.Layout;

namespace CoreKeeperSkinTool.Install;

/// <summary>What happened to one character during an install or a removal.</summary>
/// <param name="Guid">The character the entry is about.</param>
/// <param name="Path">Full path of the file that was written or deleted.</param>
/// <param name="Changed">
/// True when the file was actually written or actually deleted. False on a removal means there
/// was nothing there to begin with, or that the removal failed - see <paramref name="Error"/>.
/// </param>
/// <param name="Error">
/// Why this one character could not be dealt with, or null when nothing went wrong.
///
/// Carried in the result rather than thrown, because a removal covering several characters gets
/// part way through: throwing at the first unreadable file threw away the record of everything
/// already deleted, and the caller could neither report it nor undo it.
/// </param>
public sealed record CharacterSkinResult(string Guid, string Path, bool Changed, string? Error = null);

/// <summary>
/// Manages one image per player character.
///
/// The mod looks its image up by the character's own identifier, so each character can have a
/// different appearance and removing one leaves the others untouched. The files sit inside the
/// mod's own settings folder, which means uninstalling the mod takes every character's image
/// with it and nothing is left behind in the game.
/// </summary>
public static class CharacterSkins
{
    /// <summary>Folder holding the per-character images, inside the mod's settings folder.</summary>
    public const string FolderName = "skins";

    /// <summary>Extension of the files the mod reads.</summary>
    public const string Extension = ".png";

    /// <summary>
    /// Suffix of the copy made beside a target before it is swapped in.
    /// The mod only reads <c>&lt;guid&gt;.png</c>, so a leftover is ignored rather than loaded.
    /// </summary>
    private const string StagingSuffix = ".new";

    /// <summary>The folder holding every character's image.</summary>
    public static string DirectoryFor(string modsDirectory, string modFolderName)
    {
        EnsureModReadsThisFolder(modFolderName);

        return Path.Combine(modsDirectory, modFolderName, FolderName);
    }

    /// <summary>
    /// Refuses a folder name the mod does not read.
    ///
    /// The mod's own path is a compile-time constant, so images filed anywhere else are simply
    /// never looked at. Accepting the name silently made a rename look like a successful install
    /// and, worse, made the tool then report those characters as applied while the game showed
    /// the original appearance.
    /// </summary>
    internal static void EnsureModReadsThisFolder(string modFolderName)
    {
        PathSafety.EnsureSingleSegment(modFolderName, "MOD フォルダ名");

        if (!string.Equals(modFolderName, SheetInstaller.DefaultModFolderName, StringComparison.Ordinal))
        {
            throw new ToolException(
                "error.character.fixedFolder",
                [SheetInstaller.DefaultModFolderName, modFolderName],
                $"MOD が読み込むフォルダは {SheetInstaller.DefaultModFolderName} に固定されているため、" +
                $"{modFolderName} に置いても反映されない。" + Environment.NewLine +
                "  MOD 側のパスは定数のため、配置先のフォルダ名は変更できない。");
        }
    }

    /// <summary>The image belonging to one character.</summary>
    public static string PathFor(string modsDirectory, string modFolderName, string guid)
    {
        // The identifier reaches a file path, so it is checked rather than trusted. A save file
        // is not something the user edits, but it is still input from outside this program.
        if (!CharacterLocator.IsValidGuid(guid))
        {
            throw new ToolException(
                "error.character.badGuid", [guid], $"キャラクターの識別子が不正: {guid}");
        }

        return Path.Combine(DirectoryFor(modsDirectory, modFolderName), guid + Extension);
    }

    /// <summary>
    /// Writes the sheet as the image for each of the given characters.
    ///
    /// The sheet is verified once up front rather than per character, so a broken image is
    /// rejected before anything is written and no character is left half-applied.
    /// </summary>
    public static IReadOnlyList<CharacterSkinResult> Install(
        string sheetPath,
        SheetLayout layout,
        string modsDirectory,
        string modFolderName,
        IReadOnlyList<string> guids)
    {
        if (guids.Count == 0)
        {
            throw new ToolException(
                "error.character.noneSelected", [], "適用するキャラクターが選ばれていない。");
        }

        SheetInstaller.Verify(sheetPath, layout);

        // Every path is resolved before the first write, so an invalid identifier cannot leave
        // some characters updated and the rest not.
        //
        // Then reduced to one entry per destination. Two characters can carry the same
        // identifier - copying a save file to duplicate a character is all it takes - and two
        // identifiers differing only in case land on the same file on Windows. Either way the
        // staged copy was moved into place by the first of them and the second went looking for
        // a file that was no longer there, so the whole install failed with a FileNotFoundException
        // naming a .png.new path, after some characters had already been replaced and with no
        // copy of what they had before. Nothing about applying one picture to one destination
        // twice is meaningful, so the duplicate is simply dropped.
        List<(string Guid, string Path)> targets =
        [
            .. guids
                .Select(g => (Guid: g, Path: PathFor(modsDirectory, modFolderName, g)))
                .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase),
        ];

        // A file sitting where a folder has to go makes CreateDirectory throw IOException, and
        // the message it carries is .NET's own English one with nothing about which file or what
        // to do. Named here instead, in the user's language.
        string skins = DirectoryFor(modsDirectory, modFolderName);
        foreach (string blocked in new[] { Path.GetDirectoryName(skins)!, skins })
        {
            if (File.Exists(blocked))
            {
                throw new ToolException(
                    "error.character.fileInTheWay", [blocked],
                    $"同じ名前のファイルがあるため配置先を作れない: {blocked}" + Environment.NewLine +
                    "  そのファイルの名前を変えるか、削除してから実行すること。");
            }
        }

        Directory.CreateDirectory(skins);

        // Copied to one side first, then swapped in. Writing straight to each destination in
        // turn meant a failure partway through left the earlier characters replaced and the
        // rest not, with the caller told only that the whole thing failed. That really happens:
        // the running game holds an image open while it reads it, and backup or sync products
        // set the read-only attribute.
        List<string> staged = [];
        try
        {
            foreach ((_, string path) in targets)
            {
                // Named for this process. A fixed name is one name for every copy of the program
                // running at once, and two of them staging the same character meant the second
                // deleted the first's copy from under it: the destination had already been
                // removed, the move then failed on a file that was no longer there, and the
                // character was left with no picture at all. Measured at two characters lost out
                // of forty, on every attempt.
                string staging = $"{path}.{Environment.ProcessId}{StagingSuffix}";

                // Cleared rather than overwritten. File.Copy(overwrite: true) will not replace a
                // read-only file, so a staging file left read-only by a backup or sync product
                // made every later install for that character fail - and nothing on the way in
                // or out of this method ever removed it, so the failure was permanent.
                PathSafety.DeleteFile(staging);

                File.Copy(sheetPath, staging, overwrite: true);
                staged.Add(staging);
            }

            List<CharacterSkinResult> results = [];
            for (int i = 0; i < targets.Count; i++)
            {
                try
                {
                    // Moved over the destination rather than deleting it first. Deleting first
                    // opens a window where the character has no picture at all, and everything
                    // that can make the move fail - the running game holding the file open, a
                    // backup product marking it read-only - happens inside exactly that window.
                    // The read-only attribute is cleared only once the move has actually
                    // objected to it, so the usual case never touches the destination until it
                    // has the replacement in hand.
                    try
                    {
                        File.Move(staged[i], targets[i].Path, overwrite: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        PathSafety.ClearReadOnly(targets[i].Path);
                        File.Move(staged[i], targets[i].Path, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Say how far it got rather than only that it failed. The characters already
                    // swapped in really have changed, and the caller has to be able to tell the
                    // user which ones so the list on screen can be brought back in line.
                    staged.RemoveRange(0, i);

                    throw new ToolException(
                        "error.character.partial", [results.Count, targets.Count],
                        $"適用の途中で失敗した（{targets.Count} 体中 {results.Count} 体まで適用済み）: " +
                        ex.Message + Environment.NewLine +
                        "  ゲームを終了してから、もう一度実行すること。");
                }

                results.Add(new CharacterSkinResult(targets[i].Guid, targets[i].Path, Changed: true));
            }

            staged.Clear();
            return results;
        }
        finally
        {
            // Anything still staged belongs to a run that did not get as far as swapping it in
            foreach (string leftover in staged)
            {
                try
                {
                    // Through PathSafety, which clears the read-only attribute first. A plain
                    // Delete leaves a read-only staging file behind for good, and that file then
                    // blocks every future install for the character it belongs to.
                    PathSafety.DeleteFile(leftover);
                }
                catch (Exception)
                {
                    // A leftover temporary file is harmless; the next install clears it.
                }
            }
        }
    }

    /// <summary>
    /// Deletes the image of each of the given characters, leaving the mod itself installed.
    /// A character with no image is reported as unchanged rather than as a failure.
    /// </summary>
    public static IReadOnlyList<CharacterSkinResult> Remove(
        string modsDirectory,
        string modFolderName,
        IReadOnlyList<string> guids)
    {
        // Resolved up front, as Install does. Validating inside the loop meant a bad identifier
        // partway along deleted everything before it and then threw, and the caller lost the
        // results for the deletions that had already happened.
        List<(string Guid, string Path)> targets =
            [.. guids.Select(g => (g, PathFor(modsDirectory, modFolderName, g)))];

        List<CharacterSkinResult> results = [];

        foreach ((string guid, string path) in targets)
        {
            if (!File.Exists(path))
            {
                results.Add(new CharacterSkinResult(guid, path, Changed: false));
                continue;
            }

            try
            {
                // Clear read-only first: one such file is enough to fail the delete and leave
                // the character looking replaced with no way to undo it from here.
                PathSafety.DeleteFile(path);
                results.Add(new CharacterSkinResult(guid, path, Changed: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Recorded and carried on with. A file held open by the running game used to
                // end the whole removal with a bare IOException, which discarded the results
                // for every character already dealt with: the caller was told nothing had
                // happened while some characters had in fact been restored.
                results.Add(new CharacterSkinResult(guid, path, Changed: false, Error: ex.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// The characters that currently have an image installed.
    /// Empty when the folder does not exist, which is the state before the first install.
    /// </summary>
    public static IReadOnlySet<string> InstalledGuids(string modsDirectory, string modFolderName)
    {
        HashSet<string> found = new(StringComparer.Ordinal);

        string directory;
        try
        {
            directory = DirectoryFor(modsDirectory, modFolderName);
        }
        catch (ToolException)
        {
            // A folder the mod does not read holds nothing that counts as installed
            return found;
        }

        if (!Directory.Exists(directory))
        {
            return found;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*" + Extension))
            {
                string guid = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (CharacterLocator.IsValidGuid(guid))
                {
                    found.Add(guid);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable folder just means nothing can be reported as installed
        }

        return found;
    }

    /// <summary>
    /// Images whose character no longer exists, because it was deleted in game.
    ///
    /// They can never be applied again, since the mod looks images up by the identifier of the
    /// character being played, so they are only dead weight in the mod folder.
    /// </summary>
    public static IReadOnlyList<string> OrphanedGuids(
        string modsDirectory, string modFolderName, IReadOnlyList<GameCharacter> characters)
    {
        HashSet<string> alive = new(characters.Select(c => c.Guid), StringComparer.Ordinal);

        return [.. InstalledGuids(modsDirectory, modFolderName).Where(g => !alive.Contains(g)).Order()];
    }
}

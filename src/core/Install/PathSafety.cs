namespace CoreKeeperSkinTool.Install;

/// <summary>
/// Path checks shared by everything that writes into, or deletes from, the user's game folder.
///
/// The install and uninstall code builds paths by combining a fixed root with a name that comes
/// from outside (a CLI option, a bundled manifest). A name such as ".." or an absolute path
/// escapes that root: <c>Path.Combine(root, "..")</c> yields <c>root\..</c>, and
/// <c>Directory.Delete</c> resolves it, so the parent gets deleted instead. Names are therefore
/// restricted to a single path segment, and every resulting path is re-checked against its root.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Verifies that a name is usable as one folder or file name and nothing more.
    ///
    /// Rejects empty names, "." and "..", anything containing a separator, absolute or
    /// drive-relative paths, and characters the platform disallows in a name.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="description">What the name is, used in the error message.</param>
    /// <returns>The name unchanged, so this can be used inline.</returns>
    /// <exception cref="ToolException">The name is not a single safe path segment.</exception>
    public static string EnsureSingleSegment(string? name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ToolException($"{description}が空。");
        }

        if (name is "." or "..")
        {
            throw new ToolException(
                $"{description}に \"{name}\" は使えない。親フォルダを指す名前は削除・上書きの対象が想定外の場所になる。");
        }

        // Windows silently strips trailing dots and spaces when resolving a path, so "..." and
        // ".. " both resolve to the parent folder even though neither is spelled "..", and "X."
        // resolves to "X". A name that changes meaning on resolution cannot be checked by
        // spelling, so it is refused outright.
        if (name != name.TrimEnd('.', ' '))
        {
            throw new ToolException(
                $"{description}の末尾に \".\" や空白は使えない（{name}）。解決時に取り除かれ、別の場所を指すことになる。");
        }

        if (Path.IsPathRooted(name) || name.Contains(':'))
        {
            throw new ToolException($"{description}にパスは指定できない: {name}");
        }

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ToolException($"{description}に区切り文字は含められない: {name}");
        }

        // GetFileName strips anything that is not the final segment; a name that survives it
        // unchanged contains no path structure at all.
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            throw new ToolException($"{description}がフォルダ名として不正: {name}");
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ToolException($"{description}に使用できない文字が含まれる: {name}");
        }

        return name;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> resolves to <paramref name="root"/> itself
    /// or to something inside it. Both sides are canonicalised first, so "..", "." and
    /// duplicated separators cannot be used to slip out.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        string fullRoot = Normalize(root);
        string fullCandidate = Normalize(candidate);

        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(
            fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Canonical form used for comparing two paths: fully resolved, with no trailing separator.
    ///
    /// The trailing separator matters. <c>Path.GetFullPath</c> keeps one for a path such as
    /// <c>...\Mods\...</c> (three dots), whose trailing dots Windows strips, so the result is the
    /// Mods folder itself but spelled with a trailing backslash. Comparing that against the root
    /// as plain text then says the two differ, and a check meant to reject "this is the root
    /// itself" lets it through.
    ///
    /// Also used as the key for de-duplicating discovered folders. Comparing the strings as
    /// typed treats "X" and "X\" as two different places, which makes the same folder appear
    /// twice in a removal plan: the second attempt then finds it already gone and reports a
    /// failure for a removal that in fact succeeded.
    /// </summary>
    public static string Normalize(string path)
    {
        string full = TrimTrailingSeparators(StripExtendedPrefix(Path.GetFullPath(path)));

        return TrimTrailingSeparators(ResolveLinks(full));
    }

    /// <summary>
    /// Drops trailing separators, except the one a drive root cannot do without.
    ///
    /// Trailing separators are otherwise noise: the same folder written with and without one has
    /// to compare equal, which is what lets "is this the file I was given as input" work at all.
    /// A drive root is the exception. <c>C:\</c> is the root of C, while <c>C:</c> is wherever the
    /// process happens to be on C - so trimming it turned every path later built from that root
    /// into a drive-relative one, and the files went somewhere nobody named. Reachable from the
    /// command line, where <c>--mods-dir</c> is normalised before anything is written under it.
    /// </summary>
    private static string TrimTrailingSeparators(string path)
    {
        string trimmed = path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return trimmed.Length == 2 && trimmed[1] == Path.VolumeSeparatorChar
            ? trimmed + Path.DirectorySeparatorChar
            : trimmed;
    }

    /// <summary>
    /// Removes the <c>\\?\</c> prefix, which names the same place by a different spelling.
    ///
    /// GetFullPath keeps it, so the same file written once with it and once without compared as
    /// two different files - enough for "--output is the same file as --input" to miss, and the
    /// source picture was then overwritten by the sheet made from it, with nothing to undo.
    /// </summary>
    private static string StripExtendedPrefix(string full)
    {
        const string unc = @"\\?\UNC\";
        const string plain = @"\\?\";

        if (full.StartsWith(unc, StringComparison.Ordinal))
        {
            return @"\\" + full[unc.Length..];
        }

        return full.StartsWith(plain, StringComparison.Ordinal) ? full[plain.Length..] : full;
    }

    /// <summary>
    /// Follows a junction or symbolic link anywhere along the path to what it really points at.
    ///
    /// GetFullPath resolves "..", case and trailing separators, but not a link, so one folder
    /// reached through a junction and the same folder reached directly compared as two different
    /// places: a removal plan listed it twice, counted its files twice, and reported the second
    /// deletion as a failure because the first had already removed it.
    ///
    /// The whole path is walked rather than just the last segment, because that is where the
    /// link usually is not: moving a Steam library and leaving a link behind puts one near the
    /// top, several levels above the mod folder.
    /// </summary>
    private static string ResolveLinks(string full)
    {
        List<string> tail = [];
        DirectoryInfo? current = new(full);

        while (current is not null)
        {
            try
            {
                if (current.Exists
                    && current.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    string resolved = target.FullName;
                    tail.Reverse();
                    return tail.Count == 0 ? resolved : Path.Combine([resolved, .. tail]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                // A link that cannot be followed - a broken target, a folder that denies access
                // - leaves the path usable as its own key, which is what this did before.
                return full;
            }

            tail.Add(current.Name);
            current = current.Parent;
        }

        return full;
    }

    /// <summary>
    /// Verifies that a path sits strictly inside a root, and returns its canonical form.
    /// </summary>
    /// <exception cref="ToolException">The path resolves outside the root.</exception>
    public static string EnsureInside(string root, string candidate, string description)
    {
        if (!IsWithin(root, candidate)
            || string.Equals(Normalize(root), Normalize(candidate), StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(
                $"{description}が想定の場所の外を指している。" + Environment.NewLine +
                $"  対象: {candidate}" + Environment.NewLine +
                $"  想定: {root} の配下");
        }

        return Path.GetFullPath(candidate);
    }

    /// <summary>
    /// Deletes one file, clearing the read-only attribute first.
    /// A read-only file would otherwise refuse to go, and the caller has already decided the
    /// file is theirs to remove.
    /// </summary>
    /// <summary>
    /// Clears the read-only attribute, if the file has one and exists.
    ///
    /// Separate from <see cref="DeleteFile"/> because there is a difference between "make this
    /// writable" and "get rid of it": deleting a destination before writing its replacement
    /// leaves nothing at all if the write then fails.
    /// </summary>
    public static void ClearReadOnly(string path)
    {
        FileInfo file = new(path);
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    public static void DeleteFile(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            return;
        }

        if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }

        file.Delete();
    }

    /// <summary>
    /// Deletes a folder and everything under it, clearing the read-only attribute first.
    ///
    /// <c>Directory.Delete(path, true)</c> refuses to remove a read-only file, and that is enough
    /// to make the whole call fail. It matters here because a single read-only file left behind
    /// by a backup tool, a cloud-sync client or an antivirus product would otherwise make
    /// "remove everything" impossible and leave the mod half-deleted, and would make a reinstall
    /// fail every time on the leftover backup folder.
    ///
    /// Reparse points are removed as links: their contents are never touched.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        if (!directory.Exists)
        {
            return;
        }

        ClearReadOnly(directory);

        try
        {
            directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Windows completes a delete after the last handle closes, so a recursive delete can
            // report failure for a folder that is on its way out - a junction inside it is enough
            // to produce this. Reporting a removal as failed when it has in fact happened sends
            // the user looking for files that are no longer there, so the outcome decides.
            directory.Refresh();
            if (directory.Exists)
            {
                throw;
            }
        }
    }

    private static void ClearReadOnly(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Following the link would clear attributes on files this tool does not own
            return;
        }

        if (directory.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }

        foreach (FileInfo file in directory.EnumerateFiles())
        {
            // The same reasoning as the folder above, which had the guard and this did not. A file
            // symbolic link inside the folder resolves to a file somewhere else, and clearing
            // read-only through it changes a file this tool does not own. Nothing is lost by
            // skipping it: what the delete removes is the link, and a link's own attributes never
            // stand in the way of that.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        foreach (DirectoryInfo child in directory.EnumerateDirectories())
        {
            ClearReadOnly(child);
        }
    }
}

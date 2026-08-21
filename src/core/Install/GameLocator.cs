namespace CoreKeeperSkinTool.Install;

/// <summary>
/// A candidate folder that the mod reads its image from.
/// </summary>
/// <param name="ModsDirectory">The mods folder for one platform and user.</param>
/// <param name="Platform">Platform name, such as Steam.</param>
/// <param name="UserId">User identifier on that platform.</param>
public sealed record ModConfigLocation(string ModsDirectory, string Platform, string UserId)
{
    /// <summary>A short label for display.</summary>
    public string Describe() => $"{Platform}/{UserId}";
}

/// <summary>
/// Finds Core Keeper's mod settings folder.
///
/// The mod reads files through <c>API.ConfigFilesystem</c>, which actually resolves to
/// <c>&lt;user&gt;\AppData\LocalLow\Pugstorm\Core Keeper\&lt;platform&gt;\&lt;id&gt;\mods\</c>.
/// Several platforms or users may exist, so candidates are listed and the caller chooses.
/// </summary>
public static class GameLocator
{
    private const string PublisherFolder = "Pugstorm";
    private const string GameFolder = "Core Keeper";

    /// <summary>
    /// One per-user data folder, the parent of both <c>mods</c> and <c>saves</c>.
    /// </summary>
    /// <param name="Directory">Full path of <c>&lt;game&gt;\&lt;platform&gt;\&lt;user id&gt;</c>.</param>
    /// <param name="Platform">Platform name, such as Steam.</param>
    /// <param name="UserId">User identifier on that platform.</param>
    public sealed record UserDataDirectory(string Directory, string Platform, string UserId);

    /// <summary>
    /// Returns every per-user data folder the game has created.
    ///
    /// Kept separate from <see cref="FindModConfigLocations"/> because the characters live
    /// beside the mods folder rather than inside it, and the mods folder only appears once a
    /// mod has run. Looking for characters through the mods folder would report "no characters"
    /// on a perfectly normal installation that has simply never loaded a mod.
    /// </summary>
    public static IReadOnlyList<UserDataDirectory> FindUserDataDirectories()
    {
        string? gameRoot = GetGameDataRoot();
        if (gameRoot is null || !Directory.Exists(gameRoot))
        {
            return [];
        }

        List<UserDataDirectory> found = [];

        // The layout is <game>\<platform>\<user id>
        foreach (string platformDirectory in Directory.EnumerateDirectories(gameRoot))
        {
            foreach (string userDirectory in Directory.EnumerateDirectories(platformDirectory))
            {
                found.Add(new UserDataDirectory(
                    userDirectory,
                    Path.GetFileName(platformDirectory),
                    Path.GetFileName(userDirectory)));
            }
        }

        return found;
    }

    /// <summary>
    /// Returns candidate mod settings folders, most recently modified first, or empty when none exist.
    /// </summary>
    public static IReadOnlyList<ModConfigLocation> FindModConfigLocations()
    {
        List<ModConfigLocation> found = [];

        foreach (UserDataDirectory user in FindUserDataDirectories())
        {
            string mods = Path.Combine(user.Directory, "mods");
            if (Directory.Exists(mods))
            {
                found.Add(new ModConfigLocation(mods, user.Platform, user.UserId));
            }
        }

        return [.. found.OrderByDescending(x => Directory.GetLastWriteTimeUtc(x.ModsDirectory))];
    }

    /// <summary>
    /// Whether a folder holds the game, judged by its executable rather than its name.
    /// </summary>
    public static bool IsGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return Directory.Exists(directory)
                   && (File.Exists(Path.Combine(directory, "CoreKeeper.exe"))
                       || File.Exists(Path.Combine(directory, "CoreKeeper")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds where Core Keeper is installed by walking the Steam library list and returning
    /// only those where the executable actually exists. Empty when none is found.
    ///
    /// Steam installs a game into whichever library folder the user chose, on any drive, so the
    /// search follows Steam's own record of its libraries rather than guessing at locations.
    /// </summary>
    /// <param name="explicitDirectory">
    /// A folder the user pointed at by hand. It comes first when it really holds the game:
    /// detection exists to save them the trouble, not to overrule them.
    /// </param>
    public static IReadOnlyList<string> FindGameInstallations(string? explicitDirectory = null)
    {
        List<string> found = [];

        // Keyed on the canonical form rather than on the text as typed. A hand-entered folder
        // with a trailing separator is the same place as the one detection finds, and letting
        // both into the list makes every caller treat one game as two: the removal plan lists
        // the same folder twice, counts its files twice, and reports the second deletion as a
        // failure because the first one already removed it.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(string directory)
        {
            string key;
            try
            {
                key = PathSafety.Normalize(directory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException or System.Security.SecurityException)
            {
                // Unresolvable here means it cannot be compared either; IsGameDirectory has
                // already vouched for it, so keep it rather than dropping a real installation.
                key = directory;
            }

            if (seen.Add(key))
            {
                found.Add(directory);
            }
        }

        if (IsGameDirectory(explicitDirectory))
        {
            AddIfNew(explicitDirectory!);
        }

        foreach (string library in EnumerateSteamLibraries())
        {
            string candidate = Path.Combine(library, "steamapps", "common", GameFolder);

            if (IsGameDirectory(candidate))
            {
                AddIfNew(candidate);
            }
        }

        return found;
    }

    /// <summary>Enumerates Steam library folders.</summary>
    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);

        foreach (string steamRoot in EnumerateSteamRoots())
        {
            roots.Add(steamRoot);

            // Steam has written this file in both places over the years, and some installations
            // only have one of them. Reading both costs nothing and the results are merged.
            foreach (string relative in LibraryFolderFiles)
            {
                string vdf = Path.Combine(steamRoot, relative);
                if (!File.Exists(vdf))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(vdf);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Give up on this file only. Letting it escape would abort the whole search
                    // and hide game installations on other drives that are perfectly readable.
                    continue;
                }

                foreach (string path in ParseLibraryPaths(text))
                {
                    roots.Add(path);
                }
            }
        }

        return roots;
    }

    /// <summary>Where Steam has kept its library list, relative to the Steam folder.</summary>
    private static readonly string[] LibraryFolderFiles =
    [
        Path.Combine("steamapps", "libraryfolders.vdf"),
        Path.Combine("config", "libraryfolders.vdf"),
    ];

    /// <summary>
    /// Pulls the library folders out of Steam's libraryfolders.vdf.
    ///
    /// Current Steam writes a "path" entry per library. Older versions instead numbered them,
    /// so that form is read too, but only when no "path" entry was found at all: the current
    /// format also contains numbered entries inside its "apps" blocks, whose values are byte
    /// counts rather than folders.
    /// </summary>
    public static IReadOnlyList<string> ParseLibraryPaths(string vdfText)
    {
        List<string> found = [];

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(vdfText, "\"path\"\\s+\"(?<path>[^\"]+)\""))
        {
            Add(match.Groups["path"].Value);
        }

        if (found.Count == 0)
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(vdfText, "^\\s*\"\\d+\"\\s+\"(?<path>[^\"]+)\"",
                         System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                Add(match.Groups["path"].Value);
            }
        }

        return found;

        void Add(string raw)
        {
            // Separators inside the vdf are escaped twice
            string path = raw.Replace(@"\\", @"\").Trim();

            if (path.Length > 0 && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }
    }

    /// <summary>Candidate locations for the Steam installation itself.</summary>
    private static IEnumerable<string> EnumerateSteamRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        // Where Steam actually is, which is the only reliable source when it was not
        // installed under Program Files. Without this, a Steam on another drive is invisible
        // and the game cannot be found at all.
        foreach (string root in ReadSteamRootsFromRegistry())
        {
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }

        foreach (Environment.SpecialFolder folder in
                 new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string root = Path.Combine(Environment.GetFolderPath(folder), "Steam");
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }
    }

    /// <summary>
    /// Reads Steam's own record of where it is installed.
    /// Returns nothing when Steam is not registered, or when the registry cannot be read.
    /// </summary>
    private static IReadOnlyList<string> ReadSteamRootsFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        List<string> roots = [];

        (string Key, string Value)[] locations =
        [
            (@"Software\Valve\Steam", "SteamPath"),
            (@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (@"SOFTWARE\Valve\Steam", "InstallPath"),
        ];

        foreach ((string key, string valueName) in locations)
        {
            foreach (Microsoft.Win32.RegistryKey hive in
                     new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using Microsoft.Win32.RegistryKey? subKey = hive.OpenSubKey(key);
                    if (subKey?.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
                    {
                        // Steam records this path with forward slashes
                        roots.Add(path.Replace('/', Path.DirectorySeparatorChar));
                    }
                }
                catch (Exception)
                {
                    // An unreadable or absent key just means this candidate does not apply.
                }
            }
        }

        return roots;
    }

    /// <summary>
    /// Returns the game's user data location, or null off Windows, where this layout does not apply.
    /// </summary>
    private static string? GetGameDataRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile))
        {
            return null;
        }

        // LocalLow is not a SpecialFolder, so build the path by hand
        return Path.Combine(userProfile, "AppData", "LocalLow", PublisherFolder, GameFolder);
    }

    /// <summary>
    /// Narrows to a single candidate, throwing with an explanation when there are none, or several and no choice was given.
    /// </summary>
    /// <param name="explicitDirectory">A mods folder given explicitly by the user; null means auto-detect.</param>
    public static ModConfigLocation Resolve(string? explicitDirectory)
    {
        if (!string.IsNullOrEmpty(explicitDirectory))
        {
            if (!Directory.Exists(explicitDirectory))
            {
                throw new ToolException(
                    "error.mods.explicitMissing", [explicitDirectory],
                    $"指定された MOD 設定フォルダが存在しない: {explicitDirectory}");
            }

            // Normalised before it is kept. Windows drops a trailing dot or space when it opens
            // a path, so "…\mods " passed the check above against the real folder - and then
            // every Path.Combine built from the unnormalised string created "mods " beside it
            // and wrote there instead. The run reported success and named a path that looked
            // right, while the real mod folder was never touched.
            return new ModConfigLocation(PathSafety.Normalize(explicitDirectory), "(指定)", "(指定)");
        }

        IReadOnlyList<ModConfigLocation> candidates = FindModConfigLocations();

        if (candidates.Count == 0)
        {
            throw new ToolException(
                "error.mods.notFound", [],
                "Core Keeper の MOD 設定フォルダが見つからない。" + Environment.NewLine +
                "  ゲームを一度起動してワールドに入ると作成される。" + Environment.NewLine +
                "  場所が分かっている場合は --mods-dir で直接指定すること。");
        }

        if (candidates.Count > 1)
        {
            string list = string.Join(
                Environment.NewLine,
                candidates.Select(c => $"    {c.Describe()}  ->  {c.ModsDirectory}"));
            throw new ToolException(
                "error.mods.ambiguous", [candidates.Count],
                $"MOD 設定フォルダの候補が {candidates.Count} 件あり、どれか判断できない。" + Environment.NewLine +
                "  --mods-dir でどれを使うか指定すること。" + Environment.NewLine + list);
        }

        return candidates[0];
    }
}

using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CoreKeeperSkinTool.Install;

/// <summary>How an installation compares with what this build was checked against.</summary>
public enum GameVersionState
{
    /// <summary>The version was read and this build has been checked against it.</summary>
    Supported,

    /// <summary>The version was read and no one has checked this build against it.</summary>
    Unsupported,

    /// <summary>
    /// The version could not be read at all. Not the same as unsupported: the installation may
    /// be perfectly fine and only the way this reads the version has stopped working.
    /// </summary>
    Unknown,
}

/// <summary>What was found in one game folder.</summary>
/// <param name="State">How it compares with the supported list.</param>
/// <param name="Version">The version read, or null when it could not be read.</param>
/// <param name="Supported">The versions this build has been checked against.</param>
public sealed record GameVersionCheck(
    GameVersionState State, string? Version, IReadOnlyList<string> Supported);

/// <summary>
/// Reads the installed game's version and says whether this build has been checked against it.
///
/// Unity stores the version the game reports as <c>Application.version</c> in
/// <c>&lt;game&gt;\CoreKeeper_Data\globalgamemanagers</c>, alongside the company and product
/// names. It is read from there because the tool has to answer this while the game is closed,
/// and because the executable's own version resource holds the Unity version rather than the
/// game's.
/// </summary>
public static class GameVersion
{
    private const string ResourceName = "supported-versions.json";

    /// <summary>
    /// File holding the version, relative to the game folder.
    /// Built with the platform separator rather than written with a backslash, which elsewhere
    /// would be an ordinary filename character and would never resolve.
    /// </summary>
    private static readonly string VersionFile = Path.Combine("CoreKeeper_Data", "globalgamemanagers");

    /// <summary>
    /// How much of that file to read.
    ///
    /// The company name, product name and version all sit in the first couple of kilobytes,
    /// well inside this, while the file itself runs to several megabytes.
    /// </summary>
    private const int HeaderBytes = 64 * 1024;

    /// <summary>
    /// Product name written just before the version, used to anchor the search.
    ///
    /// Without an anchor the first version-shaped string in the file is Unity's own, which sits
    /// at the very start and would be reported as the game's version.
    /// </summary>
    private static readonly string[] Anchors = ["Core Keeper", "Pugstorm"];

    /// <summary>
    /// How far past the anchor to look. In the real file the version sits about 550 bytes after
    /// the product name; this leaves room for the layout to shift without letting the search
    /// wander into unrelated parts of the file.
    /// </summary>
    private const int SearchWindowBytes = 4096;

    private static IReadOnlyList<string>? _supported;

    /// <summary>The versions this build has been checked against, as major.minor.patch.</summary>
    public static IReadOnlyList<string> SupportedVersions => _supported ??= LoadSupported();

    private static IReadOnlyList<string> LoadSupported()
    {
        using Stream? stream = typeof(GameVersion).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new ToolException(
                $"対応バージョン表 ({ResourceName}) が見つからない。配布物が壊れている可能性がある。");
        }

        using JsonDocument document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("supported", out JsonElement supported)
            || supported.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException($"対応バージョン表 ({ResourceName}) の形式が不正。");
        }

        return [.. supported.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())];
    }

    /// <summary>Checks one game folder.</summary>
    public static GameVersionCheck Check(string? gameDirectory)
    {
        string? version = Read(gameDirectory);

        GameVersionState state = version is null
            ? GameVersionState.Unknown
            : IsSupported(version) ? GameVersionState.Supported : GameVersionState.Unsupported;

        return new GameVersionCheck(state, version, SupportedVersions);
    }

    /// <summary>
    /// The supported versions as they are shown to the user, at the granularity they are matched
    /// on: "1.3.0.1" in the list stands for every 1.3.0 build, so it is shown as "1.3.0.x". Shown as
    /// written, the warning read as if 1.3.0.2 and 1.3.0.3 were outside the supported range.
    /// </summary>
    public static string DescribeSupported(IEnumerable<string> supported) =>
        string.Join(", ", supported
            .Select(s => MajorMinorPatch(s) is { } family ? family + ".x" : s)
            .Distinct(StringComparer.Ordinal));

    /// <summary>Whether a version string matches one of the supported ones.</summary>
    public static bool IsSupported(string? version)
    {
        string? key = MajorMinorPatch(version);

        return key is not null
               && SupportedVersions.Any(s => string.Equals(MajorMinorPatch(s), key, StringComparison.Ordinal));
    }

    /// <summary>
    /// The leading three numbers of a version, which is the granularity the game itself keys on.
    /// Null when the string does not start with three numbers.
    /// </summary>
    public static string? MajorMinorPatch(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(version.Trim(), @"^(\d+)\.(\d+)\.(\d+)");

        return match.Success
            ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}"
            : null;
    }

    /// <summary>
    /// Reads the version out of a game folder, or null when it cannot be read.
    ///
    /// Never throws: a folder that turns out not to hold the game, or that cannot be read, is
    /// an answer of "unknown" rather than a failure.
    /// </summary>
    public static string? Read(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        byte[] header;
        try
        {
            string path = Path.Combine(gameDirectory, VersionFile);
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            header = new byte[(int)Math.Min(HeaderBytes, stream.Length)];

            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }

        return FindVersion(Encoding.ASCII.GetString(header));
    }

    /// <summary>
    /// Pulls the version out of the file header. Exposed so the parsing can be tested without
    /// a copy of the game.
    /// </summary>
    public static string? FindVersion(string header)
    {
        foreach (string anchor in Anchors)
        {
            int start = header.IndexOf(anchor, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            // Bounded rather than searching the rest of the file. Unity writes several other
            // version fields into the same block - in the real file two of them sit between the
            // product name and the one wanted - and they are currently skipped only because they
            // happen to be two-component strings such as "1.0". Taking the first match at any
            // distance would silently return one of those the day a build carries "1.0.0.0",
            // and a plausible wrong version is worse than none: it reads as "your game is
            // unsupported" rather than "the version could not be determined".
            int from = start + anchor.Length;
            string window = header[from..Math.Min(header.Length, from + SearchWindowBytes)];

            System.Text.RegularExpressions.MatchCollection matches =
                System.Text.RegularExpressions.Regex.Matches(
                    window, @"\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z]+)?");

            if (matches.Count == 0)
            {
                continue;
            }

            // Scored rather than taken in order. Unity's own fields in this block look like
            // "1.0" and "1.0.0.0", while the game writes "1.2.1.5-8be0", so a build suffix is
            // the one unmistakable marker; a fourth component is weak evidence because Unity's
            // defaults have one too. Position breaks the tie, because the game's own field is
            // written after Unity's in the layout observed in the real file.
            string? best = null;
            int bestScore = -1;

            foreach (System.Text.RegularExpressions.Match candidate in matches)
            {
                // A match touching the end of the window may have been cut in half by it, and
                // half a version number is worse than none: "1.2.10-abcd" sliced at the boundary
                // read as "1.2.1", which is a supported version, so an unchecked build was
                // certified as checked. Anything reaching the edge is discarded.
                if (candidate.Index + candidate.Length == window.Length
                    && from + SearchWindowBytes < header.Length)
                {
                    continue;
                }

                int score = (candidate.Groups[2].Success ? 4 : 0)
                            + (candidate.Groups[1].Success ? 1 : 0);

                // Strictly greater, so a tie keeps the earlier match. Taking the later one meant
                // that with no build suffix to tell them apart, an unrelated number further down
                // the block beat the game's own field.
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate.Value;
                }
            }

            // Nothing usable under this anchor - every match was cut off by the window. Fall
            // through to the next anchor rather than giving up: stopping at the first anchor
            // that matched anything at all was how an unrelated number became the answer.
            if (best is null)
            {
                continue;
            }

            return best;
        }

        return null;
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Install;

/// <summary>Why the game did not take the mod, as its log tells it.</summary>
public enum ModLoadFailureKind
{
    /// <summary>The game's code check refused the compiled mod.</summary>
    CodeSecurity,

    /// <summary>The game could not compile the mod's source against itself.</summary>
    Compile,

    /// <summary>The loader gave up on the mod for another reason, such as a file it could not read.</summary>
    LoadError,

    /// <summary>The mod's asset bundle could not be loaded, which takes the whole mod down with it.</summary>
    AssetBundle,

    /// <summary>
    /// The mod was loaded, but its Harmony patches could not be put in place. The game then never
    /// calls into it when it rebuilds a look, so the replacement may not happen at all.
    /// </summary>
    Patch,

    /// <summary>The loader could not read the mod's manifest, so it never saw the mod at all.</summary>
    Manifest,
}

/// <summary>A failure to load the mod, found in the game's log.</summary>
/// <param name="Kind">What went wrong.</param>
/// <param name="Reason">The loader's own name for the error, such as CompileFailed, when it gave one.</param>
/// <param name="Summary">
/// The one detail worth putting beside the kind: the namespace or type the code check refused,
/// the first compiler error, or the loader's reason. Null when the log said nothing more.
/// </param>
/// <param name="Details">Lines from the log that say more, trimmed to what a reader needs.</param>
public sealed record ModLoadFailure(
    ModLoadFailureKind Kind, string? Reason, string? Summary, IReadOnlyList<string> Details);

/// <summary>What one start of the game wrote about itself and about the mod.</summary>
/// <param name="StartedUtc">When the game started, from Unity's own header; null when it was not there.</param>
/// <param name="GameVersion">The game's version as the header gives it, such as 1.3.0.2-182b.</param>
/// <param name="GameExecutable">The executable that was started, which says which installation this was.</param>
/// <param name="ModDirectory">Where the loader found the mod, when it said.</param>
/// <param name="Failure">What went wrong with the mod on its last attempt, or null when nothing was reported.</param>
public sealed record GameLogRun(
    DateTime? StartedUtc,
    string? GameVersion,
    string? GameExecutable,
    string? ModDirectory,
    ModLoadFailure? Failure);

/// <summary>What the game's log can say about the mod installed in it now.</summary>
public enum ModLoadState
{
    /// <summary>The mod is not in the game, so there is nothing to ask the log about.</summary>
    NotInstalled,

    /// <summary>There is no log, or it could not be read.</summary>
    NoLog,

    /// <summary>
    /// The log does not say enough to tie it to what is installed now: no start time, or a mod
    /// folder whose times could not be read.
    /// </summary>
    Undetermined,

    /// <summary>The last start was of another copy of the game.</summary>
    OtherInstallation,

    /// <summary>The mod or the game has changed since that start, so it says nothing about now.</summary>
    Outdated,

    /// <summary>Nothing in the log says the mod failed.</summary>
    NoProblem,

    /// <summary>The last start of the game failed to load the mod that is installed now.</summary>
    Failed,
}

/// <summary>The answer to "did the game take the mod the last time it started".</summary>
/// <param name="State">What the log says, or why it cannot be asked.</param>
/// <param name="LogPath">The log that was read, when there was one to look for.</param>
/// <param name="Run">What was read from it, when it could be read.</param>
/// <param name="Problem">Why the log could not be read, when that is the answer.</param>
public sealed record ModLoadVerdict(
    ModLoadState State, string? LogPath, GameLogRun? Run, string? Problem = null)
{
    /// <summary>The failure to report, present only when the state is <see cref="ModLoadState.Failed"/>.</summary>
    public ModLoadFailure? Failure => State == ModLoadState.Failed ? Run?.Failure : null;
}

/// <summary>
/// Reads the game's own log to tell whether it managed to load the mod.
///
/// The game compiles the mod's source when it starts and checks the result against its own list
/// of forbidden namespaces and types. A game update can make either step fail, and then nothing
/// on this side notices: the files are in place, so the window said "installed" while the game
/// had refused the mod and was drawing every character as it always does. The game's log is the
/// one place that records it, so it is read here.
///
/// The lines looked for are the ones the game's mod loader writes (PugMod.Loader, SideLoader,
/// RoslynCSharp and Trivial.CodeSecurity in 1.3.0.2). Only failures are looked for. A later version
/// that words them differently makes this report nothing, which is where it was before it existed,
/// rather than report a failure that did not happen.
/// </summary>
public static class GameLog
{
    /// <summary>
    /// How much of the log is read.
    ///
    /// The loader runs in the first seconds of a start - within the first two hundred lines of the
    /// real logs measured - while a long session writes megabytes after it. Reading the whole of
    /// one each time the window comes back to the front would cost more than it could ever find.
    /// </summary>
    internal const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>How many lines of detail are kept. The first few say what happened; the rest repeat it.</summary>
    private const int MaxDetails = 5;

    /// <summary>
    /// A compiler error as Roslyn writes it: the file, the position, the code and the message.
    /// The loader logs every one it gets back.
    /// </summary>
    private static readonly Regex CompileError = new(
        @"^(?<path>.+)\((?<line>\d+),(?<column>\d+)\): error (?<code>CS\d+): (?<message>.+)$",
        RegexOptions.Compiled);

    /// <summary>A game version such as 1.3.0.2-182b: numbers, then the build after a hyphen.</summary>
    private static readonly Regex VersionPattern = new(
        @"^(?<numbers>\d+(?:\.\d+)*)(?:-(?<build>[0-9A-Za-z]+))?", RegexOptions.Compiled);

    /// <summary>
    /// Whether the last start of the game failed to load the mod that is installed in it now.
    ///
    /// Never throws. This is advice added to a screen that works without it, and a log that cannot
    /// be read is an answer of "cannot tell", not a reason to stop.
    /// </summary>
    /// <param name="gamePath">The installation the mod is in.</param>
    /// <param name="installed">The mod as found in that installation.</param>
    public static ModLoadVerdict CheckLastRun(string gamePath, InstalledModInfo installed) =>
        CheckLastRun(gamePath, installed, GameLocator.FindPlayerLog());

    /// <inheritdoc cref="CheckLastRun(string, InstalledModInfo)"/>
    /// <param name="gamePath">The installation the mod is in.</param>
    /// <param name="installed">The mod as found in that installation.</param>
    /// <param name="logPath">The game's log, or null when there is nowhere to look for one.</param>
    internal static ModLoadVerdict CheckLastRun(string gamePath, InstalledModInfo installed, string? logPath)
    {
        if (installed.Path is not { } modDirectory)
        {
            return new ModLoadVerdict(ModLoadState.NotInstalled, logPath, null);
        }

        if (logPath is null)
        {
            return new ModLoadVerdict(ModLoadState.NoLog, null, null);
        }

        // The loader names the mod by the manifest's name, not by its folder. The installer makes
        // the two the same, so the folder stands in when the manifest could not be read.
        string modName = string.IsNullOrWhiteSpace(installed.Name) ? Path.GetFileName(modDirectory) : installed.Name;

        (GameLogRun? run, string? problem) = Read(logPath, modName);

        return Evaluate(
            run, logPath, problem, gamePath, modDirectory, LastChangedUtc(modDirectory), GameVersion.Read(gamePath));
    }

    /// <summary>
    /// Decides what a start of the game says about the mod installed now.
    /// Kept apart from the file system so every rule can be tested with the values it turns on.
    /// </summary>
    /// <param name="run">What was read from the log, or null when nothing could be.</param>
    /// <param name="logPath">Where the log is.</param>
    /// <param name="problem">Why the log could not be read, when it could not.</param>
    /// <param name="gamePath">The installation being asked about.</param>
    /// <param name="modDirectory">Where the mod is installed in it.</param>
    /// <param name="modChangedUtc">When the mod's folder last changed; null when that could not be read.</param>
    /// <param name="gameVersion">The installation's version as it reads now; null when it cannot be read.</param>
    internal static ModLoadVerdict Evaluate(
        GameLogRun? run,
        string logPath,
        string? problem,
        string gamePath,
        string modDirectory,
        DateTime? modChangedUtc,
        string? gameVersion)
    {
        if (run is null)
        {
            return new ModLoadVerdict(ModLoadState.NoLog, logPath, null, problem);
        }

        // Without the start time nothing ties this run to the mod on disk, and a failure from a
        // copy that has since been replaced would be reported against the replacement.
        if (run.StartedUtc is not { } started)
        {
            return new ModLoadVerdict(ModLoadState.Undetermined, logPath, run);
        }

        // The log sits beside the per-user data, named after the company and the product, so
        // every copy of the game writes the same file. A second copy - one pointed at by hand,
        // say - leaves a log that says nothing about this one.
        string? executableDirectory = DirectoryOf(run.GameExecutable);
        if ((executableDirectory is not null && !SamePlace(executableDirectory, gamePath))
            || (run.ModDirectory is not null && !SamePlace(run.ModDirectory, modDirectory)))
        {
            return new ModLoadVerdict(ModLoadState.OtherInstallation, logPath, run);
        }

        if (modChangedUtc is not { } changed)
        {
            return new ModLoadVerdict(ModLoadState.Undetermined, logPath, run);
        }

        // Reinstalled after that start: the game ran the copy before it, and the one there now
        // has not been tried. The usual way here is "Update mod" pressed to fix the very failure
        // the log records, and repeating that failure then would say the fix had not worked.
        if (changed > started)
        {
            return new ModLoadVerdict(ModLoadState.Outdated, logPath, run);
        }

        // Updated since then. What the old version refused, the new one may take, and the other
        // way round; only a start of this version can say.
        if (gameVersion is not null
            && run.GameVersion is not null
            && AreDifferentBuilds(gameVersion, run.GameVersion))
        {
            return new ModLoadVerdict(ModLoadState.Outdated, logPath, run);
        }

        return new ModLoadVerdict(
            run.Failure is null ? ModLoadState.NoProblem : ModLoadState.Failed, logPath, run);
    }

    /// <summary>
    /// Reads the log at a path. The run is null when there is no log or it cannot be read, and
    /// the problem then says why, unless the file simply is not there.
    /// </summary>
    /// <param name="path">The log.</param>
    /// <param name="modName">The mod's name as its manifest gives it.</param>
    public static (GameLogRun? Run, string? Problem) Read(string path, string modName)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }

            // Shared both ways. The game holds the file open for writing for as long as it runs,
            // and it must not be kept from writing to it - nor from moving it aside to
            // Player-prev.log when it next starts - by a window that is only reading.
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            return (Parse(ReadLines(reader, stream), modName), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            // Handed back rather than thrown: see CheckLastRun
            return (null, ex.Message);
        }
    }

    /// <summary>The lines of the log, up to <see cref="MaxBytes"/>.</summary>
    private static IEnumerable<string> ReadLines(StreamReader reader, Stream stream)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;

            // The reader fills its buffer ahead of the line it hands out, so this stops up to one
            // buffer past the limit. The limit only has to keep a long session from being read
            // through, and that it still does.
            if (stream.Position > MaxBytes)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Reads one start of the game out of its log.
    /// </summary>
    /// <param name="lines">The log, line by line.</param>
    /// <param name="modName">The mod's name as its manifest gives it, which is how the loader names it.</param>
    public static GameLogRun Parse(IEnumerable<string> lines, string modName)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(modName);

        DateTime? started = null;
        string? version = null;
        string? executable = null;
        string? modDirectory = null;

        FailureBuilder? failure = null;

        // Whether the compile under way is this mod's. The loader's "got null" says nothing about
        // which mod it is about, so it is placed by what came before it.
        bool compilingThisMod = false;

        // Whose code check failed last. Its report comes as a list of lines after the summary,
        // and those name what was refused but not which assembly refused it.
        string? refusedAssembly = null;

        // The loader names a type it will not let a mod patch on the line before it says the
        // patching failed, without saying for which mod.
        string? disallowedType = null;

        // The exception that stopped the patching is logged on the line after the loader says so.
        bool patchExceptionNext = false;

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (patchExceptionNext)
            {
                patchExceptionNext = false;
                failure?.Add(line.Trim());
                failure?.Summarise(line);
                continue;
            }

            // Unity's own header. Only the first of each counts: the header comes before anything
            // else, and a mod repeating the words later must not move the start of the run.
            if (TryValue(line, "Time (UTC):", out string time))
            {
                started ??= ParseTime(time);
                continue;
            }

            if (TryValue(line, "Game version:", out string gameVersion))
            {
                version ??= gameVersion;
                continue;
            }

            if (TryValue(line, "CommandLineArgs.args[0]:", out string argument))
            {
                executable ??= argument;
                continue;
            }

            // The side loader, which scans StreamingAssets\Mods as the game starts
            if (line.StartsWith($"loaded mod {modName} at ", StringComparison.Ordinal))
            {
                modDirectory = line[$"loaded mod {modName} at ".Length..].Trim();
                continue;
            }

            if (TryValue(line, "failed to load mod:", out string unreadable)
                && string.Equals(LastSegment(unreadable), modName, StringComparison.OrdinalIgnoreCase))
            {
                modDirectory = unreadable;
                failure = new FailureBuilder(ModLoadFailureKind.Manifest);
                continue;
            }

            // A compile starting. Every attempt on this mod starts afresh, so a load that failed
            // and was then retried successfully is not reported, and the other way round.
            if (TryValue(line, "Creating modified script files at", out string scripts))
            {
                compilingThisMod = IsLoaderFolderOf(scripts, modName);
                refusedAssembly = null;

                if (compilingThisMod)
                {
                    failure = null;
                }

                continue;
            }

            if (TryRefusedAssembly(line, out string assembly))
            {
                refusedAssembly = assembly;

                if (string.Equals(assembly, modName, StringComparison.Ordinal))
                {
                    failure = new FailureBuilder(ModLoadFailureKind.CodeSecurity);
                }

                continue;
            }

            // The code check's report: one line per namespace, type or member it refused, each
            // followed by a tab-indented line per place it was used, which are left out.
            if ((line.StartsWith("Illegal ", StringComparison.Ordinal)
                 || line.StartsWith("Indirect illegal ", StringComparison.Ordinal))
                && string.Equals(refusedAssembly, modName, StringComparison.Ordinal))
            {
                failure?.Add(line);
                failure?.Summarise(AfterLastColon(line));
                continue;
            }

            Match compileError = CompileError.Match(line);
            if (compileError.Success && IsInLoaderFolderOf(compileError.Groups["path"].Value, modName))
            {
                failure ??= new FailureBuilder(ModLoadFailureKind.Compile);

                string file = LastSegment(compileError.Groups["path"].Value);
                string code = compileError.Groups["code"].Value;
                string message = compileError.Groups["message"].Value;

                failure.Add($"{file}({compileError.Groups["line"].Value},{compileError.Groups["column"].Value}): error {code}: {message}");
                failure.Summarise($"{code}: {message}");
                continue;
            }

            // Written for a refused mod and for one that did not compile alike, without a name
            if (line == "failed to compile mod, got null" && compilingThisMod)
            {
                failure ??= new FailureBuilder(ModLoadFailureKind.Compile);
                continue;
            }

            if (line == $"Failed to do source gen patch for {modName}")
            {
                failure ??= new FailureBuilder(ModLoadFailureKind.LoadError);
                continue;
            }

            // The loader's verdict on the scripts. It comes last, after whatever explained it, so
            // it names the reason without replacing the kind already found.
            if (TryValue(line, $"mod {modName} load error:", out string reason))
            {
                failure ??= new FailureBuilder(ModLoadFailureKind.LoadError);
                failure.Reason = reason;
                failure.Summarise(reason);
                compilingThisMod = false;
                refusedAssembly = null;
                continue;
            }

            if (line.StartsWith($"Successfully compiled {modName} ", StringComparison.Ordinal))
            {
                compilingThisMod = false;
                refusedAssembly = null;
                continue;
            }

            if (line.StartsWith("Trying to patch disallowed type ", StringComparison.Ordinal))
            {
                disallowedType = line;
                continue;
            }

            if (line == $"failed to patch mod {modName}, got exception")
            {
                failure ??= new FailureBuilder(ModLoadFailureKind.Patch);
                patchExceptionNext = true;
                continue;
            }

            if (line == $"mod {modName}: patching failed")
            {
                failure ??= new FailureBuilder(ModLoadFailureKind.Patch);

                if (disallowedType is not null)
                {
                    failure.Add(disallowedType);
                    failure.Summarise(disallowedType);
                }

                continue;
            }

            // Unlike a patch that did not take, this unloads the whole mod, so it replaces one
            if (line == $"failed to load assetbundle from mod {modName}")
            {
                failure = new FailureBuilder(ModLoadFailureKind.AssetBundle);
                failure.Add(line);
            }
        }

        return new GameLogRun(started, version, executable, modDirectory, failure?.Build());
    }

    /// <summary>
    /// When the mod's folder last changed, as near as the file system says: the latest creation or
    /// write time of the folder and of everything in it. Null when the folder cannot be read.
    ///
    /// Both times, because the installer extracts from a zip, and extracting sets a file's write
    /// time to the one stored in the archive - the time the mod was built, weeks before the
    /// install on the installed copy measured - while its creation time is when it was written.
    /// The latest of them all errs towards "changed since that start", which only ever holds a
    /// warning back; erring the other way would pin an old copy's failure on a fresh install.
    /// </summary>
    internal static DateTime? LastChangedUtc(string directory)
    {
        try
        {
            DirectoryInfo folder = new(directory);
            if (!folder.Exists)
            {
                return null;
            }

            DateTime latest = Later(folder.CreationTimeUtc, folder.LastWriteTimeUtc);

            foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                latest = Later(latest, Later(entry.CreationTimeUtc, entry.LastWriteTimeUtc));
            }

            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Something that changes whenever the log does, so a caller can skip reading it again when
    /// it has not. Empty when there is no log.
    /// </summary>
    public static string Fingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            FileInfo file = new(path);
            return file.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{file.Length}:{file.LastWriteTimeUtc.Ticks}")
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// A start time as the user reads it: local time, to the minute, in the Gregorian calendar.
    ///
    /// The calendar is fixed rather than taken from the user's settings. A Windows set to the
    /// Japanese calendar formats "yyyy" as the year of the era, so 2026 came out as 08.
    /// </summary>
    public static string DescribeTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ Helpers

    /// <summary>The value after a label such as "Game version:", when the line starts with it.</summary>
    private static bool TryValue(string line, string label, out string value)
    {
        if (line.StartsWith(label, StringComparison.Ordinal))
        {
            value = line[label.Length..].Trim();
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Unity writes the start as 2026-09-28T01:11:55Z.</summary>
    private static DateTime? ParseTime(string text) =>
        DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    /// <summary>
    /// The assembly a failed code check names, from
    /// "Assembly 'Name, Version=…' has failed code security verification. …".
    /// </summary>
    private static bool TryRefusedAssembly(string line, out string assembly)
    {
        const string Prefix = "Assembly '";
        const string Verdict = "' has failed code security verification";

        int end = line.IndexOf(Verdict, StringComparison.Ordinal);
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || end < Prefix.Length)
        {
            assembly = string.Empty;
            return false;
        }

        // The full name carries the version and the key after a comma; the loader names the
        // assembly after the mod, so the simple name is what identifies it.
        string fullName = line[Prefix.Length..end];
        int comma = fullName.IndexOf(',');
        assembly = (comma < 0 ? fullName : fullName[..comma]).Trim();
        return assembly.Length > 0;
    }

    /// <summary>
    /// Whether a folder is the loader's working copy of this mod,
    /// <c>%TEMP%\Pugstorm\Core Keeper\ModLoader\&lt;name&gt;</c>. Written with both separators.
    /// </summary>
    private static bool IsLoaderFolderOf(string path, string modName)
    {
        string[] segments = Segments(path);
        return segments.Length >= 2
               && string.Equals(segments[^1], modName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(segments[^2], "ModLoader", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a file sits somewhere inside the loader's working copy of this mod.</summary>
    private static bool IsInLoaderFolderOf(string path, string modName)
    {
        string[] segments = Segments(path);

        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (string.Equals(segments[i], "ModLoader", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], modName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Segments(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The last part of a path written with either separator, as the loader mixes them.</summary>
    private static string LastSegment(string path) =>
        Segments(path) is { Length: > 0 } segments ? segments[^1] : string.Empty;

    /// <summary>What the code check refused, from "Illegal reference to disallowed namespace: System.Reflection".</summary>
    private static string AfterLastColon(string line)
    {
        int colon = line.LastIndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? line : line[(colon + 2)..].Trim();
    }

    private static string? DirectoryOf(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        try
        {
            // Unity writes args[0] as it was typed. Started from cmd or a batch file by its bare
            // name it is "CoreKeeper.exe" or ".\CoreKeeper.exe", which says nothing about which
            // folder it was: read as a folder anyway, it never matched the game, and a real
            // failure of the mod in that run was put down to another installation and never
            // reported. Not knowing leaves the decision to where the mod was found.
            string path = executable.Trim().Trim('"');
            return Path.IsPathFullyQualified(path) ? Path.GetDirectoryName(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two paths name the same place however each is written. The loader writes forward
    /// slashes and backslashes in the same path, and a Steam library reached through a link is
    /// still the same library.
    /// </summary>
    private static bool SamePlace(string a, string b)
    {
        try
        {
            return string.Equals(PathSafety.Normalize(a), PathSafety.Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or PathTooLongException or System.Security.SecurityException)
        {
            // Not resolvable here, so compared as written, with the separators made alike
            return string.Equals(
                string.Join('\\', Segments(a)), string.Join('\\', Segments(b)), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// Whether two versions name different builds, going only by what both of them spell out.
    ///
    /// They come from two places - the log's header and the game's own files - and were the same
    /// string on every build measured. Were one of the readers to return less of it one day, the
    /// numbers without the build say, an exact comparison would take every start for one of
    /// another build and hide every failure from then on. So only a number that differs, or two
    /// builds both given and different, count; a part only one side gives is no evidence either way.
    /// </summary>
    private static bool AreDifferentBuilds(string a, string b)
    {
        Match left = VersionPattern.Match(a.Trim());
        Match right = VersionPattern.Match(b.Trim());

        if (!left.Success || !right.Success)
        {
            return !string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        string[] leftNumbers = left.Groups["numbers"].Value.Split('.');
        string[] rightNumbers = right.Groups["numbers"].Value.Split('.');

        for (int i = 0; i < Math.Min(leftNumbers.Length, rightNumbers.Length); i++)
        {
            // Compared as digits rather than parsed, so no number is too long to compare; "02" and
            // "2" are the same number
            if (!string.Equals(leftNumbers[i].TrimStart('0'), rightNumbers[i].TrimStart('0'), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return left.Groups["build"].Success
               && right.Groups["build"].Success
               && !string.Equals(left.Groups["build"].Value, right.Groups["build"].Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gathers one failure while the log is read.</summary>
    private sealed class FailureBuilder(ModLoadFailureKind kind)
    {
        private readonly List<string> _details = [];
        private string? _summary;

        public ModLoadFailureKind Kind { get; } = kind;

        public string? Reason { get; set; }

        /// <summary>Keeps a line of detail, once, up to the limit.</summary>
        public void Add(string detail)
        {
            if (_details.Count < MaxDetails && !_details.Contains(detail, StringComparer.Ordinal))
            {
                _details.Add(detail);
            }
        }

        /// <summary>Takes the first thing offered as the summary; later ones only repeat or follow from it.</summary>
        public void Summarise(string text)
        {
            if (_summary is null && !string.IsNullOrWhiteSpace(text))
            {
                _summary = text.Trim();
            }
        }

        public ModLoadFailure Build() => new(Kind, Reason, _summary, [.. _details]);
    }
}

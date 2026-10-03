using System.Text.Json;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Localization;

/// <summary>Everything remembered between launches, held in one file.</summary>
/// <param name="Language">Chosen interface language.</param>
/// <param name="GamePath">
/// Game folder the user pointed at by hand, or null to detect it automatically.
/// </param>
/// <param name="DisclaimerAcceptedVersion">
/// Version of the application whose terms the user accepted, or null if they never have.
/// The version is kept rather than a plain flag so that a future release which changes what is
/// being agreed to can ask again instead of relying on an answer given to different wording.
/// </param>
internal sealed record StoredSettings(
    string? Language, string? GamePath, string? DisclaimerAcceptedVersion);

/// <summary>
/// Reads and writes the settings file.
///
/// Exceptions never escape. A settings file that cannot be read or written must not stop the
/// application from starting or from working with its defaults.
/// </summary>
internal static class SettingsFile
{
    private const string FolderName = "CoreKeeperSkinTool";
    private const string FileName = "settings.json";

    /// <summary>
    /// Environment variable that redirects the settings folder.
    ///
    /// This exists so the test run does not read and rewrite the real settings of whoever is
    /// running it: the tests switch language repeatedly, and without a redirect they would
    /// leave the installed application in whatever language the last test happened to set.
    /// </summary>
    public const string DirectoryOverrideVariable = "CKS_SETTINGS_DIR";

    /// <summary>
    /// Guards the read-modify-write each setter performs.
    ///
    /// Every setter reads the whole record, changes one field and writes it all back. Two of
    /// those interleaving would let the second write undo the first, and the settings are
    /// written from the start-up notices, the main window and the language switch alike.
    /// </summary>
    private static readonly object Gate = new();

    public static StoredSettings Read()
    {
        lock (Gate)
        {
            return ReadUnlocked();
        }
    }

    private static StoredSettings ReadUnlocked() => ReadUnlocked(out _);

    /// <param name="readable">
    /// False when a settings file exists but could not be read. Distinct from "no file yet",
    /// which is the ordinary first run.
    /// </param>
    private static StoredSettings ReadUnlocked(out bool readable)
    {
        readable = true;

        try
        {
            string path = GetPath();
            if (!File.Exists(path))
            {
                return new StoredSettings(null, null, null);
            }

            using FileStream stream = File.OpenRead(path);
            StoredSettings? stored = JsonSerializer.Deserialize<StoredSettings>(stream);

            readable = stored is not null;
            return stored ?? new StoredSettings(null, null, null);
        }
        catch (Exception)
        {
            // A corrupt settings file must not prevent startup
            readable = false;
            return new StoredSettings(null, null, null);
        }
    }

    /// <summary>Replaces the whole file. Returns whether the write landed.</summary>
    public static bool Write(StoredSettings settings)
    {
        lock (Gate)
        {
            return WriteUnlocked(settings);
        }
    }

    /// <summary>
    /// Applies one change to the stored settings without losing the others.
    ///
    /// The read and the write are one operation. Doing them separately meant a reader could see
    /// the file mid-write - it used to be truncated in place - fall back to all-null, and then
    /// write those nulls over the fields it was not changing, losing the accepted terms and the
    /// chosen language at once.
    /// </summary>
    /// <returns>Whether the change reached the file on disk.</returns>
    public static bool Update(Func<StoredSettings, StoredSettings> change)
    {
        lock (Gate)
        {
            StoredSettings current = ReadUnlocked(out bool readable);

            // A file that cannot be read is read as "everything unset", and writing that back
            // turns one damaged property into the loss of every other setting. Refusing to write
            // at all is worse still: choosing the game folder is how the user recovers when
            // detection finds nothing, and a settings file that cannot be read made that button
            // do nothing at all, with no way out and nothing said.
            //
            // So the damaged file is put aside and the write goes ahead. Nothing is lost that
            // was not already unreadable, the copy stays on disk under .bad for anyone who wants
            // to look at it, and the application can be used again.
            if (!readable)
            {
                SetAsideUnreadable();
            }

            return WriteUnlocked(change(current));
        }
    }

    /// <summary>
    /// Renames a settings file that could not be read, so the next write starts from nothing
    /// rather than fighting it. Best effort: if it cannot be moved, the write will overwrite it.
    /// </summary>
    private static void SetAsideUnreadable()
    {
        try
        {
            string path = GetPath();
            if (!File.Exists(path))
            {
                return;
            }

            string damaged = path + ".bad";
            PathSafety.DeleteFile(damaged);
            File.Move(path, damaged);
        }
        catch (Exception)
        {
            // Nothing more to do. The write that follows replaces the file either way.
        }
    }

    /// <summary>
    /// Writes the file. Returns whether it landed.
    ///
    /// The exception is still swallowed - a save that cannot happen must not stop the application,
    /// and the choice being made still applies for this session. But a caller that depends on the
    /// value being on disk next time it is read has to be able to tell. Choosing the game folder
    /// is exactly that: the startup gate re-reads the file, so a write that never landed sent the
    /// user back to the same fatal screen with nothing said and no way on.
    /// </summary>
    private static bool WriteUnlocked(StoredSettings settings)
    {
        try
        {
            string path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written beside the target and swapped in, so the file on disk is always either the
            // old settings or the new ones. Writing in place truncated it first, leaving a window
            // in which any reader - including this process after a crash - saw an empty file.
            //
            // The name carries the process id. A fixed name collides: a folder of that name, or a
            // copy held open by a second instance, made every save fail from then on and left the
            // stray behind, and neither the user nor this code had any way to tell what it was.
            string staging = $"{path}.{Environment.ProcessId}.new";

            try
            {
                // A folder in the way is cleared rather than fought with
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }

                using (FileStream stream = File.Create(staging))
                {
                    JsonSerializer.Serialize(stream, settings);
                }

                try
                {
                    File.Move(staging, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The destination itself is in the way, and the file being unreadable is not
                    // the only way that happens: a read-only settings.json, or a folder of that
                    // name, both let every read succeed and every write fail, silently and for
                    // good. Choosing the game folder is the way out when detection finds nothing,
                    // so a save that can never land takes the last way out with it - the same
                    // dead end A-37 was about, reached by a different road.
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        PathSafety.ClearReadOnly(path);
                    }

                    File.Move(staging, path, overwrite: true);
                }
            }
            catch
            {
                // Take the half-finished file away rather than leaving settings.json.new sitting
                // there for good. Under portable.txt that folder is the release folder the user
                // actually looks at, and nothing else ever cleans it up.
                try
                {
                    PathSafety.DeleteFile(staging);
                }
                catch (Exception)
                {
                    // Cleanup only; the original failure is handled below.
                }

                throw;
            }

            return true;
        }
        catch (Exception)
        {
            // The current choice still applies even if it cannot be saved, so carry on
            return false;
        }
    }

    /// <summary>
    /// Where settings are stored.
    ///
    /// When portable.txt sits next to the executable, settings are stored in that folder.
    /// Deleting the release folder then leaves no settings behind, which makes removal simple.
    /// Otherwise they go to the user's application data folder as usual.
    /// </summary>
    internal static string GetPath()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (overrideDirectory is not null)
        {
            // Set but unusable is not the same as not set. Falling through on a blank value sent
            // the write to the real settings file of whoever was running the tests - silently,
            // and precisely when the redirect that was meant to prevent that had been mistyped.
            if (string.IsNullOrWhiteSpace(overrideDirectory))
            {
                throw new InvalidOperationException(
                    $"{DirectoryOverrideVariable} が空白のみに設定されている。" +
                    "設定の保存先を指定するか、変数自体を削除すること。");
            }

            return Path.Combine(overrideDirectory, FileName);
        }

        string appDirectory = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(appDirectory, "portable.txt")))
        {
            return Path.Combine(appDirectory, FileName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName,
            FileName);
    }

    /// <summary>
    /// The settings file path in a form that is safe to put in a message.
    ///
    /// GetPath throws when the redirect variable holds nothing but white space, and the callers
    /// that report a save which did not land are the last ones that should be replaced by an
    /// exception - that message is the only thing standing between the user and a screen that
    /// never changes.
    ///
    /// Naming the variable in that case is the point rather than a fallback. A blank redirect
    /// makes every read come back unset and every write get discarded for the whole run, which
    /// is what the throw was added to expose, and every path to it is inside a catch-all - so
    /// until here nothing ever showed it.
    /// </summary>
    internal static string DescribePath()
    {
        try
        {
            return GetPath();
        }
        catch (Exception)
        {
            // Written as the variable rather than a sentence: whoever set it reads any language
            return $"{DirectoryOverrideVariable}=\"\"";
        }
    }
}

/// <summary>Persists the chosen language for the next launch.</summary>
internal static class LanguageSettings
{
    /// <inheritdoc cref="SettingsFile.DirectoryOverrideVariable"/>
    public const string DirectoryOverrideVariable = SettingsFile.DirectoryOverrideVariable;

    public static string? Load() => SettingsFile.Read().Language;

    /// <summary>Stores the language, leaving every other setting as it was.</summary>
    /// <returns>
    /// Whether it reached the file. Thrown away, a folder that could not be written put the
    /// language back to the default at the next start with no reason given.
    /// </returns>
    public static bool Save(string languageCode) =>
        SettingsFile.Update(s => s with { Language = languageCode });

    internal static string GetPath() => SettingsFile.GetPath();
}

/// <summary>
/// Records that the user accepted the terms of use.
///
/// The application changes the appearance of a game that is not its own, so it is used at the
/// user's own risk. That has to be said, and agreed to, before anything can be installed.
/// </summary>
internal static class DisclaimerSettings
{
    /// <summary>Whether the terms have been accepted.</summary>
    public static bool IsAccepted =>
        !string.IsNullOrWhiteSpace(SettingsFile.Read().DisclaimerAcceptedVersion);

    /// <summary>Records acceptance of the given application version's terms.</summary>
    /// <returns>
    /// Whether it reached the file. Thrown away, a folder that could not be written asked for the
    /// terms again at every start - under a notice saying it is shown only once - with no reason.
    /// </returns>
    public static bool Accept(string applicationVersion) =>
        SettingsFile.Update(s => s with { DisclaimerAcceptedVersion = applicationVersion });

    /// <summary>Forgets the acceptance, so the notice is shown again. Used by the tests.</summary>
    public static void Reset() =>
        SettingsFile.Update(s => s with { DisclaimerAcceptedVersion = null });
}

/// <summary>
/// Persists a game folder the user pointed at by hand.
///
/// Steam can install a game into any library folder on any drive, and detection walks Steam's
/// own records to find it. This exists for the case where that fails anyway: without it, a user
/// whose installation cannot be found has no way forward at all.
/// </summary>
internal static class GamePathSettings
{
    public static string? Load() => SettingsFile.Read().GamePath;

    /// <summary>
    /// Stores the folder, or null to go back to detecting it automatically.
    /// </summary>
    /// <returns>
    /// Whether it reached the file. The startup gate re-reads the file to decide what to show, so
    /// a caller that ignores a false here shows the same screen again and says nothing.
    /// </returns>
    public static bool Save(string? gamePath) =>
        SettingsFile.Update(s => s with { GamePath = gamePath });
}

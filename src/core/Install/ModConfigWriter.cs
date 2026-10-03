using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreKeeperSkinTool.Install;

/// <summary>
/// One of the mod's settings as the game stores it on disk.
///
/// The game keeps every setting in its own small JSON file and reads it back with Unity's
/// JsonUtility, so the field names have to match exactly and all of them have to be present.
/// </summary>
/// <param name="Mod">Mod name, which is also the folder name.</param>
/// <param name="Section">Section the setting belongs to.</param>
/// <param name="Description">Description shown to the user by the game.</param>
/// <param name="Key">Setting name.</param>
/// <param name="DefaultValue">Value used when the file is absent.</param>
/// <param name="Value">The value in effect.</param>
public sealed record ModSetting(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("defaultValue")] bool DefaultValue,
    [property: JsonPropertyName("value")] bool Value);

/// <summary>
/// Writes the mod's settings from outside the game.
///
/// The game's configuration reader opens the file on every single read rather than caching it
/// (<c>ModConfigEntry.get_Value</c> calls <c>TryGet</c>, which does FileExists + Read +
/// JsonUtility.FromJsonOverwrite). A setting changed here therefore takes effect without
/// restarting the game or reloading the mod.
///
/// The layout is fixed by the game: <c>&lt;mods&gt;\&lt;mod&gt;\&lt;section&gt;-&lt;key&gt;.json</c>.
/// </summary>
public static class ModConfigWriter
{
    /// <summary>Section the mod registers all of its settings under.</summary>
    public const string Section = "General";

    /// <summary>Suffix of the temporary file a setting is written to before being swapped in.</summary>
    private const string StagingSuffix = ".new";

    /// <summary>
    /// Settings that decide whether the game's own clothing and armour are drawn on top of the
    /// replacement art. Hiding them is what makes the picture the whole character.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Description)> GearKeys =
    [
        ("hideShirt", "シャツを非表示にする。"),
        ("hidePants", "ズボンを非表示にする。"),
        ("hideHelm", "装備中の兜を非表示にする。"),
        ("hideArmor", "装備中の胴・脚防具を非表示にする。"),
    ];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Full path of the file holding one setting.</summary>
    public static string PathFor(string modsDirectory, string modFolderName, string key)
    {
        PathSafety.EnsureSingleSegment(modFolderName, "MOD フォルダ名");
        PathSafety.EnsureSingleSegment(key, "設定キー");

        return Path.Combine(modsDirectory, modFolderName, $"{Section}-{key}.json");
    }

    /// <summary>
    /// Turns the game's clothing and armour on or off.
    ///
    /// The four settings only make sense together, so all four are written to temporary files
    /// before any of them is swapped in. Writing and swapping them one at a time lets a failure
    /// on the third leave the shirt and trousers hidden while the helmet and armour stay
    /// visible — and <see cref="IsGearHidden"/> answers from the first file it can read, so the
    /// screen would go on claiming everything is hidden while the game draws half of it.
    /// </summary>
    /// <param name="hide">True to hide them, which is the default the mod ships with.</param>
    /// <returns>The files written.</returns>
    public static IReadOnlyList<string> SetGearHidden(
        string modsDirectory, string modFolderName, bool hide)
    {
        List<(string Path, string Staging)> pending = [];

        try
        {
            foreach ((string key, string description) in GearKeys)
            {
                string path = PathFor(modsDirectory, modFolderName, key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                ModSetting setting = new(modFolderName, Section, description, key, true, hide);

                // Written through a temporary file: the game may read this at any moment, and a
                // half-written file would be read as a missing setting.
                string staging = path + StagingSuffix;
                File.WriteAllText(
                    staging, JsonSerializer.Serialize(setting, Options), new UTF8Encoding(false));

                pending.Add((path, staging));
            }
        }
        catch
        {
            DiscardStaging(pending);
            throw;
        }

        List<string> written = [];

        // Every file exists in full by now, so the swaps are quick and the window in which the
        // game could observe a mixed state is as small as this can make it.
        try
        {
            foreach ((string path, string staging) in pending)
            {
                // The read-only attribute is cleared only once the move has objected to it, as
                // CharacterSkins does for the skins. Left alone, a backup or sync product that
                // marked one of the four made every attempt stop at it: half switched, "もう一度
                // 実行すると残りも切り替わる", and no attempt ever getting past it.
                try
                {
                    File.Move(staging, path, overwrite: true);
                }
                catch (UnauthorizedAccessException)
                {
                    PathSafety.ClearReadOnly(path);
                    File.Move(staging, path, overwrite: true);
                }

                written.Add(path);
            }
        }
        catch (Exception ex)
        {
            DiscardStaging(pending);

            // Say how far it got. Writing all four in advance shortens the window but cannot
            // close it, so a failure here really can leave the shirt and trousers switched and
            // the helmet and armour not. Reporting a bare failure would leave the user believing
            // nothing had changed while the game already behaves differently.
            // The inner message goes in as an argument, not just into the Japanese text. Without
            // it the translated version dropped the one part that says why - a denied folder
            // reads very differently from a file the game has open.
            throw new ToolException(
                "error.gear.partial", [written.Count, GearKeys.Count, ex.Message],
                $"防具の表示設定を切り替えきれなかった（{GearKeys.Count} 件中 {written.Count} 件だけ切り替わった）: " +
                ex.Message + Environment.NewLine +
                "  もう一度実行すると、残りも切り替わる。");
        }

        return written;
    }

    /// <summary>
    /// Removes temporary files left over from an abandoned write, so they do not accumulate
    /// next to the real settings. Files already swapped in are gone and are skipped.
    /// </summary>
    private static void DiscardStaging(IEnumerable<(string Path, string Staging)> pending)
    {
        foreach ((_, string staging) in pending)
        {
            try
            {
                PathSafety.DeleteFile(staging);
            }
            catch (Exception)
            {
                // Cleanup only. The original failure is the one worth reporting.
            }
        }
    }

    /// <summary>
    /// Reads back whether the gear is currently hidden.
    /// Returns true when the settings are absent, which is the mod's own default.
    ///
    /// Hidden means every one of them is hidden. Answering from the first file that could be
    /// read assumed the four always agree, and they do not have to: a swap that fails part way
    /// leaves some switched and some not, and reading only the first then reported "hidden"
    /// while the game went on drawing the helmet and the armour. Any setting that says otherwise
    /// now decides, so the screen errs towards saying the gear shows - and pressing the toggle
    /// rewrites all four, which puts the mixed state right.
    /// </summary>
    public static bool IsGearHidden(string modsDirectory, string modFolderName)
    {
        foreach ((string key, _) in GearKeys)
        {
            string path = PathFor(modsDirectory, modFolderName, key);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream);

                if (document.RootElement.TryGetProperty("value", out JsonElement value)
                    && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && !value.GetBoolean())
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // A malformed file just means the mod will fall back to its default
            }
        }

        return true;
    }
}

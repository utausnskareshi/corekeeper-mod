using System.Text;
using System.Text.Json;

namespace CoreKeeperSkinTool.Install;

/// <summary>
/// One player character the game has saved.
/// </summary>
/// <param name="SlotIndex">The save slot, taken from the file name.</param>
/// <param name="Guid">
/// The character's own identifier, 32 lower-case hex digits. This is what a skin is filed under,
/// because two characters may share a name and a slot number is reused once a character is deleted.
/// </param>
/// <param name="Name">The name the player gave the character; empty when it was left blank.</param>
/// <param name="LastPlayedUtc">Modification time of the save file, used only for display.</param>
public sealed record GameCharacter(int SlotIndex, string Guid, string Name, DateTime LastPlayedUtc)
{
    /// <summary>
    /// Whether this is a creative-mode character.
    ///
    /// The game keeps them in the same folder as the normal ones, starting at slot 30. This is
    /// the same range <c>SaveManager.IsCreativeModeCharacter</c> tests, so it stays in step with
    /// what the game itself considers creative.
    /// </summary>
    public bool IsCreative =>
        SlotIndex >= CharacterLocator.CreativeStartIndex
        && SlotIndex < CharacterLocator.CreativeStartIndex + CharacterLocator.CreativeCount;

    /// <summary>
    /// The number to show. Creative characters are counted from one again, because that is how
    /// the game lists them; showing the raw slot would number the first creative character 31.
    /// </summary>
    public int DisplayNumber =>
        IsCreative ? SlotIndex - CharacterLocator.CreativeStartIndex + 1 : SlotIndex + 1;

    /// <summary>A label for the character list, falling back to the number when it has no name.</summary>
    public string Describe(string unnamed) =>
        string.IsNullOrWhiteSpace(Name) ? $"#{DisplayNumber} {unnamed}" : $"#{DisplayNumber} {Name}";
}

/// <summary>
/// Finds the player characters saved on this machine.
///
/// They live beside the mods folder, in
/// <c>&lt;user&gt;\AppData\LocalLow\Pugstorm\Core Keeper\&lt;platform&gt;\&lt;id&gt;\saves\&lt;slot&gt;.json</c>.
/// The characters are read straight from those files rather than from the running game, because
/// the tool has to list them while the game is closed.
/// </summary>
public static class CharacterLocator
{
    /// <summary>Folder holding the character saves, relative to one user data folder.</summary>
    private const string SavesFolderName = "saves";

    /// <summary>Length of the identifier the game writes, in hex digits.</summary>
    private const int GuidLength = 32;

    /// <summary>
    /// Largest character name accepted, in UTF-8 bytes.
    ///
    /// The game stores it in a Unity fixed-size string of a few dozen bytes. The generous cap
    /// here only exists to stop a corrupt file from turning its declared length into an
    /// allocation of that size.
    /// </summary>
    private const int MaxNameBytes = 128;

    /// <summary>
    /// First slot used by creative-mode characters, mirroring
    /// <c>SaveManager.creativeCharacterStartIndex</c>.
    /// </summary>
    public const int CreativeStartIndex = 30;

    /// <summary>How many creative slots follow it, mirroring <c>SaveManager.numberCreativeCharacters</c>.</summary>
    public const int CreativeCount = 30;

    /// <summary>
    /// A fingerprint of the character folders, used to tell whether the list needs rebuilding.
    ///
    /// Adding or deleting a character adds or removes a file, which moves the folder's
    /// modification time, so comparing this is enough to notice a character appearing while
    /// this application is already open.
    /// </summary>
    public static string Fingerprint() => Fingerprint(
        GameLocator.FindUserDataDirectories().Select(u => Path.Combine(u.Directory, SavesFolderName)));

    /// <summary>Fingerprints the given saves folders. Split out so it can be exercised in tests.</summary>
    public static string Fingerprint(IEnumerable<string> savesDirectories)
    {
        List<string> parts = [];

        foreach (string saves in savesDirectories)
        {
            try
            {
                parts.Add(Directory.Exists(saves)
                    ? $"{saves}|{Directory.GetLastWriteTimeUtc(saves).Ticks}|{Directory.EnumerateFiles(saves, "*.json").Count()}"
                    : $"{saves}|-");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An unreadable folder always compares equal to itself, so it simply never
                // triggers a rebuild rather than triggering one on every single focus change.
                parts.Add($"{saves}|?");
            }
        }

        return string.Join(";", parts);
    }

    /// <summary>The saves folder that belongs with a mods folder.</summary>
    public static string SavesDirectoryFor(ModConfigLocation location)
    {
        string mods = location.ModsDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string? user = Path.GetDirectoryName(mods);

        // A mods folder given explicitly by the user may sit anywhere, including at a drive
        // root where there is no parent to look beside.
        return user is null ? string.Empty : Path.Combine(user, SavesFolderName);
    }

    /// <summary>
    /// Reads the characters saved beside a given mods folder, ordered by slot.
    /// Empty when the folder does not exist or holds nothing readable.
    /// </summary>
    public static IReadOnlyList<GameCharacter> Find(ModConfigLocation location) =>
        ReadFolder(SavesDirectoryFor(location));

    /// <summary>
    /// Reads the characters of every platform and user the game has data for.
    ///
    /// Used at start-up to answer "does this machine have any character at all", which must not
    /// depend on the mods folder: that folder only appears once a mod has run, so going through
    /// it would report no characters on an installation that simply has never loaded one.
    /// </summary>
    public static IReadOnlyList<GameCharacter> FindAll()
    {
        List<GameCharacter> found = [];

        foreach (GameLocator.UserDataDirectory user in GameLocator.FindUserDataDirectories())
        {
            found.AddRange(ReadFolder(Path.Combine(user.Directory, SavesFolderName)));
        }

        return found;
    }

    /// <summary>Reads every character file in one saves folder.</summary>
    private static IReadOnlyList<GameCharacter> ReadFolder(string savesDirectory)
    {
        if (string.IsNullOrEmpty(savesDirectory) || !Directory.Exists(savesDirectory))
        {
            return [];
        }

        List<GameCharacter> found = [];

        IEnumerable<string> files;
        try
        {
            // Only <slot>.json. The folder also holds .pugbackup copies of the same characters,
            // which would otherwise be listed as duplicates.
            files = Directory.EnumerateFiles(savesDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        foreach (string file in files)
        {
            GameCharacter? character = ReadCharacter(file);
            if (character is not null)
            {
                found.Add(character);
            }
        }

        return [.. found.OrderBy(c => c.SlotIndex)];
    }

    /// <summary>
    /// Reads one character file, or null when it is not one.
    ///
    /// Only the first few properties are read, and reading stops as soon as they are in hand.
    /// That is not merely an optimisation: the game writes bare <c>Infinity</c> into the deeper
    /// parts of a save, which is not valid JSON, so parsing the whole file fails outright on a
    /// character that is otherwise perfectly fine. Everything needed here sits at the front,
    /// well before anything like that.
    ///
    /// A file that still cannot be read is skipped rather than reported: the folder belongs to
    /// the game, and a future version adding a file here must not stop the tool from listing
    /// the characters it does understand.
    /// </summary>
    private static GameCharacter? ReadCharacter(string path)
    {
        if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int slot) || slot < 0)
        {
            return null;
        }

        try
        {
            (string? guid, string name) = Scan(File.ReadAllBytes(path));

            guid = (guid ?? string.Empty).Trim().ToLowerInvariant();

            return IsValidGuid(guid)
                ? new GameCharacter(slot, guid, name, File.GetLastWriteTimeUtc(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or System.Security.SecurityException
                                       or OutOfMemoryException or ArgumentException)
        {
            // Deliberately broad. One unreadable or corrupt file in the folder must never stop
            // the rest of the characters from being listed, and this runs over files the game
            // owns and a user can drop anything into.
            return null;
        }
    }

    /// <summary>
    /// Pulls the identifier and the name out of one save file.
    ///
    /// Scanning stops at the first value the reader cannot parse rather than giving up on the
    /// character: the game writes bare <c>Infinity</c>, which no JSON reader accepts, and it does
    /// so at whatever depth it likes. Everything gathered before that point is still good, so a
    /// character whose identifier was already read stays in the list even when its name could
    /// not be reached.
    /// </summary>
    private static (string? Guid, string Name) Scan(byte[] bytes)
    {
        string? guid = null;
        string name = string.Empty;

        try
        {
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions { AllowTrailingCommas = true });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, string.Empty);
            }

            bool seenOldBlock = false;
            bool seenNewBlock = false;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string property = reader.GetString() ?? string.Empty;

                if (property == "characterGuid")
                {
                    if (!reader.Read())
                    {
                        break;
                    }

                    guid = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    continue;
                }

                if (property is "characterCustomization" or "characterCustomizationNew")
                {
                    if (!reader.Read())
                    {
                        break;
                    }

                    // The block is small and well-formed, so it is worth materialising to read
                    // the name out of its byte-per-field layout.
                    using JsonDocument block = JsonDocument.ParseValue(ref reader);

                    seenOldBlock |= property == "characterCustomization";
                    seenNewBlock |= property == "characterCustomizationNew";

                    // A later block wins only when it actually holds a name: the game fills in
                    // whichever format it currently uses and leaves the other one empty.
                    string found = ReadName(block.RootElement);
                    if (!string.IsNullOrEmpty(found))
                    {
                        name = found;
                    }

                    continue;
                }

                // Stopping early keeps the scan away from the malformed tail, but it must not
                // stop before the name has been found. The two customization blocks need not be
                // adjacent, and the one written first is exactly the one the game leaves empty,
                // so stopping at the first of them lost the name entirely.
                if (guid is not null && (name.Length > 0 || (seenOldBlock && seenNewBlock)))
                {
                    break;
                }

                if (!reader.TrySkip())
                {
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // A value this reader cannot parse. Keep what was read before it.
        }

        return (guid, name);
    }

    /// <summary>Whether the identifier looks like one the game wrote.</summary>
    public static bool IsValidGuid(string? guid) =>
        guid is { Length: GuidLength } && guid.All(Uri.IsHexDigit);

    /// <summary>
    /// Reads the character name out of one customization block.
    ///
    /// Unity serialises the fixed-size string as one numbered field per byte, and splits them
    /// across a nested "offset0000" object once there are enough of them, so each byte is looked
    /// up in the nested object first and then beside it.
    /// </summary>
    private static string ReadName(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object
            || !block.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.Object
            || !name.TryGetProperty("utf8LengthInBytes", out JsonElement lengthElement)
            || !lengthElement.TryGetInt32(out int length)
            // Bounded before it becomes an allocation size. The field describes a Unity
            // fixed-size string of a few dozen bytes, so anything larger is corrupt data, and
            // taking it at face value let one bad save ask for a multi-gigabyte array.
            || length is <= 0 or > MaxNameBytes
            || !name.TryGetProperty("bytes", out JsonElement bytes)
            || bytes.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        bytes.TryGetProperty("offset0000", out JsonElement nested);

        byte[] utf8 = new byte[length];
        for (int i = 0; i < length; i++)
        {
            string field = $"byte{i:d4}";

            if (!TryReadByte(nested, field, out byte value) && !TryReadByte(bytes, field, out value))
            {
                // A missing byte means the layout is not the one this understands. Returning
                // what was read so far would show a name truncated mid-character.
                return string.Empty;
            }

            utf8[i] = value;
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(utf8).TrimEnd('\0').Trim();
    }

    private static bool TryReadByte(JsonElement owner, string field, out byte value)
    {
        value = 0;

        return owner.ValueKind == JsonValueKind.Object
               && owner.TryGetProperty(field, out JsonElement element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetByte(out value);
    }
}

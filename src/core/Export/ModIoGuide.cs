using System.Reflection;
using System.Text;

namespace CoreKeeperSkinTool.Export;

/// <summary>What the registration memo is filled in with.</summary>
public sealed record ModIoGuideValues(
    string Name,
    string Version,
    bool ShowGear,
    string GameVersionTag,
    string ZipFile,
    string LogoFile,
    ModIoIdentity Identity);

/// <summary>
/// The registration memo written beside the zip: what to enter on mod.io, which tags to pick, and
/// how to update the mod later.
///
/// The tags are what it is for. Without a Game Version tag matching the first three numbers of the
/// game's version the game warns at start-up and does not load the mod unless the player chooses
/// "Load anyway", and with Access Type "Asset" it loads none of the scripts - neither is visible
/// until a player has installed it, so the memo says exactly what to pick.
///
/// The steps follow the stages of mod.io's web form (Mod profile, then the optional stages, then
/// Upload file(s)), because the visibility is chosen in the first stage and the mod goes public as
/// soon as its first file is published: Core Keeper's mod.io has no approval step.
///
/// The text lives in Localization/modio-guide.&lt;language&gt;.txt. A file starts with lines of
/// <c>#key=text</c> (the two ways the gear can be described), then a line holding only
/// <c>---</c>, then the memo with {{token}} placeholders.
/// </summary>
public static class ModIoGuide
{
    private const string FallbackLanguage = "ja";
    private const string Separator = "---";

    /// <summary>Every placeholder a memo may use; each file uses all of them.</summary>
    public static readonly IReadOnlyList<string> Tokens =
        ["name", "version", "gear", "gameVersion", "zipFile", "logoFile", "id", "guid"];

    /// <summary>The header keys each file has to define.</summary>
    public static readonly IReadOnlyList<string> HeaderKeys = ["gearShown", "gearHidden"];

    /// <summary>The languages a memo can be written in.</summary>
    public static IReadOnlyList<string> Languages =>
        [.. typeof(ModIoGuide).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith("modio-guide.", StringComparison.Ordinal) && r.EndsWith(".txt", StringComparison.Ordinal))
            .Select(r => r["modio-guide.".Length..^".txt".Length])
            .OrderBy(c => c, StringComparer.Ordinal)];

    /// <summary>The memo in the given language, or in Japanese when there is none in it.</summary>
    public static string Render(string? languageCode, ModIoGuideValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        (IReadOnlyDictionary<string, string> header, string body) = Load(languageCode);

        Dictionary<string, string> tokens = new(StringComparer.Ordinal)
        {
            ["name"] = values.Name,
            ["version"] = values.Version,
            ["gear"] = header[values.ShowGear ? "gearShown" : "gearHidden"],
            ["gameVersion"] = values.GameVersionTag,
            ["zipFile"] = values.ZipFile,
            ["logoFile"] = values.LogoFile,
            ["id"] = values.Identity.Name,
            ["guid"] = values.Identity.Guid,
        };

        if (Placeholder.Matches(body).Any(m => !tokens.ContainsKey(m.Groups[1].Value)))
        {
            throw new ToolException(
                "error.modio.guideBroken", [],
                "mod.io 登録メモのひな形に、埋められない項目がある。配布物が壊れている可能性がある。");
        }

        // One pass, with the values put in as they are: a name that itself reads like {{version}}
        // stays the name rather than being filled in again
        string text = Placeholder.Replace(body, m => tokens[m.Groups[1].Value]);

        return text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex Placeholder =
        new(@"\{\{([A-Za-z]+)\}\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The header and the body of one language's file, LF line ends.</summary>
    public static (IReadOnlyDictionary<string, string> Header, string Body) Load(string? languageCode)
    {
        Assembly assembly = typeof(ModIoGuide).Assembly;
        string? raw = Read(assembly, languageCode) ?? Read(assembly, FallbackLanguage);
        if (raw is null)
        {
            throw new ToolException(
                "error.modio.guideMissing", [],
                "mod.io 登録メモのひな形が同梱されていない。配布物が壊れている可能性がある。");
        }

        string text = ModIoExporter.NormalizeNewlines(raw);
        int split = text.IndexOf("\n" + Separator + "\n", StringComparison.Ordinal);
        if (split < 0)
        {
            throw new ToolException(
                "error.modio.guideBroken", [],
                "mod.io 登録メモのひな形の形式が不正。配布物が壊れている可能性がある。");
        }

        Dictionary<string, string> header = new(StringComparer.Ordinal);
        foreach (string line in text[..split].Split('\n'))
        {
            if (line.StartsWith('#') && line.IndexOf('=') is > 1 and int equals)
            {
                header[line[1..equals]] = line[(equals + 1)..];
            }
        }

        if (HeaderKeys.Any(k => !header.ContainsKey(k)))
        {
            throw new ToolException(
                "error.modio.guideBroken", [],
                "mod.io 登録メモのひな形の形式が不正。配布物が壊れている可能性がある。");
        }

        return (header, text[(split + Separator.Length + 2)..]);
    }

    private static string? Read(Assembly assembly, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        using Stream? stream = assembly.GetManifestResourceStream($"modio-guide.{languageCode}.txt");
        if (stream is null)
        {
            return null;
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

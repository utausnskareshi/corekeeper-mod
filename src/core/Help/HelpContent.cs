using System.Reflection;
using System.Text.Json;

namespace CoreKeeperSkinTool.Help;

/// <summary>A term and its explanation, used to describe each part of the interface.</summary>
/// <param name="Term">Name of the item, such as a button or an input field.</param>
/// <param name="Description">What it means and when to use it.</param>
public sealed record HelpTerm(string Term, string Description);

/// <summary>One section of the help text.</summary>
/// <param name="Heading">Section heading.</param>
/// <param name="Paragraphs">Body text. Absent in the file for a section made only of terms.</param>
/// <param name="Terms">Term explanations. Absent in the file for a section made only of prose.</param>
/// <remarks>
/// A section may hold only paragraphs or only terms, so one side is missing from the JSON and
/// deserialises as null. Both are exposed as empty lists instead: a type that hands out nulls
/// makes every caller guard against them, and the ones that forget fail at run time.
/// </remarks>
/// <param name="PromptResource">
/// Name of an embedded text file shown verbatim, in a block the reader can copy. Null for the
/// sections that are only prose. This exists for text that has to survive being copied out
/// unchanged, which wrapped paragraphs do not.
/// </param>
public sealed record HelpSection(
    string Heading,
    IReadOnlyList<string>? Paragraphs,
    IReadOnlyList<HelpTerm>? Terms,
    string? PromptResource = null)
{
    /// <summary>Body text; empty when the section has none.</summary>
    public IReadOnlyList<string> Paragraphs { get; } = Paragraphs ?? [];

    /// <summary>Term explanations; empty when the section has none.</summary>
    public IReadOnlyList<HelpTerm> Terms { get; } = Terms ?? [];

    /// <summary>
    /// The verbatim block, or an empty string when the section has none.
    /// Read once here so the window has nothing to load or fail at while drawing.
    /// </summary>
    public string Prompt { get; } =
        PromptResource is null ? string.Empty : HelpContent.LoadVerbatim(PromptResource);

    /// <summary>Whether this section carries a block to copy.</summary>
    public bool HasPrompt => Prompt.Length > 0;
}

/// <summary>The complete help document.</summary>
/// <param name="Title">Title shown on the help window.</param>
/// <param name="Sections">Sections in display order.</param>
public sealed record HelpDocument(string Title, IReadOnlyList<HelpSection> Sections);

/// <summary>
/// Loads the help text for a given language.
///
/// Kept separate from the short interface strings (<c>lang.*.json</c>) because the help text
/// has structure (paragraphs, term lists) and considerable volume. Mixing it into a flat
/// key/value table would make both translation and maintenance harder.
/// </summary>
public static class HelpContent
{
    private const string FallbackCode = "ja";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads the help text for the given language, falling back to the default language
    /// when that language is not available.
    /// </summary>
    public static HelpDocument Load(string languageCode)
    {
        return TryLoad(languageCode)
               ?? TryLoad(FallbackCode)
               ?? throw new ToolException(
                   "ヘルプの本文が埋め込まれていない。配布物が壊れている可能性がある。");
    }

    /// <summary>Language codes for which help text is embedded.</summary>
    public static IReadOnlyList<string> AvailableLanguages =>
    [
        .. Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Select(name => System.Text.RegularExpressions.Regex.Match(name, @"^help\.(?<code>[A-Za-z0-9\-]+)\.json$"))
            .Where(match => match.Success)
            .Select(match => match.Groups["code"].Value)
            .OrderBy(code => code, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Reads an embedded text file that the help shows word for word.
    ///
    /// A missing file throws rather than showing an empty block: the block exists to be copied
    /// and used elsewhere, and half of it is worse than an honest failure.
    /// </summary>
    public static string LoadVerbatim(string resourceName)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new ToolException(
                $"ヘルプが参照する本文 ({resourceName}) が埋め込まれていない。配布物が壊れている可能性がある。");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd().TrimEnd();
    }

    private static HelpDocument? TryLoad(string languageCode)
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"help.{languageCode}.json");

        if (stream is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HelpDocument>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ToolException($"ヘルプの本文を解釈できない (help.{languageCode}.json): {ex.Message}", ex);
        }
    }
}

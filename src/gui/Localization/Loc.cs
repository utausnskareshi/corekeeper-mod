using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace CoreKeeperSkinTool.Gui.Localization;

/// <summary>A language that can be selected.</summary>
/// <param name="Code">Language code, such as ja or en.</param>
/// <param name="DisplayName">The language's own name for itself.</param>
/// <summary>One piece of text, labelled with the language it is written in.</summary>
/// <param name="Language">That language's own name, as it calls itself.</param>
/// <param name="Text">The text.</param>
public sealed record LocalizedText(string Language, string Text);

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Holds every string shown in the interface.
///
/// The strings live in a per-language JSON file embedded in the executable.
/// Adding a language only requires dropping in <c>Localization/lang.&lt;code&gt;.json</c>,
/// with no code changes required.
///
/// The view binds to it through <c>{loc:Tr key}</c>.
/// Switching language refreshes every bound string, so the window never has to be rebuilt.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Language used when a translation is missing.</summary>
    private const string FallbackCode = "ja";

    /// <summary>
    /// Extracts the language code from an embedded resource name.
    /// The default naming prefixes a namespace, so only the trailing lang.&lt;code&gt;.json is matched.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ResourcePattern =
        new(@"lang\.(?<code>[A-Za-z0-9\-]+)\.json$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Special key that holds the language's own name.</summary>
    private const string LanguageNameKey = "_language";

    private readonly Dictionary<string, Dictionary<string, string>> _tables;

    /// <summary>Per-key notification source the view binds to; the same key reuses one instance.</summary>
    private readonly Dictionary<string, LocalizedString> _strings = [];

    private LanguageOption _current;

    public static Loc Instance { get; } = new();

    /// <summary>Assembly the language files are embedded in; exposed so tests can inspect it.</summary>
    public static Assembly LanguageResourceAssembly => typeof(CoreKeeperSkinTool.Layout.SheetLayout).Assembly;

    private Loc()
    {
        _tables = LoadTables();

        Languages =
        [
            .. _tables
                .Select(pair => new LanguageOption(
                    pair.Key,
                    pair.Value.TryGetValue(LanguageNameKey, out string? name) ? name : pair.Key))
                .OrderBy(option => option.Code == FallbackCode ? 0 : 1)
                .ThenBy(option => option.Code, StringComparer.Ordinal),
        ];

        if (Languages.Count == 0)
        {
            throw new InvalidOperationException("言語リソースが1つも埋め込まれていない。ビルド構成が壊れている。");
        }

        string preferred = LanguageSettings.Load() ?? FallbackCode;
        _current = Languages.FirstOrDefault(x => x.Code == preferred)
                   ?? Languages.FirstOrDefault(x => x.Code == FallbackCode)
                   ?? Languages[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageOption> Languages { get; }

    /// <summary>
    /// Whether the last language chosen reached the settings file. False where the settings
    /// cannot be written, and the language then goes back to the default at the next start.
    /// </summary>
    public bool LastLanguageSaved { get; private set; } = true;

    /// <summary>The current language. Changing it swaps every string on screen at once.</summary>
    public LanguageOption Current
    {
        get => _current;
        set
        {
            if (value is null || value.Code == _current.Code)
            {
                return;
            }

            _current = value;

            // Kept for whoever shows the change to say when it will not last: this class has
            // no screen of its own
            LastLanguageSaved = LanguageSettings.Save(value.Code);

            // Swap out the text currently on screen.
            // Each binding targets a per-key LocalizedString, so notify every one of them.
            //
            // Over a copy: refreshing a binding can make the layout ask for a key it has not
            // asked for before - a template realised for the first time is enough - and Get adds
            // that key to the same dictionary, which ends the loop with "Collection was
            // modified" and leaves the rest of the screen in the old language.
            foreach (LocalizedString entry in _strings.Values.ToArray())
            {
                entry.Refresh();
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        }
    }

    /// <summary>Text for a key, falling back to the default language and finally to the key itself.</summary>
    public string this[string key]
    {
        get
        {
            if (_tables.TryGetValue(_current.Code, out Dictionary<string, string>? table)
                && table.TryGetValue(key, out string? text))
            {
                return text;
            }

            if (_tables.TryGetValue(FallbackCode, out Dictionary<string, string>? fallback)
                && fallback.TryGetValue(key, out string? fallbackText))
            {
                return fallbackText;
            }

            // Return the key itself so that a missing translation is noticeable
            return key;
        }
    }

    /// <summary>
    /// The text for a key, or null when no language defines it.
    ///
    /// Separate from the indexer because the indexer's "hand back the key" fallback is only
    /// useful on screen, where a stray key is visible and obviously wrong. A caller that has
    /// something better to fall back on needs to be able to tell the two apart.
    /// </summary>
    public string? Find(string key)
    {
        if (_tables.TryGetValue(_current.Code, out Dictionary<string, string>? table)
            && table.TryGetValue(key, out string? text))
        {
            return text;
        }

        return _tables.TryGetValue(FallbackCode, out Dictionary<string, string>? fallback)
               && fallback.TryGetValue(key, out string? fallbackText)
            ? fallbackText
            : null;
    }

    /// <summary>Builds a string with placeholders filled in.</summary>
    public string Format(string key, params object?[] args) => string.Format(this[key], args);

    /// <summary>
    /// What to show the user for an error raised by the conversion and install code.
    ///
    /// Those messages are written in Japanese, which is what the command line wants. The window
    /// can be switched to English, and until this existed the one line saying what had gone
    /// wrong was the only part of the screen that stayed Japanese - the part an English reader
    /// most needs. An error that names a key is resolved here; one that does not keeps its
    /// written message, which is still better than saying nothing.
    /// </summary>
    public string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Takes any exception so the catch blocks can call it without caring which kind they
        // hold. Anything without a key - every non-ToolException, and the paths only the
        // command line reaches - keeps its own text.
        if (exception is not ToolException { MessageKey: { } key } tool)
        {
            return exception.Message;
        }

        // Find, not the indexer. The indexer hands back the key when nothing defines it, which
        // is a useful marker on a label but ruinous here: it became the format string, so a
        // mistyped or deleted key replaced a complete sentence with the word
        // "error.image.notFound" and threw the written message away.
        if (Find(key) is not { } template)
        {
            return tool.Message;
        }

        try
        {
            return string.Format(template, [.. tool.MessageArguments]);
        }
        catch (Exception)
        {
            // Caught broadly on purpose. This method exists to hand back something showable
            // whatever happens: a template short of values throws FormatException, and an
            // argument whose ToString throws would otherwise escape from inside the handler
            // that was reporting the first error. The written message still says what happened.
            return tool.Message;
        }
    }

    /// <summary>Text for a key in one particular language, falling back the same way as the indexer.</summary>
    public string InLanguage(string languageCode, string key)
    {
        if (_tables.TryGetValue(languageCode, out Dictionary<string, string>? table)
            && table.TryGetValue(key, out string? text))
        {
            return text;
        }

        if (_tables.TryGetValue(FallbackCode, out Dictionary<string, string>? fallback)
            && fallback.TryGetValue(key, out string? fallbackText))
        {
            return fallbackText;
        }

        return key;
    }

    /// <summary>
    /// The text for a key in every language at once, each paired with that language's own name.
    ///
    /// This exists for the screens shown before the main window. The language is chosen in the
    /// main window, so at that point the reader may well be looking at a language they cannot
    /// read, with no way to change it. Those screens therefore say everything in all of them
    /// rather than guessing which one is wanted.
    /// </summary>
    public IReadOnlyList<LocalizedText> EveryLanguage(string key) =>
        [.. Languages.Select(l => new LocalizedText(l.DisplayName, InLanguage(l.Code, key)))];

    /// <inheritdoc cref="EveryLanguage(string)"/>
    public IReadOnlyList<LocalizedText> EveryLanguage(string key, params object?[] args) =>
        [.. Languages.Select(l =>
            new LocalizedText(l.DisplayName, SafeFormat(InLanguage(l.Code, key), args)))];

    /// <summary>
    /// Fills in a template, and falls back to the template itself when it cannot be filled.
    ///
    /// These are the screens shown before any window exists, and the call sits outside every
    /// try: a template asking for one more value than the caller supplies threw a
    /// FormatException that reached the top and ended the application with no window at all and
    /// nothing on screen to act on. One badly worded translation is not worth that. The
    /// unfilled template still says roughly what it meant to.
    /// </summary>
    private static string SafeFormat(string template, object?[] args)
    {
        try
        {
            return string.Format(template, args);
        }
        catch (Exception)
        {
            return template;
        }
    }

    /// <summary>
    /// The text for a key in every language, where the values filled in are themselves
    /// translated.
    ///
    /// The plain overload takes one set of arguments and stamps it into every language, which is
    /// right for a file path or a version number but wrong for anything that has its own
    /// translation: a placeholder resolved in the current language ended up spliced into all of
    /// the blocks, so the Japanese text carried an English phrase or the other way round.
    ///
    /// Named apart from <see cref="EveryLanguage(string, object?[])"/> rather than overloading
    /// it, because the arguments here are produced per language and cannot be counted by reading
    /// the call. The test that checks every call supplies as many arguments as its template asks
    /// for works on the call text, and an overload it could not tell apart would either miss
    /// real shortfalls or have to skip identifiers, which is most of the real calls.
    /// </summary>
    /// <param name="argumentsFor">Builds the arguments for one language code.</param>
    public IReadOnlyList<LocalizedText> EveryLanguageTranslated(
        string key, Func<string, object?[]> argumentsFor) =>
        [.. Languages.Select(l =>
            new LocalizedText(l.DisplayName, SafeFormat(InLanguage(l.Code, key), argumentsFor(l.Code))))];

    /// <summary>
    /// Returns the notification source for a key; this is what <c>{loc:Tr key}</c> uses.
    /// The same key returns the same instance, so a language switch can refresh them all at once.
    /// </summary>
    public LocalizedString Get(string key)
    {
        if (!_strings.TryGetValue(key, out LocalizedString? entry))
        {
            entry = new LocalizedString(key);
            _strings[key] = entry;
        }

        return entry;
    }

    /// <summary>
    /// Loads the language files.
    ///
    /// They are embedded in the Core assembly rather than the GUI one.
    /// Avalonia rewrites the app assembly during XAML compilation and loses everything except
    /// its own resources, so the files live in an assembly it does not rewrite.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        Dictionary<string, Dictionary<string, string>> tables = [];
        Assembly assembly = LanguageResourceAssembly;

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            System.Text.RegularExpressions.Match match = ResourcePattern.Match(resource);
            if (!match.Success)
            {
                continue;
            }

            string code = match.Groups["code"].Value;

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            Dictionary<string, string>? table =
                JsonSerializer.Deserialize<Dictionary<string, string>>(stream);

            if (table is not null)
            {
                tables[code] = table;
            }
        }

        return tables;
    }
}

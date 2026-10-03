using System.Text.Json;
using System.Text.RegularExpressions;
using CoreKeeperSkinTool.Layout;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that every key the UI asks for actually exists in every language file.
///
/// This guards a failure mode this project already hit once: the window kept showing Japanese
/// after switching to English, and nothing reported an error, because a missing key simply
/// resolves to something that looks plausible. A build-time list comparison catches it instead.
/// </summary>
public sealed class TranslationCoverageTests
{
    /// <summary>Finds the repository root by walking up until the source folders appear.</summary>
    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "gui", "Views"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "core", "Localization")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
    }

    private static Dictionary<string, string> LoadLanguage(string root, string code)
    {
        string path = Path.Combine(root, "src", "core", "Localization", $"lang.{code}.json");
        Assert.True(File.Exists(path), $"言語ファイルが無い: {path}");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
               ?? throw new InvalidOperationException($"言語ファイルを読めない: {path}");
    }

    [Fact]
    public void 日本語と英語のキーが完全に一致する()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        string[] missingInEnglish = [.. ja.Keys.Except(en.Keys).Order()];
        string[] missingInJapanese = [.. en.Keys.Except(ja.Keys).Order()];

        Assert.True(
            missingInEnglish.Length == 0,
            $"英語に無いキー: {string.Join(", ", missingInEnglish)}");
        Assert.True(
            missingInJapanese.Length == 0,
            $"日本語に無いキー: {string.Join(", ", missingInJapanese)}");
    }

    [Fact]
    public void 空の訳文が無い()
    {
        string root = FindRepositoryRoot();

        foreach (string code in new[] { "ja", "en" })
        {
            string[] empty = [.. LoadLanguage(root, code)
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .Order()];

            Assert.True(empty.Length == 0, $"lang.{code}.json の訳文が空: {string.Join(", ", empty)}");
        }
    }

    [Fact]
    public void XAMLが参照するキーがすべて定義されている()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");

        string viewsDirectory = Path.Combine(root, "src", "gui");
        string[] files = Directory.GetFiles(viewsDirectory, "*.axaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        // Matches both {loc:Tr some.key} and {loc:Tr Key=some.key}
        Regex pattern = new(@"\{loc:Tr\s+(?:Key\s*=\s*)?([^\},\s]+)", RegexOptions.Compiled);

        List<string> undefined = [];
        foreach (string file in files)
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value.Trim();
                if (!ja.ContainsKey(key))
                {
                    undefined.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.True(
            undefined.Count == 0,
            $"lang.ja.json に定義の無いキーを XAML が参照している: {string.Join(", ", undefined.Order())}");
    }

    /// <summary>
    /// Splits an argument list starting just after the opening parenthesis, respecting nesting
    /// and string literals so that a comma inside a string or a nested call is not mistaken for
    /// an argument separator. Returns null when the parentheses do not balance.
    /// </summary>
    private static List<string>? SplitArguments(string source, int openParenIndex)
    {
        List<string> arguments = [];
        int depth = 0;
        int start = openParenIndex + 1;
        bool inString = false;
        bool inChar = false;
        bool verbatim = false;

        for (int i = openParenIndex; i < source.Length; i++)
        {
            char c = source[i];

            if (inString)
            {
                if (verbatim && c == '"' && i + 1 < source.Length && source[i + 1] == '"') { i++; }
                else if (!verbatim && c == '\\') { i++; }
                else if (c == '"') { inString = false; verbatim = false; }
                continue;
            }

            if (inChar)
            {
                if (c == '\\') { i++; }
                else if (c == '\'') { inChar = false; }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    verbatim = i > 0 && source[i - 1] == '@';
                    break;
                case '\'':
                    inChar = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0)
                    {
                        string last = source[start..i].Trim();
                        if (last.Length > 0) { arguments.Add(last); }
                        return arguments;
                    }

                    break;
                case ',' when depth == 1:
                    arguments.Add(source[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        return null;
    }

    [Fact]
    public void Format呼び出しの引数が書式指定子の個数を満たしている()
    {
        // string.Format throws FormatException when a placeholder index has no argument, and
        // these calls sit on status-update paths, several of them inside catch blocks. A message
        // that is one placeholder short there replaces a handled error with a crash.
        //
        // EveryLanguage is covered as well, and matters more than Format does. It is used only
        // by the start-up gate, which runs before any window exists and outside any try, so a
        // FormatException there is not a bad status line but an application that will not open,
        // with nothing on screen to recover from. Its no-argument overload is checked too: that
        // one does not format at all, so a template with a placeholder shows "{0}" to the user.
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        Regex placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

        // Four call shapes carry a key and its arguments together:
        //   Loc.Format("key", a, b)            - labels and the messages built from pieces
        //   SetStatus("key", a)                - status lines, which are most of them
        //   Loc.EveryLanguage("key", a)        - the start-up notices
        //   new ToolException("key", [a, b], …) - errors the window shows in the user's language
        //
        // Three of the four were missed before. EveryLanguage was written as ".EveryLanguage(",
        // which stopped matching the moment the Func overload was renamed to
        // EveryLanguageTranslated - and renaming it was part of the same change that added this
        // check, so the one call it existed to guard went unchecked again. ToolException was
        // never scanned at all, leaving all thirty-odd error keys to fail silently: Describe
        // catches the FormatException and quietly falls back to Japanese, which is the very
        // thing translating them set out to stop. SetStatus came last: moving the status lines
        // onto it took 44 keys out of the reach of both this scan and the indexer scan below,
        // and every test in this file still passed.
        Regex call = new(
            @"(?:\.(?:Format|EveryLanguage)\(|SetStatus\(|new\s+ToolException\(\s*)\s*""(?<key>[^""]+)""",
            RegexOptions.Compiled);

        // Which shape a match came from. Kept per shape because a single total cannot notice one
        // shape falling silent: the 44 calls above went unscanned while the total stayed well
        // clear of the floor that was there to catch exactly that.
        static string Shape(string matched) =>
            matched.Contains("ToolException", StringComparison.Ordinal) ? "ToolException"
            : matched.Contains("EveryLanguage", StringComparison.Ordinal) ? "EveryLanguage"
            : matched.StartsWith("SetStatus", StringComparison.Ordinal) ? "SetStatus"
            : "Format";

        Dictionary<string, int> byShape = new()
        {
            ["Format"] = 0,
            ["SetStatus"] = 0,
            ["EveryLanguage"] = 0,
            ["ToolException"] = 0,
        };

        int checkedCalls = 0;
        List<string> problems = [];
        string[] sources = [
            .. Directory.GetFiles(Path.Combine(root, "src", "gui"), "*.cs", SearchOption.AllDirectories),
            .. Directory.GetFiles(Path.Combine(root, "src", "core"), "*.cs", SearchOption.AllDirectories),
        ];

        foreach (string file in sources.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string text = File.ReadAllText(file);

            foreach (Match match in call.Matches(text))
            {
                string key = match.Groups["key"].Value;
                bool isToolException = match.Value.Contains("ToolException", StringComparison.Ordinal);

                // ToolException also takes a plain message with no key at all, and that message
                // is a string literal in exactly the same position. Only the keyed overload is
                // of interest, and its keys all live under "error.".
                if (isToolException && !key.StartsWith("error.", StringComparison.Ordinal))
                {
                    continue;
                }

                int open = text.IndexOf('(', match.Index);
                List<string>? arguments = SplitArguments(text, open);
                if (arguments is null || arguments.Count == 0)
                {
                    continue;
                }

                int supplied;
                if (isToolException)
                {
                    // new ToolException("key", [a, b], "message") - the values sit in the
                    // collection expression, not spread across the argument list.
                    string array = arguments.Count > 1 ? arguments[1].Trim() : string.Empty;
                    if (!array.StartsWith('[') || !array.EndsWith(']'))
                    {
                        continue;
                    }

                    supplied = array.Length <= 2
                        ? 0
                        : SplitArguments("(" + array[1..^1] + ")", 0)?.Count ?? 0;
                }
                else
                {
                    // The first argument is the key itself
                    supplied = arguments.Count - 1;
                }

                checkedCalls++;
                byShape[Shape(match.Value)]++;

                foreach ((string language, Dictionary<string, string> table) in
                         new[] { ("ja", ja), ("en", en) })
                {
                    if (!table.TryGetValue(key, out string? template))
                    {
                        problems.Add($"{Path.GetFileName(file)}: lang.{language}.json に '{key}' が無い");
                        continue;
                    }

                    int required = placeholder.Matches(template)
                        .Select(m => int.Parse(m.Groups[1].Value))
                        .DefaultIfEmpty(-1)
                        .Max() + 1;

                    if (required > supplied)
                    {
                        problems.Add(
                            $"{Path.GetFileName(file)}: '{key}' は lang.{language}.json が引数 {required} 個を要求するが {supplied} 個しか渡していない");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Order()));

        // Guards the scan itself. Everything above depends on a regular expression finding the
        // calls; if it stopped matching - a rename, a change of formatting - the loop would find
        // nothing, report no problems, and pass while checking not one call. Checked per shape,
        // because a total high enough to look healthy hides one shape going quiet.
        string tally = string.Join(", ", byShape.Select(p => $"{p.Key}={p.Value}"));
        string[] quiet = [.. byShape.Where(p => p.Value < 3).Select(p => p.Key)];

        Assert.True(
            quiet.Length == 0,
            $"照合できた呼び出しが無い、または少なすぎる形: {string.Join(", ", quiet)}（内訳 {tally}）");
        Assert.True(checkedCalls >= 40, $"照合できた呼び出しが少なすぎる: {checkedCalls}（内訳 {tally}）");
    }

    [Fact]
    public void 書式指定の引数の個数が言語間で一致する()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        // A language that uses fewer placeholders than the caller supplies is harmless, but one
        // that uses more throws FormatException at run time, in whichever language is selected.
        Regex placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

        List<string> mismatched = [];
        foreach (KeyValuePair<string, string> pair in ja)
        {
            if (!en.TryGetValue(pair.Key, out string? english))
            {
                continue;
            }

            int Highest(string text) => placeholder.Matches(text)
                .Select(m => int.Parse(m.Groups[1].Value))
                .DefaultIfEmpty(-1)
                .Max();

            if (Highest(pair.Value) != Highest(english))
            {
                mismatched.Add(pair.Key);
            }
        }

        Assert.True(
            mismatched.Count == 0,
            $"日本語と英語で書式指定子の個数が違う: {string.Join(", ", mismatched.Order())}");
    }

    [Fact]
    public void プリセットとその分類の名前が日英ともに定義されている()
    {
        // preset.<key> and presetGroup.<category> are the only keys built by concatenation, and
        // the scans above only see literals, so these thirty-eight keys were checked by nothing.
        // A missing key does not fail: the indexer hands back the key itself, so the list would
        // quietly show "preset.foo" as a character's name in both languages.
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        PresetLibrary library = PresetLibrary.LoadEmbedded();

        // "starter" and "template" belong to the part guide, which PresetChoice.PartGuide builds
        // by hand rather than reading from the library.
        List<string> keys =
        [
            "preset.starter",
            "presetGroup.template",
            .. library.Presets.Select(p => $"preset.{p.Key}"),
            .. library.Categories.Select(c => $"presetGroup.{c}"),
        ];

        List<string> missing = [];
        foreach (string key in keys.Distinct().Order())
        {
            foreach ((string language, Dictionary<string, string> table) in
                     new[] { ("ja", ja), ("en", en) })
            {
                if (!table.ContainsKey(key))
                {
                    missing.Add($"lang.{language}.json に '{key}' が無い");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
        Assert.Equal(38, keys.Distinct().Count());
    }

    [Fact]
    public void Cコードが指標子で参照するキーがすべて定義されている()
    {
        // The scans above cover {loc:Tr ...} in XAML and the four keyed call shapes in C#. None
        // of them sees Loc.Instance["key"], which is how the confirmation dialogs, the list
        // labels and the one-line notices are written, so those keys were checked by nothing at
        // all. The indexer answers a missing key with the key itself, so deleting one does not
        // fail: it puts "status.starter" on screen where a sentence belongs. Key removal is real
        // work here - three unused keys were deleted in one of the earlier rounds of this review.
        //
        // Status lines used to be written this way as well. They go through SetStatus now and
        // are covered by the scan above, which is why this one sees fewer keys than it once did.
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        Regex indexer = new(@"Loc\.Instance\[\s*""(?<key>[^""]+)""\s*\]", RegexOptions.Compiled);

        string[] sources = [
            .. Directory.GetFiles(Path.Combine(root, "src", "gui"), "*.cs", SearchOption.AllDirectories),
            .. Directory.GetFiles(Path.Combine(root, "src", "core"), "*.cs", SearchOption.AllDirectories),
        ];

        List<string> problems = [];
        int checkedKeys = 0;

        foreach (string file in sources.Where(f =>
                     !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (Match match in indexer.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups["key"].Value;
                checkedKeys++;

                foreach ((string language, Dictionary<string, string> table) in
                         new[] { ("ja", ja), ("en", en) })
                {
                    if (!table.ContainsKey(key))
                    {
                        problems.Add($"{Path.GetFileName(file)}: lang.{language}.json に '{key}' が無い");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Order()));

        // Guards the scan itself: a regex that stops matching would otherwise report success
        // by finding nothing to check.
        Assert.True(checkedKeys >= 20, $"指標子アクセスの検出数が少なすぎる: {checkedKeys}");
    }

    [Fact]
    public void 選んでから渡される鍵も日英に存在する()
    {
        // Every scan above starts from a call: .Format("key"), SetStatus("key"),
        // Loc.Instance["key"]. A key that is chosen first and handed on afterwards is invisible to
        // all of them, and fifty of them are written that way - the notice window picks its title
        // with a switch and passes the result to a helper, list rows carry their label key as a
        // constructor argument, the gear line chooses between two keys with a conditional inside
        // the brackets, and two of the pipeline errors pick their key the same way.
        //
        // The first thing the user ever sees is in that group. A missing key resolves to itself,
        // so a deleted one would put "startup.title" in the title bar of the notice shown before
        // any window they can act on, with nothing anywhere to have caught it.
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        // Written out rather than read from the language file. Read from the file, deleting a
        // whole namespace would take its prefix out of the pattern as well, and the literals left
        // behind in the code would stop being checked at the very moment they became wrong.
        string[] namespaces =
        [
            "action", "anim", "app", "background", "character", "confirm", "dialog", "dir",
            "editor", "env", "error", "gear", "help", "import", "motion", "outline", "pixelart",
            "preset", "presetGroup", "preview", "section", "size", "startup", "status", "tool",
            "top", "uninstall", "views",
        ];

        // _language holds the language's own name and is not a namespace. Only additions fail:
        // a namespace emptied on purpose leaves nothing to cover, and a prefix left in the list
        // with no keys behind it makes any literal still using it show up as missing - which is
        // the answer wanted anyway.
        string[] added = [.. ja.Keys
            .Select(key => key.Split('.')[0])
            .Where(prefix => prefix != "_language" && !namespaces.Contains(prefix))
            .Distinct()
            .Order()];

        Assert.True(
            added.Length == 0,
            "鍵の名前空間が増えている。上の一覧に加えること: " + string.Join(", ", added));

        // Anchored on the opening quote and then on a namespace, rather than pairing quotes across
        // the file. A general string scan loses its place at the first literal holding a backslash
        // and quietly checks a fraction of what it appears to.
        Regex literal = new(
            $"\"((?:{string.Join('|', namespaces)})\\.[A-Za-z0-9.]+)\"", RegexOptions.Compiled);

        string[] sources = [
            .. Directory.GetFiles(Path.Combine(root, "src", "gui"), "*.cs", SearchOption.AllDirectories),
            .. Directory.GetFiles(Path.Combine(root, "src", "core"), "*.cs", SearchOption.AllDirectories),
        ];

        List<string> problems = [];
        int checkedKeys = 0;

        foreach (string file in sources.Where(f =>
                     !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (Match match in literal.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value;
                checkedKeys++;

                foreach ((string language, Dictionary<string, string> table) in
                         new[] { ("ja", ja), ("en", en) })
                {
                    if (!table.ContainsKey(key))
                    {
                        problems.Add($"{Path.GetFileName(file)}: lang.{language}.json に '{key}' が無い");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct().Order()));

        // Guards the scan itself, as the scans above do.
        Assert.True(checkedKeys >= 150, $"鍵リテラルの検出数が少なすぎる: {checkedKeys}");
    }

    [Fact]
    public void 配布物のreadmeが案内する画面の文言が実在する()
    {
        // readme.txt tells the user which buttons to press, and it is the only instruction they
        // have before the application opens. A label renamed in the language files would leave
        // it pointing at a button that no longer exists - which is exactly what happened to the
        // in-application help, where four separate passages described behaviour the program did
        // not have. Nothing outside this test compares the two.
        string root = FindRepositoryRoot();
        string readmePath = Path.Combine(root, "packaging", "readme.txt");

        Assert.True(File.Exists(readmePath), $"配布物の readme が無い: {readmePath}");

        string readme = File.ReadAllText(readmePath);

        string[] japanese = [.. LoadLanguage(root, "ja").Values];
        string[] english = [.. LoadLanguage(root, "en").Values];

        // Quoted with the corner brackets in Japanese and with double quotes in English, which
        // is what makes them findable here without listing them twice.
        Regex quoted = new(@"「(?<label>[^」]+)」|""(?<label>[^""\r\n]+)""", RegexOptions.Compiled);

        // Windows says these, not this application, so they cannot be found in the language
        // files. They are also the reason the readme exists: they appear before the program
        // gets to run at all, which is why the readme walks through them in detail.
        //
        // Kept as a list rather than by skipping the whole section: a wrong button name is
        // exactly as unhelpful whichever program owns the button, and an explicit list is
        // something a reader can check against their own copy of Windows.
        string[] windowsOwn =
        [
            "Windows によって PC が保護されました",
            "詳細情報",
            "実行",
            "プロパティ",
            "全般",
            "許可する",
            "ブロックの解除",
            "More info",
            "Run anyway",
            "Properties",
            "General",
            "Unblock",
            "OK",
            "Unknown publisher",
            "不明な発行元",
        ];

        List<string> missing = [];
        int checkedLabels = 0;

        foreach (Match match in quoted.Matches(readme))
        {
            string label = match.Groups["label"].Value.Trim();

            // Only screen labels are of interest; prose in quotation marks is not. A button
            // name is short, carries no sentence punctuation, and starts with a letter - which
            // also throws away the fragments that fall between two quoted sentences, where the
            // closing quote of one and the opening quote of the next pair up by accident.
            bool looksLikeLabel =
                label.Length is > 0 and <= 24
                && !label.Contains(',')
                && !label.Contains('、')
                && !label.Contains('。')
                && !label.EndsWith('.')
                && char.IsLetter(label[0]);

            if (!looksLikeLabel || windowsOwn.Contains(label, StringComparer.Ordinal))
            {
                continue;
            }

            checkedLabels++;

            if (!japanese.Any(v => v.Contains(label, StringComparison.Ordinal))
                && !english.Any(v => v.Contains(label, StringComparison.Ordinal)))
            {
                missing.Add(label);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"readme.txt が画面に無い文言を案内している: {string.Join(" / ", missing.Distinct().Order())}");

        // Guards the scan: a regex that stopped matching would report success by finding nothing
        Assert.True(checkedLabels >= 8, $"照合できた文言が少なすぎる: {checkedLabels}");
    }

    [Fact]
    public void ヘルプが案内する画面の文言が実在する()
    {
        // The same rule the readme is held to, applied to the help inside the window. It is the
        // longer of the two documents and the one that names the most controls, yet nothing
        // compared it against the labels until now - which is how four passages came to describe
        // behaviour the program did not have.
        string root = FindRepositoryRoot();

        string[] japanese = [.. LoadLanguage(root, "ja").Values];
        string[] english = [.. LoadLanguage(root, "en").Values];

        Regex quoted = new(@"「(?<label>[^」]+)」|""(?<label>[^""\r\n]+)""", RegexOptions.Compiled);

        // Said by Windows or by the game, not by this application, so they are not in the
        // language files. Kept explicit rather than skipping whole passages.
        string[] elsewhere =
        [
            "Windows によって PC が保護されました", "詳細情報", "実行", "プロパティ", "全般",
            "許可する", "ブロックの解除", "More info", "Run anyway", "Properties", "General",
            "Unblock", "OK", "Unknown publisher", "不明な発行元",
        ];

        List<string> missing = [];
        int checkedLabels = 0;

        foreach (string language in new[] { "ja", "en" })
        {
            string path = Path.Combine(root, "src", "core", "Localization", $"help.{language}.json");
            Assert.True(File.Exists(path), $"ヘルプが無い: {path}");

            // Parsed rather than scanned as text: the raw file is full of quoted JSON member
            // names and of the headings and terms that name the passages, none of which are
            // screen labels. Only the prose is of interest.
            foreach (Match match in quoted.Matches(string.Join(Environment.NewLine, Prose(path))))
            {
                string label = match.Groups["label"].Value.Trim();

                // Screen labels only. A button name is short, carries no sentence punctuation,
                // and starts with a letter; prose in quotation marks is not of interest here.
                bool looksLikeLabel =
                    label.Length is > 0 and <= 24
                    && !label.Contains(',')
                    && !label.Contains('、')
                    && !label.Contains('。')
                    && !label.EndsWith('.')
                    && char.IsLetter(label[0]);

                if (!looksLikeLabel || elsewhere.Contains(label, StringComparer.Ordinal))
                {
                    continue;
                }

                checkedLabels++;

                if (!japanese.Any(v => v.Contains(label, StringComparison.Ordinal))
                    && !english.Any(v => v.Contains(label, StringComparison.Ordinal)))
                {
                    missing.Add($"help.{language}: {label}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"ヘルプが画面に無い文言を案内している: {string.Join(" / ", missing.Distinct().Order())}");

        Assert.True(checkedLabels >= 20, $"照合できた文言が少なすぎる: {checkedLabels}");
    }

    [Fact]
    public void 取り込み設定のチェックボックスはすべてヘルプに載っている()
    {
        // A setting that is on by default and explained nowhere is the worst combination: the
        // user sees its effect, cannot find out what it is, and has no reason to look for a
        // switch. "Hide the face on back-facing frames" shipped that way.
        string root = FindRepositoryRoot();
        Dictionary<string, string> ja = LoadLanguage(root, "ja");
        Dictionary<string, string> en = LoadLanguage(root, "en");

        string axaml = File.ReadAllText(
            Path.Combine(root, "src", "gui", "Views", "MainWindow.axaml"));

        // Every checkbox in the window, by the key its label comes from
        Regex checkbox = new(
            @"<CheckBox\b[^>]*?Content=""\{loc:Tr\s+(?<key>[A-Za-z0-9._]+)\}""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        string helpJa = File.ReadAllText(
            Path.Combine(root, "src", "core", "Localization", "help.ja.json"));
        string helpEn = File.ReadAllText(
            Path.Combine(root, "src", "core", "Localization", "help.en.json"));

        List<string> missing = [];
        int checkedBoxes = 0;

        foreach (Match match in checkbox.Matches(axaml))
        {
            string key = match.Groups["key"].Value;
            if (!ja.TryGetValue(key, out string? labelJa) || !en.TryGetValue(key, out string? labelEn))
            {
                continue;
            }

            checkedBoxes++;

            // A label may carry a parenthetical qualifier that the help leaves off when it
            // names the control in a sentence. The name is what has to match.
            labelJa = Trim(labelJa);
            labelEn = Trim(labelEn);

            if (!helpJa.Contains(labelJa, StringComparison.Ordinal))
            {
                missing.Add($"help.ja: {labelJa}");
            }

            if (!helpEn.Contains(labelEn, StringComparison.Ordinal))
            {
                missing.Add($"help.en: {labelEn}");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"ヘルプに説明の無いチェックボックスがある: {string.Join(" / ", missing.Distinct().Order())}");

        Assert.True(checkedBoxes >= 8, $"照合できたチェックボックスが少なすぎる: {checkedBoxes}");
    }

    /// <summary>Drops a trailing parenthetical, leaving the name of the control.</summary>
    private static string Trim(string label)
    {
        int bracket = label.IndexOfAny(['(', '（']);
        return (bracket > 0 ? label[..bracket] : label).TrimEnd();
    }

    /// <summary>
    /// Every line of prose in a help file: the paragraphs, and the descriptions under each term.
    /// The headings and the terms themselves are names for passages, not labels on screen.
    /// </summary>
    private static List<string> Prose(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        List<string> prose = [];

        if (!document.RootElement.TryGetProperty("sections", out JsonElement sections))
        {
            return prose;
        }

        foreach (JsonElement section in sections.EnumerateArray())
        {
            if (section.TryGetProperty("paragraphs", out JsonElement paragraphs))
            {
                prose.AddRange(paragraphs.EnumerateArray().Select(p => p.GetString() ?? string.Empty));
            }

            if (!section.TryGetProperty("terms", out JsonElement terms))
            {
                continue;
            }

            foreach (JsonElement term in terms.EnumerateArray())
            {
                if (term.TryGetProperty("description", out JsonElement description))
                {
                    prose.Add(description.GetString() ?? string.Empty);
                }
            }
        }

        return prose;
    }
}

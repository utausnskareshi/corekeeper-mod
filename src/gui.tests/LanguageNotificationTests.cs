using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Checks that every computed property whose value depends on the language is re-announced when
/// the language changes.
///
/// A property like <c>public string SideImageLabel => FacingLabel(_sidePath);</c> reads the
/// localisation on every get and has no backing field, so nothing raises it on its own. It has to
/// be named in <c>OnLanguageChanged</c> by hand, and the list there is easy to forget: the two
/// facing labels were added without it, and switching to English left the line under both buttons
/// in Japanese for the rest of the session - in the state the window opens in, so every user
/// would have seen it.
///
/// <see cref="TranslationCoverageTests"/> already reads the repository's own source to check the
/// keys exist; this reads it to check the notifications are wired, which no test that only
/// exercises <c>Loc</c> can see.
/// </summary>
public sealed class LanguageNotificationTests
{
    internal static string ViewModelSource()
    {
        string path = Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(),
            "src", "gui", "ViewModels", "MainViewModel.cs");

        Assert.True(File.Exists(path), $"MainViewModel.cs が無い: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The body of MainViewModel, so properties belonging to the small helper classes in the
    /// same file are not mistaken for its own.
    /// </summary>
    internal static string ViewModelClass(string source)
    {
        int start = source.IndexOf("public sealed partial class MainViewModel", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainViewModel の宣言が見つからない");

        // Ends where the next top-level type begins, or at the end of the file
        Match next = Regex.Match(
            source[start..],
            @"^public (sealed |abstract )?(class|record) ",
            RegexOptions.Multiline);

        // Skip(1) because the class's own declaration matches too
        MatchCollection all = Regex.Matches(
            source[start..],
            @"^public (sealed |abstract )?(partial )?(class|record) ",
            RegexOptions.Multiline);

        int length = all.Count > 1 ? all[1].Index : source.Length - start;
        return source.Substring(start, length);
    }

    /// <summary>The body of one method, by brace counting from its signature.</summary>
    internal static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{signature} が見つからない");

        int open = source.IndexOf('{', at);
        Assert.True(open >= 0, $"{signature} の本体が見つからない");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[open..i]; }
        }

        throw new InvalidOperationException($"{signature} の本体が閉じていない");
    }

    /// <summary>Private helpers that read the localisation, so a property calling one also does.</summary>
    private static HashSet<string> LocalisedHelpers(string body)
    {
        HashSet<string> helpers = [];

        foreach (Match match in Regex.Matches(
                     body,
                     @"private\s+[\w<>?\[\],\.]+\s+(\w+)\s*\([^)]*\)\s*=>\s*([^;]+);",
                     RegexOptions.Singleline))
        {
            if (ReadsLocalisation(match.Groups[2].Value))
            {
                helpers.Add(match.Groups[1].Value);
            }
        }

        return helpers;
    }

    /// <summary>
    /// Whether an expression takes a value out of the localisation, rather than merely handing
    /// the object over. <c>Localization => Loc.Instance</c> is the whole of the latter case: the
    /// list at the top right binds through it, and what it returns is the same object in every
    /// language, so re-announcing it would say nothing.
    /// </summary>
    private static bool ReadsLocalisation(string expression) =>
        Regex.IsMatch(expression, @"Loc\.Instance\s*(\[|\.)");

    [Fact]
    public void 言語で変わる算出プロパティは全て言語切替で通知される()
    {
        string body = ViewModelClass(ViewModelSource());
        string announced = MethodBody(body, "private void OnLanguageChanged()");
        HashSet<string> helpers = LocalisedHelpers(body);

        List<string> missing = [];

        // Expression-bodied public properties: "public string Name => <expression>;"
        foreach (Match match in Regex.Matches(
                     body,
                     @"public\s+[\w<>?\[\],\.]+\s+(\w+)\s*=>\s*([^;]+);",
                     RegexOptions.Singleline))
        {
            string name = match.Groups[1].Value;
            string expression = match.Groups[2].Value;

            // A method, not a property
            if (expression.StartsWith('('))
            {
                continue;
            }

            bool localised = ReadsLocalisation(expression)
                             || helpers.Any(h => Regex.IsMatch(expression, $@"\b{Regex.Escape(h)}\s*\("));

            if (localised && !announced.Contains($"nameof({name})", StringComparison.Ordinal))
            {
                missing.Add(name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "言語切替で再通知されない算出プロパティ: " + string.Join("、", missing.Order()) +
            Environment.NewLine +
            "  OnLanguageChanged に OnPropertyChanged(nameof(…)) を足すこと。");
    }

    [Fact]
    public void 検査が実際にプロパティを見つけている()
    {
        // Without this the test above passes by finding nothing at all, which is how a check
        // that reads source goes quietly dead when the code it reads is reformatted.
        string body = ViewModelClass(ViewModelSource());
        string announced = MethodBody(body, "private void OnLanguageChanged()");

        Assert.Contains("nameof(SideImageLabel)", announced, StringComparison.Ordinal);
        Assert.Contains("nameof(PlayButtonLabel)", announced, StringComparison.Ordinal);
        Assert.NotEmpty(LocalisedHelpers(body));
    }
}

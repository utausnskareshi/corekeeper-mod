using System.Reflection;
using CoreKeeperSkinTool.Gui.Localization;
using System.Text.Json;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Language resource tests.
///
/// A missing translation only puts the key on screen rather than failing,
/// which is hard to notice by running the app, so they are compared mechanically here.
/// </summary>
public sealed class LocalizationTests
{
    private const string BaseLanguage = "lang.ja.json";

    /// <summary>Reads the embedded language files.</summary>
    private static Dictionary<string, Dictionary<string, string>> LoadAll()
    {
        Assembly assembly = Loc.LanguageResourceAssembly;
        Dictionary<string, Dictionary<string, string>> tables = [];

        foreach (string name in assembly.GetManifestResourceNames())
        {
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(name, @"lang\.(?<code>[A-Za-z0-9\-]+)\.json$");
            if (!match.Success)
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            tables[$"lang.{match.Groups["code"].Value}.json"] =
                JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }

        return tables;
    }

    [Fact]
    public void 言語ファイルが複数埋め込まれている()
    {
        Dictionary<string, Dictionary<string, string>> tables = LoadAll();

        string actual = string.Join(", ", Loc.LanguageResourceAssembly.GetManifestResourceNames());
        Assert.True(tables.Count >= 2, $"言語ファイルが {tables.Count} 件しか無い。埋め込み一覧: [{actual}]");
        Assert.Contains(BaseLanguage, tables.Keys);
        Assert.Contains("lang.en.json", tables.Keys);
    }

    [Fact]
    public void すべての言語が自分の言語名を持つ()
    {
        foreach ((string file, Dictionary<string, string> table) in LoadAll())
        {
            Assert.True(table.ContainsKey("_language"), $"{file} に _language が無い");
            Assert.False(string.IsNullOrWhiteSpace(table["_language"]), $"{file} の _language が空");
        }
    }

    [Fact]
    public void 訳の抜けが無い()
    {
        Dictionary<string, Dictionary<string, string>> tables = LoadAll();
        Dictionary<string, string> baseTable = tables[BaseLanguage];

        List<string> problems = [];
        foreach ((string file, Dictionary<string, string> table) in tables)
        {
            if (file == BaseLanguage)
            {
                continue;
            }

            foreach (string key in baseTable.Keys.Where(k => !table.ContainsKey(k)))
            {
                problems.Add($"{file}: {key} が無い");
            }

            foreach (string key in table.Keys.Where(k => !baseTable.ContainsKey(k)))
            {
                problems.Add($"{file}: {key} は {BaseLanguage} に無い余分なキー");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void 差し込み位置の数がどの言語でも一致する()
    {
        // A mismatched placeholder count only fails once that language is selected
        Dictionary<string, Dictionary<string, string>> tables = LoadAll();
        Dictionary<string, string> baseTable = tables[BaseLanguage];

        List<string> problems = [];
        foreach ((string file, Dictionary<string, string> table) in tables)
        {
            if (file == BaseLanguage)
            {
                continue;
            }

            foreach ((string key, string text) in table)
            {
                if (!baseTable.TryGetValue(key, out string? baseText))
                {
                    continue;
                }

                int expected = CountPlaceholders(baseText);
                int actual = CountPlaceholders(text);
                if (expected != actual)
                {
                    problems.Add($"{file}: {key} の差し込みが {actual} 個（{BaseLanguage} は {expected} 個）");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void 値が空の項目が無い()
    {
        List<string> problems = [];
        foreach ((string file, Dictionary<string, string> table) in LoadAll())
        {
            problems.AddRange(table
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{file}: {pair.Key} が空"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Counts how many distinct {0}-style placeholders a string uses.</summary>
    private static int CountPlaceholders(string text)
    {
        HashSet<string> found = [];
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)\}"))
        {
            found.Add(match.Groups[1].Value);
        }

        return found.Count;
    }

    [Fact]
    public void 変換や配置のエラー文が選んだ言語で出る()
    {
        // Every message src/core raises is written in Japanese, which is what the command line
        // wants. In English the one line saying what went wrong was the only part of the window
        // that stayed Japanese - the part an English reader most needs to read.
        ToolException withKey = new(
            "error.image.notFound", ["C:/nope.png"], "入力画像が見つからない: C:/nope.png");

        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "en");
        string english = Loc.Instance.Describe(withKey);

        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "ja");
        string japanese = Loc.Instance.Describe(withKey);

        Assert.Contains("C:/nope.png", english);
        Assert.Contains("C:/nope.png", japanese);
        Assert.NotEqual(english, japanese);
        Assert.DoesNotContain("入力画像", english);

        // An error with no key keeps its own text, which is still better than saying nothing
        ToolException plain = new("鍵の無いエラー");
        Assert.Equal("鍵の無いエラー", Loc.Instance.Describe(plain));

        // And so does anything that is not a ToolException at all
        Assert.Equal("よそのエラー", Loc.Instance.Describe(new InvalidOperationException("よそのエラー")));
    }

    [Fact]
    public void 向きが空になったエラーは両言語で数字が正しい位置に入る()
    {
        // These keys are the ones the scan cannot see. Every other keyed error hands its key over
        // as a literal, so TranslationCoverageTests can read the call and count what it supplies;
        // these are chosen from a table and passed in a variable, which that scan skips (its own
        // comment says so of the two facingVanished keys). Nothing else would notice if the
        // arguments went in the wrong order and the window showed the placement size as an offset.
        //
        // Driven through the pipeline rather than by building the exception here, so that what is
        // pinned is the order Build actually passes, not a copy of it written alongside.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(200, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front, side),
            SheetLayout.LoadEmbedded(),
            new SkinOptions(OffsetX: 21)));

        Assert.Equal("error.pipeline.facingEmptyFront", error.MessageKey);

        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "ja");
        Assert.Contains("横 21 / 縦 0、配置サイズ 16x19", Loc.Instance.Describe(error), StringComparison.Ordinal);

        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "en");
        string english = Loc.Instance.Describe(error);
        Assert.Contains("horizontal 21 / vertical 0, placement size 16x19", english, StringComparison.Ordinal);
        Assert.DoesNotContain("正面", english, StringComparison.Ordinal);

        // The fifth key takes the facing's own spelling first, so its numbers sit one place along.
        // A layout may name its facings anything, and that name is quoted back rather than
        // translated - it came from the user's file.
        Assert.Contains(
            "horizontal 1 / vertical 2, placement size 3x4",
            Loc.Instance.Format("error.pipeline.facingEmptyNamed", "SIDEWAYS", 1, 2, 3, 4),
            StringComparison.Ordinal);

        Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "ja");
        string named = Loc.Instance.Format("error.pipeline.facingEmptyNamed", "SIDEWAYS", 1, 2, 3, 4);
        Assert.StartsWith("SIDEWAYS", named, StringComparison.Ordinal);
        Assert.Contains("横 1 / 縦 2、配置サイズ 3x4", named, StringComparison.Ordinal);
    }

    private static SKBitmap Solid(int width, int height, SKColor colour)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, colour);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    [Fact]
    public void エラー文のキーが日英ともに定義され差し込み数も一致する()
    {
        // The keys are handed to string.Format with the arguments the throw site supplied, so a
        // template asking for one more value than was passed would turn a handled error into a
        // FormatException. Describe catches that, but the message would then silently fall back
        // to Japanese, which is exactly what this whole change set out to stop.
        Dictionary<string, Dictionary<string, string>> tables = LoadAll();
        Dictionary<string, string> ja = tables[BaseLanguage];

        string[] errorKeys =
            [.. ja.Keys.Where(k => k.StartsWith("error.", StringComparison.Ordinal)).Order()];

        Assert.NotEmpty(errorKeys);

        List<string> problems = [];
        foreach ((string file, Dictionary<string, string> table) in tables)
        {
            foreach (string key in errorKeys)
            {
                if (!table.TryGetValue(key, out string? text))
                {
                    problems.Add($"{file} に '{key}' が無い");
                    continue;
                }

                if (CountPlaceholders(text) != CountPlaceholders(ja[key]))
                {
                    problems.Add($"{file} の '{key}' の差し込み数が {BaseLanguage} と違う");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}

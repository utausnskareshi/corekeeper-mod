using CoreKeeperSkinTool.Help;
using CoreKeeperSkinTool.Layout;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Help text tests.
///
/// A missing translation or section only shows up once that language is selected,
/// so they are checked mechanically here without opening the window.
/// </summary>
public sealed class HelpContentTests
{
    private const string BaseLanguage = "ja";

    /// <summary>Everything the help says, flattened, so a phrase can be looked for anywhere in it.</summary>
    private static string AllText(string language)
    {
        HelpDocument document = HelpContent.Load(language);
        System.Text.StringBuilder builder = new();

        foreach (HelpSection section in document.Sections)
        {
            builder.AppendLine(section.Heading);

            foreach (string paragraph in section.Paragraphs)
            {
                builder.AppendLine(paragraph);
            }

            foreach (HelpTerm term in section.Terms)
            {
                builder.AppendLine(term.Term);
                builder.AppendLine(term.Description);
            }
        }

        return builder.ToString();
    }

    [Theory]
    // Controls whose on-screen wording changed as the application grew. The help described the
    // old wording for a while, which is worse than saying nothing: it sends the reader looking
    // for a control that is not there.
    [InlineData("ja", "歩行と攻撃に動きを付ける")]
    [InlineData("ja", "変形させず位置だけ動かす")]
    [InlineData("ja", "プリセット")]
    [InlineData("ja", "ランダム")]
    [InlineData("ja", "色だけ差し替え")]
    [InlineData("ja", "パーツ確認用")]
    [InlineData("ja", "一時停止")]
    [InlineData("ja", "取得")]
    [InlineData("ja", "MOD を更新")]
    [InlineData("ja", "市松模様")]
    [InlineData("en", "Animate the walk and the attack")]
    [InlineData("en", "Only move the art, never reshape it")]
    [InlineData("en", "Preset")]
    [InlineData("en", "Random")]
    [InlineData("en", "Recolour only")]
    [InlineData("en", "Part guide")]
    [InlineData("en", "Pause")]
    [InlineData("en", "Fetch")]
    [InlineData("en", "Update mod")]
    [InlineData("en", "chequerboard")]
    public void 現在の画面にある機能がヘルプに載っている(string language, string phrase)
    {
        Assert.Contains(phrase, AllText(language));
    }

    [Theory]
    // Wording that was removed from the interface. Leaving it in the help sends the reader
    // hunting for a control that no longer exists.
    [InlineData("ja", "歩行に上下動を付ける")]
    [InlineData("en", "Add a bob to the walk cycle")]
    // Statements the program does not bear out. Pictures turned 18 of the presets into front-facing
    // art; the import settings do act on a preset shown after a picture was opened; the mod shows
    // the replaced look only for your own character on your own screen; and the drawing goes
    // unprotected after a single undone stroke, not only after Undo and Redo in a row.
    [InlineData("ja", "最初から右向きに描かれている")]
    [InlineData("en", "which are drawn facing right already")]
    [InlineData("ja", "各パーツの位置と大きさは、ゲーム本来のキャラクターを実測した値に合わせてあります")]
    [InlineData("en", "The position and size of every part matches measurements")]
    [InlineData("ja", "これらの設定は使いません。変換の元になる画像が無いためです")]
    [InlineData("en", "These settings do nothing while a preset is on screen, or while")]
    [InlineData("ja", "参加者全員が同じ MOD と同じ画像を導入している必要があります")]
    [InlineData("en", "everyone needs the same mod and the same image")]
    [InlineData("ja", "「元に戻す」「やり直す」と続けて押すと、手描きが守られない状態になります")]
    [InlineData("en", "and then pressing \"Undo\" and \"Redo\" in turn leaves your drawing unprotected")]
    // Written before "Update mod" existed, when replacing an installed mod meant removing it first.
    // Removing takes the settings folder with it, so following this deleted every character's
    // picture, every captured look and the armour settings, for the same result "Update mod" gives.
    [InlineData("ja", "「MOD を削除」で一度取り除いてから")]
    [InlineData("en", "Press \"Remove mod\" in the status bar first")]
    // The application's folder going takes its settings with it, not everything: the game's
    // working copy of the mod and this application's unpacked parts stay in %TEMP%.
    [InlineData("en", "so deleting it leaves nothing behind")]
    public void 廃止した文言がヘルプに残っていない(string language, string phrase)
    {
        Assert.DoesNotContain(phrase, AllText(language));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 対応環境の記述が実装と矛盾しない(string language)
    {
        // GameLocator gives up immediately off Windows, so neither the game nor its settings
        // folder can be found there. Claiming Linux support would be a promise the code breaks.
        string text = AllText(language);

        Assert.DoesNotContain("Linux", text);
        Assert.Contains(language == "ja" ? "Windows 版のみ" : "Windows only", text);
    }

    [Theory]
    [InlineData("ja", new[] { "素肌", "髪", "目", "シャツ", "ズボン", "兜", "胴防具", "脚防具", "手持ち装備" })]
    [InlineData("en", new[] { "bare body", "hair", "eyes", "shirt", "trousers", "helmet", "chest armour", "leg armour", "held item" })]
    public void パーツ確認用の色をすべて説明している(string language, string[] parts)
    {
        // The guide draws eight measured parts plus the held-item marker. An explanation that
        // covers only some of them leaves the reader guessing at the rest, which is exactly
        // what the diagram exists to prevent.
        HelpDocument document = HelpContent.Load(language);

        HelpSection section = document.Sections.Single(s =>
            s.Terms.Count > 0 && s.Terms.All(t => t.Term.Contains('=') || t.Term.Contains('＝')));

        string terms = string.Join("\n", section.Terms.Select(t => t.Term));

        foreach (string part in parts)
        {
            Assert.Contains(part, terms, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(parts.Length, section.Terms.Count);
    }

    [Theory]
    [InlineData("ja", "薄い灰色")]
    [InlineData("en", "faint grey")]
    public void 手持ち装備の色を灰色と書いていない(string language, string phrase)
    {
        // The marker is a translucent amber (FFD170), not grey. Describing it wrongly sends
        // the reader looking for the wrong pixels.
        Assert.DoesNotContain(phrase, AllText(language), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ja", "手に持つ")]
    [InlineData("en", "Held weapons")]
    public void 手持ち装備が変更できない旨を明記している(string language, string phrase)
    {
        // Asked about directly, and the answer is not obvious from the interface
        Assert.Contains(phrase, AllText(language));
    }

    [Theory]
    [InlineData("ja", "動物に乗っている間も、ずっとこのコマが使われます")]
    [InlineData("en", "the whole time you are riding an animal")]
    public void 動物に乗っている間のコマを説明している(string language, string phrase)
    {
        // Measured on 1.3.0.3: riding an animal shows columns 7 to 9 of row 6 (sitting with the
        // arms forward) the whole time. The grid section never said so, and those three cells are
        // what the player looks at for as long as they ride.
        Assert.Contains(phrase, AllText(language));
    }

    [Theory]
    [InlineData("ja", "一覧で別の項目をいったん選んでから選び直してください")]
    [InlineData("en", "choose a different entry in the list first")]
    public void 置き換わったプリセットへの戻し方を書いている(string language, string phrase)
    {
        // The list keeps showing the last preset chosen after a conversion replaced it, and
        // choosing the entry it already shows raises no change, so nothing loads. The help said the
        // preset is replaced but not how to get it back (the test campaign of 2026-09-30).
        Assert.Contains(phrase, AllText(language));
    }

    /// <summary>
    /// The grid section tells the reader how many cells the sheet has, how many of them are
    /// frames, and how many are left empty. All three follow from the layout, so a game update
    /// that reshapes the sheet would leave the help stating numbers that no longer hold - and
    /// the empty cells are exactly what readers ask about, so a wrong count is worse than none.
    /// </summary>
    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void コマ割りの説明が実際のレイアウトと一致する(string language)
    {
        SheetLayout layout = SheetLayout.LoadEmbedded();

        int cells = (layout.Texture.Width / layout.Cell.Width)
            * (layout.Texture.Height / layout.Cell.Height);
        int unused = cells - layout.FrameCount;

        // Searched in the layout section alone, and with the words that go round each number.
        // Against the whole help text the counts were found in sentences that have nothing to do
        // with the grid - "39" appears five times in the Japanese file and "15" three, one of
        // them inside the sheet's own size, 234x156 - so the section could have been deleted
        // outright, or its numbers changed to 40 and 14, and this still passed. The empty cells
        // are exactly what readers ask about, so a wrong count is worse than none.
        HelpDocument document = HelpContent.Load(language);

        HelpSection section = document.Sections.SingleOrDefault(
            s => s.Heading.Contains("9×6", StringComparison.Ordinal)
                 || s.Heading.Contains("9x6", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"コマ割りの節が見つからない（{language}）: " +
                string.Join(" / ", document.Sections.Select(s => s.Heading)));

        string text = string.Join(
            Environment.NewLine,
            section.Paragraphs.Concat(section.Terms.Select(t => $"{t.Term} {t.Description}")));

        Assert.Contains($"{cells}", text, StringComparison.Ordinal);

        Assert.Contains(
            language == "ja" ? $"使われるのは{layout.FrameCount}コマ" : $"only {layout.FrameCount} are used",
            text,
            StringComparison.Ordinal);

        Assert.Contains(
            language == "ja" ? $"残りの{unused}マス" : $"remaining {unused} are empty",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 日英で節の構成が一致する()
    {
        HelpDocument ja = HelpContent.Load("ja");
        HelpDocument en = HelpContent.Load("en");

        Assert.Equal(ja.Sections.Count, en.Sections.Count);

        for (int i = 0; i < ja.Sections.Count; i++)
        {
            Assert.Equal(ja.Sections[i].Paragraphs.Count, en.Sections[i].Paragraphs.Count);
            Assert.Equal(ja.Sections[i].Terms.Count, en.Sections[i].Terms.Count);
        }
    }

    [Fact]
    public void 主要な言語のヘルプが埋め込まれている()
    {
        IReadOnlyList<string> languages = HelpContent.AvailableLanguages;

        Assert.Contains(BaseLanguage, languages);
        Assert.Contains("en", languages);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 題名と節がある(string language)
    {
        HelpDocument document = HelpContent.Load(language);

        Assert.False(string.IsNullOrWhiteSpace(document.Title), "題名が空");
        Assert.True(document.Sections.Count > 0, "節が1つも無い");
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 空の節が無い(string language)
    {
        HelpDocument document = HelpContent.Load(language);

        foreach (HelpSection section in document.Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Heading), "見出しが空の節がある");

            int paragraphs = section.Paragraphs?.Count ?? 0;
            int terms = section.Terms?.Count ?? 0;
            Assert.True(paragraphs + terms > 0, $"中身が空の節がある: {section.Heading}");
        }
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 空の段落や説明が無い(string language)
    {
        HelpDocument document = HelpContent.Load(language);

        foreach (HelpSection section in document.Sections)
        {
            foreach (string paragraph in section.Paragraphs ?? [])
            {
                Assert.False(string.IsNullOrWhiteSpace(paragraph), $"空の段落がある: {section.Heading}");
            }

            foreach (HelpTerm term in section.Terms ?? [])
            {
                Assert.False(string.IsNullOrWhiteSpace(term.Term), $"項目名が空: {section.Heading}");
                Assert.False(
                    string.IsNullOrWhiteSpace(term.Description),
                    $"説明が空: {section.Heading} / {term.Term}");
            }
        }
    }

    [Fact]
    public void どの言語も同じ構成になっている()
    {
        // A differing section or item count means one language is missing an explanation
        HelpDocument baseDocument = HelpContent.Load(BaseLanguage);

        List<string> problems = [];
        foreach (string language in HelpContent.AvailableLanguages.Where(l => l != BaseLanguage))
        {
            HelpDocument document = HelpContent.Load(language);

            if (document.Sections.Count != baseDocument.Sections.Count)
            {
                problems.Add($"{language}: 節の数が {document.Sections.Count}（{BaseLanguage} は {baseDocument.Sections.Count}）");
                continue;
            }

            for (int i = 0; i < document.Sections.Count; i++)
            {
                HelpSection expected = baseDocument.Sections[i];
                HelpSection actual = document.Sections[i];

                if ((expected.Paragraphs?.Count ?? 0) != (actual.Paragraphs?.Count ?? 0))
                {
                    problems.Add($"{language}: 「{expected.Heading}」の段落数が違う");
                }

                if ((expected.Terms?.Count ?? 0) != (actual.Terms?.Count ?? 0))
                {
                    problems.Add($"{language}: 「{expected.Heading}」の項目数が違う");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void 免責事項が含まれている()
    {
        // The at-your-own-risk notice is mandatory; this guards against shipping without it.
        foreach (string language in HelpContent.AvailableLanguages)
        {
            HelpDocument document = HelpContent.Load(language);
            string all = string.Join(
                " ",
                document.Sections.SelectMany(s => s.Paragraphs ?? []));

            bool mentionsOwnRisk = language == "ja"
                ? all.Contains("自己の責任") || all.Contains("ご自身の責任")
                : all.Contains("at your own risk", StringComparison.OrdinalIgnoreCase);

            Assert.True(mentionsOwnRisk, $"{language}: 自己責任である旨の記載が見つからない");
        }
    }

    [Fact]
    public void 未知の言語は既定言語へ落とす()
    {
        HelpDocument document = HelpContent.Load("xx");

        Assert.Equal(HelpContent.Load(BaseLanguage).Title, document.Title);
    }

    // ------------------------------------------------------ Image generator prompt

    /// <summary>The section holding the prompt, in one language.</summary>
    private static HelpSection PromptSection(string language) =>
        HelpContent.Load(language).Sections.Single(s => s.HasPrompt);

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 生成AI用プロンプトの節がある(string language)
    {
        Assert.NotEmpty(PromptSection(language).Prompt);
    }

    /// <summary>
    /// The prompt is fed to an image generator in English, so it is one file shared by every
    /// language rather than a copy per language that could drift.
    /// </summary>
    [Fact]
    public void プロンプト本文は全言語で同一()
    {
        string japanese = PromptSection("ja").Prompt;

        foreach (string language in HelpContent.AvailableLanguages)
        {
            Assert.Equal(japanese, PromptSection(language).Prompt);
        }
    }

    [Fact]
    public void プロンプトに差し替え箇所が1つだけある()
    {
        string prompt = PromptSection(BaseLanguage).Prompt;

        // More than one would leave the reader unsure which to replace
        Assert.Equal(1, prompt.Split("{{CHARACTER}}").Length - 1);
    }

    /// <summary>
    /// The numbers in the prompt come from the layout this tool actually targets. If the content
    /// box ever changes, the prompt is telling people to draw to the wrong shape.
    /// </summary>
    [Fact]
    public void プロンプトの寸法が実際のレイアウトと一致する()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();

        string prompt = PromptSection(BaseLanguage).Prompt;

        // Both numbers come from the layout, not just the height. The width used to be written
        // in by hand, so widening the content box would have left the prompt telling people to
        // draw to a shape the tool no longer produces, with this test still green.
        CoreKeeperSkinTool.Layout.PartsLayout parts =
            CoreKeeperSkinTool.Layout.PartsLayout.LoadEmbedded();

        // The character's own silhouette, which is what a drawing is scaled to fill: the union
        // of every measured part in the front-facing idle frame.
        int index = layout.Frames.Single(f => f is { Anim: "idle", Dir: "down" }).Index;
        IReadOnlyList<PartBox> boxes =
        [
            .. new[] { "body", "hair", "shirt", "pants" }
                .Select(name => parts.Part(name)?.For(index))
                .Where(b => b is not null)
                .Select(b => b!),
        ];

        int width = boxes.Max(b => b.X + b.W) - boxes.Min(b => b.X);

        Assert.Contains($"{width} x {layout.ContentBox.Height} pixels", prompt);
    }

    /// <summary>
    /// The prompt claims its proportions are measured from the game, so they have to match the
    /// measurements. It used to tell the generator that the head is the widest part and the
    /// shoulders narrower, which is the opposite of what the game's character does.
    /// </summary>
    [Fact]
    public void プロンプトの体型指定が実測と矛盾しない()
    {
        CoreKeeperSkinTool.Layout.SheetLayout layout =
            CoreKeeperSkinTool.Layout.SheetLayout.LoadEmbedded();
        CoreKeeperSkinTool.Layout.PartsLayout parts =
            CoreKeeperSkinTool.Layout.PartsLayout.LoadEmbedded();

        int index = layout.Frames.Single(f => f is { Anim: "idle", Dir: "down" }).Index;

        PartBox head = parts.Part("hair")!.For(index)!;
        PartBox shoulders = parts.Part("shirt")!.For(index)!;

        Assert.True(
            shoulders.W >= head.W,
            $"実測では肩 {shoulders.W}px が頭 {head.W}px 以上のはず");

        string prompt = PromptSection(BaseLanguage).Prompt;

        // Stated in the positive. Two absences only rule out the exact wording that was wrong
        // once, so the sentence carrying the correct proportion could be reworded or deleted
        // and this test would still pass - which is how the same mistake would come back.
        Assert.Contains("shoulders are the widest part", prompt, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("head is the WIDEST", prompt);
        Assert.DoesNotContain("shoulders are narrower than the head", prompt);
    }

    [Theory]
    [InlineData("ja", "{{CHARACTER}}")]
    [InlineData("en", "{{CHARACTER}}")]
    // Naming the placeholder in the prose as well, so the reader knows what to change
    public void 差し替え箇所の書き方を説明している(string language, string phrase)
    {
        Assert.Contains(phrase, AllText(language));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void プロンプトの節に書き方の例がある(string language)
    {
        // The examples are English text handed to the generator, so they read the same either way
        string terms = string.Join("\n", PromptSection(language).Terms.Select(t => t.Description));

        Assert.Contains("white T-shirt and blue jeans", terms);
        Assert.Contains("silver plate armour", terms);
    }
}

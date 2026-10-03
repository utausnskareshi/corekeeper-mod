using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Two confirmations whose wording said something the window does not do.
/// </summary>
public sealed class ConfirmWordingTests
{
    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "gui")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"リポジトリのルートが見つからない（起点: {AppContext.BaseDirectory}）");
        return directory!.FullName;
    }

    private static string Value(string language, string key)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(Root(), "src", "core", "Localization", $"lang.{language}.json")));
        return document.RootElement.GetProperty(key).GetString() ?? string.Empty;
    }

    [Fact]
    public void 終了の確認は保存しても適用しても偽にならない()
    {
        // The question comes up whenever the sheet differs from what was imported, which stays
        // true after saving and after applying to the game. "保存も適用もされていません" was then
        // false, and read as though the save had failed.
        Assert.DoesNotContain("保存も適用も", Value("ja", "confirm.exit"));
        Assert.DoesNotContain("neither saved nor applied", Value("en", "confirm.exit"));
    }

    [Fact]
    public void 自動検出に戻さない方のボタンはフォルダを選ぶと言う()
    {
        // Declining to go back to detection goes on to the folder picker, as the only way from one
        // chosen folder to another. The button said "やめる", and then the picker opened anyway.
        string code = File.ReadAllText(Path.Combine(Root(), "src", "gui", "Views", "MainWindow.axaml.cs"));

        Assert.Matches(
            @"new\(\s*Loc\.Instance\[""confirm\.gamePathReset""\],\s*""confirm\.gamePathResetOk"",\s*" +
            @"cancelLabelKey:\s*""confirm\.gamePathResetPick""\)",
            code);

        Assert.False(string.IsNullOrWhiteSpace(Value("ja", "confirm.gamePathResetPick")));
        Assert.False(string.IsNullOrWhiteSpace(Value("en", "confirm.gamePathResetPick")));
    }

    [Fact]
    public void 自動検出に戻すかの問いをEscや閉じるボタンで閉じたらフォルダ選択を開かない()
    {
        // Both buttons are an action - "go back to detection" and "choose another folder…" - so,
        // as ConfirmWindow says of such pairs, no answer must not stand for either. Closed with
        // Escape or the close button, the question went on to the folder picker all the same, the
        // same as the second button (the test campaign of 2026-10-01).
        string code = File.ReadAllText(Path.Combine(Root(), "src", "gui", "Views", "MainWindow.axaml.cs"));
        int start = code.IndexOf("private async void OnGamePathClicked(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnGamePathClicked が見つからない");
        string body = code[start..];

        int ask = body.IndexOf("await reset.ShowDialog<bool?>(this)", StringComparison.Ordinal);
        int none = body.IndexOf("answer is null", StringComparison.Ordinal);
        int picker = body.IndexOf("OpenFolderPickerAsync", StringComparison.Ordinal);

        Assert.True(ask >= 0, "問いの答えを受けていない");
        Assert.True(none > ask && none < picker, "答えが無いときにフォルダ選択より前で戻っていない");
    }

    [Fact]
    public void 自動検出に戻すかの問いを閉じたらそこで戻る()
    {
        // The check above sees "answer is null" in the right place; this one sees that it returns.
        // With the branch left empty, Escape and the close button put the folder picker up again
        // (the final review of 2026-10-02, X51b)
        string code = File.ReadAllText(Path.Combine(Root(), "src", "gui", "Views", "MainWindow.axaml.cs"));
        string body = LanguageNotificationTests.MethodBody(code, "private async void OnGamePathClicked(");

        Assert.Matches(@"if \(answer is null\)\s*\{\s*return;\s*\}", body);
    }

    [Fact]
    public void 正面が消えたときの案内は絵を渡していない向きも空になると言う()
    {
        // Copied from the right and back messages, where "only that facing" is true. The front is
        // also what a facing without its own picture is drawn from, so with one extra facing given
        // the other comes out empty as well, and the two languages said different things.
        string ja = Value("ja", "error.pipeline.facingVanishedFront");
        string en = Value("en", "error.pipeline.facingVanishedFront");

        Assert.DoesNotContain("その向きのコマだけ", ja);
        Assert.Contains("絵を渡していない向きのコマ", ja);
        Assert.DoesNotContain("the frames for that facing", en);
        Assert.Contains("any facing without a picture of its own", en);

        // The right and back messages are right as they were and stay so
        Assert.Contains("その向きのコマだけ", Value("ja", "error.pipeline.facingVanishedRight"));
        Assert.Contains("その向きのコマだけ", Value("ja", "error.pipeline.facingVanishedBack"));
    }

    [Fact]
    public void シートとして開く確認は自分で空きマスに描いた場合もあると書く()
    {
        // The empty cells are shown in the whole-sheet view, can be drawn in and are saved into the
        // PNG, and a pen stroke running off the edge of frame 2, 8, 14 or 20 lands in one. Opening
        // that sheet again asked the question on the premise that a sheet this program saved does
        // not have any (the test campaign of 2026-09-30).
        string ja = Value("ja", "confirm.openAsSheet");
        string en = Value("en", "confirm.openAsSheet");

        Assert.DoesNotContain("ふつうコマの外に絵はありません", ja);
        Assert.Contains("空きマス", ja);
        Assert.DoesNotContain("does not normally have any", en);
        Assert.Contains("empty cells", en);

        // The question itself and the way out are kept
        Assert.Contains("どちらとして開きますか", ja);
        Assert.Contains("Esc", ja);
        Assert.Contains("Which is it?", en);
        Assert.Contains("Escape", en);
    }

    [Theory]
    [InlineData("error.pipeline.noOpaque", "完全に透明", "fully transparent", null, null)]
    [InlineData("error.pipeline.noOpaqueRight", "完全に透明", "fully transparent", "右向き", "right-facing")]
    [InlineData("error.pipeline.noOpaqueBack", "完全に透明", "fully transparent", "背面", "back-facing")]
    public void 不透明な画素が残らないときの文は原因と向きを言う(string key, string causeJa, string causeEn, string? facingJa, string? facingEn)
    {
        // What the window shows. Every case said the same thing - the background tolerance or the
        // alpha cut-off - whichever picture was empty, and the likeliest cause, a picture transparent
        // to begin with, was never named (fix 8). The command line's own sentence is pinned by
        // NoOpaqueWordingTests; nothing read these, so putting the old text back passed every test.
        string ja = Value("ja", key);
        string en = Value("en", key);

        Assert.Contains(causeJa, ja);
        Assert.Contains(causeEn, en);

        if (facingJa is null)
        {
            // The front's message names no facing: it stands for any picture that came out empty
            Assert.DoesNotContain("右向き", ja);
            Assert.DoesNotContain("背面", ja);
        }
        else
        {
            Assert.Contains(facingJa, ja);
            Assert.Contains(facingEn!, en);
        }
    }

    [Fact]
    public void 背景の許容差の説明は生成AIの背景の目安と上げる場面を言う()
    {
        // The default is 16 and the help advises about 30 for the white or magenta background of an
        // AI-made picture. Where that background is not quite even, 16 leaves a band of it standing,
        // the trim takes the band as art, and the character comes out small (1024x918 trimmed, 16x14
        // placed, measured in the test campaign of 2026-09-30). The hint under the slider is what is
        // on screen when that happens, and it named only photographs.
        string ja = Value("ja", "background.hint");
        string en = Value("en", "background.hint");

        Assert.Contains("生成AI", ja);
        Assert.Contains("30 前後", ja);
        Assert.Contains("上げて", ja);
        Assert.Contains("AI", en);
        Assert.Contains("around 30", en);
        Assert.Contains("raise it", en);

        // What it said before is kept
        Assert.Contains("写真は 30〜60 程度が目安です", ja);
        Assert.Contains("Photos usually need 30-60", en);
    }

    [Fact]
    public void 取り込みが無いときの案内は起動中なら再起動と言う()
    {
        // The game loads mods once, at start (measured on 1.3.0.2 and 1.3.0.3). Installed while the
        // game was running - or removed and installed again - the mod takes nothing until the game
        // restarts, and "install it, then load the character" sent the user back to a game that
        // could not do it (the test campaign of 2026-09-30).
        Assert.Contains("起動中なら再起動", Value("ja", "status.fetchNothingCaptured"));
        Assert.Contains("restart it if it is running", Value("en", "status.fetchNothingCaptured"));
    }
}

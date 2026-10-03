using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// How the character list puts a player's own character name on screen, read from the source:
/// the window cannot be built here without a display.
/// </summary>
public sealed class CharacterNameDisplayTests
{
    [Fact]
    public void キャラクター名はチェックボックスの文字列のContentにせずTextBlockで出す()
    {
        // The Fluent CheckBox template sets RecognizesAccessKey on its ContentPresenter, so a
        // string Content becomes AccessText. Measured in a real Avalonia 12.1.1 window: a
        // character named "Neko_Mimi" was drawn as "#1 NekoMimi", "A__B" as "#4 A_B", and the
        // letter after the first "_" was registered as an access key - Alt+M silently ticked and
        // unticked that row, which decides what "Apply to game" overwrites. A TextBlock child
        // shows the name exactly as the player typed it and registers nothing.
        string axaml = File.ReadAllText(Path.Combine(
            TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", "Views", "MainWindow.axaml"));

        Match template = Regex.Match(
            axaml, @"<DataTemplate x:DataType=""vm:CharacterChoice"">(?<body>[\s\S]*?)</DataTemplate>");
        Assert.True(template.Success, "キャラクター一覧の DataTemplate が見つからない");
        string body = template.Groups["body"].Value;

        Match open = Regex.Match(body, @"<CheckBox\b(?<attributes>[^>]*)>");
        Assert.True(open.Success, "キャラクター一覧の CheckBox が見つからない");
        Assert.DoesNotMatch(@"\bContent\s*=", open.Groups["attributes"].Value);

        // With a control as its Content the CheckBox's automation peer takes its name from the
        // presenter's TextBlock once the row is laid out, and before that falls back to
        // Content.ToString() - "Avalonia.Controls.TextBlock" (measured with ControlAutomationPeer on
        // Avalonia 12.1.1). Given directly, the name does not depend on the layout.
        Assert.Contains(
            @"AutomationProperties.Name=""{Binding Display}""", open.Groups["attributes"].Value, StringComparison.Ordinal);

        Assert.Matches(
            @"<CheckBox\b[^>]*[^/]>\s*<TextBlock\b[^>]*\bText=""\{Binding Display\}""[^>]*/>\s*</CheckBox>",
            body);
    }
}

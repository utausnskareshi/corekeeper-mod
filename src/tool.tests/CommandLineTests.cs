using CoreKeeperSkinTool.Cli;
using CoreKeeperSkinTool.Sheet;

namespace CoreKeeperSkinTool.Tests;

/// <summary>Command line parsing tests, focused on never ignoring a typo silently.</summary>
public sealed class CommandLineTests
{
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["i"] = "input",
        ["o"] = "output",
    };

    private static readonly HashSet<string> ValueOptions = ["input", "output", "anim", "offset-y", "alpha-threshold"];
    private static readonly HashSet<string> Flags = ["remove-bg", "quiet"];

    private static CommandLine Parse(params string[] args) =>
        CommandLine.Parse(args, Aliases, ValueOptions, Flags);

    [Fact]
    public void 先頭の非オプション引数がコマンドになる()
    {
        CommandLine cmd = Parse("generate", "--input", "a.png");

        Assert.Equal("generate", cmd.Command);
        Assert.Equal("a.png", cmd.GetString("input"));
    }

    /// <summary>
    /// A dash too many is a typo, and this class exists to notice typos. Trimming every leading
    /// dash let "---input" run as though it had been spelled correctly.
    /// </summary>
    [Fact]
    public void ハイフンが3つ以上のオプションは拒否する()
    {
        ToolException error = Assert.Throws<ToolException>(() => Parse("generate", "---input", "a.png"));

        Assert.Contains("---input", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ハイフン1つと2つはどちらも受け付ける()
    {
        // The two spellings the help documents, and the reason the check counts rather than caps
        CommandLine cmd = Parse("generate", "-i", "a.png", "--output", "b.png");

        Assert.Equal("a.png", cmd.GetString("input"));
        Assert.Equal("b.png", cmd.GetString("output"));
    }

    [Fact]
    public void 短縮名が正式名に解決される()
    {
        CommandLine cmd = Parse("generate", "-i", "a.png", "-o", "b.png");

        Assert.Equal("a.png", cmd.GetString("input"));
        Assert.Equal("b.png", cmd.GetString("output"));
    }

    [Fact]
    public void 等号区切りでも値を受け取れる()
    {
        CommandLine cmd = Parse("generate", "--input=a.png");

        Assert.Equal("a.png", cmd.GetString("input"));
    }

    [Fact]
    public void フラグは値を取らずに立つ()
    {
        CommandLine cmd = Parse("generate", "--remove-bg", "--input", "a.png");

        Assert.True(cmd.HasFlag("remove-bg"));
        Assert.False(cmd.HasFlag("quiet"));
        Assert.Equal("a.png", cmd.GetString("input"));
    }

    [Fact]
    public void 負の数はオプションではなく値として扱う()
    {
        CommandLine cmd = Parse("generate", "--offset-y", "-3");

        Assert.Equal(-3, cmd.GetInt("offset-y", 0));
    }

    [Fact]
    public void 未知のオプションは例外にする()
    {
        // Silently ignoring a typo would generate output with unintended defaults
        ToolException ex = Assert.Throws<ToolException>(() => Parse("generate", "--outlien", "#000"));
        Assert.Contains("outlien", ex.Message);
    }

    [Fact]
    public void 値が無いオプションは例外にする()
    {
        Assert.Throws<ToolException>(() => Parse("generate", "--input"));
    }

    [Fact]
    public void フラグに値を与えたら例外にする()
    {
        Assert.Throws<ToolException>(() => Parse("generate", "--quiet=yes"));
    }

    [Fact]
    public void 必須オプションが無ければ例外にする()
    {
        CommandLine cmd = Parse("generate");

        Assert.Throws<ToolException>(() => cmd.GetRequiredString("input"));
    }

    [Fact]
    public void 整数として読めない値は例外にする()
    {
        CommandLine cmd = Parse("generate", "--offset-y", "abc");

        Assert.Throws<ToolException>(() => cmd.GetInt("offset-y", 0));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("256")]
    public void 範囲外のバイト値は例外にする(string value)
    {
        CommandLine cmd = Parse("generate", "--alpha-threshold", value);

        Assert.Throws<ToolException>(() => cmd.GetByte("alpha-threshold", 128));
    }

    [Fact]
    public void 列挙値は大文字小文字を区別せずに読む()
    {
        CommandLine cmd = Parse("generate", "--anim", "BOB");

        Assert.Equal(AnimationStyle.Bob, cmd.GetEnum("anim", AnimationStyle.Static));
    }

    [Fact]
    public void 不正な列挙値は選択肢付きで例外にする()
    {
        CommandLine cmd = Parse("generate", "--anim", "wiggle");

        ToolException ex = Assert.Throws<ToolException>(() => cmd.GetEnum("anim", AnimationStyle.Static));
        Assert.Contains("static", ex.Message);
        Assert.Contains("bob", ex.Message);
    }

    [Fact]
    public void 指定が無ければ既定値を返す()
    {
        CommandLine cmd = Parse("generate");

        Assert.Equal(7, cmd.GetInt("offset-y", 7));
        Assert.Equal(AnimationStyle.Static, cmd.GetEnum("anim", AnimationStyle.Static));
        Assert.Null(cmd.GetString("output"));
    }

    [Fact]
    public void 同じオプションを二度指定したら拒否する()
    {
        // Taking the last one silently is the usual behaviour elsewhere, and this parser
        // deliberately does not: two values for one option means the caller believes something
        // about the command that is not true. Nothing checked that, so an implementation that
        // quietly overwrote would have passed the whole suite.
        Assert.Throws<ToolException>(() => Parse("generate", "--input", "a.png", "--input", "b.png"));

        // The alias and the full name are the same option, so this counts as a duplicate too
        Assert.Throws<ToolException>(() => Parse("generate", "-i", "a.png", "--input=b.png"));

        // A repeated flag is not the same case and is accepted: it carries no value, so nothing
        // is being discarded and there is no second meaning to warn about.
        Assert.True(Parse("generate", "--quiet", "--quiet").HasFlag("quiet"));
    }

    [Fact]
    public void 空の値と未指定を区別できる()
    {
        // The install path leans on this: --character absent means every character, while
        // --character given as nothing is a mistake that must not be read as "every character".
        // Conflating them let an unset shell variable overwrite every character's picture.
        CommandLine given = Parse("generate", "--output=");

        Assert.True(given.HasOption("output"));
        Assert.Equal(string.Empty, given.GetString("output"));

        CommandLine absent = Parse("generate");

        Assert.False(absent.HasOption("output"));
        Assert.Null(absent.GetString("output"));
    }

    [Fact]
    public void 列挙オプションは名前だけを受け付ける()
    {
        // Enum.TryParse also takes a comma-separated list and a bare ordinal, and Enum.IsDefined
        // only sees the combined result, so both reached the pipeline as values the help never
        // offers: "--anim static,bob" ran as bob and "--anim 0" as static.
        Assert.Throws<ToolException>(
            () => Parse("generate", "--anim=static,bob").GetEnum("anim", AnimationStyle.Lively));
        Assert.Throws<ToolException>(
            () => Parse("generate", "--anim=0").GetEnum("anim", AnimationStyle.Lively));

        Assert.Equal(
            AnimationStyle.Bob,
            Parse("generate", "--anim=BOB").GetEnum("anim", AnimationStyle.Lively));
    }

    [Fact]
    public void 列挙オプションは前後の空白があっても序数を受け付けない()
    {
        // Enum.TryParse ignores surrounding white space, so one leading space carried an ordinal
        // straight past the "does it start with a digit" check and " 1" ran as the value
        // numbered 1 - the very form the check was added to refuse.
        Assert.Throws<ToolException>(
            () => Parse("generate", "--anim= 1").GetEnum("anim", AnimationStyle.Lively));
        Assert.Throws<ToolException>(
            () => Parse("generate", "--anim=1 ").GetEnum("anim", AnimationStyle.Lively));

        // A name with stray space around it is still a name, and still works
        Assert.Equal(
            AnimationStyle.Bob,
            Parse("generate", "--anim= bob ").GetEnum("anim", AnimationStyle.Lively));
    }
}

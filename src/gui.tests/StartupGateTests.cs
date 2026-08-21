using System.Text;
using CoreKeeperSkinTool.Gui;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// Tests for what is said before the main window opens.
///
/// Only one thing stops the application: having no game at all. A version this build was not
/// checked against is a warning the user may carry on past, and the terms of use are asked once.
///
/// The settings folder is already redirected for this assembly, so nothing here touches the real
/// settings of whoever runs the tests. The game folder is faked rather than using whatever the
/// machine happens to have installed, so the outcome does not depend on that.
/// </summary>
public sealed class StartupGateTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"cks-gate-test-{Guid.NewGuid():N}");

    private readonly string? _previousGamePath = GamePathSettings.Load();
    private readonly bool _previouslyAccepted = DisclaimerSettings.IsAccepted;

    public StartupGateTests()
    {
        Directory.CreateDirectory(_workDirectory);

        // Most tests are about the game rather than the terms, so the terms start out accepted
        // and the ones that care reset them.
        DisclaimerSettings.Accept(StartupGate.ApplicationVersion);
    }

    public void Dispose()
    {
        GamePathSettings.Save(_previousGamePath);

        if (_previouslyAccepted)
        {
            DisclaimerSettings.Accept(StartupGate.ApplicationVersion);
        }
        else
        {
            DisclaimerSettings.Reset();
        }

        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    /// <summary>Builds a folder that looks like an installation of the given version.</summary>
    private string CreateGame(string name, string? gameVersion)
    {
        string game = Path.Combine(_workDirectory, name);
        string data = Path.Combine(game, "CoreKeeper_Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(game, "CoreKeeper.exe"), "not really an executable");

        if (gameVersion is not null)
        {
            File.WriteAllText(
                Path.Combine(data, "globalgamemanagers"),
                $"\0\0\0\06000.0.59f2\0\0\0\0\0Pugstorm\0\0\0\0Core Keeper\0\0\0\0\0\0\0\0{gameVersion}\0\0\0\0",
                new UTF8Encoding(false));
        }

        return game;
    }

    private static string SupportedVersion => CoreKeeperSkinTool.Install.GameVersion.SupportedVersions[0];

    // ------------------------------------------------------------ Nothing to say

    [Fact]
    public void Check_対応バージョンかつ同意済みなら何も表示しない()
    {
        GamePathSettings.Save(CreateGame("good", SupportedVersion + ".5-8be0"));

        Assert.Empty(StartupGate.Check());
    }

    // ------------------------------------------------------------ Version differences

    /// <summary>
    /// The instruction here changed: a version this build was not checked against used to stop
    /// the application, and must now only warn. Nothing about it may be fatal.
    /// </summary>
    [Fact]
    public void Check_バージョンが違っても起動を止めない()
    {
        GamePathSettings.Save(CreateGame("future", "99.0.0-future"));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        Assert.Equal(StartupNoticeKind.Warning, notice.Kind);
    }

    [Fact]
    public void Check_バージョン警告は対応版と実際のバージョンを両方示す()
    {
        GamePathSettings.Save(CreateGame("future", "99.0.0-future"));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        // The user has to be able to see which is which, or the warning is not actionable
        Assert.Contains(SupportedVersion, notice.Message);
        Assert.Contains("99.0.0-future", notice.Message);
        Assert.Contains("99.0.0-future", notice.Detail);
    }

    [Fact]
    public void Check_バージョンを読めない場合も警告にとどめる()
    {
        GamePathSettings.Save(CreateGame("unreadable", gameVersion: null));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        Assert.Equal(StartupNoticeKind.Warning, notice.Kind);
        Assert.Contains(SupportedVersion, notice.Message);
    }

    // ------------------------------------------------------------ Terms of use

    [Fact]
    public void Check_初回は自己責任の同意を求める()
    {
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("good", SupportedVersion + ".5-8be0"));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        Assert.Equal(StartupNoticeKind.Disclaimer, notice.Kind);
        Assert.NotEmpty(notice.Message);
    }

    [Fact]
    public void Check_同意後は二度目から表示しない()
    {
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("good", SupportedVersion + ".5-8be0"));

        Assert.Single(StartupGate.Check());

        DisclaimerSettings.Accept(StartupGate.ApplicationVersion);

        Assert.Empty(StartupGate.Check());
    }

    /// <summary>
    /// Both can be outstanding at once. The terms come first because they govern using the
    /// application at all, rather than any one thing it does.
    /// </summary>
    [Fact]
    public void Check_同意とバージョン警告は同意が先に来る()
    {
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("future", "99.0.0-future"));

        IReadOnlyList<StartupNotice> notices = StartupGate.Check();

        Assert.Equal(2, notices.Count);
        Assert.Equal(StartupNoticeKind.Disclaimer, notices[0].Kind);
        Assert.Equal(StartupNoticeKind.Warning, notices[1].Kind);
    }

    // ------------------------------------------------------------ Fatal

    /// <summary>
    /// Every notice about the game offers to point at a folder. A machine can hold more than one
    /// installation, and detection can pick a different one than the user meant.
    /// </summary>
    [Fact]
    public void Check_ゲームに関する通知はフォルダ指定を提案する()
    {
        GamePathSettings.Save(CreateGame("future", "99.0.0-future"));

        Assert.True(Assert.Single(StartupGate.Check()).OfferFolderChoice);
    }

    /// <summary>
    /// A stored folder that no longer holds the game must not be trusted. It is rejected by the
    /// locator, which then falls back to searching, so what is asserted here is that a decision
    /// is still reached and that it is one the user can act on.
    /// </summary>
    [Fact]
    public void Check_保存されたフォルダが消えていても判断を返す()
    {
        GamePathSettings.Save(Path.Combine(_workDirectory, "gone"));

        IReadOnlyList<StartupNotice> notices = StartupGate.Check();

        // The stored folder is rejected and the locator falls back to searching the machine, so
        // what comes back depends on whether this machine has the game. Both outcomes are
        // legitimate, but "no notices at all" would make every assertion below vacuous - and on
        // a developer machine with a supported install that is exactly what happened, so this
        // test passed while checking nothing, and would have gone on passing with the folder
        // picker removed from the fatal notice entirely.
        if (notices.Count == 0)
        {
            Assert.True(
                GameLocator.FindGameInstallations().Count > 0,
                "通知が0件なのに、このマシンでゲームが見つかっていない。判断が返っていない。");
            return;
        }

        // Whatever it decides, nothing may be left without a way forward: a notice that stops
        // the application has to offer the folder picker, and every notice needs a reason.
        Assert.All(notices, n =>
        {
            Assert.False(string.IsNullOrWhiteSpace(n.Message));

            if (n.Kind == StartupNoticeKind.Fatal)
            {
                Assert.True(n.OfferFolderChoice);
            }
        });
    }

    /// <summary>
    /// Choosing a folder can turn one notice into several - a fatal "not found" becomes the terms
    /// plus a version warning - and the window used to keep the list it was built with, so
    /// everything after the first was silently dropped.
    /// </summary>
    [Fact]
    public void Check_同意と警告が同時に出る場合は2件返る()
    {
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("future", "99.0.0-future"));

        IReadOnlyList<StartupNotice> notices = StartupGate.Check();

        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.NotEqual(StartupNoticeKind.Fatal, n.Kind));
    }

    /// <summary>
    /// The "could not be read" placeholder has a translation of its own. Resolving it once and
    /// stamping it into every block put an English phrase inside the Japanese text.
    /// </summary>
    [Fact]
    public void バージョン不明の通知は各言語で完結している()
    {
        GamePathSettings.Save(CreateGame("unreadable", gameVersion: null));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        string japanese = notice.Details.Single(d => d.Language == "日本語").Text;
        string english = notice.Details.Single(d => d.Language == "English").Text;

        Assert.Contains("読み取れず", japanese);
        Assert.DoesNotContain("could not be read", japanese);

        Assert.Contains("could not be read", english);
        Assert.DoesNotContain("読み取れず", english);
    }

    // ------------------------------------------------------------ Language

    /// <summary>
    /// These screens come before the main window, which is the only place the language can be
    /// changed. Showing them in one language would mean a reader who cannot read it has no way
    /// to find out what they are agreeing to, so every language is written out at once.
    /// </summary>
    [Fact]
    public void 通知は全言語を併記する()
    {
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("good", SupportedVersion + ".5-8be0"));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        Assert.Equal(Loc.Instance.Languages.Count, notice.Messages.Count);
        Assert.Equal(Loc.Instance.Languages.Count, notice.Details.Count);

        foreach (LanguageOption language in Loc.Instance.Languages)
        {
            Assert.Contains(notice.Messages, m => m.Language == language.DisplayName);
        }
    }

    [Fact]
    public void 併記された各言語の本文は互いに異なる()
    {
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("good", SupportedVersion + ".5-8be0"));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        // Two identical entries would mean one language silently fell back to the other
        Assert.Equal(notice.Messages.Count, notice.Messages.Select(m => m.Text).Distinct().Count());
    }

    /// <summary>
    /// The version numbers have to survive into every language, not just the one that happened
    /// to be current when the notice was built.
    /// </summary>
    [Fact]
    public void 併記された各言語にバージョン番号が入る()
    {
        GamePathSettings.Save(CreateGame("future", "99.0.0-future"));

        StartupNotice notice = Assert.Single(StartupGate.Check());

        Assert.All(notice.Messages, m => Assert.Contains("99.0.0-future", m.Text));
        Assert.All(notice.Messages, m => Assert.Contains(SupportedVersion, m.Text));
    }

    [Fact]
    public void 通知は選択中の言語に左右されない()
    {
        LanguageOption original = Loc.Instance.Current;
        DisclaimerSettings.Reset();
        GamePathSettings.Save(CreateGame("good", SupportedVersion + ".5-8be0"));

        try
        {
            Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "en");
            string english = StartupGate.Check()[0].Message;

            Loc.Instance.Current = Loc.Instance.Languages.Single(l => l.Code == "ja");
            string japanese = StartupGate.Check()[0].Message;

            // Both languages appear either way, so the chosen one changes nothing here
            Assert.Equal(english, japanese);
        }
        finally
        {
            Loc.Instance.Current = original;
        }
    }
}

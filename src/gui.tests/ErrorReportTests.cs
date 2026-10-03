using System.Text.RegularExpressions;

namespace CoreKeeperSkinTool.Gui.Tests;

/// <summary>
/// How the window records and shows a failure it survives (src/gui/Program.cs).
///
/// Measured on this machine (the test campaign of 2026-10-01): an exception in a DispatcherTimer's
/// Tick reached neither the dispatcher's handler nor anything else, and the animation stopped for
/// good with no record; faults were told apart by the first line of their stack, so two different
/// failures in .NET's own throw helpers - or anywhere in one async handler - counted as one, and the
/// second showed nothing; a folder beside the exe that could not be written left no record at all;
/// the dialog spoke Japanese only; and the log grew across runs with no version or start marked.
/// </summary>
public sealed class ErrorReportTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cks-errorlog-{Guid.NewGuid():N}");

    public ErrorReportTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort
        }
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([TranslationCoverageTests.FindRepositoryRoot(), "src", "gui", .. parts]));

    // ------------------------------------------------------------ timers

    [Fact]
    public void アニメーションのタイマーの失敗は記録に回す()
    {
        string model = Source("ViewModels", "MainViewModel.cs");
        int tick = model.IndexOf("_animationTimer.Tick +=", StringComparison.Ordinal);
        Assert.True(tick >= 0, "アニメーションのタイマーの Tick が見つからない");

        string handler = model[tick..model.IndexOf(';', model.IndexOf("Program.ReportHandled(", tick, StringComparison.Ordinal))];
        Assert.Contains("try", handler, StringComparison.Ordinal);
        Assert.Contains("AdvanceAnimation();", handler, StringComparison.Ordinal);
        Assert.Matches(@"catch\s*\(\s*Exception\s+\w+\s*\)", handler);
    }

    [Fact]
    public void ヘルプのタイマーの失敗も記録に回す()
    {
        string help = Source("Views", "HelpWindow.axaml.cs");
        int tick = help.IndexOf("timer.Tick +=", StringComparison.Ordinal);
        Assert.True(tick >= 0, "ヘルプのタイマーの Tick が見つからない");
        Assert.Contains("Program.ReportHandled(", help[tick..], StringComparison.Ordinal);
    }

    [Fact]
    public void 観測されなかったタスクの失敗も記録する()
    {
        string program = Source("Program.cs");
        Assert.Contains("TaskScheduler.UnobservedTaskException +=", program, StringComparison.Ordinal);
        Assert.Contains("SetObserved()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void 観測されなかったタスクの失敗ではダイアログを出さない()
    {
        // It arrives on the finalizer thread, where a modal box holds up every finalizer behind it
        // (the final review of 2026-10-02, X54e)
        Assert.Contains("Report(e.Exception, HeadlineUnobserved, showDialog: false)", Source("Program.cs"), StringComparison.Ordinal);
    }

    private static string Report() => LanguageNotificationTests.MethodBody(Source("Program.cs"), "private static void Report(");

    // ------------------------------------------------------------ telling faults apart

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("例外が出なかった");
    }

    private static int ReadOutOfRangeA(List<int> list) => list[5];

    private static int ReadOutOfRangeB(List<int> list) => list[7];

    private static void ThrowAt(int which)
    {
        if (which == 1)
        {
            throw new InvalidOperationException("one");
        }

        throw new InvalidOperationException("two");
    }

    [Fact]
    public void 同じ部品から出た別の場所の失敗は別の故障と数える()
    {
        // Both throw inside List<T>.get_Item, which is the first line of both stacks
        Exception a = Catch(() => ReadOutOfRangeA([]));
        Exception b = Catch(() => ReadOutOfRangeB([]));
        Assert.NotEqual(Program.FaultSite(a), Program.FaultSite(b));

        // Two throws in one method differ by where in it they are
        Assert.NotEqual(Program.FaultSite(Catch(() => ThrowAt(1))), Program.FaultSite(Catch(() => ThrowAt(2))));

        // The same place twice is the same fault, whatever the message
        Assert.Equal(Program.FaultSite(Catch(() => ReadOutOfRangeA([]))), Program.FaultSite(a));
    }

    /// <summary>One place in this program's own code that throws two kinds of exception.</summary>
    private static void ThrowKind(bool io) =>
        throw (io ? (Exception)new IOException("io") : new InvalidOperationException("op"));

    [Fact]
    public void 待たれなかったタスクの失敗は中の例外で区別する()
    {
        // A task's failure arrives wrapped in an AggregateException that was never thrown: no stack,
        // and one type for every failure, so all of them were one fault and only the first five of a
        // run were recorded (the final review of 2026-10-02, REP6)
        Exception a = Catch(() => ReadOutOfRangeA([]));
        Exception b = Catch(() => ReadOutOfRangeB([]));
        string wrappedA = Program.FaultSignature("h", new AggregateException(a));

        Assert.NotEqual(wrappedA, Program.FaultSignature("h", new AggregateException(b)));
        Assert.Equal(Program.FaultSignature("h", a), wrappedA);

        // A wrapper inside a wrapper is seen through as well, and one place throwing two kinds of
        // exception is two faults, as it is when nothing is wrapped
        Assert.Equal(wrappedA, Program.FaultSignature("h", new AggregateException(new AggregateException(a))));
        Assert.NotEqual(
            Program.FaultSignature("h", new AggregateException(Catch(() => ThrowKind(io: true)))),
            Program.FaultSignature("h", new AggregateException(Catch(() => ThrowKind(io: false)))));

        // And this is what Report tells faults apart by
        Assert.Contains("string signature = FaultSignature(headline, exception);", Report(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ where the log goes

    [Fact]
    public void exeの隣に書けなければ次の場所に書きその場所を返す()
    {
        string unwritable = Path.Combine(_work, "missing", "\0bad");
        string fallback = Path.Combine(_work, "fallback");
        Directory.CreateDirectory(fallback);

        string? written = Program.WriteLog("entry", [unwritable, fallback], firstInRun: false);

        Assert.Equal(Path.Combine(fallback, "cks-gui-error.log"), written);
        Assert.Contains("entry", File.ReadAllText(written!), StringComparison.Ordinal);
    }

    [Fact]
    public void どこにも書けなければnullを返し例外を投げない()
    {
        Assert.Null(Program.WriteLog("entry", [Path.Combine(_work, "\0bad")], firstInRun: false));
    }

    [Fact]
    public void 記録はexeの隣から一時フォルダの順に書く()
    {
        // WriteLog falls back only to what it is given. Without the temporary folder in the list, an
        // extraction that cannot be written left no record anywhere, and the dialog said only that
        // (the final review of 2026-10-02, X56b)
        Assert.Contains(
            "WriteLog(entry.ToString(), [AppContext.BaseDirectory, Path.GetTempPath()], firstInRun)",
            Report(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void 見出しを書いたらこの起動ではもう書かない()
    {
        // WriteLog heads the entry it is told is the run's first; telling it is Report's part, and
        // without the second line every entry got the version heading (the final review of
        // 2026-10-02, X58a)
        string report = Report();
        Assert.Contains("firstInRun = !_loggedThisRun && seen < MaxEntriesPerFault;", report, StringComparison.Ordinal);
        Assert.Contains("_loggedThisRun |= firstInRun;", report, StringComparison.Ordinal);
    }

    [Fact]
    public void 起動ごとに版を書いた見出しを1度だけ入れる()
    {
        string first = Program.WriteLog("one", [_work], firstInRun: true)!;
        Program.WriteLog("two", [_work], firstInRun: false);

        string text = File.ReadAllText(first);
        Assert.Single(Regex.Matches(text, "cks-gui "));
        Assert.Contains(StartupGate.ApplicationVersion, text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("cks-gui ", StringComparison.Ordinal) < text.IndexOf("one", StringComparison.Ordinal));
    }

    [Fact]
    public void 大きくなったログは退避してから書く()
    {
        string log = Path.Combine(_work, "cks-gui-error.log");
        File.WriteAllText(log, new string('x', 1_100_000));

        Program.WriteLog("fresh", [_work], firstInRun: false);

        Assert.True(File.Exists(log + ".old"), "古いログが退避されていない");
        Assert.True(new FileInfo(log).Length < 10_000, "退避の後も大きいまま");
        Assert.Contains("fresh", File.ReadAllText(log), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ what it says

    [Fact]
    public void ダイアログの見出しと題は日英で書く()
    {
        // Loc may be what failed, and at start-up it is not running yet, so the texts are fixed and
        // carry both languages, as the start-up notice does
        string program = Source("Program.cs");

        foreach (string english in new[]
                 {
                     "The application stopped because of an unexpected error",
                     "Could not start",
                     "An error occurred during an operation",
                     "Details",
                     "Core Keeper Skin Maker",
                 })
        {
            Assert.Contains(english, program, StringComparison.Ordinal);
        }

        Assert.Contains("この起動ではこれ以上記録しない", program, StringComparison.Ordinal);
    }

    [Fact]
    public void 日英の見出しと題をそれぞれの場面で使う()
    {
        // The constants above could stay while a call went back to a Japanese-only text, and the
        // check above still passed (the final review of 2026-10-02, X57a and X57b)
        string program = Source("Program.cs");

        foreach (string use in new[]
                 {
                     "Report(e.ExceptionObject as Exception, HeadlineFatal)",
                     "Report(ex, HeadlineStartup)",
                     "Report(e.Exception, HeadlineOperation)",
                     "Report(exception, HeadlineOperation)",
                     "MessageBoxW(IntPtr.Zero, text, Caption, 0x10)",
                 })
        {
            Assert.Contains(use, program, StringComparison.Ordinal);
        }
    }
}

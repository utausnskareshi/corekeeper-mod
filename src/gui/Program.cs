using Avalonia;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreKeeperSkinTool.Gui;

sealed class Program
{
    /// <summary>Name of the crash log written next to the executable.</summary>
    private const string CrashLogName = "cks-gui-error.log";

    // Fixed texts in both languages. Loc may be what failed, and at start-up it is not running
    // yet, so these cannot be looked up; the start-up notice shows every language for the same
    // reason. An English user otherwise met a dialog in Japanese with only .NET's own sentence to read.
    private const string HeadlineFatal = "予期しないエラーで終了しました / The application stopped because of an unexpected error";
    private const string HeadlineStartup = "起動に失敗しました / Could not start";
    private const string HeadlineOperation = "操作中にエラーが発生しました / An error occurred during an operation";
    private const string HeadlineUnobserved = "裏で動いていた処理でエラーが発生しました / An error occurred in work running in the background";
    private const string Caption = "Core Keeper スキン作成ツール / Core Keeper Skin Maker";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // This is a windowed application with no console, so an unhandled exception would
        // otherwise end the process with nothing on screen and nothing written anywhere:
        // the user double-clicks the executable and simply nothing happens. Startup can
        // genuinely fail this way when an embedded resource is missing, which is exactly
        // what a partially extracted ZIP looks like.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, HeadlineFatal);

        // A task whose failure nobody awaited is otherwise lost without trace. Logged and not
        // shown: this arrives on the finalizer thread, where a modal dialog would hold up every
        // finalizer behind it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, HeadlineUnobserved, showDialog: false);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Report(ex, HeadlineStartup);
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Installs the handler that keeps an exception escaping a UI callback from killing the
    /// application. Called once the dispatcher exists, from <see cref="App"/>.
    /// </summary>
    public static void InstallDispatcherHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // Losing one action is bad; losing the window along with the user's unsaved
            // pixel edits is worse. The error is recorded and the application stays up.
            Report(e.Exception, HeadlineOperation);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Records a failure that was caught where it happened, the way the dispatcher's handler
    /// would have.
    ///
    /// For a DispatcherTimer's Tick: an exception there reaches neither the dispatcher's handler
    /// nor anything else on Avalonia 12.1.1 (measured), and the timer never ticks again, so the
    /// animation stopped for good with no dialog and no log.
    /// </summary>
    internal static void ReportHandled(Exception exception) => Report(exception, HeadlineOperation);

    /// <summary>
    /// How many times one fault is written to the log before recording stops.
    ///
    /// A fault that repeats on every tick of a timer recurs eight times a second. Writing every
    /// one of them opened, appended to and closed the file each time, and the log grew by tens of
    /// megabytes an hour while saying the same thing over and over. The first few carry all the
    /// information there is.
    /// </summary>
    private const int MaxEntriesPerFault = 5;

    /// <summary>
    /// The log is moved aside to .old once it passes this. Counted per run, the cap above starts
    /// again at every start, and a fault met at every start grew the file without end.
    /// </summary>
    private const long MaxLogBytes = 1024 * 1024;

    /// <summary>
    /// How often each fault has been seen. Kept for the life of the process: a fault that
    /// recurs is the same fault, and the first dialog has already said everything the later
    /// ones would.
    /// </summary>
    private static readonly Dictionary<string, int> FaultCounts = new(StringComparer.Ordinal);

    /// <summary>Whether this run has written to the log yet, so its header goes in once.</summary>
    private static bool _loggedThisRun;

    /// <summary>
    /// Writes the failure to a log and shows it, so there is something to act on and something
    /// to report.
    /// </summary>
    private static void Report(Exception? exception, string headline, bool showDialog = true)
    {
        string message = exception?.ToString() ?? "詳細不明のエラー / Unknown error";
        string signature = FaultSignature(headline, exception);

        int seen;
        bool firstInRun;
        lock (FaultCounts)
        {
            FaultCounts.TryGetValue(signature, out seen);
            FaultCounts[signature] = seen + 1;
            firstInRun = !_loggedThisRun && seen < MaxEntriesPerFault;
            _loggedThisRun |= firstInRun;
        }

        string? logPath = null;
        if (seen < MaxEntriesPerFault)
        {
            StringBuilder entry = new();
            entry.AppendLine($"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}] {headline}");
            entry.AppendLine(message);

            if (seen + 1 == MaxEntriesPerFault)
            {
                entry.AppendLine("(この起動ではこれ以上記録しない / not recorded again in this run)");
            }

            entry.AppendLine();

            // Beside the executable first, where a user looks; the temporary folder when that
            // folder cannot be written - an extraction into Program Files is enough - so that a
            // record is left somewhere and the dialog can say where.
            logPath = WriteLog(entry.ToString(), [AppContext.BaseDirectory, Path.GetTempPath()], firstInRun);
        }

        // Only ever one dialog per fault. The handler exists to keep the application usable
        // after a failed action; a new modal for every repeat made it unusable instead.
        if (seen > 0 || !showDialog)
        {
            return;
        }

        ShowMessage(
            $"{headline}{Environment.NewLine}{Environment.NewLine}" +
            $"{Truncate(message, 1200)}{Environment.NewLine}{Environment.NewLine}" +
            $"詳細 / Details: {logPath ?? "(書き出せませんでした / could not be written)"}");
    }

    /// <summary>
    /// Appends one entry to the first of the folders where the log can be written, and returns
    /// the file it went to, or null when none could be written. Never throws: a folder that will
    /// not take it must not turn error reporting into a second failure.
    /// </summary>
    /// <param name="firstInRun">
    /// Whether this is the run's first entry, which is headed with the version and the system,
    /// so entries from different runs and builds can be told apart in a log sent in.
    /// </param>
    internal static string? WriteLog(string entry, IReadOnlyList<string> directories, bool firstInRun)
    {
        foreach (string directory in directories)
        {
            try
            {
                string path = Path.Combine(directory, CrashLogName);

                FileInfo existing = new(path);
                if (existing.Exists && existing.Length > MaxLogBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                string text = firstInRun
                    ? $"==== cks-gui {StartupGate.ApplicationVersion} / {Environment.OSVersion} / " +
                      $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} ===={Environment.NewLine}{entry}"
                    : entry;

                File.AppendAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch (Exception)
            {
                // Try the next folder
            }
        }

        return null;
    }

    /// <summary>
    /// What tells one fault from another.
    ///
    /// Identified by where it comes from, not by what it says. Including the message meant a
    /// fault whose text carries a changing value - a coordinate, a file name, a count - was a
    /// new fault every time, so it slipped past the check in Report and put up a dialog on every
    /// repeat. The type and the place in this program's own code name the fault.
    ///
    /// A failed task's exception arrives wrapped in an AggregateException that was never thrown:
    /// it has no stack, and its type is the same for every failure, so every failure in work
    /// nobody awaited was one fault and only the first five of a run were recorded. The first
    /// exception inside it names the fault instead.
    /// </summary>
    internal static string FaultSignature(string headline, Exception? exception)
    {
        Exception? fault = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? exception
            : exception;

        return $"{headline}|{fault?.GetType().FullName}|{FaultSite(fault)}";
    }

    /// <summary>
    /// Where in this program a fault came from: the first frame in its own code, by method and IL
    /// offset.
    ///
    /// The first line of the stack named the fault before. For an exception from .NET's throw
    /// helpers that line is List`1.get_Item or ArgumentNullException.Throw wherever the program
    /// called them, so two different failures counted as one and the second showed nothing; in an
    /// async handler it was MoveNext for every failure in that handler; and the release build has
    /// no pdb, so there was no line number to tell two throws in one method apart either.
    /// </summary>
    internal static string FaultSite(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        try
        {
            StackTrace trace = new(exception, fNeedFileInfo: false);
            foreach (StackFrame frame in trace.GetFrames())
            {
                if (frame.GetMethod() is { DeclaringType: { } type } method
                    && type.FullName is { } name
                    && name.StartsWith("CoreKeeperSkinTool", StringComparison.Ordinal))
                {
                    return $"{name}.{method.Name}+{frame.GetILOffset()}";
                }
            }
        }
        catch (Exception)
        {
            // The text of the stack still names the fault well enough
        }

        return FirstFrame(exception);
    }

    /// <summary>The first stack frame as text, when no frame of this program's own is found.</summary>
    private static string FirstFrame(Exception? exception)
    {
        string? trace = exception?.StackTrace;
        if (string.IsNullOrEmpty(trace))
        {
            return string.Empty;
        }

        int end = trace.IndexOf('\n');
        return (end < 0 ? trace : trace[..end]).Trim();
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    /// <summary>
    /// Shows a message box through the OS directly, because Avalonia may not be running,
    /// which is precisely the case during a startup failure.
    /// </summary>
    private static void ShowMessage(string text)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MessageBoxW(IntPtr.Zero, text, Caption, 0x10);
                return;
            }

            Console.Error.WriteLine(text);
        }
        catch (Exception)
        {
            // Nothing further can be done; the log file above is the remaining record.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

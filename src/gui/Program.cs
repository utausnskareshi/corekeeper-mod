using Avalonia;
using Avalonia.Threading;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreKeeperSkinTool.Gui;

sealed class Program
{
    /// <summary>Name of the crash log written next to the executable.</summary>
    private const string CrashLogName = "cks-gui-error.log";

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
            Report(e.ExceptionObject as Exception, "予期しないエラーで終了しました");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Report(ex, "起動に失敗しました");
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
            Report(e.Exception, "操作中にエラーが発生しました");
            e.Handled = true;
        };
    }

    /// <summary>
    /// Writes the failure to a log next to the executable and shows it, so there is
    /// something to act on and something to report.
    /// </summary>
    /// <summary>
    /// How many times one fault is written to the log before recording stops.
    ///
    /// A fault reached from the animation timer recurs eight times a second. Writing every one
    /// of them opened, appended to and closed the file each time, and the log grew by tens of
    /// megabytes an hour while saying the same thing over and over. The first few carry all the
    /// information there is.
    /// </summary>
    private const int MaxEntriesPerFault = 5;

    /// <summary>
    /// How often each fault has been seen. Kept for the life of the process: a fault that
    /// recurs is the same fault, and the first dialog has already said everything the later
    /// ones would.
    /// </summary>
    private static readonly Dictionary<string, int> FaultCounts = new(StringComparer.Ordinal);

    private static void Report(Exception? exception, string headline)
    {
        string message = exception?.ToString() ?? "詳細不明のエラー";
        string logPath = Path.Combine(AppContext.BaseDirectory, CrashLogName);

        // Identified by where it comes from, not by what it says. Including the message meant a
        // fault whose text carries a changing value - a coordinate, a file name, a count - was a
        // new fault every time, so it slipped past the check below and put up a dialog on every
        // repeat. The type and the throwing frame are what actually name the fault.
        string signature =
            $"{headline}|{exception?.GetType().FullName}|{FirstFrame(exception)}";

        int seen;
        lock (FaultCounts)
        {
            FaultCounts.TryGetValue(signature, out seen);
            FaultCounts[signature] = seen + 1;
        }

        if (seen < MaxEntriesPerFault)
        {
            try
            {
                StringBuilder entry = new();
                entry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {headline}");
                entry.AppendLine(message);

                if (seen + 1 == MaxEntriesPerFault)
                {
                    entry.AppendLine("(このエラーはこれ以上記録しない)");
                }

                entry.AppendLine();
                File.AppendAllText(logPath, entry.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                // A read-only folder must not turn error reporting into a second failure.
                logPath = "(書き出せませんでした)";
            }
        }

        // Only ever one dialog per fault. The handler exists to keep the application usable
        // after a failed action; a new modal for every repeat made it unusable instead.
        if (seen > 0)
        {
            return;
        }

        ShowMessage(
            $"{headline}{Environment.NewLine}{Environment.NewLine}" +
            $"{Truncate(message, 1200)}{Environment.NewLine}{Environment.NewLine}" +
            $"詳細: {logPath}");
    }

    /// <summary>The first stack frame, which is where the fault actually came from.</summary>
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
                MessageBoxW(IntPtr.Zero, text, "Core Keeper スキン作成ツール", 0x10);
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

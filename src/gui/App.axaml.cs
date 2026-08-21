using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Gui.Views;

namespace CoreKeeperSkinTool.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The dispatcher exists by now, so exceptions escaping a UI callback can be
            // caught and reported instead of taking the window down with them.
            Program.InstallDispatcherHandler();

            // Stated rather than left to the framework default, because the start-up notices
            // depend on it: each opens the next window and then closes itself, which would end
            // the application under OnMainWindowClose.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;

            // Checked before the main window is built, not after: opening a window full of
            // controls that cannot do anything, only to say so afterwards, would be worse than
            // saying so first. Each notice opens the next one and closes itself, so declining
            // any of them ends the application: they run before the main window exists, and the
            // lifetime shuts down with its last window.
            IReadOnlyList<StartupNotice> notices = StartupGate.Check();

            // The DataContext is set in the window's own constructor.
            // Setting it here as well would create the same view model twice.
            desktop.MainWindow = notices.Count > 0
                ? new StartupNoticeWindow(notices[0], [.. notices.Skip(1)])
                : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

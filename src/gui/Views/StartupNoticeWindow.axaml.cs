using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Views;

/// <summary>
/// A screen shown before the main window: the terms of use, a warning worth acknowledging, or a
/// reason the application cannot run at all.
///
/// Each one either moves on to whatever comes next, or closes. Closing ends the application,
/// because these are shown before the main window exists and the desktop lifetime shuts down
/// with its last window. That is what makes a fatal notice actually stop the application.
/// </summary>
public partial class StartupNoticeWindow : Window
{
    private IReadOnlyList<StartupNotice> _remaining;
    private StartupNotice _notice;

    /// <summary>For the XAML previewer only; at run time the parameterised constructor is used.</summary>
    public StartupNoticeWindow()
        : this(new StartupNotice(StartupNoticeKind.Warning, [], []), [])
    {
    }

    /// <param name="notice">What this window is showing.</param>
    /// <param name="remaining">Notices still to show once this one is accepted.</param>
    public StartupNoticeWindow(StartupNotice notice, IReadOnlyList<StartupNotice> remaining)
    {
        // Must come first: it is what creates the named controls, which Apply then writes into.
        InitializeComponent();

        _remaining = remaining;
        _notice = notice;
        _shownMessages = notice.Messages;
        Apply(notice);

        FolderButton.Click += OnFolderClicked;
        AcceptButton.Click += OnAcceptClicked;
        DeclineButton.Click += (_, _) => Close();
    }

    private void Apply(StartupNotice notice)
    {
        _notice = notice;

        string titleKey = notice.Kind switch
        {
            StartupNoticeKind.Fatal => "startup.title",
            StartupNoticeKind.Disclaimer => "startup.disclaimerTitle",
            _ => "startup.warningTitle",
        };

        Title = Inline(titleKey);

        MessageList.ItemsSource = notice.Messages;
        DetailList.ItemsSource = notice.Details;
        DetailBorder.IsVisible = notice.Details.Any(d => !string.IsNullOrWhiteSpace(d.Text));

        FolderButton.IsVisible = notice.OfferFolderChoice;
        FolderButton.Content = Inline("startup.chooseFolder");

        // A fatal notice has nothing to accept, so the only way on is out
        AcceptButton.IsVisible = notice.Kind != StartupNoticeKind.Fatal;

        (string acceptKey, string declineKey) = notice.Kind switch
        {
            StartupNoticeKind.Disclaimer => ("startup.agree", "startup.decline"),
            _ => ("startup.continue", "startup.quit"),
        };

        AcceptButton.Content = Inline(acceptKey);
        DeclineButton.Content = Inline(declineKey);
    }

    /// <summary>
    /// A short label in every language on one line, for buttons and the title bar.
    ///
    /// Duplicates are collapsed so a label that happens to be identical in two languages is not
    /// printed twice.
    /// </summary>
    private static string Inline(string key) => string.Join(
        " / ",
        Loc.Instance.EveryLanguage(key).Select(t => t.Text).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Accepts this notice and moves on. Accepting the terms is recorded so they are only ever
    /// asked once.
    /// </summary>
    private void OnAcceptClicked(object? sender, RoutedEventArgs e)
    {
        if (_notice.Kind == StartupNoticeKind.Disclaimer && !_notSavedShown)
        {
            if (!DisclaimerSettings.Accept(StartupGate.ApplicationVersion))
            {
                // Said before going on. Taken silently, the terms came back at every start under a
                // notice saying it is shown only once, and nothing pointed at the reason: settings
                // that cannot be written - an extraction with portable.txt into a folder the user
                // cannot write to. Using the application this time is not refused; the next press
                // goes on.
                _notSavedShown = true;
                Apply(_notice with
                {
                    Messages = [.. Loc.Instance.EveryLanguage("startup.settingsNotSaved", SettingsFile.DescribePath()), .. _notice.Messages],
                });
                return;
            }
        }

        ShowNext(_remaining);
    }

    /// <summary>Whether this window has already said that the acceptance could not be saved.</summary>
    private bool _notSavedShown;

    /// <summary>
    /// Lets the user point at the installation, then works out afresh what has to be said.
    ///
    /// Re-running the whole check rather than just this notice matters: choosing a folder can
    /// turn a fatal "not found" into a version warning, or into nothing at all.
    /// </summary>
    private async void OnFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                // Every language, like the rest of this window. The language cannot be chosen
                // until the main window opens, so before that a single language is a guess -
                // and on a first run it is always the default, which leaves anyone not reading
                // Japanese with an untitled folder picker.
                Title = Inline("dialog.gamePathTitle"),
                AllowMultiple = false,
            });

            string? path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
            {
                return;
            }

            if (!GameLocator.IsGameDirectory(path))
            {
                ShowFolderProblem(Loc.Instance.EveryLanguage("status.gamePathInvalid", path));
                return;
            }

            if (!GamePathSettings.Save(path))
            {
                // Saying so is the whole point. Check() below re-reads the folder from the file,
                // so a write that did not land finds nothing again and hands back the identical
                // fatal notice - and a fatal notice hides the accept button, so the user was left
                // pressing this button forever with the screen never changing. The settings file
                // sits beside the executable when portable.txt is there, which is where an
                // extraction into a folder the user cannot write to puts it.
                ShowFolderProblem(Loc.Instance.EveryLanguage("status.gamePathNotSaved", SettingsFile.DescribePath()));
                return;
            }

            IReadOnlyList<StartupNotice> notices = StartupGate.Check();
            if (notices.Count == 0)
            {
                ShowNext([]);
                return;
            }

            // The rest of the list has to be taken as well. Choosing a folder can turn one
            // notice into several - a fatal "not found" becomes the terms plus a version
            // warning - and keeping the list this window was built with dropped all but the
            // first of them, so the version warning was never shown.
            _remaining = [.. notices.Skip(1)];
            _shownMessages = notices[0].Messages;
            Apply(notices[0]);
        }
        catch (Exception ex)
        {
            Apply(_notice with { Details = [new LocalizedText(string.Empty, ex.Message)] });
        }
    }

    /// <summary>The messages of the notice as it was shown, before any folder problem was added.</summary>
    private IReadOnlyList<LocalizedText> _shownMessages;

    /// <summary>
    /// Shows what went wrong with the folder chosen. A fatal notice is replaced by it, having
    /// nothing to accept; any other keeps what it said underneath. Replaced, the version warning
    /// lost its whole text - the unsupported version, the look that may break, the advice to back
    /// up the saves - while its "continue" button stayed, so the user agreed to something no longer
    /// on screen. Each problem replaces the last one rather than piling up.
    /// </summary>
    private void ShowFolderProblem(IReadOnlyList<LocalizedText> problem)
    {
        Apply(_notice with
        {
            Messages = _notice.Kind == StartupNoticeKind.Fatal ? problem : [.. problem, .. _shownMessages],
        });
    }

    /// <summary>
    /// Opens whatever comes after this window, then closes it.
    ///
    /// The next window is opened before this one closes so that the application is never left
    /// with no windows, which would shut it down.
    /// </summary>
    private void ShowNext(IReadOnlyList<StartupNotice> remaining)
    {
        Window next = remaining.Count > 0
            ? new StartupNoticeWindow(remaining[0], [.. remaining.Skip(1)])
            : new MainWindow();

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = next;
        }

        next.Show();
        Close();
    }
}

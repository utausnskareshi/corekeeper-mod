using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Help;

namespace CoreKeeperSkinTool.Gui.Views;

/// <summary>
/// The help window.
///
/// The text shown is that of the currently selected language.
/// Changing language while it is open is not supported; reopening picks up the new language.
/// </summary>
public partial class HelpWindow : Window
{
    /// <summary>How long the copy button confirms for before going back to its usual label.</summary>
    private static readonly TimeSpan ConfirmationDuration = TimeSpan.FromSeconds(2);

    public HelpWindow()
    {
        InitializeComponent();

        HelpDocument document = HelpContent.Load(Loc.Instance.Current.Code);

        Title = document.Title;
        TitleText.Text = document.Title;
        SectionList.ItemsSource = document.Sections;

        CloseButton.Click += (_, _) => Close();

        // The copy buttons live inside a data template, so there is no name to attach to. The
        // click bubbles up to here, where the button carries the text it copies.
        AddHandler(Button.ClickEvent, OnButtonClicked, RoutingStrategies.Bubble);
    }

    private async void OnButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button
            || !button.Classes.Contains("copy")
            || button.CommandParameter is not string text
            || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            if (GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                return;
            }

            await clipboard.SetTextAsync(text);
            Confirm(button);
        }
        catch (Exception)
        {
            // A clipboard the system will not hand over is not worth interrupting the help for;
            // the text is on screen and can still be selected by hand.
        }
    }

    /// <summary>
    /// Says the copy happened, then puts the label back.
    ///
    /// Without this the button gives no sign of having done anything, and the usual response to
    /// that is to press it again.
    /// </summary>
    private static void Confirm(Button button)
    {
        object? original = button.Content;
        button.Content = Loc.Instance["help.copied"];

        DispatcherTimer timer = new() { Interval = ConfirmationDuration };
        timer.Tick += (s, _) =>
        {
            timer.Stop();

            // Only restore if nothing else has changed the label meanwhile
            if (ReferenceEquals(button.Content, Loc.Instance["help.copied"])
                || Equals(button.Content, Loc.Instance["help.copied"]))
            {
                button.Content = original;
            }
        };
        timer.Start();
    }
}

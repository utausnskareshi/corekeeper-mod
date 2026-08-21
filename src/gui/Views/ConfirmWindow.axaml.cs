using Avalonia.Controls;
using Avalonia.Input;
using CoreKeeperSkinTool.Gui.Localization;

namespace CoreKeeperSkinTool.Gui.Views;

/// <summary>
/// A small dialog that confirms an action which cannot be undone.
///
/// The answer comes back as the result of <c>ShowDialog&lt;bool?&gt;</c>: true for the confirm
/// button, false for the other one, and null when the question was dismissed without answering
/// - Escape, or the title bar's close button.
///
/// Null matters where the two buttons are a choice rather than a yes and a no. There, false is a
/// real answer with real consequences, and letting Escape mean it turned "I picked the wrong
/// file" into the very conversion the question existed to prevent, with nothing to undo.
/// </summary>
public partial class ConfirmWindow : Window
{
    /// <summary>For the XAML previewer only; at run time the parameterised constructor is used.</summary>
    public ConfirmWindow() : this(string.Empty, "confirm.ok")
    {
    }

    /// <param name="message">What the user is being asked to confirm.</param>
    /// <param name="okLabelKey">Key for the confirm button's label, which differs per action.</param>
    /// <param name="detail">Supporting information such as the target list, shown wrapped below the message.</param>
    /// <param name="cancelLabelKey">
    /// Key for the other button. Named for a two-way choice where neither answer is a refusal;
    /// left alone it is the ordinary "cancel".
    /// </param>
    public ConfirmWindow(
        string message, string okLabelKey, string? detail = null,
        string cancelLabelKey = "confirm.cancel")
    {
        InitializeComponent();

        MessageText.Text = message;
        OkButton.Content = Loc.Instance[okLabelKey];
        CancelButton.Content = Loc.Instance[cancelLabelKey];

        DetailText.Text = detail ?? string.Empty;
        DetailText.IsVisible = !string.IsNullOrEmpty(detail);

        OkButton.Click += (_, _) => Close((bool?)true);
        CancelButton.Click += (_, _) => Close((bool?)false);

        // Escape and the close button both mean "I did not answer". Without this they came back
        // as false, which for a yes-or-no question is the same as declining and for a choice
        // between two actions silently performed the second one.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(null);
            }
        };
    }
}

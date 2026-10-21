using Avalonia.Controls;
using Avalonia.Input;
using CoreKeeperSkinTool.Export;
using CoreKeeperSkinTool.Gui.ViewModels;
using CoreKeeperSkinTool.Install;

namespace CoreKeeperSkinTool.Gui.Views;

/// <summary>
/// The "Export for mod.io" screen: the name, the version, which of the two looks, and the
/// confirmation that the picture may be published.
///
/// The choices come back as the result of <c>ShowDialog&lt;ModIoExportOptions?&gt;</c>, null when
/// the screen was left without exporting - the other button, Escape or the title bar's close
/// button. Where to save is asked afterwards, by the main window.
/// </summary>
public partial class ModIoExportWindow : Window
{
    /// <summary>For the XAML previewer only; at run time the parameterised constructor is used.</summary>
    public ModIoExportWindow()
        : this(new ModIoExportDialogModel("Skin", false, new GameVersionCheck(GameVersionState.Unknown, null, [])))
    {
    }

    public ModIoExportWindow(ModIoExportDialogModel model)
    {
        InitializeComponent();
        DataContext = model;

        ExportButton.Click += (_, _) =>
        {
            // The button is only enabled when this holds; checked again so a stale binding can never
            // hand back choices the exporter would refuse
            if (model.CanExport)
            {
                Close(model.Result());
            }
        };

        CancelButton.Click += (_, _) => Close((ModIoExportOptions?)null);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close((ModIoExportOptions?)null);
            }
        };

        // The name is what is most often changed, so the cursor starts there
        Opened += (_, _) => NameBox.Focus();
    }
}

using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace CoreKeeperSkinTool.Gui.Localization;

/// <summary>
/// Holds the text for one key and raises a notification when the language changes.
///
/// Indexer bindings never receive the language-change notification and leave the view stale, so
/// this type acts as a per-key notification source, bound to as an ordinary property.
/// </summary>
public sealed class LocalizedString(string key) : INotifyPropertyChanged
{
    public string Key { get; } = key;

    /// <summary>The text in the current language.</summary>
    public string Value => Loc.Instance[Key];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells the view that the language changed.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));

    public override string ToString() => Value;
}

/// <summary>
/// Lets XAML write <c>{loc:Tr key}</c>.
/// For example: <c>Content="{loc:Tr top.open}"</c>
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    /// <summary>Key within the language file.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedString.Value))
        {
            Source = Loc.Instance.Get(Key),
            Mode = BindingMode.OneWay,
        };
}

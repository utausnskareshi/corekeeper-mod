using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CoreKeeperSkinTool.Gui;

/// <summary>
/// Reports whether an enum value equals the parameter, so radio buttons can select enum values.
/// Converting back writes the parameter value only when the option becomes selected
/// (writing back on deselection too would cancel the selection that follows).
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public static readonly EnumMatchConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : BindingOperations.DoNothing;
}

/// <summary>
/// Converts a <c>#RRGGBB</c> string into a brush, used for the colour swatch.
/// A half-typed, invalid string yields transparent rather than an exception.
/// </summary>
public sealed class ColorStringToBrushConverter : IValueConverter
{
    public static readonly ColorStringToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Deliberately the view model's parser and not Avalonia's. The swatch is a preview of
        // the colour that will actually be painted, so the two must not read the same text
        // differently: Avalonia reads eight hex digits as AARRGGBB, the painting code as
        // RRGGBBAA, and they accepted different forms besides.
        if (value is string text && ViewModels.MainViewModel.TryParseColor(text) is { } parsed)
        {
            return new SolidColorBrush(new Color(parsed.Alpha, parsed.Red, parsed.Green, parsed.Blue));
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

using Avalonia.Media.Imaging;
using CoreKeeperSkinTool.Gui.Editing;
using CoreKeeperSkinTool.Gui.Localization;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Gui.ViewModels;

/// <summary>
/// One entry in the preset list.
///
/// Carries a thumbnail as well as a name: thirty entries told apart only by a word are hard to
/// choose between, and the thing being chosen is a picture.
/// </summary>
public sealed class PresetChoice : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Re-reads the labels after a language switch.
    ///
    /// The list is built once at start-up and never rebuilt, so without this the rows kept the
    /// language they were created in while everything around them changed.
    /// </summary>
    public void RefreshDisplay()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GroupDisplay)));
    }

    private PresetChoice(string key, string groupKey, PresetDefinition? definition, Bitmap? thumbnail)
    {
        Key = key;
        GroupKey = groupKey;
        Definition = definition;
        Thumbnail = thumbnail;
    }

    /// <summary>Identifier; the localization key is <c>preset.&lt;Key&gt;</c>.</summary>
    public string Key { get; }

    /// <summary>Group heading; the localization key is <c>presetGroup.&lt;GroupKey&gt;</c>.</summary>
    public string GroupKey { get; }

    /// <summary>The recipe, or null for the part guide, which is drawn differently.</summary>
    public PresetDefinition? Definition { get; }

    /// <summary>A single frame of the character, shown beside the name.</summary>
    public Bitmap? Thumbnail { get; }

    public string Display => Loc.Instance[$"preset.{Key}"];

    public string GroupDisplay => Loc.Instance[$"presetGroup.{GroupKey}"];

    /// <summary>The part guide: colour-coded rectangles rather than a finished character.</summary>
    public static PresetChoice PartGuide(Bitmap? thumbnail) =>
        new("starter", "template", null, thumbnail);

    public static PresetChoice For(PresetDefinition definition, Bitmap? thumbnail) =>
        new(definition.Key, definition.Category, definition, thumbnail);

    /// <summary>Tones of the chequer behind a thumbnail, matching the editor canvas.</summary>
    private static readonly SKColor CheckerDark = new(26, 26, 32);

    private static readonly SKColor CheckerLight = new(58, 58, 70);

    /// <summary>
    /// Cuts one frame out of a sheet and scales it up, so the thumbnail is legible in a list
    /// rather than a 26-pixel speck.
    ///
    /// Transparent pixels become a chequerboard. Without it a near-black character such as the
    /// shadow preset is indistinguishable from an empty row, which defeats the point of showing
    /// a picture at all.
    /// </summary>
    public static Bitmap? MakeThumbnail(SKBitmap sheet, FrameRect frame, int zoom)
    {
        int width = frame.W * zoom;
        int height = frame.H * zoom;
        int square = 3 * zoom;

        SKColor[] source = sheet.Pixels;
        SKColor[] scaled = new SKColor[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor colour =
                    source[((frame.YTopLeft + (y / zoom)) * sheet.Width) + frame.X + (x / zoom)];

                if (colour.Alpha == 0)
                {
                    colour = ((x / square) + (y / square)) % 2 == 1 ? CheckerLight : CheckerDark;
                }

                scaled[(y * width) + x] = colour;
            }
        }

        return BitmapBridge.ToBitmap(scaled, width, height);
    }
}

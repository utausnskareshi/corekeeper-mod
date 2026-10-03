using System.Reflection;
using SkiaSharp;

namespace CoreKeeperSkinTool.Layout;

/// <summary>
/// The drawn pictures that some presets are made of, embedded beside the recipes.
///
/// A preset is one of two kinds. A recipe is colours and shape choices that <c>PresetCharacter</c>
/// draws into the measured part positions. A picture is used instead for a costume with a shape of
/// its own - a wide hat, a hood, plate armour - which a recipe of seven colours cannot describe,
/// and more of the presets are pictures than recipes. The pictures go through the same path an
/// imported picture does, so a preset ends up looking exactly like the same file handed to
/// <c>generate</c>.
///
/// The pictures are stored as they came out of cleaning, not reduced to the thirteen pixels the
/// game shows. Reducing them here and again at placement would be two passes of resampling for
/// one result, and the file kept is then the source anyone could re-run rather than an
/// intermediate nobody can reproduce.
/// </summary>
public static class PresetArt
{
    /// <summary>
    /// Prefix every picture's resource name carries. Set in the project file, which maps
    /// <c>data/preset-art/&lt;name&gt;.png</c> to <c>preset-art/&lt;name&gt;.png</c>.
    /// </summary>
    private const string Prefix = "preset-art/";

    /// <summary>Whether a picture of this name is embedded. Used by the library's own checks.</summary>
    public static bool Exists(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(Prefix + name) is not null;

    /// <summary>
    /// Loads one picture.
    ///
    /// Throws rather than returning null: a preset that names a picture which is not there is a
    /// broken build, and the window would otherwise show an empty entry with nothing said.
    /// </summary>
    public static SKBitmap Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string resource = Prefix + name;

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new ToolException(
                $"プリセットの画像が埋め込まれていない ({resource})。配布物が壊れている可能性がある。");

        return SKBitmap.Decode(stream)
            ?? throw new ToolException($"プリセットの画像を読み取れない ({resource})。");
    }

    /// <summary>Every embedded picture's name, for the test that checks none has been orphaned.</summary>
    public static IEnumerable<string> Names() =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..]);
}

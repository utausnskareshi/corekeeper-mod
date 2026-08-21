using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Sheet;

/// <summary>
/// Makes a character out of a random combination of the same choices the presets are built from.
///
/// Colours are drawn in HSV rather than as random bytes: three independent channels produce mud
/// most of the time, whereas picking a hue and keeping saturation and value in a sensible band
/// gives something that reads as a deliberate palette. Related parts are given neighbouring hues
/// so an outfit hangs together instead of clashing at random.
/// </summary>
public static class RandomPreset
{
    private static readonly string[] HairStyles = ["none", "short", "long", "ponytail"];
    private static readonly string[] HelmStyles = ["none", "cap", "brim", "full", "horned", "hood", "hat"];
    private static readonly string[] Builds = ["slim", "normal", "wide"];
    /// <summary>
    /// The optional traits this can hand out, drawn one in five each.
    ///
    /// Nine of the eleven on <see cref="PresetDefinition.Features"/>. "blob" is added separately
    /// below because it decides the whole body rather than adding to it, and "beak" is left out
    /// on purpose: the one preset that wears it is not a blob, so gating it on creature here
    /// would only ever pair it with one, and on an otherwise human face a beak reads as a fault
    /// rather than as a choice. "eyesBig" was missing for no reason anyone had written down,
    /// which made the button's own description - the same choices the presets are built from -
    /// untrue about it.
    /// </summary>
    private static readonly string[] OptionalFeatures =
        ["armor", "cape", "glow", "ears", "tail", "antenna", "spots", "wings", "eyesBig"];

    /// <summary>Builds one random recipe. The library is used only for the category names.</summary>
    public static PresetDefinition Create(PresetLibrary library, Random random)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(random);

        // One hue anchors the outfit; the rest sit near it or directly opposite for the accent
        double outfitHue = random.NextDouble() * 360;
        double accentHue = (outfitHue + 140 + (random.NextDouble() * 80)) % 360;
        double armorHue = (outfitHue + (random.NextDouble() * 40) - 20 + 360) % 360;

        bool creature = random.Next(4) == 0;

        List<string> features = [];
        foreach (string feature in OptionalFeatures)
        {
            if (random.Next(5) == 0)
            {
                features.Add(feature);
            }
        }

        if (creature)
        {
            features.Add("blob");
        }

        return new PresetDefinition
        {
            Key = "random",
            Category = library.Categories.FirstOrDefault() ?? "job",

            // Skin stays in a narrow warm band unless this is a creature, where anything goes
            Skin = creature
                ? Hex(outfitHue, 0.45 + (random.NextDouble() * 0.3), 0.65 + (random.NextDouble() * 0.25))
                : Hex(25 + (random.NextDouble() * 20), 0.25 + (random.NextDouble() * 0.2), 0.78 + (random.NextDouble() * 0.15)),

            Hair = Hex(random.NextDouble() * 360, 0.3 + (random.NextDouble() * 0.5), 0.25 + (random.NextDouble() * 0.5)),
            Cloth = Hex(outfitHue, 0.35 + (random.NextDouble() * 0.4), 0.45 + (random.NextDouble() * 0.35)),
            Trouser = Hex(outfitHue, 0.35 + (random.NextDouble() * 0.35), 0.28 + (random.NextDouble() * 0.25)),
            Armor = Hex(armorHue, 0.25 + (random.NextDouble() * 0.4), 0.55 + (random.NextDouble() * 0.35)),
            Helm = Hex(armorHue, 0.3 + (random.NextDouble() * 0.4), 0.4 + (random.NextDouble() * 0.4)),
            Accent = Hex(accentHue, 0.55 + (random.NextDouble() * 0.35), 0.7 + (random.NextDouble() * 0.28)),

            HairStyle = creature ? "none" : HairStyles[random.Next(HairStyles.Length)],
            HelmStyle = creature ? "none" : HelmStyles[random.Next(HelmStyles.Length)],
            Build = Builds[random.Next(Builds.Length)],
            Features = [.. features],
        };
    }

    /// <summary>Converts an HSV colour to the hex form the recipes use.</summary>
    private static string Hex(double hue, double saturation, double value)
    {
        SKColor colour = SKColor.FromHsv(
            (float)(((hue % 360) + 360) % 360),
            (float)(Math.Clamp(saturation, 0, 1) * 100),
            (float)(Math.Clamp(value, 0, 1) * 100));

        return $"#{colour.Red:X2}{colour.Green:X2}{colour.Blue:X2}";
    }
}

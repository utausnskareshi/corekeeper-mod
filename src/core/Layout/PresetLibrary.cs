using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace CoreKeeperSkinTool.Layout;

/// <summary>
/// One preset character: colours and shape choices, and for some a picture as well.
///
/// A preset without a picture is drawn from this at the positions measured from the game, so it
/// costs a few dozen bytes instead of a 234x156 image, always covers all 39 frames, and stays
/// entirely original work. One with a picture is placed the way an imported picture is; the
/// colours are still filled in, for recolouring a drawing to that preset's palette.
/// </summary>
public sealed class PresetDefinition
{
    /// <summary>Identifier, also the localization key suffix (<c>preset.&lt;key&gt;</c>).</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    /// <summary>Group shown in the list (<c>presetGroup.&lt;category&gt;</c>).</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Name of an embedded picture to use instead of drawing the recipe, or null to draw it.
    ///
    /// A costume with a shape of its own - a wide hat, a hood, plate armour - is not something
    /// seven colours and a hair style can describe, so those presets carry a picture. The
    /// colours below are still filled in for them: the window recolours a drawing to a chosen
    /// preset's palette, and that has to keep working whichever kind the preset is.
    /// </summary>
    [JsonPropertyName("image")] public string? Image { get; set; }

    /// <summary>Whether this preset is a recipe drawn at run time rather than a picture.</summary>
    public bool IsDrawn => string.IsNullOrWhiteSpace(Image);

    [JsonPropertyName("skin")] public string Skin { get; set; } = "#E8C09A";

    [JsonPropertyName("hair")] public string Hair { get; set; } = "#6B4A2F";

    [JsonPropertyName("cloth")] public string Cloth { get; set; } = "#63A96B";

    [JsonPropertyName("trouser")] public string Trouser { get; set; } = "#4E6BA8";

    [JsonPropertyName("armor")] public string Armor { get; set; } = "#C87B4A";

    [JsonPropertyName("helm")] public string Helm { get; set; } = "#D65454";

    [JsonPropertyName("accent")] public string Accent { get; set; } = "#FFD166";

    /// <summary>none | short | long | ponytail</summary>
    [JsonPropertyName("hairStyle")] public string HairStyle { get; set; } = "short";

    /// <summary>none | cap | brim | full | horned | hood | hat</summary>
    [JsonPropertyName("helmStyle")] public string HelmStyle { get; set; } = "none";

    /// <summary>slim | normal | wide</summary>
    [JsonPropertyName("build")] public string Build { get; set; } = "normal";

    /// <summary>Extra traits: armor, cape, glow, blob, ears, tail, beak, wings, antenna, spots, eyesBig.</summary>
    [JsonPropertyName("features")] public string[] Features { get; set; } = [];

    /// <summary>
    /// Whether the recipe asks for one of the optional parts.
    /// A recipe with no "features" at all arrives with this null rather than empty, so the
    /// absence is treated as "none of them" instead of throwing.
    /// </summary>
    public bool Has(string feature) =>
        Features is not null && Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>Parses one of the colours, falling back to magenta so a typo is visible rather than silent.</summary>
    public static SKColor Parse(string hex)
    {
        string value = (hex ?? string.Empty).TrimStart('#');
        if (value.Length == 6 &&
            uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
        {
            return new SKColor(
                (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        return new SKColor(0xFF, 0x00, 0xFF);
    }
}

/// <summary>The preset characters shipped with the tool.</summary>
public sealed class PresetLibrary
{
    private const string ResourceName = "presets.json";

    [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;

    [JsonPropertyName("categories")] public string[] Categories { get; set; } = [];

    [JsonPropertyName("presets")] public List<PresetDefinition> Presets { get; set; } = [];

    /// <summary>Loads the presets embedded in this assembly.</summary>
    public static PresetLibrary LoadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new ToolException(
                $"プリセット定義が埋め込まれていない ({ResourceName})。配布物が壊れている可能性がある。");

        PresetLibrary library;
        try
        {
            library = JsonSerializer.Deserialize<PresetLibrary>(stream)
                ?? throw new ToolException("プリセット定義を読み取れない。");
        }
        catch (JsonException ex)
        {
            // Wrapped as SheetLayout.Parse does. Without it a damaged file reached the window as
            // System.Text.Json's own English message, which says where in the file the problem is
            // but nothing about what the file is for.
            throw new ToolException(
                $"プリセット定義の JSON を解釈できない ({ResourceName}){Environment.NewLine}  {ex.Message}", ex);
        }

        library.Validate();
        return library;
    }

    /// <summary>Rejects a definition that would produce duplicate or unnamed entries.</summary>
    public void Validate()
    {
        // A property the file leaves out arrives as null even though it is not declared nullable,
        // and the first use of it was a bare NullReferenceException with nothing in it naming the
        // file. The same reason the layout checks its own properties one by one.
        if (Presets is null)
        {
            throw new ToolException("プリセット定義に presets が無い。");
        }

        if (Categories is null)
        {
            throw new ToolException("プリセット定義に categories が無い。");
        }

        if (Presets.Count == 0)
        {
            throw new ToolException("プリセットが1件も定義されていない。");
        }

        List<string> errors = [];
        HashSet<string> seen = [];

        foreach (PresetDefinition preset in Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Key))
            {
                errors.Add("key の無いプリセットがある");
            }
            else if (!seen.Add(preset.Key))
            {
                errors.Add($"key が重複している: {preset.Key}");
            }

            if (!Categories.Contains(preset.Category, StringComparer.Ordinal))
            {
                errors.Add($"{preset.Key} の category が一覧に無い: {preset.Category}");
            }

            // Caught here rather than when the window builds the entry. A picture that failed to
            // be embedded would otherwise reach the user as one broken preset among thirty, at
            // the moment they clicked it, with nothing saying the build was incomplete.
            if (!preset.IsDrawn && !PresetArt.Exists(preset.Image))
            {
                errors.Add($"{preset.Key} の image が埋め込まれていない: {preset.Image}");
            }
        }

        if (errors.Count > 0)
        {
            throw new ToolException(
                "プリセット定義が不正:" + Environment.NewLine + "  - " +
                string.Join(Environment.NewLine + "  - ", errors));
        }
    }
}

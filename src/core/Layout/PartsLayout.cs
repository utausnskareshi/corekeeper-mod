using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreKeeperSkinTool.Layout;

/// <summary>A rectangle inside a frame, in coordinates relative to that frame's top-left corner.</summary>
public sealed class PartBox
{
    [JsonPropertyName("x")] public int X { get; set; }

    [JsonPropertyName("y")] public int Y { get; set; }

    [JsonPropertyName("w")] public int W { get; set; }

    [JsonPropertyName("h")] public int H { get; set; }
}

/// <summary>Where one part of the character sits, frame by frame.</summary>
public sealed class PartPlacement
{
    /// <summary>The file the measurements came from, kept for traceability.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;

    /// <summary>Frame index to the rectangle that part occupies. Absent frames do not draw it.</summary>
    [JsonPropertyName("frames")] public Dictionary<string, PartBox> Frames { get; set; } = [];

    /// <summary>Returns the rectangle for a frame, or null when the part is absent there.</summary>
    public PartBox? For(int frameIndex) =>
        Frames.TryGetValue(frameIndex.ToString(), out PartBox? box) ? box : null;
}

/// <summary>
/// Where every part of the game's own character sits.
///
/// Measured from the game by <c>scripts/generate-parts-layout.ps1</c> and stored as rectangles
/// only. This is what lets the starter template put a helmet where the game puts helmets, so a
/// picture drawn over it lines up with what other players' equipment does on screen.
/// The file holds no artwork: only positions and sizes.
/// </summary>
public sealed class PartsLayout
{
    private const string ResourceName = "player-parts.json";

    [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;

    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    [JsonPropertyName("parts")] public Dictionary<string, PartPlacement> Parts { get; set; } = [];

    /// <summary>Returns one part's placement, or null when it was never measured.</summary>
    public PartPlacement? Part(string name) =>
        Parts.TryGetValue(name, out PartPlacement? placement) ? placement : null;

    /// <summary>Loads the measurements embedded in this assembly.</summary>
    public static PartsLayout LoadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new ToolException(
                $"パーツ位置の定義が埋め込まれていない ({ResourceName})。配布物が壊れている可能性がある。");

        PartsLayout layout;
        try
        {
            layout = JsonSerializer.Deserialize<PartsLayout>(stream)
                ?? throw new ToolException("パーツ位置の定義を読み取れない。");
        }
        catch (JsonException ex)
        {
            // Wrapped as PresetLibrary and SheetLayout.Parse wrap theirs. Without it a damaged
            // file reached the window as System.Text.Json's own English message, which says where
            // in the file the problem is but nothing about what the file is or that re-extracting
            // the release is the way out.
            throw new ToolException(
                $"パーツ位置の定義の JSON を解釈できない ({ResourceName}){Environment.NewLine}  {ex.Message}", ex);
        }

        // A property the file leaves out arrives as null even though it is not declared nullable,
        // so "parts": null made the Count below a bare NullReferenceException with nothing in it
        // naming the file. The same reason PresetLibrary checks its own properties one by one.
        if (layout.Parts is null)
        {
            throw new ToolException($"パーツ位置の定義に parts が無い ({ResourceName})。");
        }

        if (layout.Parts.Count == 0)
        {
            throw new ToolException("パーツ位置の定義が空。");
        }

        return layout;
    }
}

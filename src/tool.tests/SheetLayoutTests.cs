using System.Text.Json;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for validating the layout definition.
///
/// The layout describes the game's sprite sheet geometry. A definition that has gone stale after
/// a game update, or a hand-edited file passed with --layout, must be rejected with a message
/// naming the problem rather than surfacing later as a NullReferenceException or a silently
/// misaligned sheet.
/// </summary>
public sealed class SheetLayoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"cks-layout-test-{Guid.NewGuid():N}");

    public SheetLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Serialises the embedded layout so a single property can be broken deliberately.</summary>
    private static Dictionary<string, JsonElement> EmbeddedAsMap()
    {
        SheetLayout layout = SheetLayout.LoadEmbedded();
        string json = JsonSerializer.Serialize(layout, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private string WriteLayout(Action<Dictionary<string, JsonElement>> damage)
    {
        Dictionary<string, JsonElement> map = EmbeddedAsMap();
        damage(map);

        string path = Path.Combine(_root, $"layout-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(map));
        return path;
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void 同梱のレイアウトは検証を通る()
    {
        SheetLayout layout = SheetLayout.LoadEmbedded();

        layout.Validate("embedded");

        Assert.Equal(234, layout.Texture.Width);
        Assert.Equal(156, layout.Texture.Height);
        Assert.Equal(39, layout.Frames.Count);
    }

    [Fact]
    public void パーツ位置の定義が全パーツ分埋め込まれている()
    {
        PartsLayout parts = PartsLayout.LoadEmbedded();
        SheetLayout layout = SheetLayout.LoadEmbedded();

        foreach (string name in new[]
                 { "body", "hair", "eyes", "shirt", "pants", "helm", "chestArmor", "pantsArmor" })
        {
            PartPlacement? placement = parts.Part(name);
            Assert.True(placement is not null, $"{name} の位置が測定されていない");
            Assert.NotEmpty(placement!.Frames);
        }

        // The body is present in every frame; anything less means the measurements are stale
        PartPlacement body = parts.Part("body")!;
        Assert.Equal(layout.Frames.Count, body.Frames.Count);
    }

    [Fact]
    public void パーツ位置がコマの内側に収まっている()
    {
        // A box reaching outside its cell would bleed into the neighbouring frame
        PartsLayout parts = PartsLayout.LoadEmbedded();
        SheetLayout layout = SheetLayout.LoadEmbedded();

        foreach (FrameRect frame in layout.Frames)
        {
            foreach ((string name, PartPlacement placement) in parts.Parts)
            {
                PartBox? box = placement.For(frame.Index);
                if (box is null)
                {
                    continue;
                }

                Assert.True(box.W > 0 && box.H > 0, $"{name} のコマ {frame.Index} の寸法が不正");
                Assert.True(box.X >= 0 && box.Y >= 0, $"{name} のコマ {frame.Index} が負の座標");
                Assert.True(
                    box.X + box.W <= frame.W && box.Y + box.H <= frame.H,
                    $"{name} のコマ {frame.Index} がコマの外にはみ出している");
            }
        }
    }

    [Fact]
    public void 標準キャラクターは全コマに絵があり寸法が正しい()
    {
        SheetLayout layout = SheetLayout.LoadEmbedded();
        using SKBitmap sheet = StarterCharacter.Build(layout, PartsLayout.LoadEmbedded());

        Assert.Equal(layout.Texture.Width, sheet.Width);
        Assert.Equal(layout.Texture.Height, sheet.Height);

        SKColor[] pixels = sheet.Pixels;
        foreach (FrameRect frame in layout.Frames)
        {
            bool any = false;
            for (int y = 0; y < frame.H && !any; y++)
            {
                for (int x = 0; x < frame.W && !any; x++)
                {
                    any = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha == 255;
                }
            }

            Assert.True(any, $"コマ {frame.Index} ({frame.Anim}) が空になっている");
        }
    }

    [Fact]
    public void 標準キャラクターは各パーツを別の色で描く()
    {
        // The whole point of the starter sheet: telling the helmet from the shirt at a glance
        SheetLayout layout = SheetLayout.LoadEmbedded();
        using SKBitmap sheet = StarterCharacter.Build(layout, PartsLayout.LoadEmbedded());

        FrameRect frame = layout.Frames.Single(f => f.Index == 0);
        SKColor[] pixels = sheet.Pixels;

        HashSet<uint> colours = [];
        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                SKColor c = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x];
                if (c.Alpha == 255)
                {
                    colours.Add((uint)c);
                }
            }
        }

        // Eight parts, each with a fill and an edge shade; well above the handful a single
        // flat silhouette would produce.
        Assert.True(colours.Count >= 8, $"色が {colours.Count} 種しかなく、パーツを区別できない");
    }

    [Fact]
    public void 標準キャラクターは手持ち装備の位置を半透明で示す()
    {
        SheetLayout layout = SheetLayout.LoadEmbedded();
        using SKBitmap sheet = StarterCharacter.Build(layout, PartsLayout.LoadEmbedded());

        int hint = sheet.Pixels.Count(c => c.Alpha > 0 && c.Alpha < 255);

        Assert.True(hint > 0, "手持ち装備の目安が描かれていない");
    }

    [Fact]
    public void LoadFile_存在しないファイルは明確なエラーになる()
    {
        Assert.Throws<ToolException>(
            () => SheetLayout.LoadFile(Path.Combine(_root, "nope.json")));
    }

    [Fact]
    public void LoadFile_JSONとして壊れていれば明確なエラーになる()
    {
        string path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
    }

    [Fact]
    public void LoadFile_contentBoxが無ければNullReferenceではなく説明付きで失敗する()
    {
        // System.Text.Json leaves a missing property null even on a non-nullable property.
        // Without an explicit check this only failed later, inside the conversion.
        string path = WriteLayout(map => map.Remove("contentBox"));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("contentBox", ex.Message);
    }

    [Fact]
    public void LoadFile_standingBoxが無ければ説明付きで失敗する()
    {
        string path = WriteLayout(map => map.Remove("standingBox"));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("standingBox", ex.Message);
    }

    [Fact]
    public void LoadFile_frameCountと実数が食い違えば失敗する()
    {
        string path = WriteLayout(map => map["frameCount"] = Json("99"));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("frameCount", ex.Message);
    }

    [Fact]
    public void LoadFile_コマがテクスチャの外に出ていれば失敗する()
    {
        string path = WriteLayout(map => map["texture"] = Json("""{"width":26,"height":26}"""));

        Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
    }

    [Fact]
    public void LoadFile_存在しないコマを参照するアニメーションを拒否する()
    {
        // Composing the sheet would otherwise index past the end of the frame list
        string path = WriteLayout(map => map["animations"] = Json("""{"idle":[0,1,999]}"""));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void LoadFile_cellの寸法が不正なら失敗する()
    {
        string path = WriteLayout(map => map["cell"] = Json("""{"width":0,"height":26}"""));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("cell", ex.Message);
    }

    [Fact]
    public void LoadFile_contentBoxがセルからはみ出せば失敗する()
    {
        string path = WriteLayout(
            map => map["contentBox"] = Json("""{"x":20,"y":20,"width":16,"height":19}"""));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("contentBox", ex.Message);
    }

    [Fact]
    public void LoadFile_animationsが無ければNullReferenceではなく説明付きで失敗する()
    {
        // Same reason as contentBox: a missing property arrives as null even on a non-nullable
        // property. The animation checks skipped the whole block when it was null, so the file
        // was pronounced valid and only failed later, inside SheetComposer.NeutralFor.
        string path = WriteLayout(map => map.Remove("animations"));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("animations", ex.Message);
    }

    [Fact]
    public void LoadFile_animationsが空なら失敗する()
    {
        string path = WriteLayout(map => map["animations"] = Json("{}"));

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("animations", ex.Message);
    }

    [Fact]
    public void LoadFile_部分的に重なるコマを拒否する()
    {
        // Frames are drawn one after another into a single texture with nothing but a per-cell
        // clip, so two frames sharing any pixel means the earlier one's art is painted over
        // without a word. The check compared top-left corners, which caught an exact duplicate
        // and let every partial overlap through while claiming to detect overlap.
        string path = WriteLayout(map =>
        {
            List<JsonElement> frames = [.. map["frames"].EnumerateArray()];

            Dictionary<string, JsonElement> first =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(frames[0].GetRawText())!;
            Dictionary<string, JsonElement> second =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(frames[1].GetRawText())!;

            // Half a cell across from the first frame: overlapping, but not the same corner
            second["x"] = Json((first["x"].GetInt32() + 13).ToString());
            second["yTopLeft"] = first["yTopLeft"];

            frames[1] = Json(JsonSerializer.Serialize(second));
            map["frames"] = Json(JsonSerializer.Serialize(frames));
        });

        ToolException ex = Assert.Throws<ToolException>(() => SheetLayout.LoadFile(path));
        Assert.Contains("重なっている", ex.Message);
    }
    [Theory]
    [InlineData(60000, 60000)]
    [InlineData(100000, 1000)]
    [InlineData(1000, 100000)]
    public void Validate_巨大なテクスチャを拒否する(int width, int height)
    {
        // Only the lower end was checked, so a definition naming 60000x60000 passed validation
        // and then failed inside SkiaSharp with "Unable to allocate pixels for the bitmap" - an
        // unhandled exception, a stack trace, and no mention of the file that caused it.
        SheetLayout huge = SheetLayout.LoadEmbedded() with { Texture = new SizeSpec(width, height) };

        ToolException ex = Assert.Throws<ToolException>(() => huge.Validate("test"));

        Assert.Contains("texture が大きすぎる", ex.Message);
    }

    [Fact]
    public void Validate_セルがテクスチャより大きいレイアウトを拒否する()
    {
        SheetLayout wrong = SheetLayout.LoadEmbedded() with { Cell = new SizeSpec(1000, 1000) };

        ToolException ex = Assert.Throws<ToolException>(() => wrong.Validate("test"));

        Assert.Contains("cell が texture より大きい", ex.Message);
    }

    [Fact]
    public void PaintedOutsideFrames_巨大なテクスチャでは確保せずに諦める()
    {
        // The map inside is one byte per pixel, so a huge texture would ask for gigabytes here
        // even though Validate refuses such a layout elsewhere. Anything that large is not a
        // sheet this program made, which is all this is asked to detect.
        SheetLayout huge = SheetLayout.LoadEmbedded() with { Texture = new SizeSpec(60000, 60000) };

        Assert.False(huge.PaintedOutsideFrames((_, _) => 255));
    }

}

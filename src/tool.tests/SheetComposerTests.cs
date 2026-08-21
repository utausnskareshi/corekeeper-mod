using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>Verifies that composition fills every frame and never crosses a cell boundary.</summary>
public sealed class SheetComposerTests
{
    /// <summary>Uses the embedded layout definition that actually ships.</summary>
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, color);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static SKColor At(SKBitmap bitmap, int x, int y) => bitmap.Pixels[(y * bitmap.Width) + x];

    [Fact]
    public void 埋め込みレイアウトが検証を通る()
    {
        Assert.Equal(234, Layout.Texture.Width);
        Assert.Equal(156, Layout.Texture.Height);
        Assert.Equal(39, Layout.Frames.Count);
        Assert.Equal(Layout.FrameCount, Layout.Frames.Count);
    }

    [Fact]
    public void Compose_全てのコマに絵が入る()
    {
        using SKBitmap sprite = Solid(8, 12, SKColors.Red);

        ComposeResult result = SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using SKBitmap sheet = result.Sheet;

        Assert.Equal(0, result.ClippedPixels);
        foreach (FrameRect frame in Layout.Frames)
        {
            int opaque = CountOpaque(sheet, frame);
            Assert.True(opaque > 0, $"コマ {frame.Index} ({frame.Anim}_{frame.Dir}) が空になっている");
        }
    }

    [Fact]
    public void Compose_足元と水平中心が基準どおりに揃う()
    {
        using SKBitmap sprite = Solid(8, 12, SKColors.Red);

        ComposeResult result = SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using SKBitmap sheet = result.Sheet;

        // Verify precisely on the idle frame (index 0), which carries no motion offset
        FrameRect frame = Layout.Frames.Single(f => f.Index == 0);
        SKRectI bounds = OpaqueBoundsIn(sheet, frame);

        int expectedBottom = Layout.StandingBox.BaselineY;              // 排他境界
        int expectedLeft = Layout.StandingBox.CenterX - (sprite.Width / 2);

        Assert.Equal(expectedBottom, bounds.Bottom);
        Assert.Equal(expectedBottom - sprite.Height, bounds.Top);
        Assert.Equal(expectedLeft, bounds.Left);
        Assert.Equal(sprite.Width, bounds.Width);
    }

    [Fact]
    public void Compose_大きすぎる絵はセル内に切り詰められ件数が報告される()
    {
        // Feed in art larger than one 26x26 cell
        using SKBitmap sprite = Solid(40, 40, SKColors.Red);

        ComposeResult result = SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using SKBitmap sheet = result.Sheet;

        Assert.True(result.ClippedPixels > 0, "はみ出しが検出されていない");

        // Scanned over the whole sheet, not per cell. Asking OpaqueBoundsIn whether a frame
        // stayed inside its own cell could only ever answer yes: it never looks outside the
        // cell it is handed, so the assertions held even with the clipping removed.
        HashSet<int> insideAFrame = [];
        foreach (FrameRect frame in Layout.Frames)
        {
            for (int y = 0; y < frame.H; y++)
            {
                for (int x = 0; x < frame.W; x++)
                {
                    insideAFrame.Add(((frame.YTopLeft + y) * sheet.Width) + frame.X + x);
                }
            }
        }

        SKColor[] pixels = sheet.Pixels;
        List<string> outside = [];

        for (int i = 0; i < pixels.Length && outside.Count < 8; i++)
        {
            if (pixels[i].Alpha != 0 && !insideAFrame.Contains(i))
            {
                outside.Add($"({i % sheet.Width},{i / sheet.Width})");
            }
        }

        Assert.True(outside.Count == 0, $"コマの外に描かれたピクセルがある: {string.Join(" ", outside)}");
    }

    [Fact]
    public void Compose_はみ出しても総ピクセル数がシート全体を超えない()
    {
        // If clipping to the cell works, the number of opaque pixels drawn
        // cannot exceed 39 frames times the cell area
        using SKBitmap sprite = Solid(40, 40, SKColors.Red);

        ComposeResult result = SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        using SKBitmap sheet = result.Sheet;

        int painted = sheet.Pixels.Count(p => p.Alpha != 0);
        int maximum = Layout.Frames.Count * Layout.Cell.Width * Layout.Cell.Height;

        Assert.True(painted <= maximum, $"描画量が上限を超えた: {painted} > {maximum}");
    }

    [Fact]
    public void Compose_オフセットが配置に反映される()
    {
        using SKBitmap sprite = Solid(6, 6, SKColors.Red);

        ComposeResult baseline = SheetComposer.Compose(Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));
        ComposeResult shifted = SheetComposer.Compose(Layout, sprite, new PlacementOptions(2, -3, AnimationStyle.Static));

        using (baseline.Sheet)
        using (shifted.Sheet)
        {
            FrameRect frame = Layout.Frames.Single(f => f.Index == 0);
            SKRectI a = OpaqueBoundsIn(baseline.Sheet, frame);
            SKRectI b = OpaqueBoundsIn(shifted.Sheet, frame);

            Assert.Equal(a.Left + 2, b.Left);
            Assert.Equal(a.Top - 3, b.Top);
        }
    }

    [Fact]
    public void Compose_動きはゲームが動かすコマだけを動かす()
    {
        // This test used to demand that the walk change height. It does not: measuring the
        // game's own character shows the foot line never moves through run_down. The frames
        // the game really does move are run_right and the sitting poses, and those are what
        // must move here.
        using SKBitmap sprite = Solid(6, 10, SKColors.Red);
        PartsLayout parts = PartsLayout.LoadEmbedded();

        ComposeResult stat = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static, parts));
        ComposeResult bob = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Bob, parts));

        using (stat.Sheet)
        using (bob.Sheet)
        {
            int Bottom(SKBitmap sheet, int index)
            {
                FrameRect frame = Layout.Frames.Single(f => f.Index == index);
                return OpaqueBoundsIn(sheet, frame).Bottom;
            }

            // Standing still, and walking towards or away from the camera: the feet stay put
            foreach (int index in new[] { 0 }.Concat(Layout.Animations["run_down"]).Concat(Layout.Animations["run_up"]))
            {
                Assert.Equal(Bottom(stat.Sheet, index), Bottom(bob.Sheet, index));
            }

            // Walking to the right, the game lifts the body by one pixel after the first frame
            int[] runRight = Layout.Animations["run_right"];
            Assert.Equal(Bottom(stat.Sheet, runRight[0]), Bottom(bob.Sheet, runRight[0]));
            Assert.Equal(Bottom(stat.Sheet, runRight[1]) - 1, Bottom(bob.Sheet, runRight[1]));

            // Sitting lifts the character off the floor: one pixel here, three facing away
            int sitDown = Layout.Animations["sit_down"][0];
            int sitUp = Layout.Animations["sit_up"][0];
            Assert.Equal(Bottom(stat.Sheet, sitDown) - 1, Bottom(bob.Sheet, sitDown));
            Assert.Equal(Bottom(stat.Sheet, sitUp) - 3, Bottom(bob.Sheet, sitUp));
        }
    }

    [Fact]
    public void CreateBlankTemplate_シート寸法で完全に透明()
    {
        using SKBitmap template = SheetComposer.CreateBlankTemplate(Layout);

        Assert.Equal(Layout.Texture.Width, template.Width);
        Assert.Equal(Layout.Texture.Height, template.Height);
        Assert.All(template.Pixels, p => Assert.Equal(0, p.Alpha));
    }

    [Fact]
    public void CreateGuideOverlay_全コマに枠が描かれる()
    {
        using SKBitmap guide = SheetComposer.CreateGuideOverlay(Layout);

        foreach (FrameRect frame in Layout.Frames)
        {
            // The top-left corner of a cell must carry a border line
            Assert.NotEqual(0, At(guide, frame.X, frame.YTopLeft).Alpha);
        }
    }

    // ------------------------------------------------------------------ Helpers

    private static int CountOpaque(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;
        int count = 0;
        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                if (pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha != 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Bounding box of the opaque pixels in a frame, relative to the cell's top-left corner.</summary>
    private static SKRectI OpaqueBoundsIn(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                if (pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha == 0)
                {
                    continue;
                }

                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }
        }

        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// Builds a stand-in for an imported character: hair on top, a face with two dark eyes
    /// below it, and a body under that. The whole thing is ringed by an outline, as art from an
    /// image generator invariably is.
    /// </summary>
    private static SKBitmap FaceSprite()
    {
        const int w = 11;
        const int h = 20;
        SKColor hair = new(0x7C, 0x3D, 0x17);
        SKColor skin = new(0xFE, 0xCD, 0x94);
        SKColor body = new(0x1A, 0x57, 0x9E);
        SKColor line = SKColors.Black;

        SKBitmap bitmap = PixelOps.CreateEmpty(w, h);
        SKColor[] pixels = bitmap.Pixels;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                pixels[(y * w) + x] = edge ? line : y < 5 ? hair : y < 11 ? skin : body;
            }
        }

        // Two eyes, well inside the face band
        pixels[(7 * w) + 3] = line;
        pixels[(7 * w) + 7] = line;

        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static SKColor[] FramePixels(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] all = sheet.Pixels;
        SKColor[] cell = new SKColor[frame.W * frame.H];

        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                cell[(y * frame.W) + x] = all[((frame.YTopLeft + y) * sheet.Width) + frame.X + x];
            }
        }

        return cell;
    }

    /// <summary>
    /// A sprite with a transparent background and spiky hair, which is what art from an image
    /// generator actually looks like. <see cref="FaceSprite"/> fills its whole rectangle, so its
    /// outline is only the frame around the edge, and the rule that the outline is not counted
    /// when choosing the hair colour makes no difference to it.
    /// </summary>
    private static SKBitmap SpikySprite()
    {
        const int w = 13;
        const int h = 20;
        SKColor hair = new(0x7C, 0x3D, 0x17);
        SKColor skin = new(0xFE, 0xCD, 0x94);
        SKColor body = new(0x1A, 0x57, 0x9E);
        SKColor line = SKColors.Black;

        SKBitmap bitmap = PixelOps.CreateEmpty(w, h);
        SKColor[] pixels = bitmap.Pixels;

        // Three two-pixel spikes over rows 0-3, transparent between them. At this size the
        // outline is most of what is up there, which is exactly why counting it picked black.
        int[] spikes = [2, 6, 10];
        for (int y = 0; y < 4; y++)
        {
            foreach (int sx in spikes)
            {
                pixels[(y * w) + sx] = line;
                pixels[(y * w) + sx + 1] = line;
            }
        }

        // The head and body below, ringed by the outline
        for (int y = 4; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || x == w - 1 || y == h - 1;
                pixels[(y * w) + x] = edge ? line : y < 8 ? hair : y < 13 ? skin : body;
            }
        }

        pixels[(10 * w) + 4] = line;
        pixels[(10 * w) + 8] = line;

        bitmap.Pixels = pixels;
        return bitmap;
    }

    [Fact]
    public void HideFace_トゲ髪でも輪郭色ではなく髪色で塗る()
    {
        // The hair colour is counted from the inside only. Counting the outline as well made
        // black win on a spiky-haired character - the top rows are mostly outline at this size -
        // and filled the back of the head solid black, which is worse than the face it replaced.
        SKColor hair = new(0x7C, 0x3D, 0x17);

        using SKBitmap sprite = SpikySprite();
        using SKBitmap hidden = PixelOps.HideFace(sprite);

        // Where an eye was, well inside the silhouette
        Assert.Equal(hair, hidden.Pixels[(10 * sprite.Width) + 4]);
        Assert.Equal(hair, hidden.Pixels[(10 * sprite.Width) + 8]);

        // And the outline around the head is left alone, or the silhouette dissolves
        Assert.Equal(SKColors.Black, hidden.Pixels[(10 * sprite.Width) + 0]);
        Assert.Equal(SKColors.Black, hidden.Pixels[(10 * sprite.Width) + sprite.Width - 1]);
    }

    [Fact]
    public void Compose_動きのあるコマでも背面から顔が消える()
    {
        // The variants a moving frame needs are cached, and the back-facing set has to have its
        // own key. Sharing a key with the front-facing set put the face back onto three of the
        // up frames - the ones whose motion changes the shape - and the checks above never saw
        // it, because a static sheet does not build variants at all.
        SKColor skin = new(0xFE, 0xCD, 0x94);

        using SKBitmap sprite = FaceSprite();
        ComposeResult result = SheetComposer.Compose(
            Layout, sprite,
            new PlacementOptions(0, 0, AnimationStyle.Lively, PartsLayout.LoadEmbedded()));

        using (result.Sheet)
        {
            // Every up frame, not just the idle one
            foreach (FrameRect back in Layout.Frames.Where(f => f.Dir == "up"))
            {
                Assert.DoesNotContain(
                    skin, FramePixels(result.Sheet, back));
            }

            // And the face survives everywhere it belongs, so this is not passing by erasing
            foreach (FrameRect front in Layout.Frames.Where(f => f.Dir == "down"))
            {
                Assert.Contains(skin, FramePixels(result.Sheet, front));
            }
        }
    }

    [Fact]
    public void Compose_背面のコマでも輪郭は髪色に塗り替えられない()
    {
        // Only the inside is repainted. Counting opaque pixels cannot tell the difference - the
        // fill does not change alpha - so the outline itself has to be looked at. Without this
        // the back of the head loses its dark edge and dissolves into a dark background.
        using SKBitmap sprite = FaceSprite();
        ComposeResult result = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));

        using (result.Sheet)
        {
            FrameRect back = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "up" });
            FrameRect front = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "down" });

            SKColor[] backCell = FramePixels(result.Sheet, back);
            SKColor[] frontCell = FramePixels(result.Sheet, front);

            Assert.Equal(
                frontCell.Count(p => p == SKColors.Black),
                backCell.Count(p => p == SKColors.Black) + 2);  // 目の2画素ぶんだけ減る
        }
    }

    [Fact]
    public void Compose_背面のコマから顔が消える()
    {
        // The same picture goes into all thirty-nine frames, so the face drawn for the front
        // used to appear on the back of the head, eyes and all. The game's own character has no
        // eyes at all on its back-facing frames - measured, not assumed: the eye boxes exist for
        // thirteen down frames and thirteen right frames, and for none of the up frames.
        SKColor skin = new(0xFE, 0xCD, 0x94);
        SKColor hair = new(0x7C, 0x3D, 0x17);

        using SKBitmap sprite = FaceSprite();
        ComposeResult result = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));

        using (result.Sheet)
        {
            FrameRect back = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "up" });
            FrameRect front = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "down" });

            SKColor[] backCell = FramePixels(result.Sheet, back);
            SKColor[] frontCell = FramePixels(result.Sheet, front);

            // The face is gone from the back, and the hair colour took its place
            Assert.DoesNotContain(skin, backCell);
            Assert.Contains(hair, backCell);

            // The front is untouched: it still has the face it was given
            Assert.Contains(skin, frontCell);

            // And the silhouette is the same size - only the inside was repainted
            Assert.Equal(
                frontCell.Count(p => p.Alpha != 0),
                backCell.Count(p => p.Alpha != 0));
        }
    }

    [Fact]
    public void Compose_背面の補正を切ると正面と同じ絵になる()
    {
        using SKBitmap sprite = FaceSprite();
        ComposeResult result = SheetComposer.Compose(
            Layout, sprite,
            new PlacementOptions(0, 0, AnimationStyle.Static, HideFaceOnBackFrames: false));

        using (result.Sheet)
        {
            FrameRect back = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "up" });
            FrameRect front = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "down" });

            Assert.Equal(FramePixels(result.Sheet, front), FramePixels(result.Sheet, back));
        }
    }

    [Fact]
    public void Compose_横向きのコマは背面の補正を受けない()
    {
        // Only the thirteen up frames are treated. The game's side-facing frames do have eyes,
        // so touching them would remove a feature the game itself draws.
        SKColor skin = new(0xFE, 0xCD, 0x94);

        using SKBitmap sprite = FaceSprite();
        ComposeResult result = SheetComposer.Compose(
            Layout, sprite, new PlacementOptions(0, 0, AnimationStyle.Static));

        using (result.Sheet)
        {
            FrameRect side = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "right" });
            Assert.Contains(skin, FramePixels(result.Sheet, side));
        }
    }
}

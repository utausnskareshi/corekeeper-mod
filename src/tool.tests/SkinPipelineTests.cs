using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Tests for the conversion pipeline.
///
/// This is the one path both the CLI and the GUI go through, so identical settings have to
/// produce identical output, and bad input has to fail with a message rather than an
/// OutOfMemoryException or a sheet the character cannot be seen in.
/// </summary>
public sealed class SkinPipelineTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    /// <summary>A small opaque square, the simplest thing that can be placed into a frame.</summary>
    private static SKBitmap Square(int size = 20, SKColor? color = null)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(size, size);
        SKColor[] pixels = bitmap.Pixels;
        Array.Fill(pixels, color ?? SKColors.Red);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    [Fact]
    public void Build_レイアウト通りの寸法のシートを返す()
    {
        using SKBitmap source = Square();

        SkinBuildResult result = SkinPipeline.Build(source, Layout, new SkinOptions());

        using (result.Sheet)
        {
            Assert.Equal(Layout.Texture.Width, result.Sheet.Width);
            Assert.Equal(Layout.Texture.Height, result.Sheet.Height);
            Assert.Equal((source.Width, source.Height), result.SourceSize);
        }
    }

    [Fact]
    public void Build_同じ設定なら常に同じ結果になる()
    {
        // The CLI and the GUI must never disagree for the same settings
        using SKBitmap source = Square();
        SkinOptions options = new(Colors: 8, Outline: SKColors.Black, Animation: AnimationStyle.Bob);

        SkinBuildResult first = SkinPipeline.Build(source, Layout, options);
        SkinBuildResult second = SkinPipeline.Build(source, Layout, options);

        using (first.Sheet)
        using (second.Sheet)
        {
            Assert.Equal(first.Sheet.Pixels, second.Sheet.Pixels);
            Assert.Equal(first.SpriteSize, second.SpriteSize);
        }
    }

    [Fact]
    public void Build_全てのコマに絵が置かれる()
    {
        using SKBitmap source = Square();
        SkinBuildResult result = SkinPipeline.Build(source, Layout, new SkinOptions());

        using (result.Sheet)
        {
            SKColor[] pixels = result.Sheet.Pixels;
            foreach (FrameRect frame in Layout.Frames)
            {
                bool any = false;
                for (int y = 0; y < frame.H && !any; y++)
                {
                    for (int x = 0; x < frame.W && !any; x++)
                    {
                        any = pixels[((frame.YTopLeft + y) * result.Sheet.Width) + frame.X + x].Alpha != 0;
                    }
                }

                Assert.True(any, $"コマ {frame.Index} ({frame.Anim}) が空になっている");
            }
        }
    }

    [Fact]
    public void Build_完全に透明な画像は明確なエラーになる()
    {
        using SKBitmap source = PixelOps.CreateEmpty(20, 20);

        ToolException ex = Assert.Throws<ToolException>(
            () => SkinPipeline.Build(source, Layout, new SkinOptions()));

        Assert.Contains("不透明", ex.Message);
    }

    [Fact]
    public void Build_しきい値が高すぎて絵が消える場合もエラーになる()
    {
        // A silhouette that is entirely semi-transparent disappears at a high threshold.
        // Silently installing an invisible character would be far worse than refusing.
        using SKBitmap source = Square(20, new SKColor(255, 0, 0, 60));

        Assert.Throws<ToolException>(
            () => SkinPipeline.Build(source, Layout, new SkinOptions(AlphaThreshold: 200)));
    }

    [Fact]
    public void Build_寸法が不正な画像を拒否する()
    {
        using SKBitmap empty = new();

        Assert.Throws<ToolException>(() => SkinPipeline.Build(empty, Layout, new SkinOptions()));
    }

    [Fact]
    public void Build_引数がnullなら例外を投げる()
    {
        using SKBitmap source = Square();

        // Cast because Build now also accepts a set of pictures; a bare null matches both
        Assert.Throws<ArgumentNullException>(() => SkinPipeline.Build((SKBitmap)null!, Layout, new SkinOptions()));
        Assert.Throws<ArgumentNullException>(() => SkinPipeline.Build(source, null!, new SkinOptions()));
        Assert.Throws<ArgumentNullException>(() => SkinPipeline.Build(source, Layout, null!));
    }

    [Fact]
    public void Build_輪郭線ありで配置枠が小さすぎればエラーになる()
    {
        using SKBitmap source = Square();

        Assert.Throws<ToolException>(() => SkinPipeline.Build(
            source, Layout, new SkinOptions(BoxWidth: 2, BoxHeight: 2, Outline: SKColors.Black)));
    }

    [Fact]
    public void Build_輪郭線は配置サイズを変えずに描かれる()
    {
        using SKBitmap source = Square();

        SkinBuildResult plain = SkinPipeline.Build(source, Layout, new SkinOptions());
        SkinBuildResult outlined = SkinPipeline.Build(
            source, Layout, new SkinOptions(Outline: OutlineColor));

        using (plain.Sheet)
        using (outlined.Sheet)
        {
            // The fit box shrinks by 2 to make room, so the placed art ends up the same size
            Assert.Equal(plain.SpriteSize.Width, outlined.SpriteSize.Width);
            Assert.Equal(plain.SpriteSize.Height, outlined.SpriteSize.Height);

            // And the outline is actually drawn. Size alone says nothing about that: an
            // AddOutline that only padded the bitmap by two transparent pixels would satisfy
            // the equality above, and this is the only check of the outline through the
            // pipeline rather than through PixelOps on its own.
            Assert.Contains(OutlineColor, outlined.Sheet.Pixels);
            Assert.DoesNotContain(OutlineColor, plain.Sheet.Pixels);
        }
    }

    /// <summary>A colour the plain square does not contain, so finding it proves it was added.</summary>
    private static readonly SKColor OutlineColor = new(0x11, 0x22, 0x33);

    [Fact]
    public void Build_各コマの足元がゲームと一致する()
    {
        // The motion used to be hand-written constants that did not match the game: the walk was
        // lifted a pixel it never lifts, and sitting was pushed two pixels DOWN when the game
        // lifts it up. It is now derived from the measured character, so every frame's foot line
        // must land exactly where the game's does.
        PartsLayout parts = PartsLayout.LoadEmbedded();
        PartPlacement body = parts.Part("body")!;

        using SKBitmap source = Square(13);
        SkinBuildResult result = SkinPipeline.Build(
            source, Layout, new SkinOptions(Trim: false, Animation: AnimationStyle.Lively));

        using (result.Sheet)
        {
            SKColor[] pixels = result.Sheet.Pixels;

            foreach (FrameRect frame in Layout.Frames)
            {
                int bottom = -1;
                for (int y = 0; y < frame.H; y++)
                {
                    for (int x = 0; x < frame.W; x++)
                    {
                        if (pixels[((frame.YTopLeft + y) * result.Sheet.Width) + frame.X + x].Alpha != 0)
                        {
                            bottom = y;
                        }
                    }
                }

                PartBox expected = body.For(frame.Index)!;
                Assert.Equal(expected.Y + expected.H - 1, bottom);
            }
        }
    }

    [Fact]
    public void Build_進行方向反転は横向きのコマだけを反転する()
    {
        // The game stores only right-facing frames and mirrors them to face left, so a picture
        // that faces left keeps facing left while walking right. Mirroring what goes into the
        // side frames is what makes the art follow the direction of travel - and it must not
        // touch the frames facing the camera or away from it.
        using SKBitmap source = Asymmetric();

        SkinBuildResult plain = SkinPipeline.Build(
            source, Layout, new SkinOptions(Trim: false, MirrorSideFrames: false));
        SkinBuildResult flipped = SkinPipeline.Build(
            source, Layout, new SkinOptions(Trim: false, MirrorSideFrames: true));

        using (plain.Sheet)
        using (flipped.Sheet)
        {
            foreach (FrameRect frame in Layout.Frames)
            {
                bool same = FramesMatch(plain.Sheet, flipped.Sheet, frame);

                if (frame.Dir == "right")
                {
                    Assert.False(same, $"コマ {frame.Index} ({frame.Anim}_right) が反転していない");
                }
                else
                {
                    Assert.True(same, $"コマ {frame.Index} ({frame.Anim}_{frame.Dir}) が変わってしまった");
                }
            }
        }
    }

    /// <summary>A picture that is obviously different from its own mirror image.</summary>
    private static SKBitmap Asymmetric()
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(13, 18);
        SKColor[] pixels = bitmap.Pixels;

        for (int y = 0; y < 18; y++)
        {
            for (int x = 0; x < 13; x++)
            {
                pixels[(y * 13) + x] = x < 6 ? SKColors.Red : SKColors.Blue;
            }
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static bool FramesMatch(SKBitmap a, SKBitmap b, FrameRect frame)
    {
        SKColor[] left = a.Pixels;
        SKColor[] right = b.Pixels;

        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                int index = ((frame.YTopLeft + y) * a.Width) + frame.X + x;
                if (left[index] != right[index])
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Fact]
    public void 実測データ_着席のオフセットは持ち上げ方向になっている()
    {
        // The game lifts the character off the floor when sitting - one pixel facing the camera,
        // three facing away - because the feet come up onto the seat. The old constant pushed it
        // two pixels down instead, below the line every other frame is anchored to.
        PartsLayout parts = PartsLayout.LoadEmbedded();
        PartPlacement body = parts.Part("body")!;

        int IdleBottom(string direction)
        {
            PartBox box = body.For(Layout.Animations[$"idle_{direction}"][0])!;
            return box.Y + box.H - 1;
        }

        int SitBottom(string direction)
        {
            PartBox box = body.For(Layout.Animations[$"sit_{direction}"][0])!;
            return box.Y + box.H - 1;
        }

        Assert.Equal(IdleBottom("down") - 1, SitBottom("down"));
        Assert.Equal(IdleBottom("right") - 1, SitBottom("right"));
        Assert.Equal(IdleBottom("up") - 3, SitBottom("up"));
    }

    [Fact]
    public void 実測データ_歩行コマの足元は上下に動かない()
    {
        // Measured from the game: the body's foot line never moves through run_down or run_up,
        // and drops by exactly one pixel in run_right. Anything else is invented motion.
        PartsLayout parts = PartsLayout.LoadEmbedded();
        PartPlacement body = parts.Part("body")!;

        int[] Bottoms(string direction) =>
            [.. Layout.Animations[$"run_{direction}"]
                .Select(i => body.For(i)!)
                .Select(b => b.Y + b.H - 1)];

        Assert.All(Bottoms("down"), b => Assert.Equal(22, b));
        Assert.All(Bottoms("up"), b => Assert.Equal(22, b));
        Assert.Equal([22, 21, 21, 21, 21, 21], Bottoms("right"));
    }

    [Fact]
    public void Build_はみ出し量はコマ数倍に膨らまない()
    {
        // The same sprite is drawn into all 39 frames. Summing the overflow per frame would
        // report roughly 39 times the pixels actually lost.
        using SKBitmap source = Square();

        // Enough to push part of the art out of the 26x26 cell, but not all of it:
        // a fully clipped result is rejected outright and never reports a count.
        SkinBuildResult result = SkinPipeline.Build(
            source, Layout, new SkinOptions(OffsetX: 8, OffsetY: 6));

        Assert.True(result.ClippedPixels > 0, "はみ出しが起きていない設定になっている");

        using (result.Sheet)
        {
            int cellArea = Layout.Cell.Width * Layout.Cell.Height;
            Assert.True(
                result.ClippedPixels <= cellArea,
                $"はみ出し量 {result.ClippedPixels} が1コマの面積 {cellArea} を超えている");
        }
    }

    [Fact]
    public void Build_元画像を変更しない()
    {
        // A distinct border so background removal has something to flood from without
        // erasing the whole picture, which a single flat colour would do.
        using SKBitmap source = Square(20, SKColors.White);
        SKColor[] pixels = source.Pixels;
        for (int y = 5; y < 15; y++)
        {
            for (int x = 5; x < 15; x++)
            {
                pixels[(y * source.Width) + x] = SKColors.Red;
            }
        }

        source.Pixels = pixels;
        SKColor[] before = source.Pixels;

        SkinBuildResult result = SkinPipeline.Build(
            source, Layout, new SkinOptions(RemoveBackground: true, Colors: 4));

        using (result.Sheet)
        {
            Assert.Equal(before, source.Pixels);
        }
    }
    [Theory]
    // 正方形の絵を正方形の枠へ入れれば助言は出ない（枠の高さを変えても同じ）
    [InlineData(16, 16, 16, 16, false)]
    [InlineData(10, 10, 10, 10, false)]
    // 既定の枠 16x19 に対し、比 0.6〜0.7 の絵は「十分に使えている」
    [InlineData(12, 19, 16, 19, false)]
    // 枠よりはっきり細い絵だけが助言の対象
    [InlineData(8, 19, 16, 19, true)]
    // 枠の高さを縮めても、絵と枠の比が同じなら助言は出ない
    [InlineData(8, 10, 16, 20, false)]
    // 寸法が取れていないときは何も言わない
    [InlineData(12, 19, 0, 0, false)]
    public void UsesLittleOfBox_絵と枠の縦横比を比べて判定する(
        int spriteWidth, int spriteHeight, int boxWidth, int boxHeight, bool narrow)
    {
        // Compared against the box's proportions, not its width alone. Judging by width only,
        // a perfectly square picture was told it was "tall and narrow" the moment the box height
        // was reduced - and the box is a setting the help invites people to change.
        using SKBitmap sheet = PixelOps.CreateEmpty(1, 1);

        SkinBuildResult result = new(
            sheet, (0, 0), (0, 0), (spriteWidth, spriteHeight), 0, (boxWidth, boxHeight));

        Assert.Equal(narrow, result.UsesLittleOfBox);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HideFaceOnBackFramesが合成へそのまま渡る(bool hide)
    {
        // What --keep-back-face turns off. The option is read from SkinOptions and handed to
        // PlacementOptions, and nothing checked that the handover happened: with the field
        // dropped on the way through, the flag would be accepted and ignored.
        SKColor skin = new(0xFE, 0xCD, 0x94);
        SKColor hair = new(0x7C, 0x3D, 0x17);

        // Ringed by an outline, as art from an image generator is. Without one the skin would
        // touch transparency at the edges, where HideFace deliberately leaves pixels alone.
        using SKBitmap source = PixelOps.CreateEmpty(11, 20);
        SKColor[] pixels = source.Pixels;
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 11; x++)
            {
                bool edge = x == 0 || y == 0 || x == 10 || y == 19;
                pixels[(y * 11) + x] =
                    edge ? SKColors.Black : y < 5 ? hair : y < 10 ? skin : SKColors.Blue;
            }
        }

        source.Pixels = pixels;

        SkinBuildResult built = SkinPipeline.Build(
            source, Layout,
            new SkinOptions(
                RemoveBackground: false, Trim: false,
                Resample: ResampleMode.Nearest, AlphaThreshold: 1,
                Animation: AnimationStyle.Static,
                HideFaceOnBackFrames: hide));

        using (built.Sheet)
        {
            FrameRect back = Layout.Frames.Single(f => f is { Anim: "idle", Dir: "up" });
            SKColor[] cell = FramePixels(built.Sheet, back);

            // The face survives on the back only when the option is off
            Assert.Equal(hide, !cell.Contains(skin));
        }
    }

    /// <summary>The pixels of one frame, in reading order.</summary>
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

}

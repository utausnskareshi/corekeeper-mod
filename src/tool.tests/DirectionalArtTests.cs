using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using CoreKeeperSkinTool.Sheet;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Verifies the path that takes a drawing per facing.
///
/// The game draws its own character differently from each side - the side view is two pixels
/// narrower than the front, and the back has no eyes - so a picture made for each reads better
/// at this size than one picture turned to face three ways. What is checked here is that each
/// facing's frames really come from its own picture, and that supplying one turns off the
/// stand-in the tool used to apply in its place.
/// </summary>
public sealed class DirectionalArtTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(width, height);
        SKColor[] pixels = new SKColor[width * height];
        Array.Fill(pixels, color);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    /// <summary>The colours found inside one frame, ignoring anything transparent.</summary>
    private static HashSet<SKColor> ColoursIn(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;
        HashSet<SKColor> colours = [];

        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                SKColor colour = pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x];
                if (colour.Alpha != 0)
                {
                    colours.Add(colour);
                }
            }
        }

        return colours;
    }

    private static FrameRect FrameOf(string animation) =>
        Layout.Frames.Single(f => f.Index == Layout.Animations[animation][0]);

    [Fact]
    public void 向きごとの絵がそれぞれのコマに入る()
    {
        using SKBitmap front = Solid(8, 12, SKColors.Red);
        using SKBitmap side = Solid(6, 12, SKColors.Green);
        using SKBitmap back = Solid(8, 12, SKColors.Blue);

        ComposeResult result = SheetComposer.Compose(
            Layout,
            new DirectionalArt(front, side, back),
            new PlacementOptions(0, 0, AnimationStyle.Static));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal([SKColors.Red], ColoursIn(sheet, FrameOf("idle_down")));
        Assert.Equal([SKColors.Green], ColoursIn(sheet, FrameOf("idle_right")));
        Assert.Equal([SKColors.Blue], ColoursIn(sheet, FrameOf("idle_up")));
    }

    [Fact]
    public void 指定しなかった向きは正面の絵を使う()
    {
        using SKBitmap front = Solid(8, 12, SKColors.Red);
        using SKBitmap side = Solid(6, 12, SKColors.Green);

        ComposeResult result = SheetComposer.Compose(
            Layout,
            new DirectionalArt(front, side),
            new PlacementOptions(0, 0, AnimationStyle.Static, HideFaceOnBackFrames: false));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal([SKColors.Green], ColoursIn(sheet, FrameOf("idle_right")));
        Assert.Equal([SKColors.Red], ColoursIn(sheet, FrameOf("idle_up")));
    }

    [Fact]
    public void 側面の絵があるときは左右反転しない()
    {
        // A drawing made for the right-facing frames already faces right. Mirroring it would
        // turn it round and undo the reason it was drawn.
        using SKBitmap front = Marked();
        using SKBitmap side = Marked();

        ComposeResult mirroredOnly = SheetComposer.Compose(
            Layout, new DirectionalArt(front), new PlacementOptions(0, 0, AnimationStyle.Static, MirrorSideFrames: true));

        ComposeResult withSide = SheetComposer.Compose(
            Layout,
            new DirectionalArt(front, side),
            new PlacementOptions(0, 0, AnimationStyle.Static, MirrorSideFrames: true));

        using SKBitmap mirroredSheet = mirroredOnly.Sheet;
        using SKBitmap sideSheet = withSide.Sheet;

        FrameRect frame = FrameOf("idle_right");

        // The mark sits on one side of the picture, so the two sheets disagree about which side
        // of the frame it lands on unless the supplied art was left alone
        Assert.NotEqual(MarkColumn(mirroredSheet, frame), MarkColumn(sideSheet, frame));
    }

    [Fact]
    public void 背面の絵があるときは顔を隠さない()
    {
        // A drawing of the character's back has no face to hide. Blanking its top would paint
        // over the hair that was drawn there.
        using SKBitmap front = Solid(8, 12, SKColors.Red);
        using SKBitmap back = Solid(8, 12, SKColors.Blue);

        ComposeResult result = SheetComposer.Compose(
            Layout,
            new DirectionalArt(front, null, back),
            new PlacementOptions(0, 0, AnimationStyle.Static, HideFaceOnBackFrames: true));

        using SKBitmap sheet = result.Sheet;

        // Untouched: one colour throughout, rather than the two a blanked face leaves behind
        Assert.Equal([SKColors.Blue], ColoursIn(sheet, FrameOf("idle_up")));
    }

    [Fact]
    public void 正面だけ渡したときの結果は従来と変わらない()
    {
        // The single-picture path is what almost every user takes, and it has to keep producing
        // exactly what it did before this was added.
        //
        // Held to a recorded answer rather than to the other overload. Compose(layout, bitmap, _)
        // is one line that calls Compose(layout, new DirectionalArt(bitmap), _), so comparing the
        // two ran the same code twice and agreed with itself whatever it did.
        //
        // The number below is this build's own output, and it is the published build's output
        // too: the tool at 609054f and the tool as it stands now were both run over the same
        // picture in twelve combinations of --anim, --colors, --keep-back-face and --outline,
        // and every sheet came back identical pixel for pixel. If this test fails, the
        // single-picture path has changed - check that it was meant to before recording a new
        // number here.
        using SKBitmap front = Solid(8, 12, SKColors.Red);

        ComposeResult result = SheetComposer.Compose(
            Layout, front, new PlacementOptions(0, 0, AnimationStyle.Lively));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal(SingleImageFingerprint, Fingerprint(sheet));
    }

    /// <summary>
    /// What the single-picture path draws for a plain 8x12 red block, as a short string.
    /// See <see cref="正面だけ渡したときの結果は従来と変わらない"/> for where it comes from.
    /// </summary>
    private const string SingleImageFingerprint = "2EBCCE06360F39FC51C7DE156696EC26";

    /// <summary>A short, stable description of every pixel in a sheet.</summary>
    private static string Fingerprint(SKBitmap sheet)
    {
        SKColor[] pixels = sheet.Pixels;
        byte[] bytes = new byte[pixels.Length * 4];

        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[(i * 4) + 0] = pixels[i].Red;
            bytes[(i * 4) + 1] = pixels[i].Green;
            bytes[(i * 4) + 2] = pixels[i].Blue;
            bytes[(i * 4) + 3] = pixels[i].Alpha;
        }

        // Half the digest, so a mismatch prints both values in full rather than being cut short
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..32];
    }

    [Fact]
    public void 向きごとの絵はコマの外へはみ出さない()
    {
        using SKBitmap front = Solid(8, 12, SKColors.Red);
        using SKBitmap side = Solid(14, 18, SKColors.Green);
        using SKBitmap back = Solid(8, 12, SKColors.Blue);

        ComposeResult result = SheetComposer.Compose(
            Layout,
            new DirectionalArt(front, side, back),
            new PlacementOptions(0, 0, AnimationStyle.Lively));

        using SKBitmap sheet = result.Sheet;

        // Every drawn pixel has to sit inside some frame, whichever picture it came from
        Assert.False(Layout.PaintedOutsideFrames((x, y) => sheet.Pixels[(y * sheet.Width) + x].Alpha));
    }

    [Fact]
    public void 側面は正面と同じ高さに縮小される()
    {
        // The game's character is the same height from every side. Fitting each picture to the
        // box on its own made a narrow side view taller than the front, so the character grew
        // as it turned.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(20, 60, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions(Animation: AnimationStyle.Static));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal(
            HeightOf(sheet, FrameOf("idle_down")),
            HeightOf(sheet, FrameOf("idle_right")));
    }

    [Fact]
    public void 向き別の実寸が向きごとに返る()
    {
        // The result carried one size, the front's, and it was reported beside the box as if it
        // described the whole sheet. Only the front is fitted to that box: a square side view
        // beside a 40x60 front is scaled to the front's height alone and lands at 19 wide
        // against a box of 16, so the one number was announcing a limit the sheet breaks.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(64, 64, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions(Animation: AnimationStyle.Static));

        using SKBitmap sheet = result.Sheet;

        // Tied to the sheet rather than to a written-down pair, so the numbers reported stay the
        // numbers drawn however the fitting changes
        Assert.Equal(
            (WidthOf(sheet, FrameOf("idle_right")), HeightOf(sheet, FrameOf("idle_right"))),
            result.RightSize);

        // Wider than the box, which is the case the single size hid
        Assert.True(
            result.RightSize!.Value.Width > result.BoxSize.Width,
            $"側面 {result.RightSize.Value.Width} が枠 {result.BoxSize.Width} を超えていない");

        // Left out, so there is no separate art for it to describe - the front's fills those
        // frames, and naming a size there would invent a facing the user never supplied
        Assert.Null(result.UpSize);
    }

    [Fact]
    public void 向き別の実寸は輪郭線を足した後の大きさになる()
    {
        // Measured where the facing is prepared, the size would be taken before the outline is
        // drawn, and the outline grows every picture by a pixel on each side. The front's size
        // is measured after it, so the two reported numbers would be two apart while the sheet
        // has them exactly as tall as each other.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(64, 64, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side),
            Layout,
            new SkinOptions(Animation: AnimationStyle.Static, Outline: SKColors.Black));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal(
            (WidthOf(sheet, FrameOf("idle_right")), HeightOf(sheet, FrameOf("idle_right"))),
            result.RightSize);

        Assert.Equal(result.SpriteSize.Height, result.RightSize!.Value.Height);
    }

    /// <summary>A 400x400 picture holding one opaque dot in each corner, which the shrink erases.</summary>
    private static SKBitmap Corners()
    {
        SKBitmap picture = PixelOps.CreateEmpty(400, 400);
        SKColor[] pixels = new SKColor[400 * 400];
        pixels[0] = SKColors.Black;
        pixels[399] = SKColors.Black;
        pixels[399 * 400] = SKColors.Black;
        pixels[(399 * 400) + 399] = SKColors.Black;
        picture.Pixels = pixels;
        return picture;
    }

    [Fact]
    public void 正面の絵が縮小で消えたら向き別の絵があってもその理由を示す()
    {
        // The side view survives, so the sheet is not empty and the whole-sheet message is not
        // reached; the per-facing check then said the front's cells were empty because the
        // offsets, the box size or a facing's proportions put the art outside them. None of that
        // was true - the front itself had vanished in the shrink, which is what the same picture
        // on its own is told.
        using SKBitmap front = Corners();
        using SKBitmap side = Solid(40, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions(Animation: AnimationStyle.Static)));

        Assert.Equal("error.pipeline.facingVanishedFront", error.MessageKey);
        Assert.Contains("正面", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 正面が消えたときは絵を渡していない向きのコマも空になると言う()
    {
        // Written from the right and back messages, where only that facing's frames are lost.
        // The front is also what a facing with no picture of its own is drawn from, so with only
        // a side view given the back frames come out empty too - "only that facing" was untrue
        using SKBitmap front = Corners();
        using SKBitmap side = Solid(40, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions(Animation: AnimationStyle.Static)));

        Assert.DoesNotContain("その向きのコマだけ", error.Message, StringComparison.Ordinal);
        Assert.Contains("絵を渡していない向きのコマ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 正面の絵だけが縮小で消えたときは今までどおりの理由を示す()
    {
        // The case that must not change: with no other facing, the sheet is empty outright and
        // the message names the alpha cut-off and the background removal, as it always has
        using SKBitmap front = Corners();

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front), Layout, new SkinOptions(Animation: AnimationStyle.Static)));

        Assert.Equal("error.pipeline.allTransparent", error.MessageKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 向き別の絵が縮小で消えたら理由を示して止める(bool side)
    {
        // Art made of fine lines survives the checks either side of the shrink and disappears in
        // it: the picture as it arrived has opaque pixels, and the finished sheet is not wholly
        // empty because the front is still in it. Measured on a 400x400 picture with one opaque
        // dot in each corner, all thirteen right-facing frames came out blank and the run
        // reported success - a character who vanishes whenever they walk sideways.
        using SKBitmap front = Solid(40, 60, SKColors.Red);

        SKBitmap facing = PixelOps.CreateEmpty(400, 400);
        SKColor[] pixels = new SKColor[400 * 400];
        pixels[0] = SKColors.Black;
        pixels[399] = SKColors.Black;
        pixels[399 * 400] = SKColors.Black;
        pixels[(399 * 400) + 399] = SKColors.Black;
        facing.Pixels = pixels;

        using (facing)
        {
            ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
                side ? new SkinSources(front, facing) : new SkinSources(front, null, facing),
                Layout,
                new SkinOptions(Animation: AnimationStyle.Static)));

            Assert.Contains(side ? "右向き" : "背面", error.Message, StringComparison.Ordinal);

            // One key per facing, rather than one template with the facing filled in. The window
            // resolves the template in whichever language the user chose, and a name handed over
            // as an argument would have stayed Japanese inside the English sentence.
            Assert.Equal(
                side ? "error.pipeline.facingVanishedRight" : "error.pipeline.facingVanishedBack",
                error.MessageKey);
        }
    }

    [Fact]
    public void 横に広い側面の絵でも正面と同じ高さになる()
    {
        // The case the test above cannot see. It makes the side view narrower than the front, so
        // height is what limits both of them and they agree whatever the fitting does. A side
        // view that is wider - a hat brim, a tail, an arm held out - is limited by width instead,
        // and fitting it into a box "front height tall" then left it shorter than the front: the
        // character lost a sixth of its height every time it turned to the right, which is the
        // opposite of what fitting to the front's height is for.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(60, 60, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions(Animation: AnimationStyle.Static));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal(
            HeightOf(sheet, FrameOf("idle_down")),
            HeightOf(sheet, FrameOf("idle_right")));
    }

    [Fact]
    public void 減色は3枚まとめて行われる()
    {
        // Reduced one picture at a time, each facing got its own palette of that many colours,
        // and the character changed colour as it turned - the very thing drawing three views is
        // meant to avoid. Six colours in, four allowed: a shared palette can only be four.
        using SKBitmap front = TwoTone(SKColors.Red, SKColors.DarkRed);
        using SKBitmap side = TwoTone(SKColors.Green, SKColors.DarkGreen);
        using SKBitmap back = TwoTone(SKColors.Blue, SKColors.DarkBlue);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side, back),
            Layout,
            new SkinOptions(Colors: 4, Animation: AnimationStyle.Static));

        using SKBitmap sheet = result.Sheet;

        HashSet<SKColor> used = [.. sheet.Pixels.Where(p => p.Alpha != 0)];

        Assert.True(used.Count <= 4, $"シート全体で {used.Count} 色使われている");
    }

    [Fact]
    public void 正面だけなら減色の結果は従来と変わらない()
    {
        // Held to a recorded answer for the same reason as the compose test above:
        // Build(bitmap, _, _) forwards to Build(new SkinSources(bitmap), _, _), so running both
        // and comparing them ran one implementation twice.
        using SKBitmap front = TwoTone(SKColors.Red, SKColors.DarkRed);

        SkinBuildResult result = SkinPipeline.Build(
            front, Layout, new SkinOptions(Colors: 4, Animation: AnimationStyle.Static));

        using SKBitmap sheet = result.Sheet;

        Assert.Equal(SingleImageQuantizedFingerprint, Fingerprint(sheet));
    }

    /// <summary>
    /// What the single-picture path builds from a two-tone block reduced to four colours.
    /// Recorded the same way as <see cref="SingleImageFingerprint"/>.
    /// </summary>
    private const string SingleImageQuantizedFingerprint = "EEC28A52C41CDA452626B44F7665D713";

    /// <summary>Height of the opaque art inside a frame.</summary>
    private static int HeightOf(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;
        int top = -1;
        int bottom = -1;

        for (int y = 0; y < frame.H; y++)
        {
            for (int x = 0; x < frame.W; x++)
            {
                if (pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha == 0)
                {
                    continue;
                }

                if (top < 0)
                {
                    top = y;
                }

                bottom = y;
                break;
            }
        }

        return top < 0 ? 0 : bottom - top + 1;
    }

    /// <summary>Width of the opaque art inside a frame.</summary>
    private static int WidthOf(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;
        int left = -1;
        int right = -1;

        for (int x = 0; x < frame.W; x++)
        {
            for (int y = 0; y < frame.H; y++)
            {
                if (pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x].Alpha == 0)
                {
                    continue;
                }

                if (left < 0)
                {
                    left = x;
                }

                right = x;
                break;
            }
        }

        return left < 0 ? 0 : right - left + 1;
    }

    /// <summary>A picture split between two colours, so quantisation has something to merge.</summary>
    private static SKBitmap TwoTone(SKColor upper, SKColor lower)
    {
        SKBitmap bitmap = Solid(20, 40, upper);
        SKColor[] pixels = bitmap.Pixels;

        for (int y = 20; y < 40; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                pixels[(y * 20) + x] = lower;
            }
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    /// <summary>A picture with a single mark on its left half, so a flip can be detected.</summary>
    private static SKBitmap Marked()
    {
        SKBitmap bitmap = Solid(8, 12, SKColors.Red);
        SKColor[] pixels = bitmap.Pixels;

        for (int y = 0; y < 12; y++)
        {
            pixels[(y * 8) + 1] = SKColors.Yellow;
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    /// <summary>Where the mark ended up inside a frame, as a column relative to the cell.</summary>
    private static int MarkColumn(SKBitmap sheet, FrameRect frame)
    {
        SKColor[] pixels = sheet.Pixels;

        for (int x = 0; x < frame.W; x++)
        {
            for (int y = 0; y < frame.H; y++)
            {
                if (pixels[((frame.YTopLeft + y) * sheet.Width) + frame.X + x] == SKColors.Yellow)
                {
                    return x;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// How many opaque pixels a facing's frames hold altogether - zero meaning the facing is gone.
    ///
    /// Named for what it returns. Called EmptyFramesFor at first, which is what OpaqueByDirection
    /// sounded like it counted and is the opposite of what it does: the one caller reads it as
    /// "greater than zero means there is art", which under the old name read as its own negation.
    /// </summary>
    private static int OpaquePixelsFor(SKBitmap sheet, string direction)
    {
        Dictionary<string, int> opaque = Layout.OpaqueByDirection(
            (x, y) => sheet.Pixels[(y * sheet.Width) + x].Alpha);

        Assert.True(opaque.ContainsKey(direction), $"レイアウトに {direction} のコマが無い");

        return opaque[direction];
    }

    [Fact]
    public void 位置調整で正面のコマが空になったら向き別の絵があっても止める()
    {
        // The guard over the finished sheet asked whether the SHEET was empty, and one surviving
        // facing answered no. Measured before this: the same --offset-x 21 that stops a
        // single-picture run came out, with a --side picture added, as a sheet whose thirteen
        // down frames and thirteen up frames were all blank - exit 0, and generate --install then
        // put it in the game. cks validate refused the same file: "26 コマが完全に空".
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(200, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front, side),
            Layout,
            new SkinOptions(OffsetX: 21)));

        Assert.Equal("error.pipeline.facingEmptyFront", error.MessageKey);
        Assert.Contains("正面", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 位置調整で右向きのコマだけが空になっても止める()
    {
        // The other direction of the same hole: here the front is the wide one and the side
        // picture is what leaves its cells. Measured before this: right 13/13 blank, down and up
        // full, exit 0. Each facing is centred on its own width, which is why one offset can put
        // one facing outside its cell and leave another inside.
        using SKBitmap front = Solid(60, 60, SKColors.Red);
        using SKBitmap side = Solid(10, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front, side),
            Layout,
            new SkinOptions(OffsetX: 17, Animation: AnimationStyle.Static)));

        Assert.Equal("error.pipeline.facingEmptyRight", error.MessageKey);
        Assert.Contains("右向き", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 絵が1枚でシート全体が空なら位置調整の従来の文面で止める()
    {
        // The facing check is added after the whole-sheet one, not in place of it: a sheet that
        // came out empty outright keeps the message it always had, which names the offsets and
        // the placement size, because a facing's name would say nothing about it.
        using SKBitmap front = Solid(40, 60, SKColors.Red);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front),
            Layout,
            new SkinOptions(OffsetX: 21)));

        Assert.Equal("error.pipeline.offsetOutside", error.MessageKey);
    }

    [Theory]
    [InlineData(19, 0)]
    [InlineData(-20, 0)]
    [InlineData(0, 22)]
    public void 絵が1枚でも向きが1つ生き残れば向き別の文面で止める(int offsetX, int offsetY)
    {
        // One picture does not mean the facings empty together, which an earlier version of the
        // comment above claimed. The motion shifts and resizes frame by frame and differs by
        // facing, so at these offsets - measured, one step inside the ones that empty the sheet -
        // a facing keeps a pixel or two, the sheet-wide test passes, and this check is the one
        // that speaks. Before the check these runs exited 0, and cks validate called the sheets
        // they wrote broken: "37 コマが完全に空".
        using SKBitmap front = Solid(40, 60, SKColors.Red);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front),
            Layout,
            new SkinOptions(OffsetX: offsetX, OffsetY: offsetY)));

        Assert.Equal("error.pipeline.facingEmptyFront", error.MessageKey);
    }

    [Fact]
    public void 向きの綴りが違うレイアウトでも空の向きを見つける()
    {
        // Dir is free-form and --layout may hand over any spelling. Checking only "down",
        // "right" and "up" left this check inert: measured with the embedded layout's down and up
        // capitalised, the sheet this exists to refuse - 26 empty frames, as cks validate agrees -
        // came out of Build with no complaint at all. The side picture still reaches the frames
        // spelled "right", so one facing survives and the sheet-wide test passes.
        SheetLayout capitalised = Layout with
        {
            Frames =
            [
                .. Layout.Frames.Select(frame => frame.Dir == "right"
                    ? frame
                    : frame with { Dir = frame.Dir.ToUpperInvariant() }),
            ],
        };

        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(200, 60, SKColors.Green);

        ToolException error = Assert.Throws<ToolException>(() => SkinPipeline.Build(
            new SkinSources(front, side),
            capitalised,
            new SkinOptions(OffsetX: 21)));

        // The layout's own spelling, quoted back. It came from the user's file, so it is an
        // identifier rather than a word with a translation.
        Assert.Equal("error.pipeline.facingEmptyNamed", error.MessageKey);
        Assert.Contains("DOWN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 位置調整が無ければ向き別の絵はそのまま通る()
    {
        // The guard against the guard: the same two pictures without an offset must still build,
        // with every facing drawn. A check that refused these would have stopped every
        // directional run there is.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(200, 60, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions());

        using (result.Sheet)
        {
            Assert.All(
                new[] { "down", "right", "up" },
                direction => Assert.True(
                    OpaquePixelsFor(result.Sheet, direction) > 0,
                    $"{direction} のコマに絵が入っていない"));
        }
    }

    [Fact]
    public void 極端に横長の向き別の絵は配置前に切り詰める()
    {
        // The width of the picture the scaling produces is decided by the source's proportions
        // and by nothing the user can cap: --width and --height are held to 1..26 for the reason
        // their own doc comment gives - "a mistyped 4000 took eighteen seconds, 8000 never
        // finished, and 100000 ended in SkiaSharp's own allocation failure" - and this path was
        // outside that. Measured: a 60000 by 1 picture handed to --side became a 1,440,000 by 24
        // bitmap and took 17.6 seconds to produce a sheet; with the cut it takes 0.2 and the sheet
        // is identical pixel for pixel.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(60000, 1, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions());

        using (result.Sheet)
        {
            Assert.NotNull(result.RightSize);
            Assert.True(
                result.RightSize!.Value.Width <= 5000,
                $"向き別の絵が切り詰められていない: {result.RightSize.Value.Width}");
        }
    }

    [Fact]
    public void 横長でも常識の範囲の向き別の絵はそのまま置く()
    {
        // The cut has a floor well above anything an ordinary run reaches, so that the two numbers
        // counted after it - the width reported for the facing and the pixels the composer
        // discarded - keep saying what they always said. Measured: 2000 dots wide scales to 633
        // and comes through untouched, and the sheet, that width and the clipped count are all
        // identical to what the tool produced before the cut existed.
        using SKBitmap front = Solid(40, 60, SKColors.Red);
        using SKBitmap side = Solid(2000, 60, SKColors.Green);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front, side), Layout, new SkinOptions());

        using (result.Sheet)
        {
            Assert.Equal(633, result.RightSize!.Value.Width);
        }
    }

    [Fact]
    public void 向きを名乗らないレイアウトでも組み立てられる()
    {
        // Dir is free-form and Validate never looks at it, so a layout passed to --layout that
        // leaves "dir" out arrives here as null - which is the one key a Dictionary refuses.
        // Measured: counting by facing without this ended the run with "Value cannot be null.
        // (Parameter 'key')" on a layout that had built a sheet perfectly well before.
        SheetLayout unnamed = Layout with
        {
            Frames = [.. Layout.Frames.Select(frame => frame with { Dir = null! })],
        };

        using SKBitmap front = Solid(40, 60, SKColors.Red);

        SkinBuildResult result = SkinPipeline.Build(
            new SkinSources(front), unnamed, new SkinOptions());

        result.Sheet.Dispose();
    }
}

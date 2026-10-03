using CoreKeeperSkinTool.Editing;
using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Tests;

/// <summary>
/// Pixel editing tests.
/// The focus is on keeping relative positions when editing all frames at once, never crossing
/// a cell boundary, and undo restoring the previous state.
/// </summary>
public sealed class EditorDocumentTests
{
    private static readonly SheetLayout Layout = SheetLayout.LoadEmbedded();

    private static EditorDocument CreateDocument(SKColor? fill = null)
    {
        using SKBitmap sheet = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        if (fill is { } color)
        {
            SKColor[] pixels = sheet.Pixels;
            Array.Fill(pixels, color);
            sheet.Pixels = pixels;
        }

        return new EditorDocument(sheet, Layout);
    }

    /// <summary>Converts a position relative to a cell into sheet coordinates.</summary>
    private static (int X, int Y) At(int frameIndex, int cellX, int cellY)
    {
        FrameRect frame = Layout.Frames.Single(f => f.Index == frameIndex);
        return (frame.X + cellX, frame.YTopLeft + cellY);
    }

    [Fact]
    public void Paint_単一コマ指定なら他のコマは変わらない()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 10, 10);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);

        Assert.Equal(SKColors.Red, document.GetPixel(x, y));

        (int otherX, int otherY) = At(1, 10, 10);
        Assert.Equal(0, document.GetPixel(otherX, otherY).Alpha);
    }

    [Fact]
    public void Paint_全コマ指定なら全てのコマの同じ位置が塗られる()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 10, 10);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.AllFrames);

        foreach (FrameRect frame in Layout.Frames)
        {
            SKColor color = document.GetPixel(frame.X + 10, frame.YTopLeft + 10);
            Assert.Equal(SKColors.Red, color);
        }
    }

    [Fact]
    public void Paint_範囲外は無視される()
    {
        EditorDocument document = CreateDocument();

        document.BeginChange();
        document.Paint(-1, -1, SKColors.Red, EditScope.AllFrames);
        document.Paint(Layout.Texture.Width, 0, SKColors.Red, EditScope.AllFrames);

        Assert.All(document.Pixels, p => Assert.Equal(0, p.Alpha));
    }

    [Fact]
    public void Fill_コマの外へはみ出さない()
    {
        // Filling a uniformly coloured sheet spreads across the whole image unless the border holds
        EditorDocument document = CreateDocument(SKColors.White);
        (int x, int y) = At(0, 5, 5);

        document.BeginChange();
        document.Fill(x, y, SKColors.Red, EditScope.SingleFrame);

        FrameRect target = Layout.Frames.Single(f => f.Index == 0);
        FrameRect neighbour = Layout.Frames.Single(f => f.Index == 1);

        Assert.Equal(SKColors.Red, document.GetPixel(target.X, target.YTopLeft));
        Assert.Equal(SKColors.Red, document.GetPixel(target.X + target.W - 1, target.YTopLeft + target.H - 1));
        Assert.Equal(SKColors.White, document.GetPixel(neighbour.X, neighbour.YTopLeft));
    }

    [Fact]
    public void Fill_全コマ指定なら同じ形が全コマへ複製される()
    {
        EditorDocument document = CreateDocument(SKColors.White);
        (int x, int y) = At(0, 5, 5);

        document.BeginChange();
        document.Fill(x, y, SKColors.Red, EditScope.AllFrames);

        foreach (FrameRect frame in Layout.Frames)
        {
            Assert.Equal(SKColors.Red, document.GetPixel(frame.X, frame.YTopLeft));
        }
    }

    [Fact]
    public void Fill_全コマ指定でも同じ位置が別の色のコマは塗らずに数える()
    {
        // The frames facing right or away, sitting or swinging, hold different art, so the spot
        // clicked in the front frame can be the transparent background there. Each frame's fill
        // started from whatever colour sat at that spot, so filling the shoes in frame 0 flooded the
        // whole background of the sitting frames - unseen, with one frame on screen - and the game
        // then showed a square where the character sits (the test campaign of 2026-10-01).
        EditorDocument document = CreateDocument();
        FrameRect clickedFrame = Layout.Frames.Single(f => f.Index == 0);
        FrameRect sameColour = Layout.Frames.Single(f => f.Index == 1);
        FrameRect background = Layout.Frames.Single(f => f.Index == 2);

        // A block of white at the same spot in frames 0 and 1; frame 2 has nothing there
        foreach (FrameRect frame in new[] { clickedFrame, sameColour })
        {
            for (int dy = 0; dy < 3; dy++)
            {
                for (int dx = 0; dx < 3; dx++)
                {
                    document.Paint(frame.X + 5 + dx, frame.YTopLeft + 5 + dy, SKColors.White, EditScope.SingleFrame);
                }
            }
        }

        (int x, int y) = At(0, 6, 6);
        document.BeginChange();
        int changed = document.Fill(x, y, SKColors.Red, EditScope.AllFrames, out int skipped);

        Assert.Equal(SKColors.Red, document.GetPixel(clickedFrame.X + 6, clickedFrame.YTopLeft + 6));
        Assert.Equal(SKColors.Red, document.GetPixel(sameColour.X + 6, sameColour.YTopLeft + 6));
        Assert.Equal(18, changed);

        // The background there was not flooded, and the frame is counted
        Assert.Equal(0, document.GetPixel(background.X, background.YTopLeft).Alpha);
        Assert.Equal(0, document.GetPixel(background.X + 6, background.YTopLeft + 6).Alpha);
        Assert.Equal(Layout.Frames.Count - 2, skipped);
    }

    [Fact]
    public void Fill_同じ色を指定しても無限に広がらない()
    {
        EditorDocument document = CreateDocument(SKColors.White);
        (int x, int y) = At(0, 5, 5);

        // A marker outside the filled frame. Asserting only that the clicked pixel is still
        // white proved nothing - it was white before the fill, so an empty Fill satisfied it.
        // What the name promises is that filling with the colour already there does not run on
        // and out of the frame, and that needs a pixel the fill must not reach.
        FrameRect other = Layout.Frames[1];
        document.Paint(other.X + 3, other.YTopLeft + 3, SKColors.Red, EditScope.SingleFrame);

        document.BeginChange();
        document.Fill(x, y, SKColors.White, EditScope.SingleFrame);

        Assert.Equal(SKColors.White, document.GetPixel(x, y));
        Assert.Equal(SKColors.Red, document.GetPixel(other.X + 3, other.YTopLeft + 3));

        // Nor may it spill into the cells the sheet does not use, which sit outside every frame
        Assert.All(
            UnusedCellSamples(),
            p => Assert.NotEqual(SKColors.Red, document.GetPixel(p.X, p.Y)));
    }

    /// <summary>
    /// One point inside each 26-pixel cell that no frame claims. Fifteen of the fifty-four cells
    /// are unused, so anything painting past a frame boundary lands here.
    /// </summary>
    private IEnumerable<(int X, int Y)> UnusedCellSamples()
    {
        HashSet<(int, int)> used = [.. Layout.Frames.Select(f => (f.X, f.YTopLeft))];

        for (int y = 0; y + Layout.Cell.Height <= Layout.Texture.Height; y += Layout.Cell.Height)
        {
            for (int x = 0; x + Layout.Cell.Width <= Layout.Texture.Width; x += Layout.Cell.Width)
            {
                if (!used.Contains((x, y)))
                {
                    yield return (x + 3, y + 3);
                }
            }
        }
    }

    [Theory]
    [InlineData(EditorTool.Line)]
    [InlineData(EditorTool.Rectangle)]
    [InlineData(EditorTool.FilledRectangle)]
    public void 図形は1回の取り消しでまとめて元に戻る(EditorTool tool)
    {
        // A shape writes many pixels from one gesture, so it has to come back out with one undo.
        // None of the four tools added had a test for this at all.
        EditorDocument document = CreateDocument();
        FrameRect frame = Layout.Frames[0];

        IReadOnlyList<(int X, int Y)> shape = tool switch
        {
            EditorTool.Line => EditorDocument.LinePixels(
                frame.X + 2, frame.YTopLeft + 2, frame.X + 12, frame.YTopLeft + 12),
            EditorTool.Rectangle => EditorDocument.RectanglePixels(
                frame.X + 2, frame.YTopLeft + 2, frame.X + 12, frame.YTopLeft + 12, filled: false),
            _ => EditorDocument.RectanglePixels(
                frame.X + 2, frame.YTopLeft + 2, frame.X + 12, frame.YTopLeft + 12, filled: true),
        };

        document.BeginChange();
        foreach ((int x, int y) in shape)
        {
            document.Paint(x, y, SKColors.Red, EditScope.SingleFrame, mirror: false);
        }

        document.EndChange();

        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);
        Assert.True(document.Undo());

        Assert.All(shape, p => Assert.Equal(0, document.GetPixel(p.X, p.Y).Alpha));
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void ReplaceColour_1回の取り消しで元の色に戻る()
    {
        // The colour replace tool changes hundreds of pixels across every frame from one click.
        EditorDocument document = CreateDocument(SKColors.White);
        FrameRect frame = Layout.Frames[0];

        document.BeginChange();
        int changed = document.ReplaceColour(
            frame.X + 5, frame.YTopLeft + 5, SKColors.Red, EditScope.AllFrames);

        document.EndChange();

        Assert.True(changed > 0);
        Assert.True(document.Undo());

        Assert.All(
            Layout.Frames,
            f => Assert.Equal(SKColors.White, document.GetPixel(f.X + 5, f.YTopLeft + 5)));
        Assert.False(document.IsModified);
    }

    [Fact]
    public void 左右対称で描いた2点は1回の取り消しで両方戻る()
    {
        EditorDocument document = CreateDocument();
        FrameRect frame = Layout.Frames[0];
        int y = frame.YTopLeft + 8;
        int twin = frame.X + frame.W - 1 - 4;

        document.BeginChange();
        document.Paint(frame.X + 4, y, SKColors.Red, EditScope.SingleFrame, mirror: true);
        document.EndChange();

        Assert.True(document.Undo());

        Assert.Equal(0, document.GetPixel(frame.X + 4, y).Alpha);
        Assert.Equal(0, document.GetPixel(twin, y).Alpha);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void Undo_直前の操作を取り消す()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        Assert.True(document.CanUndo);

        Assert.True(document.Undo());
        Assert.Equal(0, document.GetPixel(x, y).Alpha);
    }

    [Fact]
    public void Redo_取り消した操作をやり直す()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.Undo();

        Assert.True(document.CanRedo);
        Assert.True(document.Redo());
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));
    }

    [Fact]
    public void 新しい操作をするとやり直し履歴は破棄される()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.Undo();
        Assert.True(document.CanRedo);

        document.BeginChange();
        document.Paint(x, y, SKColors.Blue, EditScope.SingleFrame);

        Assert.False(document.CanRedo);
    }

    [Fact]
    public void ひと筆は1回の取り消しでまとめて戻る()
    {
        // The intended use is one BeginChange on press, and none during the drag
        EditorDocument document = CreateDocument();

        document.BeginChange();
        for (int i = 0; i < 5; i++)
        {
            (int x, int y) = At(0, 3 + i, 3);
            document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        }

        document.Undo();

        for (int i = 0; i < 5; i++)
        {
            (int x, int y) = At(0, 3 + i, 3);
            Assert.Equal(0, document.GetPixel(x, y).Alpha);
        }
    }

    [Fact]
    public void 取り込み直後は未編集で実際に色が変われば編集済みになる()
    {
        EditorDocument document = CreateDocument();
        Assert.False(document.IsModified);

        // Starting a stroke is not an edit by itself. It used to set IsModified immediately,
        // which meant a single click anywhere put the import settings behind a
        // "your edits will be lost" confirmation even though nothing had changed.
        document.BeginChange();
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);

        (int x, int y) = At(0, 5, 5);
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);

        Assert.True(document.IsModified);
        Assert.True(document.CanUndo);
    }

    [Fact]
    public void 同じ色を塗り直しても編集済みにならない()
    {
        EditorDocument document = CreateDocument(SKColors.Blue);
        (int x, int y) = At(0, 5, 5);

        document.BeginChange();
        document.Paint(x, y, SKColors.Blue, EditScope.SingleFrame);

        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void 取り込み直後の状態まで戻せば未編集に戻る()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 5, 5);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        Assert.True(document.IsModified);

        Assert.True(document.Undo());

        // Back at the imported sheet, so a settings change has nothing to discard
        Assert.False(document.IsModified);

        Assert.True(document.Redo());
        Assert.True(document.IsModified);
    }

    [Fact]
    public void 取り消し後は開始時のスナップショットが破棄される()
    {
        // Found by the adversarial pass. BeginChange keeps a snapshot until an edit actually
        // changes something. If the user presses Ctrl+Z mid-stroke, that snapshot describes a
        // state the history has already left behind, and committing it later resurrected the
        // pixels the undo had just removed.
        EditorDocument document = CreateDocument();
        (int ax, int ay) = At(0, 1, 1);
        (int bx, int by) = At(0, 5, 5);

        document.BeginChange();
        document.Paint(ax, ay, SKColors.Red, EditScope.SingleFrame);

        // Press again on the same pixel with the same colour: a snapshot, but no change
        document.BeginChange();
        document.Paint(ax, ay, SKColors.Red, EditScope.SingleFrame);

        Assert.True(document.Undo());
        Assert.Equal(0, document.GetPixel(ax, ay).Alpha);

        document.Paint(bx, by, SKColors.Lime, EditScope.SingleFrame);
        Assert.True(document.Undo());

        Assert.Equal(0, document.GetPixel(ax, ay).Alpha);
        Assert.Equal(0, document.GetPixel(bx, by).Alpha);
    }

    [Fact]
    public void 押下イベントの無いストロークでも取り消せる()
    {
        // Switching tools while the button is held produces edits with no BeginChange.
        // Those edits used to be silently unrecoverable and left a stale redo entry.
        EditorDocument document = CreateDocument();
        (int ax, int ay) = At(0, 1, 1);
        (int bx, int by) = At(0, 5, 5);

        // A completed stroke first, so the state carried over from it is exercised too
        document.BeginChange();
        document.Paint(ax, ay, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        Assert.True(document.Undo());
        Assert.True(document.CanRedo);

        // Now a stroke that never went through BeginChange
        document.Paint(bx, by, SKColors.Lime, EditScope.SingleFrame);

        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);

        Assert.True(document.Undo());
        Assert.Equal(0, document.GetPixel(bx, by).Alpha);
    }

    [Fact]
    public void 既に空のピクセルを消しても編集済みにならない()
    {
        // SKColors.Transparent is 0x00FFFFFF while an empty sheet is 0x00000000. Writing one
        // over the other changed the document without changing the picture, so the eraser
        // triggered the "your edits will be lost" confirmation for nothing.
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 5, 5);

        document.BeginChange();
        document.Paint(x, y, SKColors.Transparent, EditScope.SingleFrame);

        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void 消しゴムで消した箇所も塗りつぶしの対象になる()
    {
        // The flood fill compares colours exactly, so an erased pixel that held a different
        // representation of "transparent" used to be skipped, leaving holes in the fill.
        EditorDocument document = CreateDocument();
        FrameRect frame = Layout.Frames.Single(f => f.Index == 0);
        (int ex, int ey) = At(0, 2, 2);

        document.BeginChange();
        document.Paint(ex, ey, SKColors.Transparent, EditScope.SingleFrame);
        document.EndChange();

        document.BeginChange();
        document.Fill(At(0, 0, 0).X, At(0, 0, 0).Y, SKColors.Blue, EditScope.SingleFrame);

        for (int cy = 0; cy < frame.H; cy++)
        {
            for (int cx = 0; cx < frame.W; cx++)
            {
                Assert.Equal(SKColors.Blue, document.GetPixel(frame.X + cx, frame.YTopLeft + cy));
            }
        }
    }

    [Fact]
    public void Fill_全コマ反映はコマごとに領域を求める()
    {
        // The walk animation shifts the art up by a pixel in some frames. Copying one frame's
        // filled region to the others at fixed coordinates therefore painted over the character
        // in the shifted frames, and left the same number of background pixels untouched.
        EditorDocument document = CreateDocument();
        FrameRect first = Layout.Frames.Single(f => f.Index == 0);
        FrameRect shifted = Layout.Frames.Single(f => f.Index == 4);

        // Art at the same place in the cell, except one pixel higher in the shifted frame
        (int ax, int ay) = (first.X + 10, first.YTopLeft + 10);
        (int bx, int by) = (shifted.X + 10, shifted.YTopLeft + 9);

        document.BeginChange();
        document.Paint(ax, ay, SKColors.Red, EditScope.SingleFrame);
        document.Paint(bx, by, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        document.BeginChange();
        document.Fill(first.X, first.YTopLeft, SKColors.Blue, EditScope.AllFrames);

        Assert.Equal(SKColors.Red, document.GetPixel(ax, ay));
        Assert.Equal(SKColors.Red, document.GetPixel(bx, by));

        // And the background really was filled in the shifted frame, including the pixel the
        // art no longer occupies there
        Assert.Equal(SKColors.Blue, document.GetPixel(shifted.X + 10, shifted.YTopLeft + 10));
    }

    [Fact]
    public void Pixelsから内部配列を書き換えられない()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 7, 7);

        Assert.Throws<InvalidCastException>(() =>
        {
            SKColor[] live = (SKColor[])document.Pixels;
            live[(y * document.Width) + x] = SKColors.Magenta;
        });

        Assert.Equal(0, document.GetPixel(x, y).Alpha);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void Fill_全コマ反映でも他のコマの領域を越えない()
    {
        // The filled region comes from the clicked frame; replaying it into another frame
        // must be clipped the same way Paint clips, or it bleeds into the neighbouring cell.
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Fill(x, y, SKColors.Red, EditScope.AllFrames);

        foreach (FrameRect frame in Layout.Frames)
        {
            for (int cy = 0; cy < frame.H; cy++)
            {
                for (int cx = 0; cx < frame.W; cx++)
                {
                    Assert.Equal(SKColors.Red, document.GetPixel(frame.X + cx, frame.YTopLeft + cy));
                }
            }
        }

        // The assertion the test is named for. The sheet starts fully transparent, so it is one
        // connected region: without the frame clip the flood spreads across the whole texture
        // and every assertion above still passes. The layout leaves 15 of the 54 grid cells
        // unused, and those are exactly where a leak shows.
        List<string> outside = [];
        for (int sy = 0; sy < Layout.Texture.Height && outside.Count < 8; sy++)
        {
            for (int sx = 0; sx < Layout.Texture.Width && outside.Count < 8; sx++)
            {
                // Compared on alpha rather than against SKColors.Transparent: an erased pixel is
                // stored as default(SKColor), which is not that constant.
                if (!document.TryGetFrameAt(sx, sy, out _) && document.GetPixel(sx, sy).Alpha != 0)
                {
                    outside.Add($"({sx},{sy})");
                }
            }
        }

        Assert.True(outside.Count == 0, $"コマの外へ流れ出している: {string.Join(" ", outside)}");
    }

    /// <summary>
    /// Choosing a preset over work in progress used to build a fresh document, taking the undo
    /// history with it, so the edits could not be recovered by any means.
    /// </summary>
    [Fact]
    public void Replace_差し替えは1回の取り消しで元に戻せる()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        using SKBitmap other = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        SKColor[] pixels = other.Pixels;
        Array.Fill(pixels, SKColors.Blue);
        other.Pixels = pixels;

        document.Replace(other);
        Assert.Equal(SKColors.Blue, document.GetPixel(x, y));

        Assert.True(document.Undo());
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));
    }

    /// <summary>
    /// A fill that changes nothing has to be distinguishable from one that works, so the window
    /// can say so instead of leaving the previous message standing as if it had.
    /// </summary>
    [Fact]
    public void Fill_同じ色の場所を塗ると0を返す()
    {
        EditorDocument document = CreateDocument(SKColors.White);
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        int first = document.Fill(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        Assert.True(first > 0, "最初の塗りつぶしが何も変えていない");

        document.BeginChange();
        int second = document.Fill(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        Assert.Equal(0, second);
    }

    [Fact]
    public void Fill_コマの外を塗ると0を返す()
    {
        EditorDocument document = CreateDocument(SKColors.White);

        // Fifteen of the fifty-four cells belong to no frame, and the whole-sheet view shows them,
        // so they can be clicked. Found rather than assumed: which cells those are is the
        // layout's business, not this test's.
        (int X, int Y)? outside = null;

        for (int y = 0; y < Layout.Texture.Height && outside is null; y += Layout.Cell.Height)
        {
            for (int x = 0; x < Layout.Texture.Width && outside is null; x += Layout.Cell.Width)
            {
                if (!document.TryGetFrameAt(x + 1, y + 1, out _))
                {
                    outside = (x + 1, y + 1);
                }
            }
        }

        Assert.NotNull(outside);
        Assert.Equal(0, document.Fill(outside!.Value.X, outside.Value.Y, SKColors.Red, EditScope.SingleFrame));
    }

    /// <summary>
    /// Comparing one preset against another must not cost the drawing underneath them.
    ///
    /// Choosing a preset over hand-drawn work replaces the sheet and leans on undo to bring the
    /// work back, but the history holds sixty-four steps. A step per preset meant a user trying
    /// them in turn - or rolling the mouse wheel over the dropdown, which changes the selection
    /// too - pushed their own drawing off the end of it, with nothing able to return.
    /// </summary>
    [Fact]
    public void Replace_続けて差し替えても1回の取り消しで手描きに戻る()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        // Comfortably more than the history holds
        for (int step = 0; step < 200; step++)
        {
            using SKBitmap preset = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
            SKColor[] pixels = preset.Pixels;
            Array.Fill(pixels, new SKColor((byte)(step + 1), 0, 0));
            preset.Pixels = pixels;

            document.Replace(preset, foldIntoPrevious: true);
        }

        Assert.Equal(new SKColor(200, 0, 0), document.GetPixel(x, y));

        Assert.True(document.Undo());
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));
    }

    /// <summary>
    /// Folding must not swallow an edit made between two replacements: that edit is its own step,
    /// and the replacement after it starts another.
    /// </summary>
    [Fact]
    public void Replace_差し替えの間に描いた分は別の取り消しになる()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        using SKBitmap first = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        SKColor[] firstPixels = first.Pixels;
        Array.Fill(firstPixels, SKColors.Blue);
        first.Pixels = firstPixels;

        using SKBitmap second = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        SKColor[] secondPixels = second.Pixels;
        Array.Fill(secondPixels, SKColors.Green);
        second.Pixels = secondPixels;

        document.Replace(first, foldIntoPrevious: true);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        document.Replace(second, foldIntoPrevious: true);

        Assert.Equal(SKColors.Green, document.GetPixel(x, y));

        // Back to the hand-drawn pixel, not past it
        Assert.True(document.Undo());
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));

        // And back again to what the first replacement put there
        Assert.True(document.Undo());
        Assert.Equal(SKColors.Blue, document.GetPixel(x, y));
    }

    /// <summary>
    /// Trying presets and coming back to the one that was open must not make the drawing
    /// disposable.
    ///
    /// The sheet then matches what was opened again, and IsModified was decided from that alone,
    /// so it went false - while the drawing was still one undo away on the history. The window
    /// reads IsModified as "would going on lose the user's work", and with it false the next
    /// preset built a new document instead of replacing in place, and closing asked nothing:
    /// either way the history went, and the drawing with it. Wheeling over the dropdown was
    /// enough to get there.
    /// </summary>
    [Fact]
    public void Replace_元のシートに戻してもまだ失いうる手描きとして扱う()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        using SKBitmap other = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        SKColor[] otherPixels = other.Pixels;
        Array.Fill(otherPixels, SKColors.Blue);
        other.Pixels = otherPixels;

        // What was opened: CreateDocument's own empty sheet
        using SKBitmap opened = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);

        document.Replace(other, foldIntoPrevious: true);
        document.Replace(opened, foldIntoPrevious: true);

        Assert.True(document.IsModified, "履歴に手描きが残っているのに、失うものが無いと判定された");

        Assert.True(document.Undo());
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));
    }

    /// <summary>
    /// The same, reached in one step: choosing the preset that was open, straight after drawing.
    /// That goes through the path that starts a new step rather than folding into one, and it
    /// decided the flag from the contents as well.
    /// </summary>
    [Fact]
    public void Replace_描いた直後に元のシートを選んでも手描きを失いうるものとして扱う()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        using SKBitmap opened = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);

        document.Replace(opened, foldIntoPrevious: true);

        Assert.True(document.IsModified, "履歴に手描きが残っているのに、失うものが無いと判定された");

        Assert.True(document.Undo());
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));
    }

    /// <summary>
    /// Painting a pixel and erasing it back by hand leaves the sheet identical to the imported
    /// one, so the import settings must stop warning that edits will be lost - the same answer
    /// undoing gives.
    /// </summary>
    [Fact]
    public void 手で元に戻した場合も編集済みでなくなる()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 3, 3);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();
        Assert.True(document.IsModified);

        document.BeginChange();
        document.Paint(x, y, SKColors.Transparent, EditScope.SingleFrame);
        document.EndChange();

        Assert.False(document.IsModified);
    }

    [Fact]
    public void TryGetFrameAt_コマの内外を判定する()
    {
        EditorDocument document = CreateDocument();
        FrameRect expected = Layout.Frames.Single(f => f.Index == 5);

        Assert.True(document.TryGetFrameAt(expected.X + 1, expected.YTopLeft + 1, out FrameRect found));
        Assert.Equal(expected.Index, found.Index);

        // Unused grid cells, which no frame occupies, count as outside any frame
        Assert.False(document.TryGetFrameAt(Layout.Texture.Width - 1, 0, out _));
    }

    [Fact]
    public void ToBitmap_編集内容がそのまま取り出せる()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(2, 8, 8);

        document.BeginChange();
        document.Paint(x, y, SKColors.Lime, EditScope.SingleFrame);

        using SKBitmap bitmap = document.ToBitmap();

        Assert.Equal(Layout.Texture.Width, bitmap.Width);
        Assert.Equal(Layout.Texture.Height, bitmap.Height);
        Assert.Equal(SKColors.Lime, bitmap.Pixels[(y * bitmap.Width) + x]);
    }

    [Fact]
    public void 寸法がレイアウトと違うシートは受け付けない()
    {
        // Replace and Recolour both refuse this, and the constructor did not: a sheet of the
        // wrong size was accepted and then failed from inside a fill as IndexOutOfRangeException,
        // which names nothing the user can act on and comes a long way from what caused it.
        using SKBitmap wrong = PixelOps.CreateEmpty(40, 40);

        ToolException ex = Assert.Throws<ToolException>(() => new EditorDocument(wrong, Layout));

        Assert.Contains("40x40", ex.Message);
    }

    [Fact]
    public void Paint_左右対称で反対側にも同じ点が付く()
    {
        // The character faces the viewer and is symmetric about the middle of its frame, so an
        // eye or a shoulder drawn on one side belongs on the other. Counting columns by hand to
        // find the twin is exactly the tedium this removes.
        EditorDocument document = CreateDocument();
        FrameRect frame = Layout.Frames[0];

        document.BeginChange();
        document.Paint(frame.X + 4, frame.YTopLeft + 8, SKColors.Red, EditScope.SingleFrame, mirror: true);

        int twin = frame.X + frame.W - 1 - 4;

        Assert.Equal(SKColors.Red, document.GetPixel(frame.X + 4, frame.YTopLeft + 8));
        Assert.Equal(SKColors.Red, document.GetPixel(twin, frame.YTopLeft + 8));
    }

    /// <summary>
    /// Builds a sheet whose transparent pixels carry a colour underneath, the way a PNG saved by
    /// an outside editor does. Written through the raw buffer so nothing on the way in quietly
    /// tidies the value away, which is the whole point of the fixture.
    /// </summary>
    private static SKBitmap CreateSheetWithTransparentWhite()
    {
        SKBitmap sheet = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);

        // RGBA8888 unpremultiplied: white with no alpha at all.
        byte[] raw = new byte[Layout.Texture.Width * Layout.Texture.Height * 4];
        for (int i = 0; i < raw.Length; i += 4)
        {
            raw[i] = 255;
            raw[i + 1] = 255;
            raw[i + 2] = 255;
            raw[i + 3] = 0;
        }

        System.Runtime.InteropServices.Marshal.Copy(raw, 0, sheet.GetPixels(), raw.Length);

        return sheet;
    }

    [Fact]
    public void 取り込んだ透明画素は色を落として1つの値にそろえられる()
    {
        // Everything here compares colours exactly, on the strength of SetPixel collapsing every
        // fully transparent colour to one value. Pixels arriving from a file never pass through
        // SetPixel, so the guarantee has to be applied when they arrive instead.
        using SKBitmap sheet = CreateSheetWithTransparentWhite();

        // The fixture has to actually carry the colour, or the tests below prove nothing
        Assert.Equal(new SKColor(255, 255, 255, 0), sheet.Pixels[0]);

        EditorDocument document = new(sheet, Layout);

        Assert.Equal(default, document.GetPixel(0, 0));
        Assert.False(document.IsModified);
    }

    [Fact]
    public void 再取り込みしたシートでも色の置換は押した画素を塗り替える()
    {
        // The clicked pixel was normalised but the ones searched were not, so the search matched
        // pixels the tool itself had erased earlier - anywhere on the sheet - and left the pixel
        // under the cursor exactly as it was.
        using SKBitmap sheet = CreateSheetWithTransparentWhite();
        EditorDocument document = new(sheet, Layout);
        FrameRect frame = Layout.Frames[0];

        // An erased pixel elsewhere: the decoy the broken search used to find
        document.BeginChange();
        document.Paint(frame.X + 9, frame.YTopLeft + 9, SKColors.Transparent, EditScope.SingleFrame, mirror: false);

        document.BeginChange();
        int changed = document.ReplaceColour(frame.X + 5, frame.YTopLeft + 5, SKColors.Blue, EditScope.AllFrames);

        Assert.True(changed > 1, $"押した画素を含む全面が置き換わっていない（changed={changed}）");
        Assert.Equal(SKColors.Blue, document.GetPixel(frame.X + 5, frame.YTopLeft + 5));
    }

    [Fact]
    public void 再取り込みしたシートでも消しゴム跡が塗りつぶしをせき止めない()
    {
        // The bucket compares colours exactly. An erased pixel held 0x00000000 while everything
        // around it held 0x00FFFFFF, so the fill treated it as a wall and left a hole behind.
        using SKBitmap sheet = CreateSheetWithTransparentWhite();
        EditorDocument document = new(sheet, Layout);
        FrameRect frame = Layout.Frames[0];

        document.BeginChange();
        document.Paint(frame.X + 5, frame.YTopLeft + 5, SKColors.Transparent, EditScope.SingleFrame, mirror: false);

        document.BeginChange();
        document.Fill(frame.X, frame.YTopLeft, SKColors.Red, EditScope.SingleFrame);

        Assert.Equal(SKColors.Red, document.GetPixel(frame.X + 5, frame.YTopLeft + 5));
    }

    [Fact]
    public void 再取り込みしたシートで既に透明な画素を消しても編集済みにならない()
    {
        // Erasing an invisible pixel changed 0x00FFFFFF into 0x00000000, which looks identical
        // and yet counted as an edit - enough to raise "your edits will be lost" over nothing.
        using SKBitmap sheet = CreateSheetWithTransparentWhite();
        EditorDocument document = new(sheet, Layout);
        FrameRect frame = Layout.Frames[0];

        document.BeginChange();
        document.Paint(frame.X + 5, frame.YTopLeft + 5, SKColors.Transparent, EditScope.SingleFrame, mirror: false);
        document.EndChange();

        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Paint_透明を全コマ指定で塗ると全コマの同じ位置が消える()
    {
        // This is the path the right mouse button takes: it erases through Paint rather than
        // through the eraser tool, so the scope has to survive that detour. If it did not, a
        // right-click would quietly edit one frame while every other tool edited all of them.
        EditorDocument document = CreateDocument();

        foreach (FrameRect each in Layout.Frames)
        {
            document.Paint(each.X + 3, each.YTopLeft + 5, SKColors.Red, EditScope.SingleFrame, mirror: false);
        }

        document.BeginChange();
        document.Paint(Layout.Frames[0].X + 3, Layout.Frames[0].YTopLeft + 5, SKColors.Transparent, EditScope.AllFrames, mirror: false);

        Assert.All(Layout.Frames, frame =>
            Assert.Equal(0, document.GetPixel(frame.X + 3, frame.YTopLeft + 5).Alpha));
    }

    [Fact]
    public void Paint_透明を左右対称で塗ると反対側も消える()
    {
        // Erasing is drawing, so the mirror applies to it too. Someone taking an eye back out
        // expects both eyes gone, not a face left half finished.
        //
        // Both pixels are laid down without the mirror first. Painting them through the mirror
        // instead left the far one transparent from the start, so the check below passed with
        // symmetric drawing switched off entirely.
        EditorDocument document = CreateDocument();
        FrameRect frame = Layout.Frames[0];
        int twin = frame.X + frame.W - 1 - 4;

        document.Paint(frame.X + 4, frame.YTopLeft + 8, SKColors.Red, EditScope.SingleFrame, mirror: false);
        document.Paint(twin, frame.YTopLeft + 8, SKColors.Red, EditScope.SingleFrame, mirror: false);

        Assert.Equal(SKColors.Red, document.GetPixel(twin, frame.YTopLeft + 8));

        document.BeginChange();
        document.Paint(frame.X + 4, frame.YTopLeft + 8, SKColors.Transparent, EditScope.SingleFrame, mirror: true);

        Assert.Equal(0, document.GetPixel(frame.X + 4, frame.YTopLeft + 8).Alpha);
        Assert.Equal(0, document.GetPixel(twin, frame.YTopLeft + 8).Alpha);
    }

    [Fact]
    public void Paint_左右対称は中央をはさんだ2列だけを塗る()
    {
        // The frame is 26 wide, an even number, so no column is its own reflection: the two
        // middle columns are each other's. An earlier version of this asserted only that the
        // pixel asked for was painted, which held with symmetric drawing switched off, and its
        // comment claimed an odd width that this layout has never had.
        EditorDocument document = CreateDocument();
        FrameRect frame = Layout.Frames[0];
        int y = frame.YTopLeft + 8;
        int left = frame.X + ((frame.W - 1) / 2);
        int right = frame.X + frame.W - 1 - ((frame.W - 1) / 2);

        Assert.Equal(2, frame.W % 2 == 0 ? 2 : 1);
        Assert.Equal(left + 1, right);

        document.BeginChange();
        document.Paint(left, y, SKColors.Red, EditScope.SingleFrame, mirror: true);

        Assert.Equal(SKColors.Red, document.GetPixel(left, y));
        Assert.Equal(SKColors.Red, document.GetPixel(right, y));

        // And nothing else on that row inside the frame
        int painted = Enumerable.Range(frame.X, frame.W)
            .Count(x => document.GetPixel(x, y).Alpha != 0);

        Assert.Equal(2, painted);
    }

    [Fact]
    public void Paint_左右対称はどちらの側から塗っても同じ2点になる()
    {
        // The reflection has to be its own inverse, or drawing an eye from the right-hand side
        // would land somewhere other than where drawing it from the left-hand side does.
        FrameRect frame = Layout.Frames[0];
        int y = frame.YTopLeft + 8;
        int near = frame.X + 4;
        int far = frame.X + frame.W - 1 - 4;

        EditorDocument fromNear = CreateDocument();
        fromNear.Paint(near, y, SKColors.Red, EditScope.SingleFrame, mirror: true);

        EditorDocument fromFar = CreateDocument();
        fromFar.Paint(far, y, SKColors.Red, EditScope.SingleFrame, mirror: true);

        Assert.Equal(fromNear.Pixels, fromFar.Pixels);
        Assert.Equal(SKColors.Red, fromFar.GetPixel(near, y));
        Assert.Equal(SKColors.Red, fromNear.GetPixel(far, y));
    }

    [Fact]
    public void ReplaceColour_離れた同色もまとめて置き換える()
    {
        // What separates this from the bucket: a shirt's parts are not connected to each other,
        // and a change of palette has to reach all of them.
        EditorDocument document = CreateDocument(SKColors.White);
        FrameRect first = Layout.Frames[0];
        FrameRect second = Layout.Frames[1];

        document.BeginChange();
        document.Paint(first.X + 2, first.YTopLeft + 2, SKColors.Red, EditScope.SingleFrame);
        document.Paint(second.X + 5, second.YTopLeft + 9, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();

        document.BeginChange();
        int changed = document.ReplaceColour(
            first.X + 2, first.YTopLeft + 2, SKColors.Blue, EditScope.AllFrames);

        Assert.Equal(2, changed);
        Assert.Equal(SKColors.Blue, document.GetPixel(first.X + 2, first.YTopLeft + 2));
        Assert.Equal(SKColors.Blue, document.GetPixel(second.X + 5, second.YTopLeft + 9));
    }

    [Fact]
    public void ReplaceColour_全コマ指定はシート全体を対象にする()
    {
        // An earlier version of this asserted the opposite - that the fifteen unused cells and
        // the gaps between frames were left alone, on the grounds that "nothing shows them".
        // They are shown: the whole-sheet view draws the entire texture, they can be drawn on
        // there, and they are saved into the PNG. Skipping them left the colour the tool had
        // just said it replaced still sitting in the gaps, and clicking one to finish the job
        // fell into a branch that repainted the whole texture instead.
        EditorDocument document = CreateDocument();
        FrameRect first = Layout.Frames[0];
        (int X, int Y) gap = UnusedCellSamples().First();

        document.Paint(first.X + 5, first.YTopLeft + 5, SKColors.Red, EditScope.SingleFrame);
        document.Paint(gap.X, gap.Y, SKColors.Red, EditScope.SingleFrame);

        document.BeginChange();
        int changed = document.ReplaceColour(
            first.X + 5, first.YTopLeft + 5, SKColors.Blue, EditScope.AllFrames);

        Assert.Equal(2, changed);
        Assert.Equal(SKColors.Blue, document.GetPixel(first.X + 5, first.YTopLeft + 5));
        Assert.Equal(SKColors.Blue, document.GetPixel(gap.X, gap.Y));
    }

    [Fact]
    public void ReplaceColour_コマ外を単一コマ指定で押したらそのセルだけが対象になる()
    {
        // The gaps are reachable in the whole-sheet view, so "just here" needs a bounded answer
        // there too. It used to mean the whole texture, so turning "apply to every frame" off
        // and clicking a gap repainted all thirty-nine frames - the opposite of what it says.
        EditorDocument document = CreateDocument(SKColors.White);
        (int X, int Y) gap = UnusedCellSamples().First();
        FrameRect first = Layout.Frames[0];

        document.BeginChange();
        int changed = document.ReplaceColour(gap.X, gap.Y, SKColors.Red, EditScope.SingleFrame);

        Assert.Equal(Layout.Cell.Width * Layout.Cell.Height, changed);
        Assert.Equal(SKColors.Red, document.GetPixel(gap.X, gap.Y));

        // And no frame was touched
        Assert.Equal(SKColors.White, document.GetPixel(first.X + 5, first.YTopLeft + 5));
    }

    [Fact]
    public void ReplaceColour_コマ内だけの指定なら他のコマは変わらない()
    {
        EditorDocument document = CreateDocument(SKColors.White);
        FrameRect first = Layout.Frames[0];
        FrameRect second = Layout.Frames[1];

        document.BeginChange();
        document.ReplaceColour(first.X + 2, first.YTopLeft + 2, SKColors.Blue, EditScope.SingleFrame);

        Assert.Equal(SKColors.Blue, document.GetPixel(first.X + 2, first.YTopLeft + 2));
        Assert.Equal(SKColors.White, document.GetPixel(second.X + 2, second.YTopLeft + 2));
    }

    [Fact]
    public void ReplaceColour_同じ色を指定したら何も起きない()
    {
        EditorDocument document = CreateDocument(SKColors.White);
        FrameRect frame = Layout.Frames[0];

        document.BeginChange();

        Assert.Equal(0, document.ReplaceColour(
            frame.X + 2, frame.YTopLeft + 2, SKColors.White, EditScope.SingleFrame));
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void ClipToFrame_コマの外へ出た分だけを落とす()
    {
        // What keeps a drag that starts in one frame from drawing into the next. The shape
        // itself is computed in sheet coordinates and knows nothing about frames.
        FrameRect first = Layout.Frames[0];
        FrameRect second = Layout.Frames[1];

        IReadOnlyList<(int X, int Y)> line = EditorDocument.LinePixels(
            first.X + 2, first.YTopLeft + 2, second.X + 2, second.YTopLeft + 2);

        IReadOnlyList<(int X, int Y)> clipped = EditorDocument.ClipToFrame(line, first);

        Assert.NotEmpty(clipped);
        Assert.True(clipped.Count < line.Count, "はみ出した分が落ちていない");
        Assert.All(clipped, p =>
        {
            Assert.InRange(p.X, first.X, first.X + first.W - 1);
            Assert.InRange(p.Y, first.YTopLeft, first.YTopLeft + first.H - 1);
        });

        // And the part that is inside is kept whole, not thinned out
        Assert.Equal(
            line.Count(p => p.X < first.X + first.W && p.Y < first.YTopLeft + first.H),
            clipped.Count);
    }

    [Fact]
    public void ClipToFrame_コマ内に収まる図形はそのまま残る()
    {
        FrameRect frame = Layout.Frames[0];

        IReadOnlyList<(int X, int Y)> box = EditorDocument.RectanglePixels(
            frame.X + 2, frame.YTopLeft + 2, frame.X + 9, frame.YTopLeft + 9, filled: true);

        Assert.Equal(box.Count, EditorDocument.ClipToFrame(box, frame).Count);
    }

    [Fact]
    public void ClipToFrame_全部が外なら何も残らない()
    {
        FrameRect first = Layout.Frames[0];
        FrameRect second = Layout.Frames[1];

        IReadOnlyList<(int X, int Y)> box = EditorDocument.RectanglePixels(
            second.X + 2, second.YTopLeft + 2, second.X + 9, second.YTopLeft + 9, filled: true);

        Assert.Empty(EditorDocument.ClipToFrame(box, first));
    }

    [Fact]
    public void 図形の座標はコマをまたいで連続している()
    {
        // LinePixels knows nothing about frames - it is the caller that has to clip. Pinned here
        // so that the clipping test below is testing the caller's work and not this one's.
        FrameRect first = Layout.Frames[0];
        FrameRect second = Layout.Frames[1];

        IReadOnlyList<(int X, int Y)> line = EditorDocument.LinePixels(
            first.X + 2, first.YTopLeft + 2, second.X + 2, second.YTopLeft + 2);

        Assert.Contains(line, p => p.X >= first.X + first.W);
    }

    [Fact]
    public void LinePixels_極端な座標でも例外にならず有限で返る()
    {
        // Math.Abs(x1 - x0) on int.MinValue threw OverflowException - untranslated, and naming
        // nothing anyone could act on - and a span of three hundred million returned three
        // hundred million points, measured at 6 GB.
        IReadOnlyList<(int X, int Y)> a = EditorDocument.LinePixels(0, 0, int.MinValue, 0);
        IReadOnlyList<(int X, int Y)> b = EditorDocument.LinePixels(0, 0, 300_000_000, 0);
        IReadOnlyList<(int X, int Y)> c = EditorDocument.LinePixels(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);

        foreach (IReadOnlyList<(int X, int Y)> line in new[] { a, b, c })
        {
            Assert.NotEmpty(line);
            Assert.True(line.Count <= 4_096, $"点が多すぎる: {line.Count}");
        }
    }

    [Fact]
    public void RectanglePixels_極端な座標でも戻ってくる()
    {
        // Unbounded, a corner at int.MaxValue asked for every pixel of a two-billion-square
        // area - 9.8 GB and still climbing after eight seconds - and with y <= int.MaxValue the
        // loop counter wrapped round to int.MinValue and never came back.
        IReadOnlyList<(int X, int Y)> outline =
            EditorDocument.RectanglePixels(0, 0, 0, int.MaxValue, filled: false);

        Assert.NotEmpty(outline);
        Assert.True(outline.Count <= 8_192, $"点が多すぎる: {outline.Count}");

        IReadOnlyList<(int X, int Y)> box =
            EditorDocument.RectanglePixels(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue, filled: false);

        Assert.NotEmpty(box);
    }

    [Fact]
    public void Fill_コマがテクスチャからはみ出すレイアウトでも落ちない()
    {
        // Nothing validates a layout's frames against its texture, and the fill checked only the
        // frame: far enough past the end of a row it threw IndexOutOfRangeException, and short
        // of that it quietly wrote over the next row.
        SheetLayout overhanging = Layout with
        {
            Frames = [new FrameRect(0, "idle", "down", 0, 220, 130, 140, 26, 26)],
            FrameCount = 1,
        };

        using SKBitmap sheet = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        SKColor[] pixels = sheet.Pixels;
        Array.Fill(pixels, SKColors.White);
        sheet.Pixels = pixels;

        EditorDocument document = new(sheet, overhanging);

        document.BeginChange();
        document.Fill(225, 145, SKColors.Red, EditScope.SingleFrame);

        Assert.Equal(SKColors.Red, document.GetPixel(225, 145));
    }

    [Fact]
    public void LinePixels_両端を含む連続した並びを返す()
    {
        IReadOnlyList<(int X, int Y)> points = EditorDocument.LinePixels(2, 2, 6, 5);

        Assert.Equal((2, 2), points[0]);
        Assert.Equal((6, 5), points[^1]);

        // Every step moves by at most one in each direction, so the line has no gaps
        for (int i = 1; i < points.Count; i++)
        {
            Assert.True(Math.Abs(points[i].X - points[i - 1].X) <= 1);
            Assert.True(Math.Abs(points[i].Y - points[i - 1].Y) <= 1);
        }
    }

    [Fact]
    public void LinePixels_始点と終点が同じなら1点だけ返す()
    {
        Assert.Equal([(4, 4)], EditorDocument.LinePixels(4, 4, 4, 4));
    }

    [Fact]
    public void RectanglePixels_枠と塗りつぶしで数が違う()
    {
        IReadOnlyList<(int X, int Y)> outline =
            EditorDocument.RectanglePixels(1, 1, 4, 3, filled: false);
        IReadOnlyList<(int X, int Y)> filled =
            EditorDocument.RectanglePixels(1, 1, 4, 3, filled: true);

        // 4 x 3 = 12 filled; the outline leaves the two interior pixels out
        Assert.Equal(12, filled.Count);
        Assert.Equal(10, outline.Count);
        Assert.DoesNotContain((2, 2), outline);
        Assert.Contains((2, 2), filled);
    }

    [Fact]
    public void RectanglePixels_ドラッグの向きに関係なく同じ矩形になる()
    {
        // Dragging up and to the left has to give the same rectangle as down and to the right
        Assert.Equal(
            EditorDocument.RectanglePixels(1, 1, 4, 3, filled: true),
            EditorDocument.RectanglePixels(4, 3, 1, 1, filled: true));
    }

    // ------------------------------------------------------------ Revision

    // IsModified only says whether the sheet differs from the one imported, so drawing more on a
    // sheet that was already edited does not show in it. The window compares this count, taken when
    // the user agreed to lose the drawing, with its value when a conversion lands, so that what was
    // drawn in between is not thrown away with the rest.

    [Fact]
    public void Revision_編集済みの文書にさらに描くと増える()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 2, 2);
        (int laterX, int laterY) = At(0, 6, 6);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();
        Assert.True(document.IsModified);

        long atConsent = document.Revision;

        document.BeginChange();
        document.Paint(laterX, laterY, SKColors.Lime, EditScope.SingleFrame);
        document.EndChange();

        // IsModified is true both times and cannot tell them apart; the revision can
        Assert.True(document.IsModified);
        Assert.NotEqual(atConsent, document.Revision);
    }

    [Fact]
    public void Revision_何も変えない筆では増えない()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 2, 2);
        long before = document.Revision;

        // Erasing over transparency, which leaves the sheet as it was
        document.BeginChange();
        document.Paint(x, y, SKColors.Transparent, EditScope.SingleFrame);
        document.EndChange();

        Assert.Equal(before, document.Revision);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Revision_元に戻すとやり直しでも増え履歴と編集済みの判定は変わらない()
    {
        EditorDocument document = CreateDocument();
        (int x, int y) = At(0, 2, 2);

        document.BeginChange();
        document.Paint(x, y, SKColors.Red, EditScope.SingleFrame);
        document.EndChange();
        long afterStroke = document.Revision;

        Assert.True(document.Undo());
        long afterUndo = document.Revision;
        Assert.NotEqual(afterStroke, afterUndo);
        Assert.False(document.IsModified);
        Assert.True(document.CanRedo);

        Assert.True(document.Redo());
        Assert.NotEqual(afterUndo, document.Revision);
        Assert.True(document.IsModified);
        Assert.Equal(SKColors.Red, document.GetPixel(x, y));
    }

    [Fact]
    public void Revision_置き換えでも増え失うものがあるかの判定は変えない()
    {
        EditorDocument document = CreateDocument();
        using SKBitmap other = PixelOps.CreateEmpty(Layout.Texture.Width, Layout.Texture.Height);
        SKColor[] pixels = other.Pixels;
        Array.Fill(pixels, SKColors.Blue);
        other.Pixels = pixels;

        long before = document.Revision;
        document.Replace(other, foldIntoPrevious: true);

        Assert.NotEqual(before, document.Revision);

        // A replacement still neither makes work nor destroys it
        Assert.False(document.IsModified);
    }
}

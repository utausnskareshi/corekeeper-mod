using CoreKeeperSkinTool.Imaging;
using CoreKeeperSkinTool.Layout;
using SkiaSharp;

namespace CoreKeeperSkinTool.Editing;

/// <summary>The drawing tool in use.</summary>
public enum EditorTool
{
    /// <summary>Paints with the chosen colour.</summary>
    Pen,

    /// <summary>Clears pixels back to transparent.</summary>
    Eraser,

    /// <summary>Picks up the colour at the clicked position.</summary>
    Picker,

    /// <summary>Fills a connected area of the same colour.</summary>
    Bucket,

    /// <summary>Draws a straight line between the two ends of a drag.</summary>
    Line,

    /// <summary>Draws the outline of a rectangle spanned by a drag.</summary>
    Rectangle,

    /// <summary>Draws a rectangle spanned by a drag, filled through.</summary>
    FilledRectangle,

    /// <summary>Replaces every pixel of the clicked colour, connected or not.</summary>
    ColourReplace,
}

/// <summary>
/// Whether a tool acts where the pointer is, or between where it went down and where it came up.
/// </summary>
public static class EditorTools
{
    /// <summary>
    /// Whether the tool is drawn by dragging from one point to another.
    ///
    /// These do nothing until the button is released, so the view shows where the shape would
    /// land instead of painting as the pointer moves.
    /// </summary>
    public static bool IsDrag(this EditorTool tool) =>
        tool is EditorTool.Line or EditorTool.Rectangle or EditorTool.FilledRectangle;
}

/// <summary>Scope of an edit.</summary>
public enum EditScope
{
    /// <summary>Edit only the selected frame.</summary>
    SingleFrame,

    /// <summary>Edit the same position across every frame; the usual choice when working from one picture.</summary>
    AllFrames,
}

/// <summary>
/// The sprite sheet being edited.
///
/// Undo history is kept as full copies of the sheet's pixel buffer.
/// At 234x156 that is about 146 KB per step, cheap enough to snapshot on every action.
/// Simpler than tracking diffs, and wide operations such as fill need no special case.
/// </summary>
public sealed class EditorDocument
{
    /// <summary>Number of undo steps kept.</summary>
    private const int MaxHistory = 64;

    private readonly List<SKColor[]> _undo = [];
    private readonly List<SKColor[]> _redo = [];

    private SKColor[] _pixels;

    /// <summary>
    /// The sheet exactly as the conversion produced it, used to tell whether the document
    /// still differs from it after a series of edits and undos.
    /// </summary>
    private readonly SKColor[] _baseline;

    /// <summary>
    /// State captured by <see cref="BeginChange"/>, held until an edit actually changes a pixel.
    /// </summary>
    private SKColor[]? _pendingSnapshot;

    /// <summary>Whether the current stroke has already put its state onto the undo history.</summary>
    private bool _strokeRecorded;

    /// <summary>
    /// Whether the newest step on the undo history was pushed by a whole-sheet replacement.
    ///
    /// This is what lets a run of replacements fold into one step. Choosing a preset over
    /// hand-drawn work replaces the sheet and relies on undo to bring the work back, but the
    /// history is bounded: a user comparing presets - or rolling the mouse wheel over the
    /// dropdown, which changes the selection too - pushed one step per preset and drove their
    /// own drawing off the end of it, with nothing able to return.
    /// </summary>
    private bool _topOfHistoryIsReplacement;

    public EditorDocument(SKBitmap sheet, SheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(layout);

        // Checked here as Replace and Recolour already check it. The way in was the only one
        // that did not, so a sheet of the wrong size was accepted and then failed later, from
        // inside a fill, as IndexOutOfRangeException - an error naming nothing the user could
        // act on, raised a long way from what caused it.
        if (layout.Texture is not null
            && (sheet.Width != layout.Texture.Width || sheet.Height != layout.Texture.Height))
        {
            throw new ToolException(
                "error.editor.sheetSize",
                [sheet.Width, sheet.Height, layout.Texture.Width, layout.Texture.Height],
                $"シートの寸法がレイアウトと違う: {sheet.Width}x{sheet.Height} " +
                $"(期待値 {layout.Texture.Width}x{layout.Texture.Height})");
        }

        Layout = layout;
        Width = sheet.Width;
        Height = sheet.Height;

        // Normalised on the way in, not only on the way out. SetPixel collapses every fully
        // transparent colour to one value, and the rest of the class compares colours exactly on
        // the strength of that. Pixels that arrive from a file never went through SetPixel, so
        // until now the invariant held for everything this class wrote and for nothing it read.
        //
        // That gap opened when finished sheets became re-openable. A sheet saved by an outside
        // editor keeps the colour under its transparent pixels - 0x00FFFFFF is common - and the
        // three places that compare against a normalised colour then matched the wrong pixels:
        // replacing a colour recoloured somewhere the user had not clicked, a bucket fill stopped
        // dead at an erased pixel, and erasing an already-invisible pixel counted as an edit.
        //
        // What is discarded is the colour underneath full transparency, which nothing here can
        // show and nothing downstream reads.
        _pixels = [.. sheet.Pixels.Select(Normalize)];
        _baseline = (SKColor[])_pixels.Clone();
        FrameLookup = layout.Frames.ToDictionary(f => f.Index);
    }

    public SheetLayout Layout { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyDictionary<int, FrameRect> FrameLookup { get; }

    /// <summary>Whether anything was edited since import, deciding if a settings change may discard it.</summary>
    public bool IsModified { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// The current contents.
    ///
    /// Wrapped rather than returned directly: handing out the array itself lets a caller cast
    /// the interface back to <c>SKColor[]</c> and write into the document behind its back,
    /// which changes the picture without touching the undo history or <see cref="IsModified"/>.
    /// </summary>
    public IReadOnlyList<SKColor> Pixels => Array.AsReadOnly(_pixels);

    public SKColor GetPixel(int x, int y) =>
        InBounds(x, y) ? _pixels[(y * Width) + x] : default;

    /// <summary>
    /// The cell of the sheet's grid that a position falls in, clipped to the sheet.
    ///
    /// Fifteen of the fifty-four cells belong to no frame, and the whole-sheet view shows them,
    /// so they can be clicked. They still need a bounded answer to "just here", and the cell is
    /// the natural unit: it is what a frame occupies.
    /// </summary>
    public SKRectI CellAt(int x, int y)
    {
        int cellWidth = Layout.Cell?.Width > 0 ? Layout.Cell.Width : Width;
        int cellHeight = Layout.Cell?.Height > 0 ? Layout.Cell.Height : Height;

        int left = x / cellWidth * cellWidth;
        int top = y / cellHeight * cellHeight;

        return new SKRectI(
            left, top,
            Math.Min(left + cellWidth, Width),
            Math.Min(top + cellHeight, Height));
    }

    /// <summary>Copies the current contents into a new bitmap, for saving, installing and previewing.</summary>
    public SKBitmap ToBitmap()
    {
        SKBitmap bitmap = PixelOps.CreateEmpty(Width, Height);
        bitmap.Pixels = _pixels;
        return bitmap;
    }

    /// <summary>
    /// Call before starting a stroke to push the previous state onto the undo history.
    /// Calling it once on press means a whole drag stroke is undone in a single step.
    /// </summary>
    public void BeginChange()
    {
        // The snapshot is taken now but only committed once a pixel actually changes.
        // Committing on press would give a click that paints nothing an undo step that does
        // nothing, and would mark the document as edited, which locks the import settings
        // behind a "your edits will be lost" confirmation the user never earned.
        _pendingSnapshot = (SKColor[])_pixels.Clone();
        _strokeRecorded = false;
    }

    /// <summary>
    /// Call when a stroke finishes, so the next change starts a new undo step.
    ///
    /// Without this the editor cannot tell one stroke from the next, and a stroke that never
    /// went through <see cref="BeginChange"/> - which happens when the tool is switched while
    /// the button is held - would be folded into the previous step or lost entirely.
    /// </summary>
    public void EndChange()
    {
        bool recorded = _strokeRecorded;

        _pendingSnapshot = null;
        _strokeRecorded = false;

        // Re-decided from the contents at the end of every stroke, not latched on the first
        // change. Painting a pixel and then erasing it back by hand leaves the sheet identical
        // to the imported one, and latching meant the user was still warned their edits would
        // be lost - while reaching the same pixels with undo said, correctly, that they had
        // nothing to lose. One SequenceEqual per stroke is not worth the inconsistency.
        if (recorded)
        {
            RefreshModified();
        }
    }

    /// <summary>
    /// Puts the state from before the current stroke onto the undo history, once per stroke.
    /// Called from the mutating operations at the moment they first change something.
    /// </summary>
    private void RecordChange()
    {
        if (!_strokeRecorded)
        {
            // A stroke that began without BeginChange still has to be undoable, so its state
            // is captured here instead. RecordChange always runs before the write, so the
            // current contents are still the pre-change ones.
            _undo.Add(_pendingSnapshot ?? (SKColor[])_pixels.Clone());
            _pendingSnapshot = null;
            _strokeRecorded = true;

            // Any step pushed from here is an ordinary edit, so a replacement that follows it
            // starts a step of its own rather than folding into somebody else's.
            _topOfHistoryIsReplacement = false;

            if (_undo.Count > MaxHistory)
            {
                _undo.RemoveAt(0);
            }

            // Any redo history describes a future that this edit has just replaced.
            _redo.Clear();
        }

        IsModified = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add((SKColor[])_pixels.Clone());
        _pixels = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        // Whatever is on top now was not put there by the replacement that has just been undone,
        // so the next replacement must not fold into it.
        _topOfHistoryIsReplacement = false;

        // The snapshot taken when the stroke started describes a state that no longer follows
        // from the history. Committing it later would resurrect the undone pixels.
        EndChange();
        RefreshModified();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add((SKColor[])_pixels.Clone());
        _pixels = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _topOfHistoryIsReplacement = false;
        EndChange();
        RefreshModified();
        return true;
    }

    /// <summary>
    /// Re-evaluates whether the sheet still differs from the one the conversion produced.
    /// Undoing all the way back means there is nothing to lose, so the import settings
    /// should stop asking for confirmation.
    /// </summary>
    private void RefreshModified() =>
        IsModified = !_pixels.AsSpan().SequenceEqual(_baseline);

    /// <summary>
    /// Paints a pixel at a sheet coordinate.
    /// When <paramref name="scope"/> is <see cref="EditScope.AllFrames"/>, the position relative to
    /// the containing frame is computed and applied to that same position in every frame.
    /// </summary>
    public void Paint(int x, int y, SKColor color, EditScope scope) =>
        Paint(x, y, color, scope, mirror: false);

    /// <param name="mirror">
    /// Also paint the pixel's reflection across the middle of its frame.
    ///
    /// The character faces the viewer and is symmetric about that line, so most edits belong on
    /// both sides. Doing it here rather than asking the user to count columns is what makes an
    /// eye, a shoulder or a boot land where its twin already is.
    /// </param>
    public void Paint(int x, int y, SKColor color, EditScope scope, bool mirror)
    {
        PaintOne(x, y, color, scope);

        if (!mirror || !TryGetFrameAt(x, y, out FrameRect frame))
        {
            return;
        }

        // The reflection of column c in a frame w wide is w-1-c
        int mirroredX = frame.X + frame.W - 1 - (x - frame.X);
        if (mirroredX != x)
        {
            PaintOne(mirroredX, y, color, scope);
        }
    }

    private void PaintOne(int x, int y, SKColor color, EditScope scope)
    {
        if (!InBounds(x, y))
        {
            return;
        }

        if (scope == EditScope.SingleFrame || !TryGetFrameAt(x, y, out FrameRect frame))
        {
            SetPixel((y * Width) + x, color);
            return;
        }

        int cellX = x - frame.X;
        int cellY = y - frame.YTopLeft;
        foreach (FrameRect target in Layout.Frames)
        {
            int tx = target.X + cellX;
            int ty = target.YTopLeft + cellY;
            if (cellX < target.W && cellY < target.H && InBounds(tx, ty))
            {
                SetPixel((ty * Width) + tx, color);
            }
        }
    }

    /// <summary>
    /// Writes one pixel, recording the change the first time a write actually alters something.
    /// Painting the colour that is already there leaves no undo step behind.
    /// </summary>
    private void SetPixel(int index, SKColor color)
    {
        color = Normalize(color);

        if (_pixels[index] == color)
        {
            return;
        }

        RecordChange();
        _pixels[index] = color;
    }

    /// <summary>
    /// Replaces the whole sheet as one undoable step.
    ///
    /// Used when a preset is chosen over work already on the canvas. Building a fresh document
    /// instead threw the undo history away with it, so a stray change of the preset dropdown
    /// destroyed hand-drawn edits with nothing able to bring them back.
    /// </summary>
    public void Replace(SKBitmap sheet) => Replace(sheet, foldIntoPrevious: false);

    /// <summary>
    /// Replaces the whole sheet, optionally folding a run of replacements into one undo step.
    /// </summary>
    /// <param name="sheet">The sheet to adopt. Must be this document's size.</param>
    /// <param name="foldIntoPrevious">
    /// True to keep the step already on the history when the previous change was also a
    /// replacement, rather than pushing another.
    ///
    /// This is what makes the promise above true. One step per replacement is correct for a
    /// single choice and ruinous for a run of them: comparing presets over hand-drawn work, or
    /// rolling the wheel over the dropdown, pushed a step each time and drove the drawing off the
    /// end of a history that only holds sixty-four. Folding keeps the one step that matters - the
    /// state before the first of the run - so one undo returns to the drawing however many
    /// presets were tried. Any ordinary edit in between ends the run, so the next replacement
    /// starts a step of its own and that edit stays undoable too.
    /// </param>
    public void Replace(SKBitmap sheet, bool foldIntoPrevious)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (sheet.Width != Width || sheet.Height != Height)
        {
            throw new ToolException(
                "error.editor.replaceSize", [sheet.Width, sheet.Height, Width, Height],
                $"差し替え元の寸法が違う: {sheet.Width}x{sheet.Height} (期待値 {Width}x{Height})");
        }

        SKColor[] source = sheet.Pixels;

        if (foldIntoPrevious && _topOfHistoryIsReplacement && _undo.Count > 0)
        {
            // Written with the stroke already marked as recorded, so the writes below find
            // nothing to push. The step on the history is the state before the first replacement
            // of this run, which is the one worth returning to.
            _pendingSnapshot = null;
            _strokeRecorded = true;

            for (int i = 0; i < _pixels.Length; i++)
            {
                SetPixel(i, source[i]);
            }

            _strokeRecorded = false;

            // Both of these are what RecordChange would have done. The redo history described a
            // future that this replacement has just replaced, and whether the document still
            // differs from the imported sheet has to be decided again from its contents.
            _redo.Clear();
            RefreshModified();
        }
        else
        {
            BeginChange();

            for (int i = 0; i < _pixels.Length; i++)
            {
                SetPixel(i, source[i]);
            }

            EndChange();
        }

        _topOfHistoryIsReplacement = true;
    }

    /// <summary>
    /// Swaps colours for those of another sheet, leaving every pixel where it is.
    ///
    /// Each distinct colour of this sheet is matched to the colour the other sheet has in the
    /// same place, choosing whichever candidate occurs most often. Trying a palette therefore
    /// costs none of the shapes the user has drawn, which a straight reload would.
    /// </summary>
    /// <param name="palette">A sheet of the same size whose colours are to be adopted.</param>
    public void Recolour(SKBitmap palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        if (palette.Width != Width || palette.Height != Height)
        {
            throw new ToolException(
                "error.editor.paletteSize", [palette.Width, palette.Height, Width, Height],
                $"配色元の寸法が違う: {palette.Width}x{palette.Height} (期待値 {Width}x{Height})");
        }

        SKColor[] source = palette.Pixels;

        // For every colour in use, count what the other sheet has at those same positions
        Dictionary<SKColor, Dictionary<SKColor, int>> votes = [];
        for (int i = 0; i < _pixels.Length; i++)
        {
            SKColor from = _pixels[i];
            if (from.Alpha == 0)
            {
                continue;
            }

            SKColor to = source[i];
            if (to.Alpha == 0)
            {
                continue;
            }

            if (!votes.TryGetValue(from, out Dictionary<SKColor, int>? tally))
            {
                tally = [];
                votes[from] = tally;
            }

            tally[to] = tally.TryGetValue(to, out int count) ? count + 1 : 1;
        }

        if (votes.Count == 0)
        {
            return;
        }

        Dictionary<SKColor, SKColor> mapping = votes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderByDescending(v => v.Value).First().Key);

        BeginChange();

        for (int i = 0; i < _pixels.Length; i++)
        {
            if (mapping.TryGetValue(_pixels[i], out SKColor replacement))
            {
                SetPixel(i, replacement);
            }
        }

        EndChange();
    }

    /// <summary>
    /// Collapses every fully transparent colour to one value.
    ///
    /// <c>SKColors.Transparent</c> is 0x00FFFFFF, transparent white, while an empty sheet is
    /// filled with 0x00000000. Both are invisible, so writing one over the other changed the
    /// document without changing the picture: erasing an already-empty pixel marked the document
    /// edited and triggered the "your edits will be lost" confirmation, and a bucket fill
    /// afterwards skipped the erased pixels because it compares colours exactly.
    /// </summary>
    private static SKColor Normalize(SKColor color) =>
        color.Alpha == 0 ? default : color;

    /// <summary>
    /// Replaces every pixel of one colour with another.
    ///
    /// Unlike the bucket this ignores whether the pixels touch, which is what makes it the tool
    /// for a change of palette: recolouring a shirt means every part of that shirt across all
    /// thirty-nine frames, and those parts are not connected to each other.
    /// </summary>
    /// <param name="scope">
    /// Whether to work inside the clicked frame only, or across the whole sheet.
    /// </param>
    /// <returns>How many pixels changed, so the caller can say when nothing matched.</returns>
    public int ReplaceColour(int x, int y, SKColor color, EditScope scope)
    {
        if (!InBounds(x, y))
        {
            return 0;
        }

        SKColor target = Normalize(_pixels[(y * Width) + x]);
        SKColor replacement = Normalize(color);

        if (target == replacement)
        {
            return 0;
        }

        int changed = 0;

        // "Apply to every frame" means everywhere on the sheet, and everywhere is the whole
        // texture: the fifteen unused cells and the gaps between frames are drawn in the
        // whole-sheet view, are editable there, and are saved into the PNG. An earlier pass
        // restricted this to the frames alone so the count would not be inflated by pixels
        // "nothing shows" - but they are shown, and the restriction left the same colour sitting
        // in the gaps after the tool said it had replaced it, with no way to reach it: clicking
        // one fell into the branch below and recoloured the entire texture instead.
        if (scope == EditScope.AllFrames)
        {
            for (int i = 0; i < _pixels.Length; i++)
            {
                if (_pixels[i] == target)
                {
                    SetPixel(i, replacement);
                    changed++;
                }
            }

            return changed;
        }

        // One cell at a time otherwise. A click inside a frame means that frame; a click in a
        // gap or an unused cell means the cell it landed in, which keeps the answer bounded and
        // predictable. It used to mean the whole texture, so turning "apply to every frame" off
        // and clicking a gap repainted all thirty-nine frames - the opposite of what it says.
        SKRectI area = TryGetFrameAt(x, y, out FrameRect clicked)
            ? new SKRectI(clicked.X, clicked.YTopLeft, clicked.X + clicked.W, clicked.YTopLeft + clicked.H)
            : CellAt(x, y);

        for (int fy = area.Top; fy < area.Bottom && fy < Height; fy++)
        {
            for (int fx = area.Left; fx < area.Right && fx < Width; fx++)
            {
                int index = (fy * Width) + fx;
                if (_pixels[index] == target)
                {
                    SetPixel(index, replacement);
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// The pixels a straight line from one point to the other passes through.
    ///
    /// Separate from drawing so the view can show where the line will land before the button is
    /// released. Bresenham, the same walk the freehand stroke uses to close the gaps between
    /// pointer samples, so a drawn line and a fast drag look alike.
    /// </summary>
    /// <remarks>
    /// Both ends are brought inside <see cref="MaxSpan"/> first, and the arithmetic is done in
    /// long. Neither was true before: <c>Math.Abs(x1 - x0)</c> threw OverflowException on
    /// int.MinValue - untranslated, and naming nothing the user could act on - and a span of
    /// three hundred million returned three hundred million points, measured at 6 GB. Nothing
    /// in the window can produce coordinates like these, but a public method that answers a
    /// silly question with a silly amount of memory is one call away from being a hang.
    /// </remarks>
    public static IReadOnlyList<(int X, int Y)> LinePixels(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, -MaxSpan, MaxSpan);
        y0 = Math.Clamp(y0, -MaxSpan, MaxSpan);
        x1 = Math.Clamp(x1, -MaxSpan, MaxSpan);
        y1 = Math.Clamp(y1, -MaxSpan, MaxSpan);

        List<(int X, int Y)> points = [];

        long dx = Math.Abs((long)x1 - x0);
        long dy = -Math.Abs((long)y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        long error = dx + dy;

        while (true)
        {
            points.Add((x0, y0));

            if (x0 == x1 && y0 == y1)
            {
                return points;
            }

            long doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// How far outside the sheet a shape's ends may reach before they are brought in.
    ///
    /// Generous next to a 234x156 sheet, and small enough that the worst case is a list of a
    /// few million entries rather than one the machine cannot hold. Shapes are clipped to a
    /// frame afterwards anyway, so nothing legitimate is lost by pulling the ends in first.
    /// </summary>
    private const int MaxSpan = 1024;

    /// <summary>The pixels of a rectangle, as an outline or filled through.</summary>
    /// <summary>
    /// Drops the positions that fall outside one frame.
    ///
    /// <see cref="LinePixels"/> and <see cref="RectanglePixels"/> work in sheet coordinates and
    /// know nothing of frames, so a drag that began in one frame and ended in the next ran the
    /// shape straight across the boundary and into the neighbour. The frames are separate
    /// pictures that happen to be stored side by side, and nobody means to draw across them.
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> ClipToFrame(
        IReadOnlyList<(int X, int Y)> pixels, FrameRect frame)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(frame);

        return
        [
            // Widened to long, like the layout validation. A frame placed near int.MaxValue makes
            // frame.X + frame.W wrap negative, and every point inside it then read as outside.
            .. pixels.Where(p =>
                p.X >= frame.X && p.X < (long)frame.X + frame.W
                && p.Y >= frame.YTopLeft && p.Y < (long)frame.YTopLeft + frame.H),
        ];
    }

    /// <remarks>
    /// Bounded by <see cref="MaxSpan"/>, like <see cref="LinePixels"/>. A rectangle clipped to a
    /// box is still a rectangle, so nothing about the shape is lost. Unbounded, a corner at
    /// int.MaxValue asked for every pixel in a two-billion-square area: measured at 9.8 GB and
    /// still climbing after eight seconds, and with <c>y &lt;= bottom</c> on int.MaxValue the
    /// loop counter wrapped round to int.MinValue and never came back.
    /// </remarks>
    public static IReadOnlyList<(int X, int Y)> RectanglePixels(
        int x0, int y0, int x1, int y1, bool filled)
    {
        int left = Math.Clamp(Math.Min(x0, x1), -MaxSpan, MaxSpan);
        int right = Math.Clamp(Math.Max(x0, x1), -MaxSpan, MaxSpan);
        int top = Math.Clamp(Math.Min(y0, y1), -MaxSpan, MaxSpan);
        int bottom = Math.Clamp(Math.Max(y0, y1), -MaxSpan, MaxSpan);

        List<(int X, int Y)> points = [];

        if (filled)
        {
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    points.Add((x, y));
                }
            }

            return points;
        }

        // The four sides, walked directly. Scanning the whole area and keeping only the pixels
        // on its edge gave the same answer at the cost of the area: a rectangle a thousand
        // across spent a billion tests to produce four thousand points.
        for (int x = left; x <= right; x++)
        {
            points.Add((x, top));

            if (bottom != top)
            {
                points.Add((x, bottom));
            }
        }

        for (int y = top + 1; y < bottom; y++)
        {
            points.Add((left, y));

            if (right != left)
            {
                points.Add((right, y));
            }
        }

        return points;
    }

    /// <summary>
    /// Fills a connected area of one colour. The search is confined to the frame so it cannot spill over.
    ///
    /// With <see cref="EditScope.AllFrames"/> the fill is computed separately inside each frame,
    /// starting from the same position relative to that frame. Copying one frame's region to the
    /// others instead would paint at fixed coordinates, and the walk animation shifts the art up
    /// by a pixel in some frames, so the copied region landed on top of the character in those
    /// frames while leaving the same number of background pixels unfilled.
    /// </summary>
    /// <returns>
    /// How many pixels changed, so the caller can tell a fill that did something from one that
    /// did not. Clicking inside an area already that colour is an ordinary mistake, and reporting
    /// nothing left whatever the last action had said on screen, which reads as if it worked.
    /// </returns>
    public int Fill(int x, int y, SKColor color, EditScope scope)
    {
        if (!InBounds(x, y) || !TryGetFrameAt(x, y, out FrameRect frame))
        {
            return 0;
        }

        // Normalised the same way SetPixel normalises, so that filling an area that was
        // erased earlier is recognised as a no-op instead of repainting invisible pixels.
        color = Normalize(color);

        int cellX = x - frame.X;
        int cellY = y - frame.YTopLeft;

        IEnumerable<FrameRect> targets = scope == EditScope.AllFrames
            ? Layout.Frames
            : [frame];

        int changed = 0;

        foreach (FrameRect destination in targets)
        {
            // Frames may differ in size, so the position can fall outside a smaller one
            if (cellX < destination.W && cellY < destination.H)
            {
                changed += FillWithinFrame(
                    destination, destination.X + cellX, destination.YTopLeft + cellY, color);
            }
        }

        return changed;
    }

    /// <summary>
    /// Flood fills the area connected to one pixel, never leaving the frame it belongs to.
    /// </summary>
    /// <returns>How many pixels changed within this frame.</returns>
    private int FillWithinFrame(FrameRect frame, int startX, int startY, SKColor color)
    {
        if (!InBounds(startX, startY))
        {
            return 0;
        }

        SKColor target = _pixels[(startY * Width) + startX];
        if (target == color)
        {
            return 0;
        }

        Stack<(int X, int Y)> pending = new();
        HashSet<(int X, int Y)> visited = [];

        pending.Push((startX, startY));
        visited.Add((startX, startY));

        while (pending.Count > 0)
        {
            (int px, int py) = pending.Pop();
            SetPixel((py * Width) + px, color);

            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = px + dx;
                int ny = py + dy;

                if (nx < frame.X || ny < frame.YTopLeft ||
                    nx >= frame.X + frame.W || ny >= frame.YTopLeft + frame.H)
                {
                    continue;
                }

                // The frame is checked, and so is the sheet. A layout can place a frame that
                // runs off the texture - nothing validates one against the other - and then the
                // frame check alone let the index walk past the end of the row: far enough for
                // IndexOutOfRangeException, and short of that, quietly over the next row.
                if (!InBounds(nx, ny))
                {
                    continue;
                }

                if (!visited.Add((nx, ny)) || _pixels[(ny * Width) + nx] != target)
                {
                    continue;
                }

                pending.Push((nx, ny));
            }
        }

        // Every visited pixel held the target colour and the target differs from the fill colour,
        // so the set of visited pixels is exactly the set that changed.
        return visited.Count;
    }

    /// <summary>Looks up the frame at a coordinate.</summary>
    public bool TryGetFrameAt(int x, int y, out FrameRect frame)
    {
        foreach (FrameRect candidate in Layout.Frames)
        {
            if (x >= candidate.X && y >= candidate.YTopLeft &&
                x < candidate.X + candidate.W && y < candidate.YTopLeft + candidate.H)
            {
                frame = candidate;
                return true;
            }
        }

        frame = null!;
        return false;
    }

    private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
}

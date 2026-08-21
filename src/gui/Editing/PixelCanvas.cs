using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using CoreKeeperSkinTool.Layout;

namespace CoreKeeperSkinTool.Gui.Editing;

/// <summary>An interaction with the pixel editing surface.</summary>
public sealed class PixelPointerEventArgs(
    int x, int y, bool isStart, bool isErase = false, bool isInterpolated = false) : EventArgs
{
    /// <summary>X coordinate on the sheet.</summary>
    public int X { get; } = x;

    /// <summary>Y coordinate on the sheet.</summary>
    public int Y { get; } = y;

    /// <summary>Whether this begins a stroke (a press), which marks an undo boundary.</summary>
    public bool IsStart { get; } = isStart;

    /// <summary>
    /// Whether the stroke should erase, whatever tool is selected.
    ///
    /// The right button erases. Every pixel editor does this, so it is the first thing anyone
    /// who has used one will try, and it saves switching tool twice to take one pixel back out.
    /// </summary>
    public bool IsErase { get; } = isErase;

    /// <summary>
    /// Whether this position was filled in between two pointer samples rather than reported by
    /// one.
    ///
    /// Freehand tools need every one of these or the stroke comes out dotted. A shape only ever
    /// uses the latest position, so for those the interpolated ones are work thrown away - and
    /// not a little of it, since each one recomputed the whole shape.
    /// </summary>
    public bool IsInterpolated { get; } = isInterpolated;
}

/// <summary>
/// A surface that magnifies the sprite sheet and allows pixel-level editing.
///
/// It does not draw anything itself; it only reports the coordinate that was pressed.
/// The caller owns the tool and colour, leaving this type free to handle display and input only.
/// </summary>
public sealed class PixelCanvas : Control
{
    public static readonly StyledProperty<WriteableBitmap?> SourceProperty =
        AvaloniaProperty.Register<PixelCanvas, WriteableBitmap?>(nameof(Source));

    public static readonly StyledProperty<int> ZoomProperty =
        AvaloniaProperty.Register<PixelCanvas, int>(nameof(Zoom), 8);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<PixelCanvas, bool>(nameof(ShowGrid), true);

    public static readonly StyledProperty<bool> ShowGuidesProperty =
        AvaloniaProperty.Register<PixelCanvas, bool>(nameof(ShowGuides), true);

    public static readonly StyledProperty<bool> UseLightBackgroundProperty =
        AvaloniaProperty.Register<PixelCanvas, bool>(nameof(UseLightBackground));

    /// <summary>Frame to display; null shows the whole sheet.</summary>
    public static readonly StyledProperty<FrameRect?> FrameProperty =
        AvaloniaProperty.Register<PixelCanvas, FrameRect?>(nameof(Frame));

    /// <summary>The frame layout, used to draw borders and guides in whole-sheet view.</summary>
    public static readonly StyledProperty<SheetLayout?> LayoutProperty =
        AvaloniaProperty.Register<PixelCanvas, SheetLayout?>(nameof(Layout));

    /// <summary>
    /// Pixels a shape tool would write if the button were released now.
    ///
    /// Drawn over the picture rather than into it. A line or a rectangle is only decided once
    /// both ends are known, so painting it into the sheet on every pointer move would mean
    /// undoing it again on the next one.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<(int X, int Y)>?> PreviewPixelsProperty =
        AvaloniaProperty.Register<PixelCanvas, IReadOnlyList<(int X, int Y)>?>(nameof(PreviewPixels));

    public IReadOnlyList<(int X, int Y)>? PreviewPixels
    {
        get => GetValue(PreviewPixelsProperty);
        set => SetValue(PreviewPixelsProperty, value);
    }

    /// <summary>
    /// The two tones of the chequerboard drawn behind the sheet.
    ///
    /// A flat colour cannot say "nothing is here": a dark character on a dark background, or a
    /// white one on a light background, makes the empty pixels indistinguishable from the art.
    /// A chequer has no single colour to be mistaken for, which is why every pixel editor uses
    /// one. Both pairs stay low-contrast so they never compete with the sheet itself.
    /// </summary>
    private static readonly ImmutableSolidColorBrush DarkBackground = new(Color.FromRgb(26, 26, 32));

    private static readonly ImmutableSolidColorBrush DarkBackgroundAlt = new(Color.FromRgb(58, 58, 70));

    private static readonly ImmutableSolidColorBrush LightBackground = new(Color.FromRgb(222, 222, 228));

    private static readonly ImmutableSolidColorBrush LightBackgroundAlt = new(Color.FromRgb(184, 184, 194));

    /// <summary>Side of one chequer square, in sheet pixels.</summary>
    private const int CheckerPixels = 4;
    private static readonly ImmutablePen GridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1);
    private static readonly ImmutablePen FramePen = new(new ImmutableSolidColorBrush(Color.FromArgb(150, 0, 200, 255)), 1);
    private static readonly ImmutablePen BaselinePen = new(new ImmutableSolidColorBrush(Color.FromArgb(170, 255, 220, 0)), 1);
    private static readonly ImmutablePen BoxPen = new(new ImmutableSolidColorBrush(Color.FromArgb(120, 255, 0, 200)), 1);
    private static readonly ImmutablePen CursorPen = new(new ImmutableSolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1);

    /// <summary>Shown where a shape tool would write. Half transparent, so the picture underneath
    /// stays visible while the shape is being sized.</summary>
    private static readonly ImmutableSolidColorBrush PreviewBrush =
        new(Color.FromArgb(140, 255, 255, 255));

    private bool _painting;

    /// <summary>Whether a button is held and a stroke is under way.</summary>
    public bool IsPainting => _painting;

    /// <summary>Last pixel painted in the current stroke, used to fill the gap to the next one.</summary>
    private (int x, int y)? _lastPainted;

    /// <summary>Whether the stroke in progress was begun with the right button.</summary>
    private bool _erasing;
    private (int X, int Y)? _hover;

    static PixelCanvas()
    {
        AffectsRender<PixelCanvas>(
            SourceProperty, ZoomProperty, ShowGridProperty, ShowGuidesProperty,
            UseLightBackgroundProperty, FrameProperty, LayoutProperty, PreviewPixelsProperty);
        AffectsMeasure<PixelCanvas>(SourceProperty, ZoomProperty, FrameProperty);
    }

    public PixelCanvas()
    {
        // Do not let interpolation blur pixel art
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>The editing surface was pressed or dragged.</summary>
    public event EventHandler<PixelPointerEventArgs>? PixelPointer;

    /// <summary>The cursor moved; used to refresh the coordinate readout.</summary>
    public event EventHandler<PixelPointerEventArgs>? HoverChanged;

    /// <summary>
    /// Raised when a stroke finishes, whether by releasing the button or by losing the pointer.
    /// The document needs this to close the current undo step; without it a stroke that starts
    /// without a press event, which happens when the tool is switched mid-drag, is folded into
    /// the previous step and cannot be undone on its own.
    /// </summary>
    public event EventHandler? StrokeEnded;

    public WriteableBitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool ShowGuides
    {
        get => GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    public bool UseLightBackground
    {
        get => GetValue(UseLightBackgroundProperty);
        set => SetValue(UseLightBackgroundProperty, value);
    }

    public FrameRect? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public SheetLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The visible region, in sheet coordinates.</summary>
    private (int X, int Y, int W, int H) ViewArea
    {
        get
        {
            if (Frame is { } frame)
            {
                return (frame.X, frame.YTopLeft, frame.W, frame.H);
            }

            WriteableBitmap? source = Source;
            return source is null
                ? (0, 0, 1, 1)
                : (0, 0, source.PixelSize.Width, source.PixelSize.Height);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        (_, _, int w, int h) = ViewArea;
        int zoom = Math.Max(1, Zoom);
        return new Size(w * zoom, h * zoom);
    }

    /// <summary>
    /// Paints the chequerboard that stands for "transparent".
    ///
    /// The squares are measured in sheet pixels rather than screen pixels, so the pattern zooms
    /// with the art and always lines up with the pixel grid instead of drifting against it. At
    /// very low zoom the squares would be a shimmer, so it falls back to a flat colour.
    /// </summary>
    private void DrawTransparencyBackground(DrawingContext context, Rect destination, int zoom)
    {
        ImmutableSolidColorBrush baseBrush = UseLightBackground ? LightBackground : DarkBackground;
        context.FillRectangle(baseBrush, destination);

        if (zoom < 2)
        {
            return;
        }

        ImmutableSolidColorBrush altBrush = UseLightBackground ? LightBackgroundAlt : DarkBackgroundAlt;
        double square = CheckerPixels * zoom;

        int columns = (int)Math.Ceiling(destination.Width / square);
        int rows = (int)Math.Ceiling(destination.Height / square);

        for (int row = 0; row < rows; row++)
        {
            for (int column = row % 2; column < columns; column += 2)
            {
                double x = column * square;
                double y = row * square;

                // Clipped to the drawing area so the last square does not overhang the canvas
                double width = Math.Min(square, destination.Width - x);
                double height = Math.Min(square, destination.Height - y);
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                context.FillRectangle(altBrush, new Rect(x, y, width, height));
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        (int viewX, int viewY, int viewW, int viewH) = ViewArea;
        int zoom = Math.Max(1, Zoom);
        Rect destination = new(0, 0, viewW * zoom, viewH * zoom);

        DrawTransparencyBackground(context, destination, zoom);

        if (Source is { } source)
        {
            context.DrawImage(source, new Rect(viewX, viewY, viewW, viewH), destination);
        }

        if (ShowGrid && zoom >= 5)
        {
            for (int x = 0; x <= viewW; x++)
            {
                context.DrawLine(GridPen, new Point(x * zoom, 0), new Point(x * zoom, viewH * zoom));
            }

            for (int y = 0; y <= viewH; y++)
            {
                context.DrawLine(GridPen, new Point(0, y * zoom), new Point(viewW * zoom, y * zoom));
            }
        }

        DrawGuides(context, viewX, viewY, viewW, viewH, zoom);

        // Drawn under the cursor box, so the cursor stays visible on top of its own preview
        if (PreviewPixels is { Count: > 0 } preview)
        {
            foreach ((int x, int y) in preview)
            {
                // Bounded to what is on show, as the grid above is. With "apply to all frames"
                // on, the preview carries the same cell in every one of the thirty-nine frames,
                // and while a single frame is being viewed the other thirty-eight land outside
                // this control - which nothing clips until the scroll viewer, so they appeared
                // as a grid of pale squares out in the empty margin beside the frame.
                if (x < viewX || y < viewY || x >= viewX + viewW || y >= viewY + viewH)
                {
                    continue;
                }

                context.FillRectangle(
                    PreviewBrush,
                    new Rect((x - viewX) * zoom, (y - viewY) * zoom, zoom, zoom));
            }
        }

        if (_hover is { } hover)
        {
            context.DrawRectangle(
                null, CursorPen,
                new Rect((hover.X - viewX) * zoom, (hover.Y - viewY) * zoom, zoom, zoom));
        }
    }

    /// <summary>Overlays cell borders and the standing reference: area, foot line and centre.</summary>
    private void DrawGuides(DrawingContext context, int viewX, int viewY, int viewW, int viewH, int zoom)
    {
        if (!ShowGuides || Layout is not { } layout)
        {
            return;
        }

        IEnumerable<FrameRect> frames = Frame is { } single ? [single] : layout.Frames;

        foreach (FrameRect frame in frames)
        {
            double left = (frame.X - viewX) * zoom;
            double top = (frame.YTopLeft - viewY) * zoom;
            double width = frame.W * zoom;
            double height = frame.H * zoom;

            if (left + width < 0 || top + height < 0 || left > viewW * zoom || top > viewH * zoom)
            {
                continue;
            }

            context.DrawRectangle(null, FramePen, new Rect(left, top, width, height));

            StandingBoxSpec box = layout.StandingBox;
            context.DrawRectangle(
                null, BoxPen,
                new Rect(left + (box.X * zoom), top + (box.Y * zoom), box.Width * zoom, box.Height * zoom));

            double baseline = top + (box.BaselineY * zoom);
            context.DrawLine(BaselinePen, new Point(left, baseline), new Point(left + width, baseline));

            double center = left + (box.CenterX * zoom);
            context.DrawLine(BaselinePen, new Point(center, top), new Point(center, top + height));
        }
    }

    // ------------------------------------------------------------ Input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        PointerPointProperties buttons = e.GetCurrentPoint(this).Properties;
        if (!buttons.IsLeftButtonPressed && !buttons.IsRightButtonPressed)
        {
            return;
        }

        // A stroke belongs to the button that began it. Pressing the other one part-way through
        // used to re-enter here and flip the stroke between painting and erasing mid-way, which
        // left one drag doing two different things to the picture.
        if (_painting)
        {
            e.Handled = true;
            return;
        }

        _painting = true;
        _erasing = buttons.IsRightButtonPressed;
        _lastPainted = null;
        e.Pointer.Capture(this);
        RaiseForPoint(e.GetPosition(this), isStart: true);
        e.Handled = true;
    }

    /// <summary>
    /// Ends the stroke when the pointer capture is taken away, for instance because another
    /// window came to the front. Without this the canvas keeps painting on mouse movement
    /// even though no button is held, since no release event ever arrives.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        StopPainting();
    }

    private void StopPainting()
    {
        bool wasPainting = _painting;
        _painting = false;
        _erasing = false;
        _lastPainted = null;

        if (wasPainting)
        {
            StrokeEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Point position = e.GetPosition(this);
        UpdateHover(position);

        if (!_painting)
        {
            return;
        }

        // The stroke belongs to the button that began it, so that button being up ends it.
        // Avalonia raises PointerReleased only when the last button comes up, so with two held
        // down, letting go of the drawing one left the stroke running: the pen went on painting
        // while only the right button - the eraser - was still down, and the other way round the
        // eraser went on erasing under the left. Checked here because there is no event for it.
        PointerPointProperties buttons = e.GetCurrentPoint(this).Properties;
        bool held = _erasing ? buttons.IsRightButtonPressed : buttons.IsLeftButtonPressed;

        if (!held)
        {
            StopPainting();
            e.Pointer.Capture(null);
            return;
        }

        RaiseForPoint(position, isStart: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_painting)
        {
            StopPainting();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Swallows the wheel while a stroke is in progress.
    ///
    /// The scroll moves the sheet under a pointer that has not moved, so the next sample lands
    /// far from the last one and the gap between them is filled in - measured at a sixty-pixel
    /// line straight through the character, times thirty-nine with "apply to every frame" on.
    /// Nothing is lost by holding the view still until the button comes up.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_painting)
        {
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private void UpdateHover(Point position)
    {
        (int x, int y)? pixel = ToPixel(position);
        if (pixel == _hover)
        {
            return;
        }

        _hover = pixel;
        InvalidateVisual();

        if (pixel is { } value)
        {
            HoverChanged?.Invoke(this, new PixelPointerEventArgs(value.x, value.y, isStart: false));
        }
    }

    private void RaiseForPoint(Point position, bool isStart)
    {
        if (ToPixel(position) is not { } pixel)
        {
            // Leaving the drawable area breaks the stroke. Keeping the last pixel meant that
            // coming back somewhere else joined the two with a straight line: measured on the
            // whole-sheet view, one sample after a detour painted 233 pixels through twelve
            // frames, and with "apply to every frame" on, over nine thousand.
            //
            // An earlier pass recorded this as continuity worth having, on the grounds that a
            // brief excursion should not break the line. It buys a smoother line for a small
            // detour and pays for it by drawing something nobody asked for after a large one,
            // which is the wrong way round for an operation that changes the picture.
            _lastPainted = null;
            return;
        }

        // Pointer samples arrive far apart when the mouse moves quickly, so raising an event only
        // for the sampled positions would leave gaps in the stroke. Filling in the pixels between
        // the previous position and this one makes a drag draw a continuous line.
        if (!isStart && _lastPainted is { } previous && previous != pixel)
        {
            RaiseLine(previous, pixel);
        }
        else
        {
            PixelPointer?.Invoke(this, new PixelPointerEventArgs(pixel.x, pixel.y, isStart, _erasing));
        }

        _lastPainted = pixel;
    }

    /// <summary>
    /// Raises an event for every pixel between two points, excluding the starting one,
    /// which was already handled by the previous sample. Bresenham's line algorithm.
    /// </summary>
    private void RaiseLine((int x, int y) from, (int x, int y) to)
    {
        int dx = Math.Abs(to.x - from.x);
        int dy = -Math.Abs(to.y - from.y);
        int stepX = from.x < to.x ? 1 : -1;
        int stepY = from.y < to.y ? 1 : -1;
        int error = dx + dy;

        int x = from.x;
        int y = from.y;

        while (true)
        {
            if (x != from.x || y != from.y)
            {
                // Everything up to the far end is filled in; the far end itself is where the
                // pointer actually was, and the shape tools redraw only for that one.
                bool interpolated = x != to.x || y != to.y;
                PixelPointer?.Invoke(
                    this, new PixelPointerEventArgs(x, y, isStart: false, _erasing, interpolated));
            }

            if (x == to.x && y == to.y)
            {
                return;
            }

            int doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x += stepX;
            }

            if (doubled <= dx)
            {
                error += dx;
                y += stepY;
            }
        }
    }

    /// <summary>Converts a screen position into sheet pixel coordinates, or null when outside.</summary>
    private (int x, int y)? ToPixel(Point position)
    {
        (int viewX, int viewY, int viewW, int viewH) = ViewArea;
        int zoom = Math.Max(1, Zoom);

        // A non-finite coordinate must be rejected before the conversion. Casting NaN to int
        // yields 0, so a NaN position would silently pass the bounds test below and paint the
        // top-left pixel, which the user never pointed at.
        if (double.IsNaN(position.X) || double.IsNaN(position.Y)
            || double.IsInfinity(position.X) || double.IsInfinity(position.Y))
        {
            return null;
        }

        double scaledX = position.X / zoom;
        double scaledY = position.Y / zoom;

        // Outside the range of int the cast is undefined, so reject before converting
        if (scaledX < int.MinValue || scaledX > int.MaxValue
            || scaledY < int.MinValue || scaledY > int.MaxValue)
        {
            return null;
        }

        int localX = (int)Math.Floor(scaledX);
        int localY = (int)Math.Floor(scaledY);

        if (localX < 0 || localY < 0 || localX >= viewW || localY >= viewH)
        {
            return null;
        }

        return (viewX + localX, viewY + localY);
    }
}

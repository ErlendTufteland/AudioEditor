using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioEditor.Core;

namespace AudioEditor.App.Controls;

/// <summary>
/// Draws a clip's waveform. In edit mode it shows the whole file with a time ruler,
/// draggable trim handles and a cursor, zooms with the mouse wheel and pans when dragged with
/// the middle button. Otherwise it shows
/// only the trimmed part, the way the clip sounds on the timeline. Either way it can draw the
/// beat grid on top; in edit mode the handles and cursor then snap to it.
/// </summary>
/// <remarks>
/// The waveform itself is drawn into a bitmap that is only redrawn when the visible range,
/// size or gain changes. Everything that moves often (cursor, trim shading, handles, fades)
/// is drawn on top, so playback and trimming stay cheap to render.
/// </remarks>
public sealed class WaveformView : FrameworkElement
{
    public const double MinimumLength = 0.01;

    private const double HandleHitWidth = 7;
    private const double RulerHeight = 20;
    private const double BarLaneHeight = 16;
    private const double MinimumViewLength = 0.05;

    /// <summary>How much of the view one wheel notch pans by.</summary>
    private const double PanStep = 0.15;

    /// <summary>Grid lines closer together than this many pixels are left out.</summary>
    private const double MinGridSpacing = 6;
    private const double MinLabelSpacing = 36;

    /// <summary>Bitmaps are made a multiple of this many pixels wide.</summary>
    private const int BitmapWidthStep = 256;

    /// <summary>#4FC3F7, opaque, as a Pbgra32 pixel.</summary>
    private const int WavePixel = unchecked((int)0xFF4FC3F7);

    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(0x16, 0x1A, 0x1F));
    private static readonly Brush RulerBrush = Frozen(Color.FromRgb(0x1E, 0x23, 0x29));
    private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0xB0, 0x16, 0x1A, 0x1F));
    private static readonly Brush RulerTextBrush = Frozen(Color.FromRgb(0x8B, 0x96, 0xA1));
    private static readonly Brush StartBrush = Frozen(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly Brush EndBrush = Frozen(Color.FromRgb(0xEF, 0x53, 0x50));
    private static readonly Brush PositionBrush = Frozen(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush BarLaneBrush = Frozen(Color.FromRgb(0x1A, 0x1F, 0x25));
    private static readonly Brush TempoBrush = Frozen(Color.FromRgb(0xCE, 0x93, 0xD8));
    private static readonly Brush LabelBackdropBrush = Frozen(Color.FromArgb(0xA0, 0x16, 0x1A, 0x1F));
    private static readonly Pen CenterPen = FrozenPen(Color.FromRgb(0x2A, 0x31, 0x38), 1);
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x46, 0x50, 0x5A), 1);
    private static readonly Pen FadePen = FrozenPen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 1.2);
    private static readonly Pen StartPen = new Pen(StartBrush, 2).Frozen();
    private static readonly Pen EndPen = new Pen(EndBrush, 2).Frozen();
    private static readonly Pen PositionPen = new Pen(PositionBrush, 1.5).Frozen();
    private static readonly Pen BarPen = FrozenPen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen BeatPen = FrozenPen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen SubdivisionPen = FrozenPen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen TempoPen = new Pen(TempoBrush, 1.5).Frozen();
    private static readonly Typeface RulerTypeface = new("Segoe UI");
    /// <summary>Pixels for the waveform being drawn; shared, since only the UI thread draws.</summary>
    private static int[] scratch = [];

    private static readonly double[] RulerSteps =
        [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(WaveformPeaks), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty TrimStartProperty = DependencyProperty.Register(
        nameof(TrimStart), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TrimEndProperty = DependencyProperty.Register(
        nameof(TrimEnd), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>The cursor or playhead, in seconds into the source file. NaN hides it.</summary>
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.Register(
        nameof(FadeIn), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FadeOutProperty = DependencyProperty.Register(
        nameof(FadeOut), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Linear gain applied to the drawing, so volume changes are visible.</summary>
    public static readonly DependencyProperty GainProperty = DependencyProperty.Register(
        nameof(Gain), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The beat grid to draw and snap to, in timeline time. Null hides it.</summary>
    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        nameof(Grid), typeof(TempoMap), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the start of the file is on the timeline, which places the grid.</summary>
    public static readonly DependencyProperty TimelineOriginProperty = DependencyProperty.Register(
        nameof(TimelineOrigin), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Distance between the grid lines that dragging snaps to, in beats. 0 does not snap.</summary>
    public static readonly DependencyProperty SnapStepProperty = DependencyProperty.Register(
        nameof(SnapStep), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double viewStart;
    private double viewLength;
    private DragTarget drag;
    private double dragOrigin;
    private double panAnchorX;
    private double panViewStart;
    private WriteableBitmap? waveBitmap;
    private WaveKey waveKey;
    private Drawing? ruler;
    private RulerKey rulerKey;
    private Drawing? gridDrawing;
    private GridKey gridKey;

    public WaveformView()
    {
        // The bitmap is drawn at device resolution, so show it pixel for pixel.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    private enum DragTarget { None, Start, End, Position, Pan }

    private readonly record struct WaveKey(WaveformPeaks Peaks, int Width, int Height, double Start, double Length, double Gain);

    private readonly record struct RulerKey(double Start, double Length, double Width, double PixelsPerDip);

    private readonly record struct GridKey(
        TempoMap Grid, double Origin, double Start, double Length, Rect Area, bool BarLane, double Step, double PixelsPerDip);

    public WaveformPeaks? Peaks
    {
        get => (WaveformPeaks?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double TrimStart
    {
        get => (double)GetValue(TrimStartProperty);
        set => SetValue(TrimStartProperty, value);
    }

    public double TrimEnd
    {
        get => (double)GetValue(TrimEndProperty);
        set => SetValue(TrimEndProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double FadeIn
    {
        get => (double)GetValue(FadeInProperty);
        set => SetValue(FadeInProperty, value);
    }

    public double FadeOut
    {
        get => (double)GetValue(FadeOutProperty);
        set => SetValue(FadeOutProperty, value);
    }

    public double Gain
    {
        get => (double)GetValue(GainProperty);
        set => SetValue(GainProperty, value);
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public TempoMap? Grid
    {
        get => (TempoMap?)GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public double TimelineOrigin
    {
        get => (double)GetValue(TimelineOriginProperty);
        set => SetValue(TimelineOriginProperty, value);
    }

    public double SnapStep
    {
        get => (double)GetValue(SnapStepProperty);
        set => SetValue(SnapStepProperty, value);
    }

    public void ZoomToFit() => SetView(0, Duration);

    public void ZoomIn() => ZoomAround(ZoomAnchor(), 0.5);

    public void ZoomOut() => ZoomAround(ZoomAnchor(), 2);

    /// <summary>Moves the view by wheel notches; positive moves it later.</summary>
    public void Pan(double notches) => SetView(viewStart + notches * viewLength * PanStep, viewLength);

    /// <summary>Shows the trimmed part with a little margin on both sides.</summary>
    public void ZoomToTrim()
    {
        double length = TrimEnd - TrimStart;
        double margin = Math.Max(length * 0.05, 0.05);
        SetView(TrimStart - margin, length + 2 * margin);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var (start, length) = ViewRange();
        var grid = Duration > 0 && length > 0 ? Grid : null;
        bool barLane = IsEditable && grid is not null;
        double top = IsEditable ? RulerHeight + (barLane ? BarLaneHeight : 0) : 0;
        var waveArea = new Rect(0, top, width, Math.Max(0, height - top));
        double middle = Math.Round(waveArea.Top + waveArea.Height / 2) + 0.5;
        dc.DrawLine(CenterPen, new Point(0, middle), new Point(width, middle));

        var peaks = length > 0 && waveArea.Height > 0 ? Peaks : null;
        if (peaks is not null)
            DrawWave(dc, peaks, waveArea, start, length);
        if (grid is not null)
            DrawGrid(dc, grid, waveArea, start, length, barLane);
        if (peaks is not null)
        {
            if (IsEditable)
            {
                // Dim what is trimmed away.
                double x0 = Math.Clamp(ToX(TrimStart), 0, width);
                double x1 = Math.Clamp(ToX(TrimEnd), 0, width);
                dc.DrawRectangle(ShadeBrush, null, new Rect(0, top, x0, waveArea.Height));
                dc.DrawRectangle(ShadeBrush, null, new Rect(x1, top, width - x1, waveArea.Height));
            }
            DrawFades(dc, waveArea);
        }

        if (IsEditable)
        {
            DrawRuler(dc, width, start, length);
            if (Duration > 0)
                DrawHandles(dc, width, height);
        }
        DrawPosition(dc, width, height);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || !IsEditable || Duration <= 0 || ActualWidth <= 0 || drag != DragTarget.None)
            return;

        // Dragging with the middle button grabs the waveform and moves it along.
        drag = DragTarget.Pan;
        dragOrigin = TimelineOrigin;
        panAnchorX = e.GetPosition(this).X;
        panViewStart = viewStart;
        Cursor = Cursors.ScrollWE;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || drag != DragTarget.Pan)
            return;

        ReleaseMouseCapture();
        UpdateCursor(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEditable || Duration <= 0 || drag == DragTarget.Pan)
            return;

        double x = e.GetPosition(this).X;
        dragOrigin = TimelineOrigin;
        drag = HitTest(x);
        if (drag == DragTarget.None)
        {
            // A click places the cursor; the trim handles only move once they are dragged.
            drag = DragTarget.Position;
            ApplyDrag(x);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsEditable || Duration <= 0)
            return;

        double x = e.GetPosition(this).X;
        if (drag != DragTarget.None && IsMouseCaptured)
            ApplyDrag(x);
        else
            UpdateCursor(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (drag == DragTarget.Pan)
            return;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        drag = DragTarget.None;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        drag = DragTarget.None;
        // The grid stood still during the drag; put it where the new trim places it.
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEditable || Duration <= 0 || ActualWidth <= 0)
            return;

        double notches = e.Delta / 120.0;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            Pan(-notches);
        }
        else
        {
            double x = e.GetPosition(this).X;
            ZoomAround(ToTime(x), Math.Pow(0.8, notches), x / ActualWidth);
        }
        e.Handled = true;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((WaveformView)d).ZoomToFit();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)d;
        double previous = (double)e.OldValue;
        double current = (double)e.NewValue;
        if (!view.IsEditable || view.drag != DragTarget.None || double.IsNaN(current) || double.IsNaN(previous))
            return;

        // Turn the page when the playing position runs off the right edge.
        double end = view.viewStart + view.viewLength;
        if (current > end && previous >= view.viewStart && previous <= end)
            view.SetView(current - view.viewLength * 0.05, view.viewLength);
    }

    private (double Start, double Length) ViewRange() => IsEditable
        ? (viewStart, viewLength)
        : (TrimStart, Math.Max(TrimEnd - TrimStart, 1e-6));

    private double ToX(double time)
    {
        var (start, length) = ViewRange();
        return length <= 0 ? 0 : (time - start) / length * ActualWidth;
    }

    private double ToTime(double x)
    {
        var (start, length) = ViewRange();
        return ActualWidth <= 0 ? start : start + x / ActualWidth * length;
    }

    private double ZoomAnchor()
    {
        double position = Position;
        bool visible = !double.IsNaN(position) && position >= viewStart && position <= viewStart + viewLength;
        return visible ? position : viewStart + viewLength / 2;
    }

    private void ZoomAround(double time, double factor, double? anchorFraction = null)
    {
        double length = Math.Clamp(viewLength * factor, Math.Min(MinimumViewLength, Duration), Math.Max(Duration, MinimumViewLength));
        double fraction = anchorFraction ?? (viewLength > 0 ? (time - viewStart) / viewLength : 0.5);
        SetView(time - fraction * length, length);
    }

    private void SetView(double start, double length)
    {
        double duration = Duration;
        if (duration <= 0)
        {
            viewStart = 0;
            viewLength = 0;
        }
        else
        {
            viewLength = Math.Clamp(length, Math.Min(MinimumViewLength, duration), duration);
            viewStart = Math.Clamp(start, 0, duration - viewLength);
        }
        InvalidateVisual();
    }

    private DragTarget HitTest(double x)
    {
        double toStart = Math.Abs(x - ToX(TrimStart));
        double toEnd = Math.Abs(x - ToX(TrimEnd));
        if (Math.Min(toStart, toEnd) > HandleHitWidth)
            return DragTarget.None;
        if (Math.Abs(toStart - toEnd) < 0.5)
            return x < ToX(TrimStart) ? DragTarget.Start : DragTarget.End;
        return toStart < toEnd ? DragTarget.Start : DragTarget.End;
    }

    private void UpdateCursor(double x) => Cursor = HitTest(x) == DragTarget.None ? Cursors.Arrow : Cursors.SizeWE;

    /// <summary>
    /// The grid line nearest to a time in the file, or the start or end of the file if that is
    /// nearer. Holding Alt turns snapping off.
    /// </summary>
    private double Snap(double time)
    {
        if (Grid is not { } grid || SnapStep <= 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            return time;

        // The grid as it was when the drag started, so it does not move along with the start handle.
        double line = grid.Snap(time + dragOrigin, SnapStep) - dragOrigin;
        return new[] { line, 0, Duration }.MinBy(t => Math.Abs(t - time));
    }

    private void ApplyDrag(double x)
    {
        if (drag == DragTarget.Pan)
        {
            // The spot that was grabbed stays under the mouse.
            SetView(panViewStart - (x - panAnchorX) / ActualWidth * viewLength, viewLength);
            return;
        }

        double time = Snap(ToTime(x));
        double duration = Duration;
        switch (drag)
        {
            case DragTarget.Start:
                SetCurrentValue(TrimStartProperty, Math.Clamp(time, 0, Math.Max(0, TrimEnd - MinimumLength)));
                break;
            case DragTarget.End:
                SetCurrentValue(TrimEndProperty, Math.Clamp(time, Math.Min(duration, TrimStart + MinimumLength), duration));
                break;
            case DragTarget.Position:
                SetCurrentValue(PositionProperty, Math.Clamp(time, 0, duration));
                break;
        }
    }

    private void DrawWave(DrawingContext dc, WaveformPeaks peaks, Rect area, double start, double length)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Round(area.Width * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Round(area.Height * dpi.DpiScaleY));
        var key = new WaveKey(peaks, width, height, start, length, Gain);
        if (waveBitmap is null || key != waveKey)
        {
            waveBitmap = RenderWave(peaks, width, height, start, length, dpi);
            waveKey = key;
        }

        // The bitmap can be wider than needed; the unused part is transparent and clipped away.
        dc.PushClip(new RectangleGeometry(area));
        dc.DrawImage(waveBitmap, new Rect(area.X, area.Y, waveBitmap.PixelWidth / dpi.DpiScaleX, area.Height));
        dc.Pop();
    }

    private WriteableBitmap RenderWave(WaveformPeaks peaks, int width, int height, double start, double length, DpiScale dpi)
    {
        // Round the width up so a clip that is being trimmed keeps its bitmap while it changes size.
        int capacity = (width + BitmapWidthStep - 1) / BitmapWidthStep * BitmapWidthStep;
        var bitmap = waveBitmap;
        if (bitmap is null || bitmap.PixelHeight != height || bitmap.PixelWidth < width || bitmap.PixelWidth > capacity + 2 * BitmapWidthStep)
            bitmap = new WriteableBitmap(capacity, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);

        int stride = bitmap.PixelWidth;
        int size = stride * height;
        if (scratch.Length < size)
            scratch = new int[size];
        else
            Array.Clear(scratch, 0, size);

        // One vertical span per pixel column, from the lowest to the highest sample in it.
        double secondsPerColumn = length / width;
        double middle = height / 2.0;
        double half = middle * 0.92;
        float gain = (float)Gain;
        for (int x = 0; x < width; x++)
        {
            double time = start + x * secondsPerColumn;
            var (low, high) = peaks.GetRange(time, time + secondsPerColumn);
            int top = (int)Math.Floor(middle - Math.Clamp(high * gain, -1, 1) * half);
            int bottom = (int)Math.Ceiling(middle - Math.Clamp(low * gain, -1, 1) * half);
            top = Math.Clamp(top, 0, height - 1);
            bottom = Math.Clamp(Math.Max(bottom, top + 1), 1, height);
            for (int y = top; y < bottom; y++)
                scratch[y * stride + x] = WavePixel;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, stride, height), scratch, stride * 4, 0);
        return bitmap;
    }
    private void DrawFades(DrawingContext dc, Rect area)
    {
        double length = TrimEnd - TrimStart;
        double fadeIn = Math.Min(FadeIn, length);
        double fadeOut = Math.Min(FadeOut, length);
        if (fadeIn > 0)
            dc.DrawLine(FadePen, new Point(ToX(TrimStart), area.Bottom), new Point(ToX(TrimStart + fadeIn), area.Top + 1));
        if (fadeOut > 0)
            dc.DrawLine(FadePen, new Point(ToX(TrimEnd - fadeOut), area.Top + 1), new Point(ToX(TrimEnd), area.Bottom));
    }

    private void DrawGrid(DrawingContext dc, TempoMap grid, Rect area, double start, double length, bool barLane)
    {
        // While a handle is dragged the clip moves under the grid; keep the grid where it was.
        double origin = drag == DragTarget.None ? TimelineOrigin : dragOrigin;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = new GridKey(grid, origin, start, length, area, barLane, SnapStep, pixelsPerDip);
        if (gridDrawing is null || key != gridKey)
        {
            gridDrawing = BuildGrid(key);
            gridKey = key;
        }
        dc.DrawDrawing(gridDrawing);
    }

    private static Drawing BuildGrid(GridKey key)
    {
        var (grid, origin, start, length, area, barLane, snapStep, pixelsPerDip) = key;
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // In the clip editor the bar numbers get a lane of their own under the time ruler.
            double top = area.Top;
            if (barLane)
            {
                top = RulerHeight;
                dc.DrawRectangle(BarLaneBrush, null, new Rect(0, top, area.Width, BarLaneHeight));
            }

            double from = start + origin;
            double pixelsPerSecond = area.Width / length;
            double pixelsPerBeat = 60 / grid.HighestBpm * pixelsPerSecond;
            double pixelsPerBar = pixelsPerBeat * grid.BeatsPerBar;

            // The finest lines that are not too close together where the tempo is fastest.
            double step = snapStep is > 0 and < 1 && snapStep * pixelsPerBeat >= MinGridSpacing ? snapStep
                : pixelsPerBeat >= MinGridSpacing ? 1
                : grid.BeatsPerBar * PowerOfTwoAtLeast(MinGridSpacing / pixelsPerBar);
            int labelEvery = PowerOfTwoAtLeast(MinLabelSpacing / pixelsPerBar);

            foreach (var (beat, time) in grid.Lines(from, from + length, step))
            {
                double bars = beat / grid.BeatsPerBar;
                bool isBar = IsWhole(bars);
                int bar = (int)Math.Round(bars) + 1;
                if (isBar && bar > 1 && grid.ChangeAt(bar) is not null)
                    continue;

                double x = Math.Round((time - from) * pixelsPerSecond) + 0.5;
                var pen = isBar ? BarPen : IsWhole(beat) ? BeatPen : SubdivisionPen;
                dc.DrawLine(pen, new Point(x, top), new Point(x, area.Bottom));
                if (isBar && (bar - 1) % labelEvery == 0)
                    DrawGridLabel(dc, bar.ToString(CultureInfo.InvariantCulture), RulerTextBrush, x, top, barLane, pixelsPerDip);
            }

            // Tempo changes are always shown, however far the view is zoomed out.
            foreach (var change in grid.Changes.Skip(1))
            {
                double x = Math.Round((grid.BarStart(change.Bar) - from) * pixelsPerSecond) + 0.5;
                if (x < 0 || x > area.Width)
                    continue;
                dc.DrawLine(TempoPen, new Point(x, top), new Point(x, area.Bottom));
                string label = $"{change.Bar} · {change.Bpm.ToString("0.##", CultureInfo.InvariantCulture)} BPM";
                DrawGridLabel(dc, label, TempoBrush, x, top, barLane, pixelsPerDip);
            }

            drawing.ClipGeometry = new RectangleGeometry(new Rect(0, top, area.Width, Math.Max(0, area.Bottom - top)));
        }
        drawing.Freeze();
        return drawing;
    }

    private static void DrawGridLabel(DrawingContext dc, string label, Brush brush, double x, double top, bool barLane, double pixelsPerDip)
    {
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            RulerTypeface, barLane ? 10.5 : 9.5, brush, pixelsPerDip);
        var origin = new Point(x + 3, top + (barLane ? 1 : 2));
        // Over the waveform a dark backdrop keeps the label readable.
        if (!barLane)
            dc.DrawRectangle(LabelBackdropBrush, null, new Rect(origin.X - 1, origin.Y, text.Width + 2, text.Height));
        dc.DrawText(text, origin);
    }

    private static bool IsWhole(double value) => Math.Abs(value - Math.Round(value)) < 1e-6;

    /// <summary>The smallest power of two that is at least <paramref name="value"/>.</summary>
    private static int PowerOfTwoAtLeast(double value)
    {
        int result = 1;
        while (result < value && result < 1 << 30)
            result *= 2;
        return result;
    }

    private void DrawRuler(DrawingContext dc, double width, double start, double length)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = new RulerKey(start, length, width, pixelsPerDip);
        if (ruler is null || key != rulerKey)
        {
            ruler = BuildRuler(width, start, length, pixelsPerDip);
            rulerKey = key;
        }
        dc.DrawDrawing(ruler);
    }

    private Drawing BuildRuler(double width, double start, double length, double pixelsPerDip)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, width, RulerHeight));
            if (length > 0)
            {
                double minimumStep = length / width * 90;
                double step = RulerSteps.FirstOrDefault(s => s >= minimumStep, RulerSteps[^1]);
                double minorStep = step / 5;

                for (long i = (long)Math.Ceiling(start / minorStep); i * minorStep <= start + length; i++)
                {
                    double time = i * minorStep;
                    double x = Math.Round((time - start) / length * width) + 0.5;
                    bool major = i % 5 == 0;
                    dc.DrawLine(TickPen, new Point(x, RulerHeight - (major ? 8 : 4)), new Point(x, RulerHeight));
                    if (!major)
                        continue;

                    string label = step >= 1 ? FormatWholeSeconds(time) : TimeFormat.Format(time);
                    var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        RulerTypeface, 10.5, RulerTextBrush, pixelsPerDip);
                    dc.DrawText(text, new Point(x + 3, 2));
                }
            }
        }
        drawing.Freeze();
        return drawing;
    }
    private void DrawHandles(DrawingContext dc, double width, double height)
    {
        double start = Math.Round(ToX(TrimStart));
        if (start >= -10 && start <= width + 10)
        {
            dc.DrawLine(StartPen, new Point(start, 0), new Point(start, height));
            dc.DrawRectangle(StartBrush, null, new Rect(start, 0, 9, 14));
        }

        double end = Math.Round(ToX(TrimEnd));
        if (end >= -10 && end <= width + 10)
        {
            dc.DrawLine(EndPen, new Point(end, 0), new Point(end, height));
            dc.DrawRectangle(EndBrush, null, new Rect(end - 9, 0, 9, 14));
        }
    }

    private void DrawPosition(DrawingContext dc, double width, double height)
    {
        double position = Position;
        if (double.IsNaN(position))
            return;

        double x = Math.Round(ToX(position)) + 0.5;
        if (x < -1 || x > width + 1)
            return;

        dc.DrawLine(PositionPen, new Point(x, 0), new Point(x, height));
        var marker = new StreamGeometry();
        using (var context = marker.Open())
        {
            context.BeginFigure(new Point(x - 5, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(x + 5, 0), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(x, 7), isStroked: false, isSmoothJoin: false);
        }
        marker.Freeze();
        dc.DrawGeometry(PositionBrush, null, marker);
    }

    private static string FormatWholeSeconds(double seconds)
    {
        long total = (long)Math.Round(seconds);
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness) => new Pen(Frozen(color), thickness).Frozen();
}

internal static class FreezableExtensions
{
    public static T Frozen<T>(this T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

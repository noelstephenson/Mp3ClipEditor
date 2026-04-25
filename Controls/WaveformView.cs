using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Mp3ClipEditorTagger.Controls;

public sealed class WaveformView : FrameworkElement
{
    private const double HandleHitWidth = 14;
    private const double MarkerWidth = 18;
    private const double MarkerHeight = 14;
    private const double MinGapSeconds = 0.1;
    private const double HorizontalPadding = 12;
    private const double VerticalPadding = 8;
    private const double DragThreshold = 4;

    private DragMode _dragMode;
    private bool _isPendingSelectionClick;
    private Point _dragStartPoint;
    private double _dragStartSelectionStartSeconds;
    private double _dragStartSelectionEndSeconds;

    public event EventHandler<WaveformSeekRequestedEventArgs>? SeekRequested;

    public static readonly DependencyProperty PeaksProperty =
        DependencyProperty.Register(
            nameof(Peaks),
            typeof(float[]),
            typeof(WaveformView),
            new FrameworkPropertyMetadata(Array.Empty<float>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationSecondsProperty =
        DependencyProperty.Register(
            nameof(DurationSeconds),
            typeof(double),
            typeof(WaveformView),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartSecondsProperty =
        DependencyProperty.Register(
            nameof(SelectionStartSeconds),
            typeof(double),
            typeof(WaveformView),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectionEndSecondsProperty =
        DependencyProperty.Register(
            nameof(SelectionEndSeconds),
            typeof(double),
            typeof(WaveformView),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlayheadRatioProperty =
        DependencyProperty.Register(
            nameof(PlayheadRatio),
            typeof(double?),
            typeof(WaveformView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[] Peaks
    {
        get => (float[])GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    public double SelectionStartSeconds
    {
        get => (double)GetValue(SelectionStartSecondsProperty);
        set => SetValue(SelectionStartSecondsProperty, value);
    }

    public double SelectionEndSeconds
    {
        get => (double)GetValue(SelectionEndSecondsProperty);
        set => SetValue(SelectionEndSecondsProperty, value);
    }

    public double? PlayheadRatio
    {
        get => (double?)GetValue(PlayheadRatioProperty);
        set => SetValue(PlayheadRatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height;
        return new Size(width, Math.Max(220, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var bounds = new Rect(new Point(0, 0), RenderSize);
        var backgroundBrush = new SolidColorBrush(Color.FromRgb(24, 20, 16));
        var roundedBounds = new RectangleGeometry(bounds, 14, 14);
        drawingContext.DrawGeometry(backgroundBrush, new Pen(backgroundBrush, 1), roundedBounds);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (Peaks is null || Peaks.Length == 0 || DurationSeconds <= 0)
        {
            DrawCenteredText(drawingContext, bounds, "Select a track to load its waveform.");
            return;
        }

        var innerBounds = new Rect(
            HorizontalPadding,
            VerticalPadding,
            Math.Max(1, bounds.Width - (HorizontalPadding * 2)),
            Math.Max(1, bounds.Height - (VerticalPadding * 2)));

        var startRatio = GetClampedRatio(SelectionStartSeconds);
        var endRatio = GetClampedRatio(SelectionEndSeconds);
        var selectionStartX = innerBounds.Left + (startRatio * innerBounds.Width);
        var selectionEndX = innerBounds.Left + (endRatio * innerBounds.Width);

        var inactiveOverlay = new SolidColorBrush(Color.FromArgb(150, 7, 6, 5));
        var selectionBrush = new SolidColorBrush(Color.FromArgb(85, 255, 218, 194));
        var waveBrush = new SolidColorBrush(Color.FromRgb(255, 215, 176));
        var handleBrush = new SolidColorBrush(Color.FromRgb(201, 92, 54));
        var handlePen = new Pen(handleBrush, 2);

        drawingContext.PushClip(roundedBounds);

        drawingContext.DrawRectangle(inactiveOverlay, null, new Rect(innerBounds.Left, innerBounds.Top, Math.Max(0, selectionStartX - innerBounds.Left), innerBounds.Height));
        drawingContext.DrawRectangle(inactiveOverlay, null, new Rect(selectionEndX, innerBounds.Top, Math.Max(0, innerBounds.Right - selectionEndX), innerBounds.Height));
        drawingContext.DrawRectangle(selectionBrush, null, new Rect(selectionStartX, innerBounds.Top, Math.Max(0, selectionEndX - selectionStartX), innerBounds.Height));

        var markerReservedHeight = MarkerHeight + 10;
        var waveformTop = innerBounds.Top;
        var waveformBottom = Math.Max(waveformTop + 20, innerBounds.Bottom - markerReservedHeight);
        var waveformHeight = waveformBottom - waveformTop;
        var centerY = waveformTop + (waveformHeight / 2d);
        var maxWaveHeight = Math.Max(16, (waveformHeight / 2d) - 10);
        var waveBounds = new Rect(innerBounds.Left, waveformTop, innerBounds.Width, waveformHeight);
        drawingContext.PushClip(new RectangleGeometry(waveBounds));

        var spacing = Peaks.Length <= 1
            ? innerBounds.Width
            : innerBounds.Width / (Peaks.Length - 1d);
        var wavePen = new Pen(waveBrush, Math.Max(1, spacing * 0.6));

        for (var index = 0; index < Peaks.Length; index++)
        {
            var x = GetWaveformX(index, Peaks.Length, innerBounds);
            var amplitude = Math.Clamp(Peaks[index], 0f, 1f);
            var halfHeight = amplitude * (float)maxWaveHeight;
            drawingContext.DrawLine(
                wavePen,
                new Point(x, centerY - halfHeight),
                new Point(x, centerY + halfHeight));
        }

        drawingContext.Pop();

        drawingContext.DrawLine(handlePen, new Point(selectionStartX, 0), new Point(selectionStartX, waveformBottom));
        drawingContext.DrawLine(handlePen, new Point(selectionEndX, 0), new Point(selectionEndX, waveformBottom));
        DrawHandleMarker(drawingContext, selectionStartX, bounds.Height, handleBrush);
        DrawHandleMarker(drawingContext, selectionEndX, bounds.Height, handleBrush);

        if (PlayheadRatio is double playhead)
        {
            var playheadX = innerBounds.Left + (Math.Clamp(playhead, 0, 1) * innerBounds.Width);
            drawingContext.DrawLine(
                new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1.5),
                new Point(playheadX, 0),
                new Point(playheadX, waveformBottom));
        }

        drawingContext.Pop();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (Peaks is null || Peaks.Length == 0 || DurationSeconds <= 0)
        {
            return;
        }

        var point = e.GetPosition(this);
        var startX = HorizontalPadding + (GetClampedRatio(SelectionStartSeconds) * Math.Max(1, RenderSize.Width - (HorizontalPadding * 2)));
        var endX = HorizontalPadding + (GetClampedRatio(SelectionEndSeconds) * Math.Max(1, RenderSize.Width - (HorizontalPadding * 2)));
        var clickedSeconds = GetSecondsFromPoint(point.X);

        if (IsPointInsideMarker(point, startX, RenderSize.Height))
        {
            Focus();
            CaptureMouse();
            _dragMode = DragMode.StartHandle;
            UpdateSelectionFromPoint(point.X);
            e.Handled = true;
            return;
        }

        if (IsPointInsideMarker(point, endX, RenderSize.Height))
        {
            Focus();
            CaptureMouse();
            _dragMode = DragMode.EndHandle;
            UpdateSelectionFromPoint(point.X);
            e.Handled = true;
            return;
        }

        if (clickedSeconds >= SelectionStartSeconds && clickedSeconds <= SelectionEndSeconds)
        {
            Focus();
            CaptureMouse();
            _dragMode = DragMode.PendingSelection;
            _isPendingSelectionClick = true;
            _dragStartPoint = point;
            _dragStartSelectionStartSeconds = SelectionStartSeconds;
            _dragStartSelectionEndSeconds = SelectionEndSeconds;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragMode == DragMode.None)
        {
            var point = e.GetPosition(this);
            var startX = HorizontalPadding + (GetClampedRatio(SelectionStartSeconds) * Math.Max(1, RenderSize.Width - (HorizontalPadding * 2)));
            var endX = HorizontalPadding + (GetClampedRatio(SelectionEndSeconds) * Math.Max(1, RenderSize.Width - (HorizontalPadding * 2)));
            var hoveredSeconds = GetSecondsFromPoint(point.X);
            Cursor = IsPointInsideMarker(point, startX, RenderSize.Height) || IsPointInsideMarker(point, endX, RenderSize.Height)
                ? Cursors.SizeWE
                : hoveredSeconds >= SelectionStartSeconds && hoveredSeconds <= SelectionEndSeconds
                    ? Cursors.SizeAll
                    : Cursors.Arrow;
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (_dragMode == DragMode.PendingSelection)
        {
            if (Math.Abs(currentPoint.X - _dragStartPoint.X) >= DragThreshold)
            {
                _dragMode = DragMode.SelectionRange;
                _isPendingSelectionClick = false;
                Cursor = Cursors.SizeAll;
            }
            else
            {
                return;
            }
        }

        UpdateSelectionFromPoint(currentPoint.X);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragMode != DragMode.None)
        {
            var point = e.GetPosition(this);
            if (_dragMode == DragMode.PendingSelection && _isPendingSelectionClick)
            {
                var clickedSeconds = GetSecondsFromPoint(point.X);
                if (clickedSeconds >= SelectionStartSeconds && clickedSeconds <= SelectionEndSeconds)
                {
                    SeekRequested?.Invoke(this, new WaveformSeekRequestedEventArgs(clickedSeconds));
                }
            }

            ReleaseMouseCapture();
            _dragMode = DragMode.None;
            _isPendingSelectionClick = false;
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }
    }

    private void UpdateSelectionFromPoint(double x)
    {
        if (RenderSize.Width <= 0 || DurationSeconds <= 0)
        {
            return;
        }

        var drawableWidth = Math.Max(1, RenderSize.Width - (HorizontalPadding * 2));
        var ratio = Math.Clamp((x - HorizontalPadding) / drawableWidth, 0, 1);
        var seconds = ratio * DurationSeconds;

        if (_dragMode == DragMode.StartHandle)
        {
            var maxStartSeconds = Math.Max(0, SelectionEndSeconds - MinGapSeconds);
            if (PlayheadRatio is double playheadRatio)
            {
                var playheadSeconds = Math.Clamp(playheadRatio, 0, 1) * DurationSeconds;
                maxStartSeconds = Math.Min(maxStartSeconds, Math.Max(0, playheadSeconds - MinGapSeconds));
            }

            SelectionStartSeconds = Math.Clamp(seconds, 0, maxStartSeconds);
        }
        else if (_dragMode == DragMode.EndHandle)
        {
            SelectionEndSeconds = Math.Clamp(seconds, SelectionStartSeconds + MinGapSeconds, DurationSeconds);
        }
        else if (_dragMode == DragMode.SelectionRange)
        {
            var clipLength = _dragStartSelectionEndSeconds - _dragStartSelectionStartSeconds;
            var deltaSeconds = GetSecondsFromPoint(x) - GetSecondsFromPoint(_dragStartPoint.X);
            var maxStartSeconds = Math.Max(0d, DurationSeconds - clipLength);
            if (PlayheadRatio is double playheadRatio)
            {
                var playheadSeconds = Math.Clamp(playheadRatio, 0, 1) * DurationSeconds;
                maxStartSeconds = Math.Min(maxStartSeconds, Math.Max(0d, playheadSeconds - MinGapSeconds));
            }

            var newStartSeconds = Math.Clamp(_dragStartSelectionStartSeconds + deltaSeconds, 0d, maxStartSeconds);
            SelectionStartSeconds = newStartSeconds;
            SelectionEndSeconds = Math.Clamp(newStartSeconds + clipLength, newStartSeconds + MinGapSeconds, DurationSeconds);
        }
    }

    private double GetSecondsFromPoint(double x)
    {
        var drawableWidth = Math.Max(1, RenderSize.Width - (HorizontalPadding * 2));
        var ratio = Math.Clamp((x - HorizontalPadding) / drawableWidth, 0, 1);
        return ratio * DurationSeconds;
    }

    private double GetClampedRatio(double seconds)
    {
        if (DurationSeconds <= 0)
        {
            return 0;
        }

        return Math.Clamp(seconds / DurationSeconds, 0, 1);
    }

    private static bool IsPointInsideMarker(Point point, double markerCenterX, double controlHeight)
    {
        var halfWidth = MarkerWidth / 2d;
        var top = controlHeight - MarkerHeight - 4;
        return point.X >= markerCenterX - halfWidth
            && point.X <= markerCenterX + halfWidth
            && point.Y >= top
            && point.Y <= top + MarkerHeight;
    }

    private static void DrawHandleMarker(DrawingContext drawingContext, double x, double height, Brush brush)
    {
        var halfWidth = MarkerWidth / 2d;
        var top = height - MarkerHeight - 4;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x - halfWidth, top), true, true);
            context.LineTo(new Point(x + halfWidth, top), true, false);
            context.LineTo(new Point(x, top + MarkerHeight), true, false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(brush, null, geometry);
    }

    private static void DrawCenteredText(DrawingContext drawingContext, Rect bounds, string text)
    {
        var dpi = Application.Current?.MainWindow is not null
            ? VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip
            : 1d;

        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            new SolidColorBrush(Color.FromRgb(224, 210, 194)),
            dpi);

        var location = new Point((bounds.Width - formattedText.Width) / 2d, (bounds.Height - formattedText.Height) / 2d);
        drawingContext.DrawText(formattedText, location);
    }

    private static double GetWaveformX(int index, int peakCount, Rect innerBounds)
    {
        if (peakCount <= 1)
        {
            return innerBounds.Left;
        }

        var ratio = index / (peakCount - 1d);
        return innerBounds.Left + (ratio * innerBounds.Width);
    }

    private enum DragMode
    {
        None,
        PendingSelection,
        SelectionRange,
        StartHandle,
        EndHandle
    }
}

public sealed class WaveformSeekRequestedEventArgs(double seconds) : EventArgs
{
    public double Seconds { get; } = seconds;
}

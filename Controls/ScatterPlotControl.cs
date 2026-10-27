using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public class ScatterPlotControl : CachedPlotControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IList<TestValue>), typeof(ScatterPlotControl),
            new FrameworkPropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty LslProperty =
        DependencyProperty.Register(nameof(Lsl), typeof(double?), typeof(ScatterPlotControl),
            new FrameworkPropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty UslProperty =
        DependencyProperty.Register(nameof(Usl), typeof(double?), typeof(ScatterPlotControl),
            new FrameworkPropertyMetadata(null, OnDataChanged));

    public IList<TestValue>? Values
    {
        get => (IList<TestValue>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double? Lsl { get => (double?)GetValue(LslProperty); set => SetValue(LslProperty, value); }
    public double? Usl { get => (double?)GetValue(UslProperty); set => SetValue(UslProperty, value); }

    public double? Mean { get; private set; }
    public double? Sigma { get; private set; }
    public bool ShowLsl { get; private set; } = true;
    public bool ShowUsl { get; private set; } = true;
    public bool ShowMean { get; private set; }
    public bool ShowThreeSigma { get; private set; }

    // Reuse the detail worker's statistics. Drawing reference lines never scans the samples.
    public void SetReferenceLines(double? mean, double? sigma, bool showLsl, bool showUsl, bool showMean, bool showThreeSigma)
    {
        if (mean is double m && !double.IsFinite(m)) mean = null;
        if (sigma is double s && (!double.IsFinite(s) || s < 0)) sigma = null;
        if ((Mean, Sigma, ShowLsl, ShowUsl, ShowMean, ShowThreeSigma) ==
            (mean, sigma, showLsl, showUsl, showMean, showThreeSigma)) return;
        (Mean, Sigma, ShowLsl, ShowUsl, ShowMean, ShowThreeSigma) =
            (mean, sigma, showLsl, showUsl, showMean, showThreeSigma);
        InvalidatePlot();
    }

    public static readonly DependencyProperty IsYAxisLockedProperty =
        DependencyProperty.Register(nameof(IsYAxisLocked), typeof(bool), typeof(ScatterPlotControl),
            new FrameworkPropertyMetadata(false, (d, _) => ((ScatterPlotControl)d).UpdateYAxisLock()));

    public bool IsYAxisLocked
    {
        get => (bool)GetValue(IsYAxisLockedProperty);
        set => SetValue(IsYAxisLockedProperty, value);
    }

    private (double Min, double Max)? _lockedYRange;

    public static readonly DependencyProperty UseLimitRangeProperty = DependencyProperty.Register(
        nameof(UseLimitRange), typeof(bool), typeof(ScatterPlotControl), new FrameworkPropertyMetadata(false, (d, _) =>
        {
            var plot = (ScatterPlotControl)d;
            plot._lockedYRange = null;
            plot.UpdateVerticalRange();
            plot.InvalidatePlot();
            plot._overlayLayer.InvalidateVisual();
        }));
    public bool UseLimitRange { get => (bool)GetValue(UseLimitRangeProperty); set => SetValue(UseLimitRangeProperty, value); }

    public static (double Min, double Max)? LimitRange(double? lsl, double? usl)
    {
        if (lsl is not double low || usl is not double high || !double.IsFinite(low) || !double.IsFinite(high) || high <= low) return null;
        double half = (high - low) * .5;
        double min = low - half, max = high + half;
        return double.IsFinite(min) && double.IsFinite(max) ? (min, max) : null;
    }

    private void UpdateYAxisLock()
    {
        if (IsYAxisLocked && _pointCount > 0)
        {
            double min = _dataMinY, max = _dataMaxY;
            if (_hasPlot) GetVisibleRange(out _, out _, out min, out max);
            _lockedYRange = (min, max);
        }
        else if (!IsYAxisLocked) _lockedYRange = null;
        UpdateVerticalRange();
        InvalidatePlot();
        _overlayLayer.InvalidateVisual();
    }

    private void UpdateVerticalRange()
    {
        _scaleY = 1; _offsetY = 0;
        if (_pointCount == 0) return; // Keep the lock across the temporary empty series while loading another item.
        double pad = (_series.Maximum - _series.Minimum) * 0.05;
        if (pad <= 0) pad = Math.Max(Math.Abs(_series.Minimum) * 0.01, 1e-12);
        var range = (_series.Minimum - pad, _series.Maximum + pad);
        if (UseLimitRange && LimitRange(Lsl, Usl) is { } limits) range = limits;
        if (IsYAxisLocked)
        {
            _lockedYRange ??= range; // A lock enabled before selecting a test captures its first range.
            range = _lockedYRange.Value;
        }
        (_dataMinY, _dataMaxY) = range;
    }

    private static readonly Color PointColor = Color.FromRgb(30, 120, 200);
    private static readonly Color GroupDarkColor = Color.FromRgb(31, 95, 165);
    private static readonly Color GroupLightColor = Color.FromRgb(128, 185, 231);
    private static readonly Color MarkDarkColor = Color.FromRgb(22, 125, 62);
    private static readonly Color MarkLightColor = Color.FromRgb(116, 209, 144);
    private static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(48, 22, 125, 62)));
    private static readonly Pen SelectionPen = Freeze(new Pen(Freeze(new SolidColorBrush(MarkDarkColor)), 1));
    private static readonly Color OutColor = Color.FromRgb(220, 60, 60);

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(210, 210, 210)));
    private static readonly Brush CrossBrush = Freeze(new SolidColorBrush(Color.FromRgb(168, 168, 168)));
    private static readonly Brush LabelBg = Freeze(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
    private static readonly Pen AxisPen = Freeze(new Pen(Brushes.Black, 1));
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Pen GroupPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(218, 223, 230))), 1));
    private static readonly Pen CrossPen = Freeze(new Pen(CrossBrush, 1));
    private static readonly Pen RedPen = Freeze(new Pen(Brushes.Red, 1.5));
    private static readonly Pen MeanPen = Freeze(new Pen(Brushes.DarkGreen, 2));
    private static readonly Pen SigmaPen = Freeze(new Pen(Brushes.Orange, 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen LabelBorder = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(180, 180, 180))), 0.8));
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private const double PointRadius = 1.35;
    private const string InteractionHelp = "滚轮缩放 · 左键平移 · 右键拖动框选 · 双击左键重置";
    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    public string Unit { get; private set; } = "";
    public string HorizontalAxisTitle => _series.Order switch
        { ScatterOrder.Site => "Site", ScatterOrder.Wafer => "Wafer", _ => "样本序号" };

    private static T Freeze<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }

    private readonly OverlayLayer _overlayLayer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly ScatterChipMarks _chipMarks = new();
    private bool[] _markedPoints = Array.Empty<bool>();
    private Point? _rightStart;
    private Point _rightPosition;
    private bool _selecting;
    private long _markRevision;
    public int ChipMarkCount => _chipMarks.Count;
    public event EventHandler? ChipMarksChanged;

    public void ClearChipMarks()
    {
        _markRevision++;
        CancelRightGesture();
        _chipMarks.Clear();
        _markedPoints = Array.Empty<bool>();
        // Raster scratch belongs to the renderer. It is reset at the next frame;
        // clearing marks must not change a buffer an in-progress frame enumerates.
        InvalidatePlot();
        ChipMarksChanged?.Invoke(this, EventArgs.Empty);
    }
    private int _batch;
    private long _renderCount;

    private double _scaleX = 1.0;
    private double _scaleY = 1.0;
    private double _offsetX;
    private double _offsetY;
    private Point? _dragStart;
    private double _dataMinX, _dataMaxX, _dataMinY, _dataMaxY;
    private double _left, _top, _plotW, _plotH;
    private bool _hasPlot;
    private Point? _hover;
    private ScatterSeries _series = ScatterSeries.Empty;
    private int _pointCount;
    private FormattedText[] _groupLabels = Array.Empty<FormattedText>();
    private double _groupLabelDpi;

    // Real-time rasterization buffers (reused across frames, zero resize stretching).
    private WriteableBitmap? _pointBitmap;
    private double _pointBitmapDpi;
    private uint[] _pixelBuffer = Array.Empty<uint>();
    private byte[] _occupancy = Array.Empty<byte>();
    private readonly List<(int X, int Y)> _failCoords = new(256);
    private readonly List<(int X, int Y, bool Light)> _markedCoords = new(256);
    private double _stampDpi;
    private int _stampRadius;
    private byte[] _stampAlpha = Array.Empty<byte>();
    private uint[] _stampNormal = Array.Empty<uint>();
    private uint[] _stampDark = Array.Empty<uint>();
    private uint[] _stampLight = Array.Empty<uint>();
    private uint[] _stampOut = Array.Empty<uint>();
    private uint[] _stampMarkDark = Array.Empty<uint>();
    private uint[] _stampMarkLight = Array.Empty<uint>();
    private const int MaxRasterPixels = 4_000_000;

    public ScatterPlotControl()
    {
        // Disable CachedPlotControl's static bitmap stretching; render every frame live.
        UseBitmapCache = false;
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        // Native ToolTip popups can activate persistent automation work on the main grid.
        // Keep help in the existing lightweight overlay alongside the cursor coordinates.
        AutomationProperties.SetHelpText(this,
            "横轴：样本序号（从 1 开始）；纵轴：测量值。绘制视野内全部有效测量点，不抽样、不聚合。" + InteractionHelp);

        _overlayLayer = new OverlayLayer(this) { IsHitTestVisible = false };
        AddVisualChild(_overlayLayer);
        AddLogicalChild(_overlayLayer);
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            _overlayLayer.InvalidateVisual();
        };
        LostMouseCapture += (_, _) => { _dragStart = null; CancelRightGesture(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _rightStart.HasValue) { CancelRightGesture(); e.Handled = true; }
        };
        Unloaded += (_, _) =>
        {
            _markRevision++;
            CancelRightGesture();
            _hoverTimer.Stop();
            _hover = null;
            ReleaseRasterBuffers();
            _overlayLayer.InvalidateVisual();
        };

        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseRightButtonDown += OnMouseRightButtonDown;
        MouseRightButtonUp += OnMouseRightButtonUp;
    }

    private void ReleaseRasterBuffers()
    {
        _pointBitmap = null;
        _pointBitmapDpi = 0;
        _pixelBuffer = Array.Empty<uint>();
        _occupancy = Array.Empty<byte>();
        _markedCoords.Clear();
        if (_markedCoords.Capacity > 256) _markedCoords.Capacity = 256;
        _failCoords.Clear();
        if (_failCoords.Capacity > 256) _failCoords.Capacity = 256;
    }

    public void SetData(IList<TestValue>? values, double? lsl, double? usl, string unit = "")
    {
        Unit = unit ?? "";
        _batch++;
        Values = values;
        Lsl = lsl;
        Usl = usl;
        _batch--;
        RebuildPointCache();
    }

    public void SetSeries(ScatterSeries series, double? lsl, double? usl, string unit)
    {
        if (_lockedYRange is { } locked && MeasurementDisplay.ConversionFactor(Unit, unit) is double ratio)
            _lockedYRange = double.IsFinite(locked.Min * ratio) && double.IsFinite(locked.Max * ratio)
                ? (locked.Min * ratio, locked.Max * ratio) : null;
        _batch++;
        Values = null;
        Lsl = lsl;
        Usl = usl;
        Unit = unit ?? "";
        _batch--;
        ApplySeries(series);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ScatterPlotControl)d;
        if (c._batch != 0) return;
        c.RebuildPointCache();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _overlayLayer;

    protected override bool HasPlotContent => _pointCount > 0;

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        _overlayLayer.Measure(new Size(Math.Max(0, w), Math.Max(0, h)));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        const double left = ChartAxis.PlotLeft, right = ChartAxis.PlotRight, bottom = ChartAxis.PlotBottom;
        double top = string.IsNullOrWhiteSpace(Title) ? 32 : Math.Max(32, ChartAxis.HeadingFontSize(finalSize) + 15);
        double nextW = Math.Max(0, finalSize.Width - left - right);
        double nextH = Math.Max(0, finalSize.Height - top - bottom);
        bool sizeChanged = _plotW != nextW || _plotH != nextH;
        if (_plotW > 0) _offsetX *= nextW / _plotW;
        if (_plotH > 0) _offsetY *= nextH / _plotH;
        _left = left;
        _top = top;
        _plotW = nextW;
        _plotH = nextH;
        _hasPlot = _plotW >= 20 && _plotH >= 20 && _pointCount > 0;
        _overlayLayer.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        if (sizeChanged)
        {
            CancelRightGesture();
            InvalidateVisual();
            _overlayLayer.InvalidateVisual();
        }
        return finalSize;
    }

    private void ResetZoom()
    {
        _scaleX = 1; _offsetX = 0;
        if (!IsYAxisLocked) UpdateVerticalRange();
        InvalidatePlot();
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CancelRightGesture();
        Point mouse = e.GetPosition(this);
        if (_pointCount == 0 || !IsInPlot(mouse)) return;
        ZoomAt(mouse, e.Delta);
        e.Handled = true;
    }

    private void ZoomAt(Point mouse, int delta)
    {
        double nextScale = Math.Clamp(_scaleX * (delta > 0 ? 1.15 : 1.0 / 1.15), 0.001, 100000);
        double zoomFactor = nextScale / _scaleX;

        _offsetX = mouse.X - _left - (mouse.X - _left - _offsetX) * zoomFactor;
        if (!IsYAxisLocked)
        {
            double nextScaleY = Math.Clamp(_scaleY * zoomFactor, 0.001, 100000);
            double originY = _top + _plotH;
            _offsetY = originY - mouse.Y - (originY - mouse.Y - _offsetY) * (nextScaleY / _scaleY);
            _scaleY = nextScaleY;
        }
        _scaleX = nextScale;
        _hover = mouse;
        InvalidatePlot();
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelRightGesture();
        if (e.ClickCount >= 2)
        {
            ResetZoom();
            _dragStart = null;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (StartRightGesture(e.GetPosition(this))) e.Handled = true;
    }

    private bool StartRightGesture(Point start)
    {
        CancelRightGesture();
        _dragStart = null;
        if (!IsInPlot(start)) return false;
        Focus();
        _rightStart = _rightPosition = start;
        if (CaptureMouse()) BeginBoxSelection();
        else CancelRightGesture();
        return true;
    }

    private void BeginBoxSelection()
    {
        if (!_rightStart.HasValue || !_hasPlot) return;
        _selecting = true;
        _hover = null;
        _overlayLayer.InvalidateVisual();
    }

    private Rect SelectionRectangle()
    {
        Point Clamp(Point p) => new(Math.Clamp(p.X, _left, _left + _plotW), Math.Clamp(p.Y, _top, _top + _plotH));
        return _rightStart is Point start ? new Rect(Clamp(start), Clamp(_rightPosition)) : Rect.Empty;
    }

    private void CancelRightGesture()
    {
        bool release = _rightStart.HasValue;
        _rightStart = null;
        _selecting = false;
        if (release && IsMouseCaptured) ReleaseMouseCapture();
        _overlayLayer.InvalidateVisual();
    }

    private void CompleteRightGesture(Point end)
    {
        if (_rightStart is not Point start) return;
        _rightPosition = end;
        var rectangle = SelectionRectangle();
        bool select = _selecting;
        long revision = _markRevision;
        var series = _series;
        CancelRightGesture();
        // Releasing capture can reenter ClearChipMarks, SetSeries or Unloaded.
        // Never commit a selection that was cleared/replaced during that event.
        if (revision != _markRevision || !ReferenceEquals(series, _series)) return;
        if (select)
        {
            if (rectangle.Width < 2 || rectangle.Height < 2) return;
            if (TryScreenToData(rectangle.TopLeft, out double x1, out double y1) &&
                TryScreenToData(rectangle.BottomRight, out double x2, out double y2) &&
                _chipMarks.AddRectangle(series, x1, x2, y1, y2))
            {
                _markedPoints = _chipMarks.CreateMask(series);
                InvalidatePlot();
                ChipMarksChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightStart.HasValue) return;
        CompleteRightGesture(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (!_rightStart.HasValue && IsMouseCaptured)
            ReleaseMouseCapture();
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (IsMouseCaptured) return;
        _hoverTimer.Stop();
        _hover = null;
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point current = e.GetPosition(this);
        if (_rightStart.HasValue)
        {
            if (e.RightButton != MouseButtonState.Pressed) { CancelRightGesture(); return; }
            _rightPosition = current;
            if (_selecting && !_hoverTimer.IsEnabled) _hoverTimer.Start();
            e.Handled = true;
        }
        else if (_dragStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            _hover = current;
            _offsetX += current.X - _dragStart.Value.X;
            if (!IsYAxisLocked) _offsetY -= current.Y - _dragStart.Value.Y;
            _dragStart = current;
            InvalidatePlot();
            _overlayLayer.InvalidateVisual();
        }
        else QueueHover(current);
    }

    private void QueueHover(Point point)
    {
        if (_hover == point) return;
        _hover = point;
        if (!_hoverTimer.IsEnabled) _hoverTimer.Start();
    }

    private bool IsInPlot(Point p)
        => _hasPlot && p.X >= _left && p.X <= _left + _plotW && p.Y >= _top && p.Y <= _top + _plotH;

    private double MapX(double dataX)
    {
        double norm = (_dataMaxX == _dataMinX) ? 0.5 : (dataX - _dataMinX) / (_dataMaxX - _dataMinX);
        return _left + norm * _plotW * _scaleX + _offsetX;
    }

    private double MapY(double dataY)
    {
        double norm = (_dataMaxY == _dataMinY) ? 0.5 : (dataY - _dataMinY) / (_dataMaxY - _dataMinY);
        return _top + _plotH - (norm * _plotH * _scaleY + _offsetY);
    }

    private bool TryScreenToData(Point screen, out double dataX, out double dataY)
    {
        dataX = dataY = 0;
        if (!_hasPlot || _plotW < 1 || _plotH < 1) return false;

        double normX = (screen.X - _left - _offsetX) / (_plotW * _scaleX);
        double normY = (_top + _plotH - screen.Y - _offsetY) / (_plotH * _scaleY);
        dataX = _dataMinX + normX * (_dataMaxX - _dataMinX);
        dataY = _dataMinY + normY * (_dataMaxY - _dataMinY);
        return true;
    }

    private void GetVisibleRange(out double minX, out double maxX, out double minY, out double maxY)
    {
        TryScreenToData(new Point(_left, _top + _plotH), out minX, out minY);
        TryScreenToData(new Point(_left + _plotW, _top), out maxX, out maxY);
        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minY > maxY) (minY, maxY) = (maxY, minY);
        if (Math.Abs(maxX - minX) < 1e-15) { minX -= 1; maxX += 1; }
        if (Math.Abs(maxY - minY) < 1e-15) { minY -= 1; maxY += 1; }
    }

    private void RebuildPointCache()
    {
        var values = Values;
        ApplySeries(values == null ? ScatterSeries.Empty : ScatterSeries.Build(
            values as IReadOnlyList<TestValue> ?? values.ToArray(), Lsl, Usl, trackChips: true));
    }

    private void ApplySeries(ScatterSeries series)
    {
        Mean = Sigma = null;
        _markRevision++;
        CancelRightGesture();
        _series = series;
        _markedPoints = _chipMarks.CreateMask(series);
        _markedCoords.Clear();
        if (_markedCoords.Capacity > Math.Max(256L, (long)series.Points.Length * 4))
            _markedCoords.Capacity = Math.Max(256, series.Points.Length);
        _groupLabels = Array.Empty<FormattedText>();
        _pointCount = series.SampleCount;
        _failCoords.Clear();
        if (_failCoords.Capacity > Math.Max(256L, (long)_pointCount * 4))
            _failCoords.Capacity = Math.Max(256, _pointCount);
        if (_pointCount == 0) ReleaseRasterBuffers();
        _hoverTimer.Stop();
        _hover = null;
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _scaleX = _scaleY = 1;
        _offsetX = _offsetY = 0;
        if (_pointCount > 0)
        {
            bool grouped = series.Order != ScatterOrder.SerialNumber;
            _dataMinX = grouped || _pointCount == 1 ? 0.5 : 1;
            _dataMaxX = grouped || _pointCount == 1 ? _pointCount + 0.5 : _pointCount;
        }
        UpdateVerticalRange();
        _hasPlot = _plotW >= 20 && _plotH >= 20 && _pointCount > 0;
        AutomationProperties.SetHelpText(this, "横轴：" + HorizontalAxisTitle +
            (series.Order == ScatterOrder.SerialNumber ? "（从 1 开始）；" : "分组，组内按 SN 排序；") +
            "纵轴：测量值。绘制视野内全部有效测量点，不抽样、不聚合。" + InteractionHelp);
        InvalidatePlot();
        _overlayLayer.InvalidateVisual();
    }

    protected override void DrawPlot(DrawingContext dc)
    {
        _renderCount++;

        double w = ActualWidth;
        double h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));

        if (w >= 180 && h >= 120)
        {
            string valueTitle = ChartAxis.ValueTitle(Unit);
            double captionWidth = Math.Min(140, (w - 32) / 3);
            ChartAxis.DrawTitle(dc, this, valueTitle, new Rect(8, 3, captionWidth, 24));
            double captionEnd = 8 + Math.Min(captionWidth, ChartAxis.Text(this, valueTitle).Width);
            ChartAxis.DrawHeading(dc, this, Title, captionEnd + 12, w - ChartAxis.PlotRight);
            var axisTitle = ChartAxis.Text(this, HorizontalAxisTitle);
            ChartAxis.DrawTitle(dc, this, HorizontalAxisTitle,
                new Rect(Math.Max(4, (_left + w - ChartAxis.PlotRight - axisTitle.Width) / 2),
                    _top + _plotH + ChartAxis.AxisTitleGap, axisTitle.Width + 2, 17));
        }

        if (w < 40 || h < 40 || !_hasPlot)
            return;

        var plotRect = new Rect(_left, _top, _plotW, _plotH);
        GetVisibleRange(out double visMinX, out double visMaxX, out double visMinY, out double visMaxY);

        var yTicks = ChartAxis.Ticks(visMinY, visMaxY, Math.Max(3, (int)(_plotH / 32)));
        bool grouped = _series.Order != ScatterOrder.SerialNumber;
        var xTicks = grouped ? Array.Empty<double>() :
            ChartAxis.Ticks(visMinX, visMaxX, Math.Max(1, (int)(_plotW / 85)), integers: true);
        double yStep = yTicks.Count > 1 ? yTicks[1] - yTicks[0] : visMaxY - visMinY;

        dc.PushClip(new RectangleGeometry(plotRect));

        foreach (var tick in yTicks)
        {
            double y = MapY(tick);
            dc.DrawLine(GridPen, new Point(_left, y), new Point(_left + _plotW, y));
        }

        foreach (var tick in xTicks)
        {
            double x = MapX(tick);
            dc.DrawLine(GridPen, new Point(x, _top), new Point(x, _top + _plotH));
        }
        if (grouped) DrawGroupBoundaries(dc, visMinX, visMaxX);

        void ReferenceLine(bool show, double? value, Pen pen)
        {
            if (!show || value is not double v || !double.IsFinite(v) || v < visMinY || v > visMaxY) return;
            double y = MapY(v);
            dc.DrawLine(pen, new Point(_left, y), new Point(_left + _plotW, y));
        }
        ReferenceLine(ShowLsl, Lsl, RedPen);
        ReferenceLine(ShowUsl, Usl, RedPen);
        ReferenceLine(ShowMean, Mean, MeanPen);
        if (ShowThreeSigma && Mean.HasValue && Sigma.HasValue)
        {
            ReferenceLine(true, Mean - 3 * Sigma, SigmaPen);
            ReferenceLine(true, Mean + 3 * Sigma, SigmaPen);
        }

        RenderPointsBitmap(dc, plotRect, visMinX, visMaxX, grouped);
        dc.Pop();

        ChartAxis.DrawAxes(dc, AxisPen, plotRect);

        foreach (var tick in yTicks)
        {
            double y = MapY(tick);
            if (y < _top - 2 || y > _top + _plotH + 2) continue;
            dc.DrawLine(AxisPen, new Point(_left - 4, y), new Point(_left, y));
            var ft = ChartAxis.Text(this, ChartAxis.Format(tick, yStep));
            dc.DrawText(ft, new Point(_left - 6 - ft.Width, y - ft.Height / 2));
        }

        double lastLabelRight = double.NegativeInfinity;
        foreach (var tick in xTicks)
        {
            double x = MapX(tick);
            if (x < _left - 2 || x > _left + _plotW + 2) continue;
            dc.DrawLine(AxisPen, new Point(x, _top + _plotH), new Point(x, _top + _plotH + 4));
            var ft = ChartAxis.Text(this, ChartAxis.Format(tick));
            double tx = Math.Clamp(x - ft.Width / 2, 2, Math.Max(2, w - ft.Width - 2));
            if (tx < lastLabelRight + 8) continue;
            dc.DrawText(ft, new Point(tx, _top + _plotH + ChartAxis.TickLabelGap));
            lastLabelRight = tx + ft.Width;
        }
        if (grouped) DrawGroupLabels(dc, visMinX, visMaxX);
    }

    private void EnsureStamp(double dpi)
    {
        if (_stampDpi == dpi && _stampAlpha.Length > 0) return;
        _stampDpi = dpi;
        double radius = PointRadius * dpi;
        int r = Math.Max(1, (int)Math.Ceiling(radius + 0.5));
        int size = r * 2 + 1;
        int len = size * size;
        _stampRadius = r;
        _stampAlpha = new byte[len];
        _stampNormal = new uint[len];
        _stampDark = new uint[len];
        _stampLight = new uint[len];
        _stampOut = new uint[len];
        _stampMarkDark = new uint[len];
        _stampMarkLight = new uint[len];

        static uint Premultiply(Color c, byte a)
        {
            if (a == 0) return 0;
            uint pr = (uint)((c.R * a + 127) / 255);
            uint pg = (uint)((c.G * a + 127) / 255);
            uint pb = (uint)((c.B * a + 127) / 255);
            return ((uint)a << 24) | (pr << 16) | (pg << 8) | pb;
        }

        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double coverage = Math.Clamp(radius + 0.5 - dist, 0.0, 1.0);
                byte alpha = (byte)Math.Round(coverage * 255.0);
                int idx = (dy + r) * size + (dx + r);
                _stampAlpha[idx] = alpha;
                _stampNormal[idx] = Premultiply(PointColor, alpha);
                _stampDark[idx] = Premultiply(GroupDarkColor, alpha);
                _stampLight[idx] = Premultiply(GroupLightColor, alpha);
                _stampOut[idx] = Premultiply(OutColor, alpha);
                _stampMarkDark[idx] = Premultiply(MarkDarkColor, alpha);
                _stampMarkLight[idx] = Premultiply(MarkLightColor, alpha);
            }
        }
    }

    private void RenderPointsBitmap(DrawingContext dc, Rect plotRect, double visMinX, double visMaxX, bool grouped)
    {
        var points = _series.Points;
        var markedPoints = _markedPoints;
        if (points.Length == 0 || _plotW < 1 || _plotH < 1) return;

        // Offscreen report export renders at 144 DPI (1.5x); match it so exported scatter plots stay crisp.
        double renderDpi = PresentationSource.FromVisual(this) == null ? Math.Max(1.5, Dpi) : Dpi;
        double targetW = _plotW * renderDpi, targetH = _plotH * renderDpi;
        double bitmapScale = Math.Min(1, Math.Sqrt(MaxRasterPixels / (targetW * targetH)));
        bitmapScale = Math.Min(bitmapScale, Math.Min(8192 / targetW, 8192 / targetH));
        int bmpW = Math.Clamp((int)Math.Round(targetW * bitmapScale), 1, 8192);
        int bmpH = Math.Min(Math.Clamp((int)Math.Round(targetH * bitmapScale), 1, 8192), MaxRasterPixels / bmpW);
        int pixelCount = bmpW * bmpH;

        if (_pointBitmap == null || _pointBitmap.PixelWidth != bmpW || _pointBitmap.PixelHeight != bmpH || _pointBitmapDpi != renderDpi)
        {
            _pointBitmap = new WriteableBitmap(bmpW, bmpH, 96.0 * renderDpi, 96.0 * renderDpi, PixelFormats.Pbgra32, null);
            _pointBitmapDpi = renderDpi;
        }
        // Grow geometrically during a drag; release a much larger old allocation after shrinking.
        if (_pixelBuffer.Length < pixelCount || _pixelBuffer.Length > Math.Max(65536L, (long)pixelCount * 4))
        {
            int capacity = _pixelBuffer.Length < pixelCount
                ? Math.Min(MaxRasterPixels, Math.Max(pixelCount, Math.Max(65536, _pixelBuffer.Length * 2)))
                : Math.Max(65536, pixelCount);
            _pixelBuffer = new uint[capacity];
            _occupancy = new byte[capacity];
        }

        Array.Clear(_pixelBuffer, 0, pixelCount);
        Array.Clear(_occupancy, 0, pixelCount);
        _failCoords.Clear();
        _markedCoords.Clear();
        EnsureStamp(bitmapScale < 1 ? Math.Min(bmpW / _plotW, bmpH / _plotH) : renderDpi);

        double pxPerDipX = bmpW / _plotW;
        double pxPerDipY = bmpH / _plotH;

        double scaleX, biasX;
        if (_dataMaxX == _dataMinX)
        {
            scaleX = 0;
            biasX = (0.5 * _plotW * _scaleX + _offsetX) * pxPerDipX;
        }
        else
        {
            double invSpanX = 1.0 / (_dataMaxX - _dataMinX);
            scaleX = (_plotW * _scaleX * invSpanX) * pxPerDipX;
            biasX = (_offsetX - _dataMinX * _plotW * _scaleX * invSpanX) * pxPerDipX;
        }

        double scaleY, biasY;
        if (_dataMaxY == _dataMinY)
        {
            scaleY = 0;
            biasY = (_plotH - (0.5 * _plotH * _scaleY + _offsetY)) * pxPerDipY;
        }
        else
        {
            double invSpanY = 1.0 / (_dataMaxY - _dataMinY);
            scaleY = -(_plotH * _scaleY * invSpanY) * pxPerDipY;
            biasY = (_plotH - _offsetY + _dataMinY * _plotH * _scaleY * invSpanY) * pxPerDipY;
        }

        int r = _stampRadius;
        int start = ScatterSeries.LowerBound(points, visMinX - 1);
        var groups = _series.Groups;
        int groupIndex = grouped && start < points.Length ? ScatterSeries.LowerBoundGroup(groups, points[start].Index) : 0;

        for (int i = start; i < points.Length && points[i].Index <= visMaxX + 1; i++)
        {
            var point = points[i];
            int ix = (int)Math.Round(Math.FusedMultiplyAdd(point.Index, scaleX, biasX));
            int iy = (int)Math.Round(Math.FusedMultiplyAdd(point.Value, scaleY, biasY));
            if (ix < -r || ix >= bmpW + r || iy < -r || iy >= bmpH + r) continue;

            if (grouped)
                while (groupIndex + 1 < groups.Length && point.Index > groups[groupIndex].EndIndex) groupIndex++;

            // A clipped point outside the image must not occupy the center of an inside point.
            bool centerInside = (uint)ix < (uint)bmpW && (uint)iy < (uint)bmpH;
            int centerIdx = centerInside ? iy * bmpW + ix : -1;

            if (i < markedPoints.Length && markedPoints[i])
            {
                bool light = grouped && groupIndex % 2 != 0;
                if (!centerInside || _occupancy[centerIdx] < 4)
                {
                    if (centerInside) _occupancy[centerIdx] = light ? (byte)5 : (byte)4;
                    _markedCoords.Add((ix, iy, light));
                }
                continue;
            }

            if (point.OutOfSpec)
            {
                if (!centerInside || _occupancy[centerIdx] < 3)
                {
                    if (centerInside) _occupancy[centerIdx] = 3;
                    _failCoords.Add((ix, iy));
                }
                continue;
            }

            byte state = !grouped ? (byte)1 : (groupIndex % 2 == 0 ? (byte)1 : (byte)2);
            if (centerInside && _occupancy[centerIdx] != 0) continue;
            if (centerInside) _occupancy[centerIdx] = state;

            uint[] stamp = !grouped ? _stampNormal : (state == 1 ? _stampDark : _stampLight);
            StampCircle(bmpW, bmpH, ix, iy, r, stamp);
        }

        // Second pass: stamp out-of-spec red points on top so failures are never hidden behind pass points.
        for (int i = 0; i < _failCoords.Count; i++)
        {
            var (ix, iy) = _failCoords[i];
            StampCircle(bmpW, bmpH, ix, iy, r, _stampOut);
        }

        // Marking is a display override, including failed points; specifications/statistics stay intact.
        foreach (var (ix, iy, light) in _markedCoords)
            StampCircle(bmpW, bmpH, ix, iy, r, light ? _stampMarkLight : _stampMarkDark);

        _pointBitmap.WritePixels(new Int32Rect(0, 0, bmpW, bmpH), _pixelBuffer, bmpW * sizeof(uint), 0);
        dc.DrawImage(_pointBitmap, plotRect);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StampCircle(int bmpW, int bmpH, int cx, int cy, int r, uint[] stampColors)
    {
        int size = r * 2 + 1;
        var alphas = _stampAlpha;
        var pixels = _pixelBuffer;

        // Interior fast path: zero per-pixel bounds checks.
        if (cx >= r && cx < bmpW - r && cy >= r && cy < bmpH - r)
        {
            int stampIdx = 0;
            for (int dy = -r; dy <= r; dy++)
            {
                int rowOffset = (cy + dy) * bmpW + cx;
                for (int dx = -r; dx <= r; dx++, stampIdx++)
                {
                    byte a = alphas[stampIdx];
                    if (a == 0) continue;
                    int idx = rowOffset + dx;
                    uint src = stampColors[stampIdx];
                    uint dst = pixels[idx];
                    pixels[idx] = (dst == 0 || a == 255) ? src : BlendPremultiplied(src, a, dst);
                }
            }
            return;
        }

        int dyMin = Math.Max(-r, -cy);
        int dyMax = Math.Min(r, bmpH - 1 - cy);
        int dxMin = Math.Max(-r, -cx);
        int dxMax = Math.Min(r, bmpW - 1 - cx);

        for (int dy = dyMin; dy <= dyMax; dy++)
        {
            int rowOffset = (cy + dy) * bmpW + cx;
            int stampRow = (dy + r) * size + r;
            for (int dx = dxMin; dx <= dxMax; dx++)
            {
                int stampIdx = stampRow + dx;
                byte a = alphas[stampIdx];
                if (a == 0) continue;
                int idx = rowOffset + dx;
                uint src = stampColors[stampIdx];
                uint dst = pixels[idx];
                pixels[idx] = (dst == 0 || a == 255) ? src : BlendPremultiplied(src, a, dst);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BlendPremultiplied(uint src, byte srcA, uint dst)
    {
        uint invA = (uint)(255 - srcA);
        uint b = (src & 0xFF) + (((dst & 0xFF) * invA + 127) >> 8);
        uint g = ((src >> 8) & 0xFF) + ((((dst >> 8) & 0xFF) * invA + 127) >> 8);
        uint r = ((src >> 16) & 0xFF) + ((((dst >> 16) & 0xFF) * invA + 127) >> 8);
        uint a = (src >> 24) + (((dst >> 24) * invA + 127) >> 8);
        if (b > 255) b = 255;
        if (g > 255) g = 255;
        if (r > 255) r = 255;
        if (a > 255) a = 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private void DrawGroupBoundaries(DrawingContext dc, double min, double max)
    {
        var groups = _series.Groups;
        for (int i = ScatterSeries.LowerBoundGroup(groups, min); i < groups.Length && groups[i].StartIndex - 0.5 <= max; i++)
        {
            double x = MapX(groups[i].StartIndex - 0.5);
            dc.DrawLine(GroupPen, new Point(x, _top), new Point(x, _top + _plotH));
        }
        if (groups.Length > 0)
        {
            double x = MapX(groups[^1].EndIndex + 0.5);
            dc.DrawLine(GroupPen, new Point(x, _top), new Point(x, _top + _plotH));
        }
    }

    private void DrawGroupLabels(DrawingContext dc, double min, double max)
    {
        var groups = _series.Groups;
        if (_groupLabels.Length != groups.Length || _groupLabelDpi != Dpi)
        {
            _groupLabels = groups.Select(g => ChartAxis.Text(this,
                _series.Order == ScatterOrder.Site ? "Site " + g.Name : g.Name)).ToArray();
            _groupLabelDpi = Dpi;
        }
        for (int i = ScatterSeries.LowerBoundGroup(groups, min); i < groups.Length && groups[i].StartIndex - 0.5 <= max; i++)
        {
            double boundary = MapX(groups[i].StartIndex - 0.5);
            if (boundary >= _left && boundary <= _left + _plotW)
                dc.DrawLine(GroupPen, new Point(boundary, _top + _plotH), new Point(boundary, _top + _plotH + 4));
            double left = Math.Max(_left, boundary);
            double right = Math.Min(_left + _plotW, MapX(groups[i].EndIndex + 0.5));
            if (right - left <= 2) continue;
            var label = _groupLabels[i];
            double scale = Math.Min(1, Math.Max(1, right - left - 8) / Math.Max(1, label.WidthIncludingTrailingWhitespace));
            double x = (left + right - label.WidthIncludingTrailingWhitespace * scale) / 2;
            double y = _top + _plotH + 6 + (16 - label.Height * scale) / 2;
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawText(label, new Point());
            dc.Pop(); dc.Pop();
        }
        if (groups.Length > 0)
        {
            double boundary = MapX(groups[^1].EndIndex + 0.5);
            if (boundary >= _left && boundary <= _left + _plotW)
                dc.DrawLine(GroupPen, new Point(boundary, _top + _plotH), new Point(boundary, _top + _plotH + 4));
        }
    }

    private sealed class OverlayLayer : FrameworkElement
    {
        private readonly ScatterPlotControl _owner;

        public OverlayLayer(ScatterPlotControl owner) => _owner = owner;

        protected override void OnRender(DrawingContext dc)
        {
            var o = _owner;
            if (o.IsResizePreview) return;
            if (o._selecting)
            {
                dc.DrawRectangle(SelectionFill, SelectionPen, o.SelectionRectangle());
                return;
            }
            if (o._hover is not Point hp || !o.IsInPlot(hp))
                return;

            dc.PushClip(new RectangleGeometry(new Rect(o._left, o._top, o._plotW, o._plotH)));
            dc.DrawLine(CrossPen, new Point(o._left, hp.Y), new Point(o._left + o._plotW, hp.Y));
            dc.DrawLine(CrossPen, new Point(hp.X, o._top), new Point(hp.X, o._top + o._plotH));
            dc.Pop();

            if (!o.TryScreenToData(hp, out double dx, out double dy))
                return;

            string groupText = "";
            int group = ScatterSeries.LowerBoundGroup(o._series.Groups, dx);
            if (group < o._series.Groups.Length && dx >= o._series.Groups[group].StartIndex - 0.5)
                groupText = o.HorizontalAxisTitle + " " + o._series.Groups[group].Name + "\n";
            string label = groupText + $"序号≈{dx:G5}  测量值={dy:G6} {o.Unit}\n" +
                (o.IsYAxisLocked ? "纵轴已锁定 · 横向缩放/拖拽 · 双击重置横轴" : InteractionHelp);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, UiTypeface, 11, Brushes.Black, o.Dpi)
            {
                MaxTextWidth = Math.Max(1, ActualWidth - 18)
            };

            double w = ActualWidth;
            double h = ActualHeight;
            double boxW = ft.Width + 10;
            double boxH = ft.Height + 6;
            double lx = hp.X + 12;
            double ly = hp.Y + 12;
            if (lx + boxW > w - 4) lx = hp.X - boxW - 8;
            if (ly + boxH > h - 4) ly = hp.Y - boxH - 8;
            lx = Math.Clamp(lx, 4, Math.Max(4, w - boxW - 4));
            ly = Math.Clamp(ly, 4, Math.Max(4, h - boxH - 4));

            dc.DrawRectangle(LabelBg, LabelBorder, new Rect(lx, ly, boxW, boxH));
            dc.DrawText(ft, new Point(lx + 5, ly + 3));
        }
    }
}

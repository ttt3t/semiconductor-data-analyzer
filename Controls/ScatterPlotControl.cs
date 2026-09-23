using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer.Controls;

public class ScatterPlotControl : FrameworkElement
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

    private static readonly Brush PointBrush = Freeze(new SolidColorBrush(Color.FromRgb(30, 120, 200)));
    private static readonly Brush OutBrush = Freeze(new SolidColorBrush(Color.FromRgb(220, 60, 60)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(210, 210, 210)));
    private static readonly Brush CrossBrush = Freeze(new SolidColorBrush(Color.FromRgb(168, 168, 168)));
    private static readonly Brush LabelBg = Freeze(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
    private static readonly Pen AxisPen = Freeze(new Pen(Brushes.Black, 1));
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Pen CrossPen = Freeze(new Pen(CrossBrush, 1));
    private static readonly Pen RedPen = Freeze(new Pen(Brushes.Red, 1.5));
    private static readonly Pen LabelBorder = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(180, 180, 180))), 0.8));
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private const double PointRadius = 1.35;
    private const string InteractionHelp = "滚轮缩放 · 左键拖拽 · 双击/右键重置";
    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    public string Unit { get; private set; } = "";

    private static T Freeze<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }

    private readonly OverlayLayer _overlayLayer;
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

    public ScatterPlotControl()
    {
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

        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseRightButtonDown += OnMouseRightButtonDown;
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
        InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }

    public void SetSeries(ScatterSeries series, double? lsl, double? usl, string unit)
    {
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
        c.InvalidateVisual();
        c._overlayLayer?.InvalidateVisual();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _overlayLayer;

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
        const double left = ChartAxis.PlotLeft, right = ChartAxis.PlotRight, top = 32, bottom = 54;
        _left = left;
        _top = top;
        _plotW = Math.Max(0, finalSize.Width - left - right);
        _plotH = Math.Max(0, finalSize.Height - top - bottom);
        _hasPlot = _plotW >= 20 && _plotH >= 20 && _pointCount > 0;
        _overlayLayer.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    private void ResetZoom()
    {
        _scaleX = _scaleY = 1.0;
        _offsetX = _offsetY = 0;
        InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_pointCount == 0 || !IsInPlot(e.GetPosition(this))) return;

        Point mouse = e.GetPosition(this);
        double nextScale = Math.Clamp(_scaleX * (e.Delta > 0 ? 1.15 : 1.0 / 1.15), 0.2, 50);
        double zoomFactor = nextScale / _scaleX;

        _offsetX = mouse.X - _left - (mouse.X - _left - _offsetX) * zoomFactor;
        double originY = _top + _plotH;
        _offsetY = originY - mouse.Y - (originY - mouse.Y - _offsetY) * zoomFactor;
        _scaleX = _scaleY = nextScale;
        _hover = mouse;
        InvalidateVisual();
        _overlayLayer.InvalidateVisual();
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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
        ResetZoom();
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (IsMouseCaptured) return;
        _hover = null;
        _overlayLayer.InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point current = e.GetPosition(this);
        _hover = current;

        if (_dragStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            _offsetX += current.X - _dragStart.Value.X;
            _offsetY -= current.Y - _dragStart.Value.Y;
            _dragStart = current;
            InvalidateVisual();
        }

        _overlayLayer.InvalidateVisual();
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
            values as IReadOnlyList<TestValue> ?? values.ToArray(), Lsl, Usl));
    }

    private void ApplySeries(ScatterSeries series)
    {
        _series = series;
        _pointCount = series.SampleCount;
        _hover = null;
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _scaleX = _scaleY = 1;
        _offsetX = _offsetY = 0;
        if (_pointCount > 0)
        {
            double pad = (series.Maximum - series.Minimum) * 0.05;
            if (pad <= 0) pad = Math.Max(Math.Abs(series.Minimum) * 0.01, 1e-12);
            _dataMinX = _pointCount == 1 ? 0.5 : 1;
            _dataMaxX = _pointCount == 1 ? 1.5 : _pointCount;
            _dataMinY = series.Minimum - pad;
            _dataMaxY = series.Maximum + pad;
        }
        _hasPlot = _plotW >= 20 && _plotH >= 20 && _pointCount > 0;
        InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _renderCount++;

        double w = ActualWidth;
        double h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));

        if (w >= 180 && h >= 120)
        {
            ChartAxis.DrawTitle(dc, this, ChartAxis.ValueTitle(Unit), new Rect(8, 3, w - 16, 24));
            ChartAxis.DrawTitle(dc, this, "样本序号", new Rect(Math.Max(4, (_left + w - 60) / 2), h - 22, 70, 21));
        }

        if (w < 40 || h < 40 || !_hasPlot)
            return;

        var plotRect = new Rect(_left, _top, _plotW, _plotH);
        GetVisibleRange(out double visMinX, out double visMaxX, out double visMinY, out double visMaxY);

        var yTicks = ChartAxis.Ticks(visMinY, visMaxY, Math.Max(3, (int)(_plotH / 32)));
        var xTicks = ChartAxis.Ticks(visMinX, visMaxX, Math.Max(1, (int)(_plotW / 85)), integers: true);
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

        if (Lsl.HasValue)
        {
            double y = MapY(Lsl.Value);
            dc.DrawLine(RedPen, new Point(_left, y), new Point(_left + _plotW, y));
        }
        if (Usl.HasValue)
        {
            double y = MapY(Usl.Value);
            dc.DrawLine(RedPen, new Point(_left, y), new Point(_left + _plotW, y));
        }

        double r = PointRadius;
        var points = _series.Points;
        int start = ScatterSeries.LowerBound(points, visMinX - 1);
        for (int i = start; i < points.Length && points[i].Index <= visMaxX + 1; i++)
        {
            var point = points[i];
            double x = MapX(point.Index), y = MapY(point.Value);
            if (y < _top - r || y > _top + _plotH + r) continue;
            dc.DrawEllipse(point.OutOfSpec ? OutBrush : PointBrush, null, new Point(x, y), r, r);
        }
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
            dc.DrawText(ft, new Point(tx, _top + _plotH + 6));
            lastLabelRight = tx + ft.Width;
        }

    }

    private sealed class OverlayLayer : FrameworkElement
    {
        private readonly ScatterPlotControl _owner;

        public OverlayLayer(ScatterPlotControl owner) => _owner = owner;

        protected override void OnRender(DrawingContext dc)
        {
            var o = _owner;
            if (o._hover is not Point hp || !o.IsInPlot(hp))
                return;

            dc.PushClip(new RectangleGeometry(new Rect(o._left, o._top, o._plotW, o._plotH)));
            dc.DrawLine(CrossPen, new Point(o._left, hp.Y), new Point(o._left + o._plotW, hp.Y));
            dc.DrawLine(CrossPen, new Point(hp.X, o._top), new Point(hp.X, o._top + o._plotH));
            dc.Pop();

            if (!o.TryScreenToData(hp, out double dx, out double dy))
                return;

            string label = $"序号≈{dx:G5}  测量值={dy:G6} {o.Unit}\n{InteractionHelp}";
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

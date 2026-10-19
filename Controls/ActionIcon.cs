using System.Windows;
using System.Windows.Media;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>Small reusable vector glyphs, with no icon fonts, bitmaps or per-frame animation.</summary>
public sealed class ActionIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(ActionIcon),
        new FrameworkPropertyMetadata("File", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ActionIcon),
        new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = CreateShapes();

    private static IReadOnlyDictionary<string, Geometry> CreateShapes()
    {
        var paths = new Dictionary<string, string>
        {
            ["Open"] = "M3,18 L3,6 L9,6 L11,9 L21,9 L18,19 L3,19 L6,11 L20,11",
            ["Import"] = "M4,14 L4,20 L20,20 L20,14 M12,3 L12,15 M7,10 L12,15 L17,10",
            ["Export"] = "M4,14 L4,20 L20,20 L20,14 M12,15 L12,3 M7,8 L12,3 L17,8",
            ["Report"] = "M5,3 L15,3 L20,8 L20,21 L5,21 Z M15,3 L15,8 L20,8 M9,17 L9,14 M13,17 L13,11 M17,17 L17,13",
            ["Filter"] = "M3,4 L21,4 L14,12 L14,19 L10,21 L10,12 Z",
            ["Map"] = "M3,3 L9,3 L9,9 L3,9 Z M15,3 L21,3 L21,9 L15,9 Z M3,15 L9,15 L9,21 L3,21 Z M15,15 L21,15 L21,21 L15,21 Z",
            ["Heatmap"] = "M3,3 L21,3 L21,21 L3,21 Z M3,9 L21,9 M3,15 L21,15 M9,3 L9,21 M15,3 L15,21",
            ["Table"] = "M3,4 L21,4 L21,20 L3,20 Z M3,9 L21,9 M3,14 L21,14 M10,4 L10,20",
            ["Wafer"] = "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M8,4 L8,20 M16,4 L16,20 M4,8 L20,8 M4,16 L20,16",
            ["Prediction"] = "M3,3 L3,21 L21,21 M6,16 L11,11 L15,14 L21,5 M16,5 L21,5 L21,10",
            ["Info"] = "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M12,10 L12,17 M12,6 L12,6.5",
            ["File"] = "M5,3 L15,3 L20,8 L20,21 L5,21 Z M15,3 L15,8 L20,8 M9,12 L16,12 M9,16 L16,16",
            ["Copy"] = "M8,7 L20,7 L20,21 L8,21 Z M15,3 L4,3 L4,17",
            ["Combine"] = "M3,5 L8,5 L12,10 L21,10 M3,19 L8,19 L12,14 L21,14 M17,6 L21,10 M17,18 L21,14",
            ["Chart"] = "M3,3 L3,21 L21,21 M7,17 L7,12 L10,12 L10,17 M13,17 L13,6 L16,6 L16,17 M19,17 L19,10",
            ["Scatter"] = "M3,3 L3,21 L21,21 M7,15 L7.2,15 M10,10 L10.2,10 M14,14 L14.2,14 M16,6 L16.2,6 M20,10 L20.2,10"
        };
        var shapes = paths.ToDictionary(p => p.Key, p => { var geometry = Geometry.Parse(p.Value); geometry.Freeze(); return geometry; });
        shapes["Settings"] = CreateSettingsGear();
        return shapes;
    }

    private static Geometry CreateSettingsGear()
    {
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            for (int tooth = 0; tooth < 8; tooth++)
                for (int corner = 0; corner < 4; corner++)
                {
                    double angle = (tooth * 45 + (corner switch { 0 => -22.5, 1 => -15, 2 => 15, _ => 22.5 })) * Math.PI / 180;
                    double radius = corner is 1 or 2 ? 10.5 : 8;
                    var point = new Point(12 + radius * Math.Cos(angle), 12 + radius * Math.Sin(angle));
                    if (tooth == 0 && corner == 0) context.BeginFigure(point, true, true);
                    else context.LineTo(point, true, false);
                }
        }
        var gear = new GeometryGroup { FillRule = FillRule.EvenOdd };
        gear.Children.Add(outline);
        gear.Children.Add(new EllipseGeometry(new Point(12, 12), 3.3, 3.3));
        gear.Freeze();
        return gear;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Shapes.TryGetValue(Kind, out var shape) || ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Stroke, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(Kind == "Settings" ? Stroke : null, Kind == "Settings" ? null : pen, shape);
        dc.Pop(); dc.Pop();
    }
}

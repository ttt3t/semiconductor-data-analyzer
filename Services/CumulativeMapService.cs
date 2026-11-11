using System.Globalization;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>One sparse count index shared by every category; dense maps exist only in visible tiles.</summary>
public sealed class CumulativeMapData
{
    internal readonly Dictionary<(int X, int Y), int> Totals = new();
    private readonly Dictionary<Color, Brush[]> _palettes = new();
    public IReadOnlyList<CumulativeMapCategory> Categories { get; internal set; } = Array.Empty<CumulativeMapCategory>();
    public int WaferCount { get; internal set; }
    public int ObservationCount { get; internal set; }
    public int UnclassifiedCount { get; internal set; }
    public int CoordinateCount => Totals.Count;
    public const string Basis = "分母为该坐标实际有芯片数据的晶圆数；无芯片不计入。";
    public int TotalAt(int x, int y) => Totals.GetValueOrDefault((x, y));

    internal Brush[] Palette(Color color)
    {
        // Called on the UI thread when a tile is realized. Reuse palettes for repeated BIN colors.
        if (_palettes.TryGetValue(color, out var palette)) return palette;
        palette = new Brush[256];
        for (int i = 0; i < palette.Length; i++)
        {
            double fraction = i / 255.0;
            byte Blend(byte start, byte end) => (byte)Math.Round(start + (end - start) * fraction);
            var brush = new SolidColorBrush(Color.FromRgb(Blend(229, color.R), Blend(231, color.G), Blend(235, color.B)));
            brush.Freeze(); palette[i] = brush;
        }
        return _palettes[color] = palette;
    }
}

public sealed class CumulativeMapCategory
{
    internal readonly Dictionary<(int X, int Y), int> Hits = new();
    public required CumulativeMapData Data { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required Brush Color { get; init; }
    public int Count { get; internal set; }
    public double Percent => Data.ObservationCount == 0 ? 0 : 100.0 * Count / Data.ObservationCount;
    public int CountAt(int x, int y) => Hits.GetValueOrDefault((x, y));
    public double ProbabilityAt(int x, int y) => Data.TotalAt(x, y) is int total && total > 0 ? (double)CountAt(x, y) / total : 0;

    public WaferMapPage CreatePage()
    {
        var palette = Data.Palette(Color is SolidColorBrush solid ? solid.Color : Colors.Gray);
        var dies = new List<WaferDie>(Data.CoordinateCount);
        foreach (var (position, total) in Data.Totals)
        {
            int count = Hits.GetValueOrDefault(position);
            double percent = 100.0 * count / total;
            dies.Add(new WaferDie
            {
                WaferId = $"全部 {Data.WaferCount} 片晶圆", X = position.X, Y = position.Y,
                Category = Category, Value = percent, Unit = "%",
                Color = palette[(int)Math.Round(255.0 * count / total)],
                AdditionalInfo = $"{Title}\nX：{position.X}    Y：{position.Y}\n命中 {count} / 有芯片 {total} 片\n该坐标占比：{percent:F2}%"
            });
        }
        return new(Title, dies, Summary: $"总占比 {Percent:F2}% · {Data.WaferCount} 片",
            SummaryDetails: $"累计命中 {Count:N0} / 芯片出现 {Data.ObservationCount:N0} 次\n{CumulativeMapData.Basis}");
    }

    public MapTableText CopyTable() => MapTableCopyService.Build(Data.Totals.Select(entry =>
        new MapTableEntry(entry.Key.X, entry.Key.Y,
            (100.0 * Hits.GetValueOrDefault(entry.Key) / entry.Value).ToString("F2", CultureInfo.InvariantCulture) + "%", null, null)), MapTableKind.SBin);
}

public static class CumulativeMapService
{
    public static CumulativeMapData Build(IReadOnlyList<WaferMapPage> pages, MapTableKind kind, CancellationToken token = default)
    {
        var data = new CumulativeMapData();
        var categories = new Dictionary<string, CumulativeMapCategory>(StringComparer.Ordinal);
        CumulativeMapCategory Add(string category, Brush color)
        {
            if (categories.TryGetValue(category, out var existing)) return existing;
            string title = kind == MapTableKind.PassFail ? (category == "PASS" ? "P 累计" : "F 累计") :
                $"{(kind == MapTableKind.SBin ? "SBIN" : "HBIN")} {category} 累计";
            return categories[category] = new() { Data = data, Category = category, Title = title, Color = color };
        }
        if (kind == MapTableKind.PassFail)
        {
            Add("PASS", WaferMapWindow.ColorForCategory("PASS"));
            Add("FAIL", WaferMapWindow.ColorForCategory("FAIL"));
        }
        foreach (var wafer in pages.GroupBy(p => p.WaferId, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            // A wafer contributes at most once per coordinate, even if input contains duplicate snapshots.
            var latest = new Dictionary<(int X, int Y), WaferDie>();
            foreach (var page in wafer)
                foreach (var die in page.Dies)
                {
                    token.ThrowIfCancellationRequested();
                    latest[(die.X, die.Y)] = die;
                }
            if (latest.Count == 0) continue;
            data.WaferCount++;
            foreach (var (position, die) in latest)
            {
                token.ThrowIfCancellationRequested();
                data.Totals[position] = data.Totals.GetValueOrDefault(position) + 1;
                data.ObservationCount++;
                string category = kind == MapTableKind.PassFail ? die.Category.ToUpperInvariant() : BinPalette.Key(die.Category);
                if (kind == MapTableKind.PassFail && category is not ("PASS" or "FAIL"))
                { data.UnclassifiedCount++; continue; }
                var group = Add(category, die.Color);
                group.Hits[position] = group.Hits.GetValueOrDefault(position) + 1;
                group.Count++;
            }
        }
        data.Categories = data.CoordinateCount == 0 ? Array.Empty<CumulativeMapCategory>() :
            kind == MapTableKind.PassFail ? new[] { categories["PASS"], categories["FAIL"] } :
            categories.Values.OrderBy(c => NumericText.SortKey(c.Category)).ThenBy(c => c.Category, StringComparer.Ordinal).ToArray();
        return data;
    }
}

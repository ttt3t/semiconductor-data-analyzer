using System.Globalization;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

public enum MapTableKind { SBin, HBin, PassFail }
public readonly record struct MapTableEntry(int X, int Y, string? SBin, string? HBin, string? Category);
public sealed record MapTableText(string Text, int Columns, int Rows, int DieCount, int ErrorCount);

/// <summary>Creates a tab-separated, coordinate-preserving map only when the user copies it.</summary>
public static class MapTableCopyService
{
    private const long MaxCells = 4_000_000;
    private const long MaxCharacters = 32_000_000;

    public static MapTableText Build(IEnumerable<MapTableEntry> entries, MapTableKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var cells = new Dictionary<(int X, int Y), string>();
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var die in entries)
        {
            string value = kind switch
            {
                MapTableKind.SBin => BinText(die.SBin),
                MapTableKind.HBin => BinText(die.HBin),
                _ => die.Category?.Trim().ToUpperInvariant() switch { "PASS" => "P", "FAIL" => "F", _ => "E" }
            };
            // Match the map renderer: the last record at a coordinate replaces the earlier one.
            cells[(die.X, die.Y)] = value;
            minX = Math.Min(minX, die.X); maxX = Math.Max(maxX, die.X);
            minY = Math.Min(minY, die.Y); maxY = Math.Max(maxY, die.Y);
        }
        if (cells.Count == 0) return new("", 0, 0, 0, 0);
        long columns = (long)maxX - minX + 1, rows = (long)maxY - minY + 1;
        // Include the coordinate headers in Excel's row/column limits. Reject bad coordinate spans
        // before expanding a sparse wafer into a potentially enormous rectangular string.
        if (columns > 16_383 || rows > 1_048_575 || (columns + 1) * (rows + 1) > MaxCells)
            throw new InvalidOperationException("坐标范围过大，无法复制为 Map 表格；请检查坐标或缩小当前晶圆 / Site 范围。");
        long estimate = (columns + 1) * (rows + 1) + (columns + rows) * 12 + rows + 8;
        int errors = 0;
        foreach (string value in cells.Values)
        {
            estimate += value.Length;
            if (kind == MapTableKind.PassFail && value == "E") errors++;
        }
        if (estimate > MaxCharacters)
            throw new InvalidOperationException("Map 表格文本过大，无法复制；请缩小当前晶圆 / Site 范围。");

        var text = new StringBuilder((int)estimate);
        text.Append("Y\\X");
        for (long x = minX; x <= maxX; x++) text.Append('\t').Append(x.ToString(CultureInfo.InvariantCulture));
        text.Append("\r\n");
        // The GUI uses a top-left origin: both coordinate axes increase right/down.
        for (long y = minY; y <= maxY; y++)
        {
            text.Append(y.ToString(CultureInfo.InvariantCulture));
            for (long x = minX; x <= maxX; x++)
            {
                text.Append('\t');
                if (cells.TryGetValue(((int)x, (int)y), out string? value)) text.Append(value);
            }
            text.Append("\r\n");
        }
        return new(text.ToString(), (int)columns, (int)rows, cells.Count, errors);
    }

    private static string BinText(string? value) => string.IsNullOrWhiteSpace(value) ? "" :
        value.Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

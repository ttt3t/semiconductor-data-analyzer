using System.Buffers;
using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record FailChipExportResult(int FailChips, int Records, string? ReferenceSerial)
{
    public int RetestGoodChips { get; init; }
}

/// <summary>Streams rows directly into XLSX; never holds a workbook-sized cell graph in memory.</summary>
public static class FailChipXlsxService
{
    private static readonly string[] IdentityHeaders = { "组别", "Wafer", "X", "Y", "SN / UUID", "Site", "测试顺序", "原始序列号", "SBIN", "HBIN", "HBIN 判定", "Record ID" };
    private sealed record Chip(List<CsvRecord> History, List<CsvRecord> Rows, bool Good);

    public static FailChipExportResult Save(string path, CsvParseResult data, AnalyzerConfig config,
        IReadOnlyCollection<string> wafers, RetestMode mode, double? outlierSigma = null)
    {
        AnalysisOperation.CheckCancellation();
        var selectedWafers = wafers.ToHashSet(StringComparer.Ordinal);
        if (selectedWafers.Count == 0 || selectedWafers.Except(data.Wafers).Any()) throw new InvalidDataException("请选择有效晶圆。");
        if (outlierSigma.HasValue && (!double.IsFinite(outlierSigma.Value) || outlierSigma <= 0)) throw new InvalidDataException("离群点 σ 必须大于 0。");
        var source = data.Records.Where(r => selectedWafers.Contains(r.WaferId)).ToList();
        var selected = DatasetService.SelectRetestRecords(source, mode);
        var byId = selected.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var classifier = new HBinClassifier(config);
        var chips = source.GroupBy(DatasetService.ChipKey).Select(g =>
        {
            var history = g.ToList();
            return new Chip(history, history.Where(r => byId.ContainsKey(r.Id)).Select(r => byId[r.Id]).ToList(),
                classifier.Classify(history[^1].HBin) == OriginalDisposition.Pass);
        }).Where(c => c.Rows.Count > 0).ToArray();
        var fails = chips.Where(c => !c.Good).ToArray();
        var retestGoods = chips.Where(c => c.Good && c.History.Count > 1).ToArray();
        if (fails.Length == 0 && retestGoods.Length == 0)
            throw new InvalidDataException("所选晶圆及复测口径下没有 Fail 芯片或复测良品芯片。");
        int columns = IdentityHeaders.Length + data.Items.Count;
        if (columns > 16384) throw new InvalidDataException("测试项数量超过 Excel 列数上限，请减少测试项。");
        var bounds = outlierSigma.HasValue ? IntegratedCsvService.CalculateOutlierBounds(data.Items, selected, outlierSigma.Value) : null;
        using var rows = new RowReader(data.Items);
        var stats = new (int Count, double Mean, double M2)[data.Items.Count];
        var vector = new double[data.Items.Count];
        void ReadChip(Chip chip)
        {
            Array.Fill(vector, double.NaN);
            foreach (var record in chip.Rows)
            {
                rows.Read(record);
                for (int i = 0; i < vector.Length; i++)
                    if (rows.Present(i)) vector[i] = rows.TryNumber(i, out double value) &&
                        (bounds == null || value >= bounds[i].Lower && value <= bounds[i].Upper) ? value : double.NaN;
            }
        }
        foreach (var chip in chips.Where(c => c.Good && fails.Length > 0))
        {
            AnalysisOperation.CheckCancellation(); ReadChip(chip);
            for (int i = 0; i < vector.Length; i++) if (double.IsFinite(vector[i]))
            {
                ref var s = ref stats[i]; s.Count++; double delta = vector[i] - s.Mean;
                s.Mean += delta / s.Count; s.M2 += delta * (vector[i] - s.Mean);
            }
        }
        Chip? reference = null; int coverage = 0; double best = double.PositiveInfinity;
        foreach (var chip in chips.Where(c => c.Good && fails.Length > 0))
        {
            AnalysisOperation.CheckCancellation(); ReadChip(chip);
            int measured = 0; double score = 0;
            for (int i = 0; i < vector.Length; i++) if (double.IsFinite(vector[i]) && stats[i].Count > 0)
            {
                double sigma = Math.Sqrt(Math.Max(0, stats[i].M2 / stats[i].Count));
                double distance = (vector[i] - stats[i].Mean) / (sigma > 0 && double.IsFinite(sigma) ? sigma : Math.Max(1, Math.Abs(stats[i].Mean)));
                measured++; score += distance * distance;
            }
            if (measured > 0 && (measured > coverage || measured == coverage && score / measured < best))
            { reference = chip; coverage = measured; best = score / measured; }
        }
        // Keep the existing reference in the Fail comparison sheet only.
        if (fails.Length == 0) reference = null;
        var output = (reference == null ? Enumerable.Empty<Chip>() : new[] { reference }).Concat(fails).ToArray();
        void ValidateRows(IReadOnlyList<Chip> sheetChips)
        {
            long totalRows = 7L + sheetChips.Sum(c => (long)c.Rows.Count);
            if (totalRows > 1048576) throw new InvalidDataException("导出行数超过 Excel 单个工作表上限，请减少所选晶圆。");
        }
        ValidateRows(output);
        ValidateRows(retestGoods);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SpreadsheetDocument.Create(temp, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart(); workbook.Workbook = new Workbook();
                var styles = workbook.AddNewPart<WorkbookStylesPart>(); styles.Stylesheet = Styles(); styles.Stylesheet.Save();
                var sheets = workbook.Workbook.AppendChild(new Sheets());
                void WriteSheet(string name, IReadOnlyList<Chip> sheetChips, bool includeReference = false)
                {
                    var sheet = workbook.AddNewPart<WorksheetPart>();
                    using var writer = OpenXmlWriter.Create(sheet);
                    writer.WriteStartElement(new Worksheet());
                    writer.WriteElement(new SheetViews(new SheetView(new Pane { HorizontalSplit = 1, VerticalSplit = 1,
                        TopLeftCell = "B2", ActivePane = PaneValues.BottomRight, State = PaneStateValues.Frozen }) { WorkbookViewId = 0 }));
                    writer.WriteElement(new SheetFormatProperties { DefaultRowHeight = 20 });
                    writer.WriteElement(new Columns(new Column { Min = 1, Max = 12, Width = 16, CustomWidth = true },
                        new Column { Min = 13, Max = (uint)Math.Max(13, columns), Width = 20, CustomWidth = true }));
                    writer.WriteStartElement(new SheetData());
                    uint row = 0;
                    void WriteRow(IEnumerable<(string Text, double? Number, uint Style)> cells)
                    {
                        AnalysisOperation.CheckCancellation(); row++;
                        writer.WriteStartElement(new Row { RowIndex = row, Height = row == 1 ? 44 : 23, CustomHeight = true });
                        int column = 0;
                        foreach (var cell in cells)
                        {
                            var value = new Cell { CellReference = ColumnName(++column) + row, StyleIndex = cell.Style };
                            if (cell.Number.HasValue) { value.DataType = CellValues.Number; value.CellValue = new CellValue(cell.Number.Value.ToString("R", CultureInfo.InvariantCulture)); }
                            else { value.DataType = CellValues.InlineString; value.InlineString = new InlineString(new Text(Clean(cell.Text)) { Space = SpaceProcessingModeValues.Preserve }); }
                            writer.WriteElement(value);
                        }
                        writer.WriteEndElement();
                    }
                    (string, double?, uint) T(string text, uint style = 0) => (text, null, style);
                    WriteRow(IdentityHeaders.Concat(data.Items.Select(i => i.DisplayName)).Select(s => T(s, 5)));
                    void Metadata(string label, Func<TestItemData, (string, double?, uint)> value) => WriteRow(
                        new[] { T(label, 6) }.Concat(Enumerable.Repeat(T("", 6), IdentityHeaders.Length - 1)).Concat(data.Items.Select(value)));
                    Metadata("#TestNumber", i => T(i.TestNumber, 6)); Metadata("#Unit", i => T(i.ShownUnit, 6));
                    Metadata("#LSL", i => ("", i.DisplayLowLimit, 6)); Metadata("#USL", i => ("", i.DisplayHighLimit, 6));
                    Metadata("#SBIN", i => T(i.SBin, 6)); Metadata("#HBIN", i => T(i.HBin, 6));
                    int chipIndex = 0, failIndex = 0, goodIndex = 0;
                    foreach (var chip in sheetChips)
                    {
                        string label = includeReference && ReferenceEquals(chip, reference) ? "良品参考" : chip.Good ? "复测良品 " + ++goodIndex : "Fail 芯片 " + ++failIndex;
                        bool dark = chipIndex++ % 2 != 0;
                        uint passStyle = dark ? 8u : 1u, failStyle = dark ? 9u : 2u;
                        uint invalidStyle = dark ? 10u : 3u, absentStyle = dark ? 11u : 4u;
                        uint identityStyle = dark ? 13u : 12u;
                        var order = chip.History.Select((r, n) => (r.Id, n)).ToDictionary(p => p.Id, p => p.n);
                        foreach (var record in chip.Rows)
                        {
                            rows.Read(record);
                            IEnumerable<(string, double?, uint)> Cells()
                            {
                                string[] identity = { label, record.WaferId, record.X?.ToString() ?? "", record.Y?.ToString() ?? "", record.RetestUuid ?? record.SerialNumber,
                                    record.Site, order[record.Id] == 0 ? "首测" : "复测 " + order[record.Id], record.OriginalSerialNumber ?? record.SerialNumber,
                                    record.SBin, record.HBin, classifier.Classify(record.HBin).ToString(), record.Id };
                                for (int i = 0; i < identity.Length; i++) yield return T(identity[i], i == 10 ? classifier.Classify(record.HBin) == OriginalDisposition.Pass ? passStyle : failStyle : identityStyle);
                                for (int i = 0; i < data.Items.Count; i++)
                                {
                                    if (!rows.Present(i)) { yield return T("", absentStyle); continue; }
                                    var item = data.Items[i];
                                    if (!rows.TryNumber(i, out double number))
                                    {
                                        string raw = rows.Text(i);
                                        bool absent = config.NotApplicableTokens.Concat(config.MissingTokens).Append(StdfParser.NotApplicableValue).Contains(raw.Trim(), StringComparer.OrdinalIgnoreCase);
                                        yield return T(raw, absent ? absentStyle : invalidStyle); continue;
                                    }
                                    if (bounds != null && (number < bounds[i].Lower || number > bounds[i].Upper)) { yield return T("", absentStyle); continue; }
                                    bool failed = item.LowLimit.HasValue && number < item.LowLimit || item.HighLimit.HasValue && number > item.HighLimit;
                                    yield return ("", number * item.DisplayFactor, failed ? failStyle : passStyle);
                                }
                            }
                            WriteRow(Cells());
                        }
                    }
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    sheets.Append(new Sheet { Name = name, SheetId = (uint)sheets.ChildElements.Count + 1, Id = workbook.GetIdOfPart(sheet) });
                }
                WriteSheet("Fail 芯片", output, includeReference: true);
                WriteSheet("复测良品芯片", retestGoods);
                workbook.Workbook.Save();
            }
            AnalysisOperation.CheckCancellation(); File.Move(temp, path, true);
            return new(fails.Length, output.Sum(c => c.Rows.Count) + retestGoods.Sum(c => c.Rows.Count), reference?.History[^1].SerialNumber)
                { RetestGoodChips = retestGoods.Length };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string ColumnName(int column)
    { string result = ""; while (column > 0) { column--; result = (char)('A' + column % 26) + result; column /= 26; } return result; }
    private static string Clean(string value) => new(value.Take(32767).Where(c => c >= ' ' || c is '\n' or '\r' or '\t').ToArray());
    private static Stylesheet Styles()
    {
        var fonts = new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Calibri" }));
        var fills = new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }));
        foreach (string color in new[] { "FFE2F0D9", "FFFCE0E8", "FFFFF2CC", "FFF1F3F5", "FF315B82", "FFE5EEF8", "FFD9E2F3",
            "FFA9D18E", "FFF2A0AF", "FFFFE699", "FFD9DEE3", "FFF9FBFD", "FFE4EAF2" })
            fills.Append(new Fill(new PatternFill(new ForegroundColor { Rgb = color }) { PatternType = PatternValues.Solid }));
        var formats = new CellFormats(new CellFormat { FontId = 0, FillId = 0, BorderId = 0 });
        for (uint i = 1; i <= 13; i++) formats.Append(new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = i == 5 })
            { FillId = i + 1, FontId = i == 5 ? 1u : 0u, BorderId = 0, ApplyFill = true, ApplyAlignment = true });
        return new Stylesheet(fonts, fills, new Borders(new Border()), new CellStyleFormats(new CellFormat()), formats,
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }));
    }

    private sealed class RowReader : IDisposable
    {
        private readonly IReadOnlyList<TestItemData> _items;
        private readonly Dictionary<MeasurementSchema, int[]> _schemas = new();
        private CsvRecord _record = null!; private int[]? _indices;
        private RawMeasurementStore.RawRow? _row; private byte[]? _buffer;
        public RowReader(IReadOnlyList<TestItemData> items) => _items = items;
        public void Read(CsvRecord record)
        {
            AnalysisOperation.CheckCancellation(); _record = record; _row = null; _indices = null;
            if (record.Measurements.IsOnDisk && record.Measurements.Schema is { } schema)
            {
                if (!_schemas.TryGetValue(schema, out _indices)) _schemas[schema] = _indices = _items.Select(i => schema.Indices.GetValueOrDefault(i.Id, -1)).ToArray();
                if (record.Measurements.Count > 0) _row = record.Measurements.ReadRow(ref _buffer);
            }
        }
        public bool Present(int i) => _indices == null ? _record.Measurements.ContainsKey(_items[i].Id) :
            _row != null && _indices[i] >= 0 && _record.Measurements.PresentAt(_indices[i]);
        public bool TryNumber(int i, out double value)
        {
            if (_row != null && _indices != null && _row.IsNumeric(_indices[i])) { value = _row.ReadNumeric(_indices[i]); return double.IsFinite(value); }
            return DatasetService.TryValue(Text(i), out value);
        }
        public string Text(int i) => _row != null && _indices != null ? _row.RawTextSpan(_indices[i]).ToString() : _record.Measurements.GetValueOrDefault(_items[i].Id, "");
        public void Dispose() { if (_buffer != null) ArrayPool<byte>.Shared.Return(_buffer); _buffer = null; }
    }
}

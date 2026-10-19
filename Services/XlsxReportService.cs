using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClosedXML.Excel;
using SemiconductorCsvAnalyzer.Controls;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record XlsxReportOptions(IReadOnlyCollection<string> Wafers, RetestMode RetestMode,
    double? OutlierSigma, int HistogramMode, bool CombineSites, IReadOnlyCollection<string> FocusedItemIds,
    bool ShowBinNumbers = false, bool ShowSiteBorders = false, bool CombineWafers = true,
    ScatterOrder ScatterOrder = ScatterOrder.SerialNumber);

public static class XlsxReportService
{
    private static readonly string[] SummaryHeaders = { "关注", "Test Num", "Test Item", "Site", "Wafer", "SBIN", "HBIN", "Unit",
        "LSL", "USL", "Count", "Fails", "Error", "Site σ", "Mean", "Max", "Min", "Median", "σ", "Robust Mean", "Robust Sigma", "Cpk", "InSpec%" };
    private static int SummaryLastColumn => SummaryHeaders.Length + 1;
    private static readonly XLColor Ink = XLColor.FromHtml("#20344A"), Header = XLColor.FromHtml("#315B82"),
        Stripe = XLColor.FromHtml("#F1F5F9"), Rule = XLColor.FromHtml("#CBD5E1");

    // A separate STA owns temporary WPF drawings. The main dispatcher keeps animating;
    // workbook/images and its rendering dispatcher are released when this export ends.
    public static Task SaveAsync(string path, CsvParseResult data, AnalyzerConfig config,
        XlsxReportOptions options, string sourceName, CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        var token = cancellationToken;
        var lease = new DatasetLease(data.Records);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                using (lease)
                {
                    using var operation = new AnalysisOperation(token);
                    token.ThrowIfCancellationRequested();
                    Save(path, data, config, options, sourceName);
                }
                completion.SetResult();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.SetCanceled(token); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "XLSX report export" };
        worker.SetApartmentState(ApartmentState.STA);
        try { worker.Start(); }
        catch { lease.Dispose(); throw; }
        return completion.Task;
    }

    public static void Save(string path, CsvParseResult data, AnalyzerConfig config,
        XlsxReportOptions options, string sourceName)
    {
        AnalysisOperation.CheckCancellation();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("报告绘图需要 STA 线程，请调用 SaveAsync。");
        if (data.Records.Count == 0) throw new InvalidDataException("没有可导出的测试记录。");
        if (options.HistogramMode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(options.HistogramMode));
        if (options.OutlierSigma is double n && (!double.IsFinite(n) || n <= 0))
            throw new ArgumentOutOfRangeException(nameof(options.OutlierSigma));
        var records = IntegratedCsvService.SelectRecords(data, options.Wafers, options.RetestMode);
        var bounds = options.OutlierSigma.HasValue
            ? IntegratedCsvService.CalculateOutlierBounds(data.Items, records, options.OutlierSigma.Value) : null;
        var rowsById = records.Select((record, index) => (record.Id, index)).ToDictionary(p => p.Id, p => p.index);
        var sites = data.Records.Where(r => options.Wafers.Contains(r.WaferId) && r.Measurements.Count > 0)
            .Select(r => SiteKey(r.Site)).Distinct()
            .OrderBy(NumericText.SortKey).ThenBy(s => s, StringComparer.Ordinal).ToArray();
        if (sites.Length == 0) sites = new[] { "(空)" };
        var wafers = data.Wafers.Where(options.Wafers.Contains).ToArray();
        var focused = options.FocusedItemIds.ToHashSet(StringComparer.Ordinal);
        if ((long)data.Items.Count * (options.CombineSites ? 1 : sites.Length) *
            (options.CombineWafers ? 1 : wafers.Length) > 1_048_568)
            throw new InvalidDataException("统计行数超过 Excel 单个工作表上限，请减少所选晶圆或合并 Site / Wafer。");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        try
        {
            using (var workbook = new XLWorkbook())
            {
                workbook.Style.Font.FontName = "Microsoft YaHei";
                workbook.Style.Font.FontSize = 10;
                workbook.Style.Font.FontColor = Ink;
                var summaries = Sheet(workbook, "Testitem Yield", "测试项统计", sourceName, Context(options));
                var sbins = Sheet(workbook, "SBIN Map", "SBIN 分布与分类", sourceName, BinContext(options));
                var hbins = Sheet(workbook, "HBIN Map", "HBIN 分布与统计", sourceName, BinContext(options));
                var favorites = Sheet(workbook, "关注测项", "关注测试项", sourceName,
                    Context(options) + "；关注图表合并 Site 和 Wafer；直方图：" + HistogramName(options.HistogramMode) +
                    "；散点排序：" + (options.ScatterOrder == ScatterOrder.Site ? "Site → SN" : options.ScatterOrder == ScatterOrder.Wafer ? "晶圆 → SN" : "SN"));
                SummaryColumns(summaries); SummaryColumns(favorites);
                WriteHeader(summaries, 7, 2, SummaryHeaders);
                int summaryRow = 8, focusRow = 7;
                for (int i = 0; i < data.Items.Count; i++)
                {
                    AnalysisOperation.CheckCancellation();
                    // Build just this item's selected column, not an extra copy of the full dataset.
                    var item = SelectItem(data.Items[i], records, rowsById, bounds?[i]);
                    foreach (var summary in SummaryService.Build(new[] { item }, sites, wafers,
                        options.CombineSites, options.CombineWafers, config))
                        WriteSummary(summaries, summaryRow++, summary, focused.Contains(item.Id));
                    if (!focused.Contains(item.Id)) continue;
                    var combined = SummaryService.Build(new[] { item }, sites, true, config)[0];
                    WriteHeader(favorites, focusRow, 2, SummaryHeaders);
                    WriteSummary(favorites, focusRow + 1, combined, true);
                    var detail = DetailCalculator.Calculate(new(item, null, options.HistogramMode, config.BinCount,
                        config.IncludeOutOfLimitData, config.ShowLowLimit, config.ShowHighLimit, config.ShowMean, config.ShowThreeSigma,
                        ScatterOrder: options.ScatterOrder, WaferOrder: wafers), AnalysisOperation.Token);
                    var histogram = new HistogramControl();
                    histogram.Apply(detail.Bins, item.DisplayLowLimit, item.DisplayHighLimit, detail.Mean, detail.Sigma,
                        config.ShowLowLimit, config.ShowHighLimit, config.ShowMean, config.ShowThreeSigma, item.ShownUnit);
                    Plot(favorites, focusRow + 3, 2, histogram, "Histogram_" + i, 1440, 400);
                    var scatter = new ScatterPlotControl();
                    scatter.SetSeries(detail.Scatter, item.DisplayLowLimit, item.DisplayHighLimit, item.ShownUnit);
                    Plot(favorites, focusRow + 21, 2, scatter, "Scatter_" + i, 1440, 400);
                    histogram.Bins = null; scatter.SetSeries(ScatterSeries.Empty, null, null, "");
                    favorites.PageSetup.AddHorizontalPageBreak(focusRow + 39);
                    focusRow += 41;
                }
                if (summaryRow > 8) summaries.Range(7, 2, summaryRow - 1, SummaryLastColumn).SetAutoFilter();
                if (data.Items.Count == 0) summaries.Cell(8, 2).Value = "源文件仅包含芯片/BIN 信息，没有逐项测试结果。";
                summaries.SheetView.Freeze(7, 4);
                summaries.PageSetup.SetRowsToRepeatAtTop(7, 7);
                if (focusRow == 7) favorites.Cell(7, 2).Value = "未勾选关注测项。可在主列表的“关注”列选择后重新导出。";
                BinSheet(sbins, data, config, options, soft: true);
                BinSheet(hbins, data, config, options, soft: false);
                summaries.PageSetup.PrintAreas.Add(1, 2, Math.Max(8, summaryRow - 1), SummaryLastColumn);
                favorites.PageSetup.PrintAreas.Add(1, 2, Math.Max(8, focusRow - 2), SummaryLastColumn);
                AnalysisOperation.CheckCancellation();
                workbook.SaveAs(temp);
            }
            AnalysisOperation.CheckCancellation();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static TestItemData SelectItem(TestItemData source, List<CsvRecord> records,
        Dictionary<string, int> rowsById, (double Lower, double Upper)? bounds)
    {
        var item = DatasetService.CloneItem(source);
        item.Values.Initialize(records, item.Id);
        foreach (var sample in source.Values)
        {
            AnalysisOperation.CheckCancellation();
            if (rowsById.TryGetValue(sample.Record.Id, out int row) && records[row].Measurements.ContainsKey(item.Id) &&
                (!bounds.HasValue || sample.Value >= bounds.Value.Lower && sample.Value <= bounds.Value.Upper))
                item.Values.SetRow(row, sample.Value);
        }
        item.Values.Complete();
        foreach (var error in source.Errors)
            if (rowsById.TryGetValue(error.Id, out int row) && records[row].Measurements.ContainsKey(item.Id))
                item.Errors.Add(records[row]);
        return item;
    }

    private static string SiteKey(string site) => string.IsNullOrWhiteSpace(site) ? "(空)" : site;
    private static string ModeName(RetestMode mode) => mode switch
    { RetestMode.First => "只保留首测", RetestMode.Last => "复测覆盖首测",
        RetestMode.OnlyRetests => "只保留复测", _ => "首测与复测均保留" };
    private static string HistogramName(int mode) => mode switch { 1 => "规格区间", 2 => "中位数中心", _ => "固定等宽" };
    private static string Context(XlsxReportOptions options) =>
        $"晶圆 {options.Wafers.Count} 个 | {ModeName(options.RetestMode)} | " +
        (options.OutlierSigma.HasValue ? $"剔除均值 ± {options.OutlierSigma.Value:G}σ 以外的测项值（单次）" : "不剔除离群点") +
        (options.CombineSites ? " | 合并 Site" : " | 按 Site 分行") +
        (options.CombineWafers ? " | 合并 Wafer" : " | 按 Wafer 分行");
    private static string BinContext(XlsxReportOptions options) =>
        (options.RetestMode == RetestMode.OnlyRetests ? "仅统计有复测测项的芯片；" : "") +
        "按晶圆与芯片选择整条" + (options.RetestMode == RetestMode.First ? "首测" : "末测") +
        "记录；Map 仅绘制有完整坐标的芯片。BIN 占比以有相应 BIN 的芯片为分母。";

    private static IXLWorksheet Sheet(XLWorkbook workbook, string name, string title, string source, string context)
    {
        var sheet = workbook.Worksheets.Add(name);
        sheet.ShowGridLines = false;
        sheet.TabColor = Header;
        sheet.Column(1).Width = 3;
        sheet.RowHeight = 18;
        sheet.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Cell(2, 2).Value = title;
        sheet.Cell(2, 2).Style.Font.FontSize = 16;
        sheet.Cell(2, 2).Style.Font.Bold = true;
        sheet.Row(2).Height = 28;
        sheet.Cell(3, 2).Value = "数据：" + source;
        sheet.Cell(4, 2).Value = context;
        sheet.Row(4).Height = 25;
        sheet.Range(5, 2, 5, 20).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        sheet.Range(5, 2, 5, 20).Style.Border.BottomBorderColor = Rule;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = XLPaperSize.A3Paper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.SheetView.ZoomScale = 85;
        return sheet;
    }

    private static void SummaryColumns(IXLWorksheet sheet)
    {
        double[] widths = { 6, 12, 34, 9, 16, 9, 9, 10, 14, 14, 11, 9, 9, 13, 15, 15, 15, 15, 15, 16, 16, 12, 12 };
        for (int i = 0; i < widths.Length; i++) sheet.Column(i + 2).Width = widths[i];
    }

    private static void WriteHeader(IXLWorksheet sheet, int row, int col, params string[] headers)
    {
        for (int i = 0; i < headers.Length; i++) sheet.Cell(row, col + i).Value = headers[i];
        var range = sheet.Range(row, col, row, col + headers.Length - 1);
        range.Style.Fill.BackgroundColor = Header;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorderColor = XLColor.White;
        sheet.Row(row).Height = 26;
    }

    private static void WriteSummary(IXLWorksheet sheet, int row, TestItemSummary summary, bool focused)
    {
        var item = summary.Data;
        object?[] values = { focused ? "✓" : "", item.TestNumber, item.DisplayName, summary.Site, summary.Wafer, item.SBin, item.HBin, item.ShownUnit,
            item.DisplayLowLimit, item.DisplayHighLimit, summary.Count, summary.Fails, summary.Eorr, summary.SiteSigma,
            summary.Count > 0 ? summary.Mean : null, summary.Max, summary.Min, summary.Median, summary.Count > 0 ? summary.Sigma : null,
            summary.RobustMean, summary.RobustSigma, summary.Cpk, summary.Count > 0 ? summary.InSpecPercent / 100 : null };
        for (int i = 0; i < values.Length; i++) Value(sheet.Cell(row, i + 2), values[i]);
        var range = sheet.Range(row, 2, row, SummaryLastColumn);
        if (row % 2 == 0) range.Style.Fill.BackgroundColor = Stripe;
        range.Style.Alignment.WrapText = false;
        sheet.Row(row).Height = 25;
        sheet.Cell(row, 4).Style.Alignment.WrapText = true;
        int textWidth = item.DisplayName.Sum(c => c > 255 ? 2 : 1);
        if (textWidth > 32) sheet.Row(row).Height = 25 * Math.Ceiling(textWidth / 32.0);
        sheet.Cell(row, 12).Style.NumberFormat.Format = "#,##0";
        sheet.Cell(row, 13).Style.NumberFormat.Format = "#,##0";
        sheet.Cell(row, 14).Style.NumberFormat.Format = "#,##0";
        sheet.Cell(row, SummaryLastColumn).Style.NumberFormat.Format = "0.00%";
        sheet.Cell(row, SummaryLastColumn - 1).Style.NumberFormat.Format = "0.000";
        sheet.Cell(row, 13).Style.Fill.BackgroundColor = XLColor.FromHtml(summary.Fails == 0 ? "#DCF3E2" : "#FCE0E8");
        if (summary.Eorr > 0) sheet.Cell(row, 14).Style.Font.FontColor = XLColor.FromHtml("#B42318");
    }

    private static void Value(IXLCell cell, object? value)
    {
        if (value is double number)
        {
            if (!double.IsFinite(number)) { cell.Value = "-"; return; }
            cell.Value = number;
            cell.Style.NumberFormat.Format = number != 0 && (Math.Abs(number) < .0001 || Math.Abs(number) >= 1e9)
                ? "0.00000E+00" : "0.######";
        }
        else if (value is int count) { cell.Value = count; cell.Style.NumberFormat.Format = "#,##0"; }
        else cell.Value = value?.ToString() ?? "-";
    }

    private static void BinSheet(IXLWorksheet sheet, CsvParseResult data, AnalyzerConfig config, XlsxReportOptions options, bool soft)
    {
        var colors = BinPalette.Create(data.Records.Select(r => soft ? r.SBin : r.HBin));
        XLColor BinColor(string bin)
        {
            var color = ((SolidColorBrush)colors[BinPalette.Key(bin)]).Color;
            return XLColor.FromArgb(color.R, color.G, color.B);
        }
        sheet.Columns(2, 11).Width = 11;
        sheet.Column(12).Width = 3;
        sheet.Column(13).Width = 8; sheet.Column(14).Width = 18;
        sheet.Columns(15, 20).Width = 12;
        int row = 7, index = 0;
        var mode = options.RetestMode == RetestMode.First ? RetestMode.First : RetestMode.Last;
        foreach (string wafer in data.Wafers.Where(options.Wafers.Contains))
        {
            AnalysisOperation.CheckCancellation();
            var raw = data.Records.Where(r => r.WaferId == wafer).ToList();
            if (options.RetestMode == RetestMode.OnlyRetests)
            {
                var retested = DatasetService.SelectRetestRecords(raw, RetestMode.OnlyRetests)
                    .Select(DatasetService.ChipKey).ToHashSet();
                raw = raw.Where(r => retested.Contains(DatasetService.ChipKey(r))).ToList();
            }
            var chosen = new Dictionary<(string Wafer, int? X, int? Y, string Fallback), CsvRecord>();
            foreach (var record in raw)
            {
                AnalysisOperation.CheckCancellation();
                if (mode == RetestMode.First) chosen.TryAdd(DatasetService.ChipKey(record), record);
                else chosen[DatasetService.ChipKey(record)] = record;
            }
            var dies = chosen.Values.Where(r => r.X.HasValue && r.Y.HasValue).Select(r =>
            {
                string category = soft ? r.SBin : r.HBin;
                if (string.IsNullOrWhiteSpace(category)) category = "(空)";
                return new WaferDie { X = r.X!.Value, Y = r.Y!.Value, WaferId = wafer, Site = r.Site,
                    Category = category, Color = colors[category] };
            }).ToList();
            var stats = BinStatisticsService.Build(raw, mode, config);
            sheet.Cell(row, 2).Value = "Wafer：" + wafer;
            sheet.Cell(row, 2).Style.Font.Bold = true;
            sheet.Row(row).Height = 27;
            sheet.Cell(row + 1, 2).Value = $"芯片 {stats.ChipCount:N0} | 可绘制 {dies.Count:N0} | 缺完整坐标 {stats.ChipCount - dies.Count:N0} | 无 BIN {(soft ? stats.MissingSBin : stats.MissingHBin):N0}";
            var map = new WaferMapControl { Dies = dies,
                ShowBinNumbers = options.ShowBinNumbers, ShowSiteBorders = options.ShowSiteBorders };
            if (dies.Count > 0) Plot(sheet, row + 3, 2, map, "Wafer_" + index++, 740, 500);
            else sheet.Cell(row + 4, 2).Value = raw.Count == 0
                ? "该晶圆没有符合当前复测口径的记录。" : "该晶圆没有完整 X/Y 坐标，无法绘制 Map；统计如下。";
            map.Dies = null;
            WriteHeader(sheet, row + 3, 13, "颜色", soft ? "SBIN" : "HBIN");
            int legendRow = row + 4;
            foreach (string category in dies.Select(d => d.Category).Distinct().OrderBy(NumericText.SortKey).ThenBy(s => s))
            {
                sheet.Cell(legendRow, 13).Style.Fill.BackgroundColor = BinColor(category);
                sheet.Cell(legendRow++, 14).Value = category;
            }
            int statRow = Math.Max(row + 26, legendRow + 2);
            var bins = soft ? stats.SBins : stats.HBins;
            WriteHeader(sheet, statRow, 2, soft ? new[] { "SBIN", "数量", "占比", "分类", "释义" } : new[] { "HBIN", "数量", "占比" });
            int detailRow = statRow + 1;
            foreach (var bin in bins)
            {
                sheet.Cell(detailRow, 2).Value = bin.Bin;
                sheet.Cell(detailRow, 2).Style.Fill.BackgroundColor = BinColor(bin.Bin);
                var color = ((SolidColorBrush)colors[BinPalette.Key(bin.Bin)]).Color;
                sheet.Cell(detailRow, 2).Style.Font.FontColor = color.R * .299 + color.G * .587 + color.B * .114 < 150 ? XLColor.White : Ink;
                Value(sheet.Cell(detailRow, 3), bin.Count);
                sheet.Cell(detailRow, 4).Value = bin.Percent / 100;
                sheet.Cell(detailRow, 4).Style.NumberFormat.Format = "0.00%";
                if (soft) { sheet.Cell(detailRow, 5).Value = bin.Category; sheet.Cell(detailRow, 6).Value = bin.Description; }
                detailRow++;
            }
            if (soft)
            {
                WriteHeader(sheet, statRow, 8, "分类", "数量", "占比");
                int categoryRow = statRow + 1;
                foreach (var category in stats.Categories)
                {
                    sheet.Cell(categoryRow, 8).Value = category.Category;
                    Value(sheet.Cell(categoryRow, 9), category.Count);
                    sheet.Cell(categoryRow, 10).Value = category.Percent / 100;
                    sheet.Cell(categoryRow, 10).Style.NumberFormat.Format = "0.00%";
                    categoryRow++;
                }
                detailRow = Math.Max(detailRow, categoryRow);
            }
            row = Math.Max(detailRow, statRow + 2) + 4;
            sheet.PageSetup.AddHorizontalPageBreak(row - 2);
        }
        sheet.PageSetup.PrintAreas.Add(1, 2, Math.Max(8, row - 2), 20);
    }

    private static void Plot(IXLWorksheet sheet, int row, int col, FrameworkElement control, string name, int width, int height)
    {
        if (control is CachedPlotControl cached) cached.UseBitmapCache = false;
        control.Width = width; control.Height = height;
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width * 3 / 2, height * 3 / 2, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        var picture = sheet.AddPicture(stream);
        picture.Name = name;
        picture.MoveTo(sheet.Cell(row, col)).WithSize(width, height);
    }
}

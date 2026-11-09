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

public static class ManagementReportService
{
    public static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Product report" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); return completion.Task;
    }
    public static byte[] TrendImage(ManagementTrendData data, string title, bool yield = true, ManagementChartLayout? layout = null)
    {
        int width = TrendImageWidth(data, layout);
        double chartHeight = layout?.ChartHeight ?? TrendChartHeight(data);
        double headerWidth = layout?.HeaderWidth ?? 150;
        double headerHeight = layout?.HeaderHeight ?? 22, rowHeight = layout?.RowHeight ?? 22;
        double cellWidth = layout?.ColumnWidth ?? (width - 152.0) / Math.Max(1, data.Labels.Length);
        int height = TrendImageHeight(data, layout);
        if (height > 12000) throw new InvalidDataException("报表分类过多，请减少所选分类。");
        // Keep the requested logical size in XLSX. Very wide individual columns
        // use a bounded raster width so extreme zoom cannot allocate huge bitmaps.
        double rasterWidth = Math.Min(16000, Math.Floor(63_000_000.0 / height));
        var imageSize = new ChartImageSize(width, height, 96 * Math.Min(1, rasterWidth / width), 96);
        imageSize.Validate();
        var chart = new ManagementChartControl { Width = width, Height = chartHeight };
        if (layout != null) chart.SetColumnLayout(cellWidth, headerWidth + 1, layout.RightPadding);
        chart.SetData(data);
        chart.Measure(new Size(width, chartHeight)); chart.Arrange(new Rect(0, 0, width, chartHeight)); chart.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var heading = ChartAxis.Text(chart, title, 18, Brushes.Black);
            heading.MaxTextWidth = Math.Max(1, width - 16); heading.MaxLineCount = 1; heading.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(heading, new Point(Math.Max(20, (width - heading.Width) / 2), 4));
            dc.PushTransform(new TranslateTransform(0, 32));
            dc.DrawRectangle(new VisualBrush(chart), null, new Rect(0, 0, width, chartHeight)); dc.Pop();
            double tableY = chartHeight + 32;
            var border = new Pen(Brushes.LightGray, 1); border.Freeze();
            void Text(string text, double x, double y, double available, bool center, bool multiline = false)
            {
                var label = ChartAxis.Text(chart, text, 10, Brushes.Black); label.MaxTextWidth = Math.Max(1, available - 8);
                label.MaxLineCount = multiline ? 2 : 1; label.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(label, new Point(x + (center ? Math.Max(6, (available - label.Width) / 2) : 8), y + 3));
            }
            dc.DrawRectangle(Brushes.AliceBlue, border, new Rect(1, tableY, width - 2, headerHeight)); Text("测试步骤 / 分类", 1, tableY, headerWidth, true);
            for (int c = 0; c < data.Labels.Length; c++) Text(layout == null ? data.Labels[c] : data.Labels[c].Replace(" / ", "\n"), headerWidth + 1 + c * cellWidth, tableY, cellWidth, true, layout != null);
            for (int r = 0; r < data.Series.Count; r++)
            {
                var series = data.Series[r]; double y = tableY + headerHeight + r * rowHeight;
                dc.DrawRectangle(r % 2 == 0 ? Brushes.White : Brushes.WhiteSmoke, border, new Rect(1, y, width - 2, rowHeight));
                dc.DrawLine(new Pen(series.LineBrush, 3), new Point(10, y + rowHeight / 2), new Point(30, y + rowHeight / 2));
                Text(series.Category, 32, y, headerWidth - 31, false);
                for (int c = 0; c < data.Labels.Length; c++)
                {
                    double x = headerWidth + 1 + c * cellWidth; dc.DrawLine(border, new Point(x, y), new Point(x, y + rowHeight));
                    Text(c < series.Values.Length && series.Values[c] is double value ? value.ToString(data.Mode == ManagementChartMode.StackedCounts ? "N0" : "F1", CultureInfo.CurrentCulture) + (data.Mode == ManagementChartMode.StackedCounts ? "" : "%") : "–", x, y, cellWidth, true);
                }
            }
        }
        var bitmap = new RenderTargetBitmap(imageSize.PixelWidth, imageSize.PixelHeight, imageSize.DpiX, imageSize.DpiY, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static int TrendChartHeight(ManagementTrendData data) => data.IsStacked ? 572 : 440;
    public static int TrendImageWidth(ManagementTrendData data, ManagementChartLayout? layout = null) => layout == null ? 1440 :
        checked((int)Math.Ceiling(layout.HeaderWidth + 1 + data.Labels.Length * layout.ColumnWidth + layout.RightPadding));
    public static int TrendImageHeight(ManagementTrendData data, ManagementChartLayout? layout = null) =>
        checked((int)Math.Ceiling((layout?.ChartHeight ?? TrendChartHeight(data)) + 40 + (layout?.HeaderHeight ?? 22) + data.Series.Count * (layout?.RowHeight ?? 22)));
    public static IEnumerable<ManagementTrendData> Pages(ManagementTrendData data, int columnsPerPage = 10, ManagementChartLayout? layout = null)
    {
        if (columnsPerPage <= 0) throw new ArgumentOutOfRangeException(nameof(columnsPerPage));
        if (layout is { ColumnWidth: > 0 })
            columnsPerPage = (int)Math.Clamp(Math.Floor((16000 - layout.HeaderWidth - 1 - layout.RightPadding) / layout.ColumnWidth), 1, columnsPerPage);
        for (int start = 0; start < data.Labels.Length; start += columnsPerPage)
            yield return data with { Labels = data.Labels.Skip(start).Take(columnsPerPage).ToArray(),
                Series = data.Series.Select(s => s with { Values = s.Values.Skip(start).Take(columnsPerPage).ToArray(), Counts = s.Counts?.Skip(start).Take(columnsPerPage).ToArray() }).ToList(),
                SampleCounts = data.SampleCounts?.Skip(start).Take(columnsPerPage).ToArray(),
                Descriptions = data.Descriptions?.Skip(start).Take(columnsPerPage).ToArray(),
                CumulativeStart = data.CumulativeStart + (data.Mode == ManagementChartMode.Pareto && data.Series.Count > 0 ? data.Series[0].Values.Take(start).Sum(v => v ?? 0) : 0) };
    }
    public static void SaveWorkbook(string path, ManagedProduct product, string filter = "", CancellationToken token = default, ManagementPeriod period = ManagementPeriod.Wafer, ManagementReportOptions? options = null)
    {
        if (options != null) { product = ManagementAnalytics.Select(product, options.Scopes); filter = ""; }
        if (product.Snapshot is not { } snapshot) throw new InvalidDataException("请先计算产品良率。");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var book = new XLWorkbook())
            {
                string periodLabel = period == ManagementPeriod.Year ? "按年" : period == ManagementPeriod.Month ? "按月" : period == ManagementPeriod.Lot ? "按批次" : "按晶圆";
                book.Style.Font.FontName = "Microsoft YaHei"; book.Style.Font.FontSize = 10;
                IXLWorksheet Sheet(string name, string[] headers)
                {
                    var sheet = book.AddWorksheet(name); sheet.Cell(1, 1).Value = product.Name + " · " + name;
                    sheet.Cell(2, 1).Value = $"计算时间 {snapshot.CalculatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}；{(product.IncludePartialPass ? "Pass + PartialPass" : "Pass")}；{(product.PreferGoodRetest ? "良品优先" : product.RetestMode.ToString())}；未覆盖文件 {snapshot.FailedFiles}；筛选 {filter}；趋势口径 {periodLabel}";
                    for (int i = 0; i < headers.Length; i++) sheet.Cell(4, i + 1).Value = headers[i];
                    var head = sheet.Range(4, 1, 4, Math.Max(1, headers.Length)); head.Style.Fill.BackgroundColor = XLColor.FromHtml("#315B82"); head.Style.Font.FontColor = XLColor.White; head.Style.Font.Bold = true;
                    sheet.SheetView.FreezeRows(4); sheet.Columns().Width = 16; sheet.Rows().Height = 15; return sheet;
                }
                void Write(IXLWorksheet sheet, int row, params object?[] values)
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        var cell = sheet.Cell(row, i + 1);
                        switch (values[i])
                        {
                            case int n: cell.Value = n; break;
                            case double n: cell.Value = n; cell.Style.NumberFormat.Format = "0.00%"; break;
                            case null: cell.Value = "–"; break;
                            default: cell.Value = values[i]!.ToString() ?? ""; break;
                        }
                    }
                    if (row % 2 == 0) sheet.Range(row, 1, row, values.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
                }
                var headers = new[] { "类型", "批次", "晶圆", "测试步骤", "芯片总数", "良品", "不良", "未完成", "缺温芯片", "身份缺失", "良率", "Pass", "PartialPass", "来源", "说明" };
                var wafer = Sheet("晶圆良率", headers); int row = 5;
                var scoped = ManagementTrendService.DetailYieldRows(product, filter);
                foreach (var r in scoped) { token.ThrowIfCancellationRequested(); Write(wafer, row++, r.Kind, r.Lot, r.Wafer, r.StageName, r.HasSampleCount ? r.Total : null, r.HasSampleCount ? r.Good : null, r.HasSampleCount ? r.Failed : null, r.HasSampleCount ? r.Pending : null, r.HasSampleCount ? r.MissingTemperature : null, r.HasSampleCount ? r.MissingIdentity : null, r.Yield / 100, r.HasDispositionCounts ? r.Pass : null, r.HasDispositionCounts ? r.PartialPass : null, r.Source, !r.CanCalculateYield ? "无法计算；" + r.Note : r.Note); }
                var lot = Sheet("批次良率", headers); row = 5;
                foreach (var group in scoped.GroupBy(r => (r.Kind, r.Lot, r.StageId, r.StageName)))
                {
                    int total = group.Sum(r => r.Total), good = group.Sum(r => r.Good);
                    bool known = group.All(r => r.HasSampleCount), disposition = group.All(r => r.HasDispositionCounts);
                    Write(lot, row++, group.Key.Kind, group.Key.Lot, "合计", group.Key.StageName, known ? total : null, known ? good : null, known ? group.Sum(r => r.Failed) : null, known ? group.Sum(r => r.Pending) : null, known ? group.Sum(r => r.MissingTemperature) : null, known ? group.Sum(r => r.MissingIdentity) : null, ProductYieldService.AggregateYield(group) / 100, disposition ? group.Sum(r => r.Pass) : null, disposition ? group.Sum(r => r.PartialPass) : null, string.Join(" / ", group.Select(r => r.Source).Distinct()), known ? "按芯片数加权" : "未提供完整样本数时不计算加权合计");
                }
                if (period is ManagementPeriod.Month or ManagementPeriod.Year)
                {
                    var timed = Sheet(periodLabel + "良率", new[] { "统计期间" }.Concat(headers).ToArray()); row = 5;
                    foreach (var r in ManagementTrendService.DetailYieldRows(product, filter, period))
                        Write(timed, row++, r.PeriodLabel, r.Kind, r.Lot, r.Wafer, r.StageName, r.HasSampleCount ? r.Total : null,
                            r.HasSampleCount ? r.Good : null, r.HasSampleCount ? r.Failed : null, r.HasSampleCount ? r.Pending : null,
                            r.HasSampleCount ? r.MissingTemperature : null, r.HasSampleCount ? r.MissingIdentity : null,
                            r.Yield / 100, r.HasDispositionCounts ? r.Pass : null, r.HasDispositionCounts ? r.PartialPass : null, r.Source, r.Note);
                    timed.Cell(3, 1).Value = "按测试时间、芯片数加权；跨温匹配后按末次已测必测步骤时间归档。未指定时间单列，不使用登记日期。";
                }
                var trend = Sheet("良率趋势图", new[] { "分温度与综合良率 · " + periodLabel + " · 每页最多 10 列" }); row = 6;
                foreach (string kind in new[] { "CP", "FT" })
                        foreach (var page in Pages(ManagementTrendService.Create(product, kind, filter, period: period), layout: options?.YieldLayout))
                        {
                            token.ThrowIfCancellationRequested();
                            var layout = options?.YieldLayout;
                            byte[] image = TrendImage(page with { TargetYield = options?.TargetYield ?? 85 }, product.Name + " · " + kind + " " + periodLabel + "良率", layout: layout);
                            using var stream = new MemoryStream(image);
                            int imageHeight = TrendImageHeight(page, layout), imageWidth = TrendImageWidth(page, layout);
                            int displayWidth = layout == null ? 1200 : imageWidth, displayHeight = (int)Math.Ceiling(imageHeight * (double)displayWidth / imageWidth);
                            trend.AddPicture(stream).MoveTo(trend.Cell(row, 1)).WithSize(displayWidth, displayHeight);
                            row += (int)Math.Ceiling(displayHeight / 20.0) + 3;
                        }
                var cats = Sheet("SBIN 分类占比", new[] { "类型", "批次", "晶圆", "测试步骤", "分类", "数量", "有效 SBIN", "占比" }); row = 5;
                foreach (var c in ManagementTrendService.CategoryRows(product, filter, period == ManagementPeriod.Lot ? ManagementPeriod.Lot : ManagementPeriod.Wafer))
                    Write(cats, row++, c.Kind, c.Lot, c.Wafer, product.Flow.FirstOrDefault(s => s.Id == c.StageId)?.Label, c.Category, c.Count, c.Total, c.Percent / 100);
                cats.Cell(2, 1).Value += "；分类只覆盖自动解析的逐颗记录，指定良率不产生分类分布。";
                if (period is ManagementPeriod.Month or ManagementPeriod.Year)
                {
                    var timed = Sheet(periodLabel + "SBIN 分类占比", new[] { "统计期间", "类型", "测试步骤", "分类", "数量", "有效 SBIN", "占比" }); row = 5;
                    foreach (var c in ManagementTrendService.CategoryRows(product, filter, period))
                        Write(timed, row++, c.PeriodLabel, c.Kind, product.Flow.FirstOrDefault(s => s.Id == c.StageId)?.Label, c.Category, c.Count, c.Total, c.Percent / 100);
                    timed.Cell(3, 1).Value = "各分类数量合计 ÷ 对应期间有效 SBIN 总数；只覆盖自动解析的逐颗记录。";
                }
                string binType = options?.BinType == "SBIN" ? "SBIN" : "HBIN";
                var categoryPlots = Sheet("Bin分类图", new[] { binType + " Fail 堆叠图 · 每张最多 13 列 · 数量 / 百分比口径跟随界面 · 仅自动解析记录" }); row = 6;
                foreach (var stage in product.Flow)
                    foreach (var page in Pages(ManagementTrendService.DisplayCounts(ManagementTrendService.FailTrend(product, stage.Id, binType, filter, period), options?.BinDisplay ?? ManagementCountDisplay.Count), 13, options?.BinLayout))
                    {
                        token.ThrowIfCancellationRequested();
                        var layout = options?.BinLayout;
                        using var stream = new MemoryStream(TrendImage(page, product.Name + " · " + stage.Label + " · " + binType + " Fail · " + periodLabel, false, layout));
                        int imageHeight = TrendImageHeight(page, layout), imageWidth = TrendImageWidth(page, layout);
                        int displayWidth = layout == null ? 1200 : imageWidth, displayHeight = (int)Math.Ceiling(imageHeight * (double)displayWidth / imageWidth);
                        categoryPlots.AddPicture(stream).MoveTo(categoryPlots.Cell(row, 1)).WithSize(displayWidth, displayHeight);
                        row += (int)Math.Ceiling(displayHeight / 20.0) + 3;
                    }
                var defectPlots = Sheet("不良分类图", new[] { "按配置的 SBIN 分类汇总不良 · 每张最多 13 列 · 显示口径与界面一致 · 未配置的 SBIN 归入未分类" }); row = 6;
                foreach (var stage in product.Flow)
                    foreach (var page in Pages(ManagementTrendService.DisplayCounts(ManagementTrendService.DefectTrend(product, stage.Id, filter, period), options?.DefectDisplay ?? ManagementCountDisplay.Count), 13, options?.DefectLayout))
                    {
                        token.ThrowIfCancellationRequested();
                        var layout = options?.DefectLayout;
                        using var stream = new MemoryStream(TrendImage(page, product.Name + " · " + stage.Label + " · SBIN 不良分类 · " + periodLabel, false, layout));
                        int imageHeight = TrendImageHeight(page, layout), imageWidth = TrendImageWidth(page, layout);
                        int displayWidth = layout == null ? 1200 : imageWidth, displayHeight = (int)Math.Ceiling(imageHeight * (double)displayWidth / imageWidth);
                        defectPlots.AddPicture(stream).MoveTo(defectPlots.Cell(row, 1)).WithSize(displayWidth, displayHeight);
                        row += (int)Math.Ceiling(displayHeight / 20.0) + 3;
                    }
                AddAnalysisSheets(book, product, filter, token);
                var files = Sheet("文件清单", new[] { "文件名", "原文件路径", "批次", "晶圆覆盖", "步骤", "原记录数", "身份缺失", "状态", "测试时间" }); row = 5;
                var selectedLots = scoped.Select(r => r.Lot).ToHashSet(StringComparer.Ordinal);
                foreach (var file in product.Files.Where(f => filter.Length == 0 || selectedLots.Contains(f.Lot)))
                {
                    var f = snapshot.Files.FirstOrDefault(s => s.FileId == file.Id);
                    Write(files, row++, file.FileName, file.FilePath, file.Lot, file.WaferOverride, product.Flow.FirstOrDefault(s => s.Id == file.StageId)?.Label ?? file.StageId, file.UseSpecifiedYield ? file.SpecifiedTotal : f?.Records, file.UseSpecifiedYield ? null : f?.MissingIdentity, f == null ? "未计算" : f.Error.Length == 0 ? file.UseSpecifiedYield ? "指定良率 · " + file.SpecifiedOrigin : "已统计" : f.Error, file.TestTimeText.Length > 0 ? file.TestTimeText : "未指定时间");
                }
                files.Column(2).Width = 65;
                files.Column(9).Width = 24;
                var flow = Sheet("流程与口径", new[] { "顺序", "步骤", "温度 °C", "必测", "身份规则" }); row = 5;
                foreach (var s in product.Flow) Write(flow, row++, row - 5, s.Kind + " · " + s.Name, s.Temperature.ToString("g", CultureInfo.CurrentCulture), s.Required ? "是" : "否", s.Kind == "CP" ? "批次 + 晶圆 + XY" : "批次 + UUID Test Num: " + product.UuidTestNumbers);
                flow.Cell(row + 1, 1).Value = "综合分母为必测步骤芯片并集；全部通过才算良品，缺温/身份缺失为未完成。缺温但已有明确失败的芯片仍为不良。CP 与 FT 不做无依据的身份合并。";
                foreach (string line in product.ConfigText.Split('\n')) flow.Cell(row++ + 3, 1).Value = line.TrimEnd('\r');
                token.ThrowIfCancellationRequested();
                book.SaveAs(temporary);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static byte[] AnalysisImage(ManagementTrendData data, string title, int width = 1200, int height = 460)
    {
        var chart = new ManagementChartControl { Width = width, Height = height, Title = title, ShowLegend = data.Mode is ManagementChartMode.RadialLines or ManagementChartMode.CenterEdge };
        chart.SetData(data); chart.Measure(new Size(width, height)); chart.Arrange(new Rect(0, 0, width, height)); chart.UpdateLayout();
        using var png = ChartImage.Png(ChartImage.Capture(chart, new ChartImageSize(width, height)));
        return png.ToArray();
    }

    private static void AddAnalysisSheets(XLWorkbook book, ManagedProduct product, string filter, CancellationToken token)
    {
        IXLWorksheet Sheet(string name, string note)
        {
            var sheet = book.AddWorksheet(name); sheet.Columns().Width = 13; sheet.Rows().Height = 15;
            sheet.Cell(1, 1).Value = product.Name + " · " + name; sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(2, 1).Value = note; return sheet;
        }
        void Picture(IXLWorksheet sheet, int row, int column, ManagementTrendData data, string title, int width = 1150, int height = 450)
        {
            token.ThrowIfCancellationRequested(); using var png = new MemoryStream(AnalysisImage(data, title, width, height));
            sheet.AddPicture(png).MoveTo(sheet.Cell(row, column)).WithSize(width, height);
        }
        void Table(IXLWorksheet sheet, int row, params string[] headers)
        {
            for (int col = 0; col < headers.Length; col++) sheet.Cell(row, col + 1).Value = headers[col];
            sheet.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#315B82");
            sheet.Range(row, 1, row, headers.Length).Style.Font.FontColor = XLColor.White;
        }
        var scopeSheet = Sheet("导出范围", "仅以下勾选批次 / 晶圆参与本报告的所有统计、图表及明细。");
        Table(scopeSheet, 4, "类型", "批次", "晶圆"); int scopeRow = 5;
        foreach (var scope in ManagementAnalytics.Scopes(product).Where(s => ManagementAnalytics.Matches(s.Lot, s.Wafer, filter)))
        { scopeSheet.Cell(scopeRow, 1).Value = scope.Kind; scopeSheet.Cell(scopeRow, 2).Value = scope.Lot; scopeSheet.Cell(scopeRow++, 3).Value = scope.Wafer; }
        var pareto = Sheet("Fail Pareto", "SBIN 图显示 Top 12，每个编号下方为配置释义，完整释义见下表；累计占比保留全体不良分母。HBIN 较多时分图。");
        var root = Sheet("SBIN Fail 归因", "显示数量最多的 12 个 SBIN；说明来自产品中配置的 SBIN 释义，不自动推断芯片失效原因。");
        var radial = Sheet("晶圆径向良率", "分区为 r/R：≤40%、40–70%、70–90%、>90%；中心和半径根据已测 XY 估算，局部测试不等同物理晶圆边缘。无样本分区留空。");
        var comparison = Sheet("中心边缘对比", "中心区 ≤40%，边缘区 >90%；仅纳入有有效 XY 的 CP 芯片。差值单位为百分点（pp）。");
        int pRow = 4, rootRow = 4, radialRow = 4, comparisonRow = 4;
        var selected = filter.Length == 0 ? product : ManagementAnalytics.Select(product, ManagementAnalytics.Scopes(product).Where(s => ManagementAnalytics.Matches(s.Lot, s.Wafer, filter)).ToHashSet());
        foreach (var stage in product.Flow.Where(s => selected.Snapshot?.Yields.Any(r => r.StageId == s.Id) == true))
        {
            string title = product.Name + " · " + stage.Label;
            var hbinPages = Pages(ManagementTrendService.Pareto(selected, stage.Id, "HBIN"), 8).ToArray();
            var sbinPages = Pages(ManagementTrendService.Pareto(selected, stage.Id, "SBIN", 12), 13).ToArray();
            int pageCount = Math.Max(hbinPages.Length, sbinPages.Length);
            if (pageCount == 0) { pareto.Cell(pRow, 1).Value = stage.Label + "：当前范围没有不良数据。"; pRow += 2; }
            for (int index = 0; index < pageCount; index++)
            {
                if (index < hbinPages.Length) Picture(pareto, pRow, 1, hbinPages[index], title + $" · HBIN Pareto（{index + 1}/{hbinPages.Length}）", 570, 430);
                if (index < sbinPages.Length) Picture(pareto, pRow, 8, sbinPages[index], title + " · SBIN Pareto Top 12", 1150, 430);
                pRow += 25;
            }
            Table(pareto, pRow++, "步骤", "类型", "Bin", "说明", "Fail 数量", "Fail 占比", "累计占比");
            foreach (string type in new[] { "HBIN", "SBIN" }) foreach (var bin in ManagementAnalytics.Bins(selected, stage.Id, type))
            {
                pareto.Cell(pRow, 1).Value = stage.Label; pareto.Cell(pRow, 2).Value = type; pareto.Cell(pRow, 3).Value = bin.Bin;
                pareto.Cell(pRow, 4).Value = bin.Description; pareto.Cell(pRow, 5).Value = bin.Count;
                pareto.Cell(pRow, 6).Value = bin.Percent / 100; pareto.Cell(pRow, 7).Value = bin.Cumulative / 100;
                pareto.Range(pRow, 6, pRow, 7).Style.NumberFormat.Format = "0.00%"; pRow++;
            }
            pRow += 3;
            Picture(root, rootRow, 1, ManagementTrendService.RootCause(selected, stage.Id), title + " · SBIN Fail 分类", 1150, 520); rootRow += 29;
            Table(root, rootRow++, "步骤", "Bin", "配置释义", "Fail 数量", "Fail 占比");
            foreach (var bin in ManagementAnalytics.Bins(selected, stage.Id, "SBIN"))
            {
                root.Cell(rootRow, 1).Value = stage.Label; root.Cell(rootRow, 2).Value = bin.Bin; root.Cell(rootRow, 3).Value = bin.Description;
                root.Cell(rootRow, 4).Value = bin.Count; root.Cell(rootRow, 5).Value = bin.Percent / 100; root.Cell(rootRow, 5).Style.NumberFormat.Format = "0.00%"; rootRow++;
            }
            rootRow += 3;
            if (stage.Kind != "CP") continue;
            var radialData = ManagementTrendService.RadialTrend(selected, stage.Id);
            foreach (var series in radialData.Series.Chunk(12))
            { Picture(radial, radialRow, 1, radialData with { Series = series.ToList() }, title + " · 径向良率"); radialRow += 25; }
            if (radialData.Series.Count == 0) { radial.Cell(radialRow, 1).Value = stage.Label + "：没有可用的完整 XY / 几何范围。"; radialRow += 2; }
            Table(radial, radialRow++, "步骤", "批次", "晶圆", "分区", "样本数", "良品", "良率", "估算中心 X", "估算中心 Y", "估算半径");
            foreach (var row in selected.Snapshot!.Radial.Where(r => r.StageId == stage.Id))
            {
                radial.Cell(radialRow, 1).Value = stage.Label; radial.Cell(radialRow, 2).Value = row.Lot; radial.Cell(radialRow, 3).Value = row.Wafer;
                radial.Cell(radialRow, 4).Value = ManagementAnalytics.Regions[row.Region]; radial.Cell(radialRow, 5).Value = row.Total; radial.Cell(radialRow, 6).Value = row.Good;
                if (row.Yield is double value) { radial.Cell(radialRow, 7).Value = value / 100; radial.Cell(radialRow, 7).Style.NumberFormat.Format = "0.00%"; }
                radial.Cell(radialRow, 8).Value = row.CenterX; radial.Cell(radialRow, 9).Value = row.CenterY; radial.Cell(radialRow, 10).Value = row.Radius; radialRow++;
            }
            radialRow += 3;
            foreach (var page in Pages(ManagementTrendService.CenterEdge(selected, stage.Id)))
            { Picture(comparison, comparisonRow, 1, page, title + " · 中心区与边缘区"); comparisonRow += 25; }
            Table(comparison, comparisonRow++, "步骤", "批次", "晶圆", "中心样本", "中心良率", "边缘样本", "边缘良率", "边缘 − 中心（pp）");
            foreach (var group in selected.Snapshot.Radial.Where(r => r.StageId == stage.Id).GroupBy(r => (r.Lot, r.Wafer)))
            {
                var center = group.Single(r => r.Region == 0); var edge = group.Single(r => r.Region == 3);
                comparison.Cell(comparisonRow, 1).Value = stage.Label; comparison.Cell(comparisonRow, 2).Value = group.Key.Lot; comparison.Cell(comparisonRow, 3).Value = group.Key.Wafer;
                comparison.Cell(comparisonRow, 4).Value = center.Total; comparison.Cell(comparisonRow, 6).Value = edge.Total;
                if (center.Yield is double c) { comparison.Cell(comparisonRow, 5).Value = c / 100; comparison.Cell(comparisonRow, 5).Style.NumberFormat.Format = "0.00%"; }
                if (edge.Yield is double e) { comparison.Cell(comparisonRow, 7).Value = e / 100; comparison.Cell(comparisonRow, 7).Style.NumberFormat.Format = "0.00%"; }
                if (center.Yield.HasValue && edge.Yield.HasValue) comparison.Cell(comparisonRow, 8).Value = edge.Yield.Value - center.Yield.Value;
                comparisonRow++;
            }
            comparisonRow += 3;
        }
    }
}

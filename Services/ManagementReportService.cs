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
    public static byte[] TrendImage(ManagementTrendData data, string title, bool yield = true)
    {
        const int width = 1440, chartHeight = 440;
        double cellWidth = Math.Max(80, (width - 202.0) / Math.Max(1, data.Labels.Length));
        int height = chartHeight + 95 + (data.Series.Count + 1) * 30;
        if (height > 12000) throw new InvalidDataException("报表分类过多，请减少所选分类。");
        var chart = new WaferCategoryTrendControl { Width = width, Height = chartHeight, ColumnWidth = cellWidth,
            FitYAxisToData = yield, AxisTitle = yield ? "良率（%）" : "分类占比（%）" };
        chart.SetData(data.Labels, data.Series);
        chart.Measure(new Size(width, chartHeight)); chart.Arrange(new Rect(0, 0, width, chartHeight)); chart.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var heading = ChartAxis.Text(chart, title, 22, Brushes.Black);
            heading.MaxTextWidth = width - 40; heading.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(heading, new Point(Math.Max(20, (width - heading.Width) / 2), 12));
            dc.PushTransform(new TranslateTransform(0, 50));
            dc.DrawRectangle(new VisualBrush(chart), null, new Rect(0, 0, width, chartHeight)); dc.Pop();
            double tableY = chartHeight + 50;
            var border = new Pen(Brushes.LightGray, 1); border.Freeze();
            void Text(string text, double x, double y, double available, bool center)
            {
                var label = ChartAxis.Text(chart, text, 12, Brushes.Black); label.MaxTextWidth = Math.Max(1, available - 12);
                label.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(label, new Point(x + (center ? Math.Max(6, (available - label.Width) / 2) : 8), y + 6));
            }
            dc.DrawRectangle(Brushes.AliceBlue, border, new Rect(1, tableY, width - 2, 30)); Text("测试步骤 / 分类", 1, tableY, 200, true);
            for (int c = 0; c < data.Labels.Length; c++) Text(data.Labels[c], 201 + c * cellWidth, tableY, cellWidth, true);
            for (int r = 0; r < data.Series.Count; r++)
            {
                var series = data.Series[r]; double y = tableY + (r + 1) * 30;
                dc.DrawRectangle(r % 2 == 0 ? Brushes.White : Brushes.WhiteSmoke, border, new Rect(1, y, width - 2, 30));
                dc.DrawLine(new Pen(series.LineBrush, 3), new Point(10, y + 15), new Point(30, y + 15));
                Text(series.Category, 32, y, 169, false);
                for (int c = 0; c < data.Labels.Length; c++)
                {
                    double x = 201 + c * cellWidth; dc.DrawLine(border, new Point(x, y), new Point(x, y + 30));
                    Text(c < series.Values.Length && series.Values[c] is double value ? value.ToString("F2", CultureInfo.CurrentCulture) + "%" : "–", x, y, cellWidth, true);
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    public static IEnumerable<ManagementTrendData> Pages(ManagementTrendData data)
    {
        for (int start = 0; start < data.Labels.Length; start += 10)
            yield return new(data.Labels.Skip(start).Take(10).ToArray(),
                data.Series.Select(s => new WaferCategorySeries(s.Category, s.LineBrush, s.Values.Skip(start).Take(10).ToArray())).ToList());
    }
    public static void SaveWorkbook(string path, ManagedProduct product, string filter = "", CancellationToken token = default, ManagementPeriod period = ManagementPeriod.Wafer)
    {
        if (product.Snapshot is not { } snapshot) throw new InvalidDataException("请先计算产品良率。");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var book = new XLWorkbook())
            {
                string periodLabel = period == ManagementPeriod.Year ? "按年" : period == ManagementPeriod.Month ? "按月" : period == ManagementPeriod.Lot ? "按批次" : "按晶圆";
                book.Style.Font.FontName = "Microsoft YaHei"; book.Style.Font.FontSize = 11;
                IXLWorksheet Sheet(string name, string[] headers)
                {
                    var sheet = book.AddWorksheet(name); sheet.Cell(1, 1).Value = product.Name + " · " + name;
                    sheet.Cell(2, 1).Value = $"计算时间 {snapshot.CalculatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}；{(product.IncludePartialPass ? "Pass + PartialPass" : "Pass")}；{product.RetestMode}；未覆盖文件 {snapshot.FailedFiles}；筛选 {filter}；趋势口径 {periodLabel}";
                    for (int i = 0; i < headers.Length; i++) sheet.Cell(4, i + 1).Value = headers[i];
                    var head = sheet.Range(4, 1, 4, Math.Max(1, headers.Length)); head.Style.Fill.BackgroundColor = XLColor.FromHtml("#315B82"); head.Style.Font.FontColor = XLColor.White; head.Style.Font.Bold = true;
                    sheet.SheetView.FreezeRows(4); sheet.Columns().Width = 18; return sheet;
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
                        foreach (var page in Pages(ManagementTrendService.Create(product, kind, filter, period: period)))
                        {
                            token.ThrowIfCancellationRequested();
                            byte[] image = TrendImage(page, product.Name + " · " + kind + " " + periodLabel + "良率");
                            using var stream = new MemoryStream(image);
                            int imageHeight = 535 + (page.Series.Count + 1) * 30;
                            trend.AddPicture(stream).MoveTo(trend.Cell(row, 1)).WithSize(1200, (int)(imageHeight * 1200.0 / 1440));
                            row += (int)Math.Ceiling(imageHeight * 1200.0 / 1440 / 20) + 3;
                        }
                var cats = Sheet("不良分类", new[] { "类型", "批次", "晶圆", "测试步骤", "分类", "数量", "有效 SBIN", "占比" }); row = 5;
                foreach (var c in ManagementTrendService.CategoryRows(product, filter, period == ManagementPeriod.Lot ? ManagementPeriod.Lot : ManagementPeriod.Wafer))
                    Write(cats, row++, c.Kind, c.Lot, c.Wafer, product.Flow.FirstOrDefault(s => s.Id == c.StageId)?.Label, c.Category, c.Count, c.Total, c.Percent / 100);
                cats.Cell(2, 1).Value += "；分类只覆盖自动解析的逐颗记录，指定良率不产生分类分布。";
                if (period is ManagementPeriod.Month or ManagementPeriod.Year)
                {
                    var timed = Sheet(periodLabel + "不良分类", new[] { "统计期间", "类型", "测试步骤", "分类", "数量", "有效 SBIN", "占比" }); row = 5;
                    foreach (var c in ManagementTrendService.CategoryRows(product, filter, period))
                        Write(timed, row++, c.PeriodLabel, c.Kind, product.Flow.FirstOrDefault(s => s.Id == c.StageId)?.Label, c.Category, c.Count, c.Total, c.Percent / 100);
                    timed.Cell(3, 1).Value = "各分类数量合计 ÷ 对应期间有效 SBIN 总数；只覆盖自动解析的逐颗记录。";
                }
                var categoryPlots = Sheet("分类趋势图", new[] { "SBIN 分类占比 · 仅自动解析记录" }); row = 6;
                foreach (var stage in product.Flow)
                    foreach (var page in Pages(ManagementTrendService.CreateCategoryTrend(product, stage.Id, filter, period)))
                    {
                        token.ThrowIfCancellationRequested();
                        using var stream = new MemoryStream(TrendImage(page, product.Name + " · " + stage.Label + " · 分类占比 · " + periodLabel, false));
                        int imageHeight = 535 + (page.Series.Count + 1) * 30;
                        categoryPlots.AddPicture(stream).MoveTo(categoryPlots.Cell(row, 1)).WithSize(1200, (int)(imageHeight * 1200.0 / 1440));
                        row += (int)Math.Ceiling(imageHeight * 1200.0 / 1440 / 20) + 3;
                    }
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
}

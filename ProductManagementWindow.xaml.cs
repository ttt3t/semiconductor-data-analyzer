using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;
using SemiconductorCsvAnalyzer.Controls;

namespace SemiconductorCsvAnalyzer;

public partial class ProductManagementWindow : Window
{
    public sealed record FileRow(ManagedDataFile File, string Stage, string Status, int Records)
    { public string Name => File.FileName; public string Lot => File.Lot; public string Wafer => File.WaferOverride; public string Path => File.FilePath; }
    private ProductCatalogStore? _store;
    private ManagedProduct? _product;
    private bool _refreshing, _closed, _busy;
    private long _version;
    private CancellationTokenSource? _calculation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _filterTimer;
    private readonly IdleMemoryReclaimer _memoryReclaimer;
    private readonly ManagementGridFilter _yieldFilters, _fileFilters, _categoryFilters;
    private ManagementPeriod Period => PeriodYear?.IsChecked == true ? ManagementPeriod.Year : PeriodMonth?.IsChecked == true ? ManagementPeriod.Month : PeriodLot?.IsChecked == true ? ManagementPeriod.Lot : ManagementPeriod.Wafer;

    public ProductManagementWindow(ProductCatalogStore? store = null)
    {
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RefreshDetails(); };
        InitializeComponent();
        var filterHeader = (DataTemplate)FindResource("ManagedFilterHeader");
        _yieldFilters = new(DgYields, filterHeader); _fileFilters = new(DgFiles, filterHeader); _categoryFilters = new(DgCategories, filterHeader);
        _memoryReclaimer = new IdleMemoryReclaimer(Dispatcher, () => !_closed && !_busy && _gate.CurrentCount == 1 &&
            Application.Current.Windows.OfType<MainWindow>().All(w => w.CanReclaimMemory));
        try { _store = store ?? new ProductCatalogStore(); RefreshProducts(); }
        catch (Exception ex) { BtnAddProduct.IsEnabled = false; TxtNotice.Text = "产品目录未能读取：" + ex.Message + "。仍可使用独立分析器。"; }
        var layout = new WindowLayoutPersistence(this, "ProductManagement");
        layout.TrackTable("Files", DgFiles); layout.TrackTable("Yields", DgYields);
        Closed += (_, _) =>
        {
            _closed = true; ++_version; _calculation?.Cancel(); _filterTimer.Stop(); _memoryReclaimer.Dispose();
            _yieldFilters.Dispose(); _fileFilters.Dispose(); _categoryFilters.Dispose();
            YieldTrend.SetData(new(Array.Empty<string>(), new()));
            DgFiles.ItemsSource = DgYields.ItemsSource = DgCategories.ItemsSource = DgFlow.ItemsSource = null;
            ProductList.ItemsSource = CategoryStages.ItemsSource = null; _product = null; _store = null;
        };
    }
    private void RefreshProducts(string? select = null)
    {
        if (_store == null) return;
        string? id = select ?? _product?.Id;
        _refreshing = true;
        var rows = _store.Catalog.Products.Where(p => p.Name.Contains(TxtProductSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        ProductList.ItemsSource = rows;
        ProductList.SelectedItem = rows.FirstOrDefault(p => p.Id == id) ?? rows.FirstOrDefault();
        _refreshing = false;
        SelectProduct(ProductList.SelectedItem as ManagedProduct);
    }
    private void SelectProduct(ManagedProduct? product)
    {
        if (_product?.Id != product?.Id) { CancelCalculation(); TxtFilter.Text = ""; _yieldFilters.Clear(); _fileFilters.Clear(); _categoryFilters.Clear(); }
        _product = product;
        TxtProductName.Text = product?.Name ?? "欢迎使用产品数据管理";
        TxtProductInfo.Text = product?.FlowDescription ?? "";
        ProductActions.IsEnabled = product != null && !_busy;
        DgFlow.ItemsSource = product?.Flow;
        CategoryStages.ItemsSource = product?.Flow; CategoryStages.SelectedIndex = product?.Flow.Count > 0 ? 0 : -1;
        TxtRules.Text = product == null ? "" : $"良品口径：{(product.IncludePartialPass ? "Pass + PartialPass" : "Pass")}；复测：{(product.RetestMode == RetestMode.Last ? "保留末次整条记录" : "保留首测整条记录")}。CP：批次 + 晶圆 + XY；FT：批次 + UUID（Test Num {product.UuidTestNumbers}）。综合分母为必测温度芯片并集，缺温/身份缺失显示未完成。文件登记顺序决定同温跨文件复测的先后。";
        RefreshDetails();
    }
    private void RefreshDetails()
    {
        if (_closed) return;
        string filter = TxtFilter.Text.Trim();
        var product = _product; var snapshot = product?.Snapshot;
        var rows = snapshot?.Yields.Where(r => ManagementTrendService.Matches(r.Lot, r.Wafer, filter)).ToArray() ?? Array.Empty<ManagedYieldRow>();
        _yieldFilters?.SetItems(product == null ? Array.Empty<ManagedYieldRow>() : ManagementTrendService.DetailYieldRows(product, filter, Period));
        void Metric(string kind, TextBlock yield, TextBlock count)
        {
            if (product != null && !product.Flow.Any(s => s.Kind == kind)) { yield.Text = "未启用"; count.Text = "产品未包含 " + kind + " 流程"; return; }
            var combined = rows.Where(r => r.Kind == kind && r.IsCombined).ToArray(); int total = combined.Sum(r => r.Total), good = combined.Sum(r => r.Good);
            double? rate = ProductYieldService.AggregateYield(combined);
            yield.Text = rate.HasValue ? rate.Value.ToString("F2") + "%" : combined.Any(r => !r.CanCalculateYield || !r.HasSampleCount) ? "无法计算" : "–";
            count.Text = combined.Length == 0 ? "待登记对应步骤数据" : combined.Any(r => !r.HasSampleCount) ? "未提供样本数或无法自动综合" : $"{good:N0} / {total:N0} 颗 · 未完成 {combined.Sum(r => r.Pending):N0}";
        }
        Metric("CP", TxtCpYield, TxtCpCount); Metric("FT", TxtFtYield, TxtFtCount);
        TxtLots.Text = $"{rows.Select(r => r.Lot).Distinct().Count()} / {rows.Where(r => r.Kind == "CP").Select(r => (r.Lot, r.Wafer)).Distinct().Count()}";
        TxtFiles.Text = $"{product?.Files.Count ?? 0} / {snapshot?.FailedFiles ?? 0}";
        TxtCalculated.Text = snapshot == null ? "尚未计算" : $"上次 {snapshot.CalculatedUtc.ToLocalTime():MM-dd HH:mm}";
        if (product != null)
        {
            bool current = ProductYieldService.IsCurrent(product);
            TxtNotice.Text = snapshot == null ? "" :
                !current ? "原文件、路径或测试配置已变化，当前显示上次统计，请重新计算。" :
                snapshot.FailedFiles > 0 ? $"{snapshot.FailedFiles} 个文件未参与计算，统计仅覆盖成功读取的数据。请在数据文件页查看原因。" :
                $"已覆盖 {snapshot.Files.Count} 条登记数据。";
        }
        else TxtNotice.Text = "";
        var files = product?.Files.Where(f => filter.Length == 0 || f.Lot.Contains(filter, StringComparison.OrdinalIgnoreCase) || f.WaferOverride.Contains(filter, StringComparison.OrdinalIgnoreCase) || f.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray() ?? Array.Empty<ManagedDataFile>();
        _fileFilters?.SetItems(files.Select(file =>
        {
            var state = snapshot?.Files.FirstOrDefault(f => f.FileId == file.Id);
            string status;
            try
            {
                if (file.UseSpecifiedYield) status = state?.Error.Length > 0 ? state.Error : "指定良率 · " + file.SpecifiedOrigin;
                else
                {
                    var info = new FileInfo(file.FilePath);
                    status = !info.Exists ? "原文件不存在" : state == null ? "待计算" : state.Error.Length > 0 ? state.Error :
                        info.Length != state.Length || info.LastWriteTimeUtc != state.LastWriteUtc ? "文件已修改，请重算" : "已统计";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { status = ex.Message; }
            return new FileRow(file, product!.Flow.FirstOrDefault(s => s.Id == file.StageId)?.Label ?? (file.StageId.StartsWith("COMBINED:") ? file.SpecifiedKind + " 综合良率" : "步骤不存在"), status, state?.Records ?? 0);
        }).ToList());
        RefreshTrend(); RefreshCategories();
    }
    private void RefreshTrend()
    {
        if (YieldTrend == null) return;
        YieldTrend.SetData(_product == null ? new(Array.Empty<string>(), new()) :
            ManagementTrendService.Create(_product, FtMode.IsChecked == true ? "FT" : "CP", TxtFilter.Text.Trim(), period: Period));
    }
    private void RefreshCategories()
    {
        if (DgCategories == null) return;
        string? id = (CategoryStages.SelectedItem as ProductTestStage)?.Id;
        _categoryFilters?.SetItems(_product == null ? Array.Empty<ManagedCategoryCount>() : ManagementTrendService.CategoryRows(_product, TxtFilter.Text.Trim(), Period).Where(c => c.StageId == id).ToArray());
    }
    private void Period_Changed(object sender, RoutedEventArgs e)
    {
        if (DgYields == null || YieldPeriodColumn == null) return;
        bool timed = Period is ManagementPeriod.Month or ManagementPeriod.Year;
        YieldPeriodColumn.Visibility = CategoryPeriodColumn.Visibility = TxtPeriodNote.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        _yieldFilters?.Clear(); _categoryFilters?.Clear(); RefreshDetails();
    }
    private void ProductSearch_Changed(object sender, TextChangedEventArgs e) { if (_store != null) RefreshProducts(); }
    private void Product_Changed(object sender, SelectionChangedEventArgs e) { if (!_refreshing) SelectProduct(ProductList.SelectedItem as ManagedProduct); }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (_filterTimer != null && !_closed) { _filterTimer.Stop(); _filterTimer.Start(); } }
    private void TrendMode_Changed(object sender, RoutedEventArgs e) => RefreshTrend();
    private void CategoryStage_Changed(object sender, SelectionChangedEventArgs e) => RefreshCategories();
    private void SaveProduct(ManagedProduct product)
    {
        if (_store == null) throw new InvalidOperationException("产品目录不可用。");
        var catalog = ProductCatalogStore.Clone(_store.Catalog); int index = catalog.Products.FindIndex(p => p.Id == product.Id);
        if (index < 0) catalog.Products.Add(product); else catalog.Products[index] = product;
        _store.Save(catalog); RefreshProducts(product.Id);
    }
    private void Standalone_Click(object sender, RoutedEventArgs e) => new MainWindow().Show();
    private void DeleteProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _store == null || _busy) return;
        if (MessageBox.Show(this, $"删除产品“{_product.Name}”及其 {_product.Files.Count} 条登记和统计？\n只删除本机产品目录中的记录，所有原始数据文件均保留。", "删除产品", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { CancelCalculation(); _store.DeleteProduct(_product.Id); RefreshProducts(); TxtStatus.Text = "产品登记与统计已删除，原始文件保留。"; }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private void AddProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_store == null || _busy) return;
        var dialog = new ProductEditorWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_store.Catalog.Products.Any(p => string.Equals(p.Name, dialog.Product.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("产品名称已存在。");
            TxtProductSearch.Clear(); SaveProduct(dialog.Product);
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private async void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy) return;
        var dialog = new ProductEditorWindow(_product) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (_store!.Catalog.Products.Any(p => p.Id != dialog.Product.Id && string.Equals(p.Name, dialog.Product.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("产品名称已存在。");
            if (!dialog.Product.Name.Contains(TxtProductSearch.Text, StringComparison.OrdinalIgnoreCase)) TxtProductSearch.Clear();
            SaveProduct(dialog.Product);
            if (dialog.Product.Files.Count > 0) await CalculateAsync(dialog.Product.Id);
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy) return;
        var files = new OpenFileDialog { Filter = DataFileService.OpenFilter, Multiselect = true, Title = "选择产品数据文件（保留原路径）" };
        if (files.ShowDialog() != true) return;
        var dialog = new ManagedImportWindow(_product, files.FileNames) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var product = ProductCatalogStore.Clone(_product);
            product.FilenameRule = dialog.FilenameRule;
            foreach (var file in dialog.Files)
                if (!product.Files.Any(f => string.Equals(f.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase) && f.StageId == file.StageId && f.Lot == file.Lot && f.WaferOverride == file.WaferOverride)) product.Files.Add(file);
            SaveProduct(product); await CalculateAsync(product.Id);
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private void SetBusy(bool busy)
    {
        bool completed = _busy && !busy;
        _busy = busy; ProductActions.IsEnabled = !busy && _product != null; BtnAddProduct.IsEnabled = !busy && _store != null;
        BusyBar.Visibility = BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (completed) _memoryReclaimer.Request();
    }
    private async void SpecifiedYield_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy) return;
        var dialog = new ManagedImportWindow(_product, Array.Empty<string>(), true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { var product = ProductCatalogStore.Clone(_product); product.FilenameRule = dialog.FilenameRule; product.Files.AddRange(dialog.Files); SaveProduct(product); await CalculateAsync(product.Id); }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private void CancelCalculation()
    {
        ++_version; var cancellation = _calculation; _calculation = null; cancellation?.Cancel(); SetBusy(false);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelCalculation(); TxtStatus.Text = "已取消，保留上次统计。"; }
    private async void Recalculate_Click(object sender, RoutedEventArgs e) { if (_product != null) await CalculateAsync(_product.Id); }
    public async Task<bool> CalculateAsync(string productId)
    {
        if (_store == null) return false;
        var source = _store.Catalog.Products.FirstOrDefault(p => p.Id == productId); if (source == null) return false;
        _calculation?.Cancel(); var cancellation = new CancellationTokenSource(); _calculation = cancellation;
        var token = cancellation.Token; long version = ++_version; var product = ProductCatalogStore.Clone(source);
        SetBusy(true); TxtStatus.Text = "正在计算产品良率…";
        bool entered = false;
        try
        {
            await _gate.WaitAsync(token); entered = true;
            var progress = new Progress<string>(text => { if (!_closed && version == _version) TxtStatus.Text = text; });
            var result = await Task.Run(() => ProductYieldService.Calculate(product, token, progress), token);
            token.ThrowIfCancellationRequested();
            if (_closed || version != _version || _store == null) return false;
            product.Snapshot = result; SaveProduct(product);
            TxtStatus.Text = result.FailedFiles > 0 ? $"计算完成，{result.FailedFiles} 个文件未覆盖，请查看数据文件页。" : "计算完成。";
            return true;
        }
        catch (OperationCanceledException) { if (!_closed && version == _version) TxtStatus.Text = "已取消，保留上次统计。"; }
        catch (Exception ex) { if (!_closed && version == _version) TxtStatus.Text = "计算未完成：" + ex.Message; }
        finally
        {
            if (entered) _gate.Release();
            if (ReferenceEquals(_calculation, cancellation)) _calculation = null;
            if (!_closed && version == _version) SetBusy(false);
            cancellation.Dispose();
        }
        return false;
    }
    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy) return;
        MainWindow? window = null;
        try
        {
            var sources = ManagedAnalysisService.Sources(_product, DgFiles.SelectedItems.OfType<FileRow>().Select(r => r.File.Id));
            var config = ConfigLoader.Parse(sources[0].ConfigText, _product.Name + ".ini"); window = new MainWindow(); window.Show();
            await window.OpenManagedFilesAsync(sources, config, _product.RetestMode);
        }
        catch (Exception ex) { window?.Close(); TxtStatus.Text = "打开失败：" + ex.Message; }
    }
    private async void Relocate_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy || DgFiles.SelectedItem is not FileRow row) return;
        var file = new OpenFileDialog { Filter = DataFileService.OpenFilter, FileName = row.Name, Title = "重新定位原数据文件" };
        if (file.ShowDialog() != true) return;
        try { var product = ProductCatalogStore.Clone(_product); product.Files.Single(f => f.Id == row.File.Id).FilePath = Path.GetFullPath(file.FileName); SaveProduct(product); await CalculateAsync(product.Id); }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private async void EditFile_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy || DgFiles.SelectedItem is not FileRow row) return;
        var dialog = new ManagedImportWindow(_product, Array.Empty<string>(), row.File.UseSpecifiedYield, row.File) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var product = ProductCatalogStore.Clone(_product); int index = product.Files.FindIndex(f => f.Id == row.File.Id);
            product.FilenameRule = dialog.FilenameRule;
            product.Files[index] = dialog.Files.Single(); SaveProduct(product); await CalculateAsync(product.Id);
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private async void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy) return;
        var ids = DgFiles.SelectedItems.OfType<FileRow>().Select(r => r.File.Id).ToHashSet();
        if (ids.Count == 0 || MessageBox.Show($"从产品目录移除 {ids.Count} 条登记？原数据文件会保留。", "移除登记", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { string id = _product.Id; _store!.RemoveFiles(id, ids); RefreshProducts(id); await CalculateAsync(id); }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
    private void CategoryTrend_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || CategoryStages.SelectedItem is not ProductTestStage stage) return;
        var result = ManagementTrendService.Categories(_product, stage.Id, TxtFilter.Text.Trim(), Period);
        if (result.Wafers.Count == 0) { TxtStatus.Text = "该步骤暂无自动解析的分类数据；指定汇总良率不含逐颗分类分布。"; return; }
        var window = new WaferCategoryTrendWindow(result.Wafers, result.CategoryRows.Select(r => r.Category)) { Owner = this, Title = _product.Name + " · " + stage.Label + " · 分类趋势" + (Period == ManagementPeriod.Year ? "（按年）" : Period == ManagementPeriod.Month ? "（按月）" : Period == ManagementPeriod.Lot ? "（按批次）" : "") };
        window.UpdateStatistics(result, _product.RetestMode == RetestMode.First); window.Show();
    }
    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy) return;
        var file = new SaveFileDialog { Filter = "Excel 报告|*.xlsx", FileName = SafeName(_product.Name) + "-产品良率报告.xlsx" };
        if (file.ShowDialog() != true) return;
        string id = _product.Id;
        if (!ProductYieldService.IsCurrent(_product) && !await CalculateAsync(id)) return;
        if (_product?.Id != id || _product.Snapshot == null || _closed) return;
        var product = ProductCatalogStore.Clone(_product); string filter = TxtFilter.Text.Trim(); var period = Period;
        var cancellation = new CancellationTokenSource(); _calculation = cancellation; long version = ++_version; SetBusy(true);
        try { await ManagementReportService.RunStaAsync(() => ManagementReportService.SaveWorkbook(file.FileName, product, filter, cancellation.Token, period)); if (!_closed && version == _version) TxtStatus.Text = "管理报告已导出：" + file.FileName; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && version == _version) TxtStatus.Text = "导出失败：" + ex.Message; }
        finally { if (ReferenceEquals(_calculation, cancellation)) _calculation = null; cancellation.Dispose(); if (!_closed && version == _version) SetBusy(false); }
    }
    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (_product == null || _busy || YieldTrend.SelectedData.Labels.Length == 0) return;
        if (YieldTrend.SelectedData.Series.Count == 0) { TxtStatus.Text = "请至少勾选一个测试步骤后再导出趋势图。"; return; }
        var file = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = SafeName(_product.Name) + "-良率趋势.png" };
        if (file.ShowDialog() != true) return;
        string id = _product.Id;
        if (!ProductYieldService.IsCurrent(_product) && !await CalculateAsync(id)) return;
        if (_product?.Id != id || _closed) return;
        var data = YieldTrend.SelectedData; string title = _product.Name + (FtMode.IsChecked == true ? " · FT 良率" : " · CP 良率");
        title += Period == ManagementPeriod.Year ? "（按年）" : Period == ManagementPeriod.Month ? "（按月）" : Period == ManagementPeriod.Lot ? "（按批次）" : "（按晶圆）";
        var cancellation = new CancellationTokenSource(); _calculation = cancellation; long version = ++_version; SetBusy(true);
        try
        {
            int pages = 0;
            await ManagementReportService.RunStaAsync(() =>
            {
                foreach (var page in ManagementReportService.Pages(data))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    string path = pages == 0 ? file.FileName : Path.Combine(Path.GetDirectoryName(file.FileName)!, Path.GetFileNameWithoutExtension(file.FileName) + $"-{pages + 1}-{Guid.NewGuid():N}.png");
                    File.WriteAllBytes(path, ManagementReportService.TrendImage(page, title)); pages++;
                }
            });
            if (!_closed && version == _version) TxtStatus.Text = $"已导出 {pages} 张趋势图（每张最多 10 个晶圆 / 批次）。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && version == _version) TxtStatus.Text = ex.Message; }
        finally { if (ReferenceEquals(_calculation, cancellation)) _calculation = null; cancellation.Dispose(); if (!_closed && version == _version) SetBusy(false); }
    }
    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

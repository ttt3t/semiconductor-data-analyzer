using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;
using SemiconductorCsvAnalyzer.Controls;

namespace SemiconductorCsvAnalyzer;

public partial class MainWindow : Window
{
    private AnalyzerConfig _config = new();
    private readonly RecentFilesService _recentFiles;
    private readonly UiPreferencesService _uiPreferences;
    private List<TestItemData> _allItems = new();
    private List<TestItemSummary> _summaries = new();
    private ListCollectionView? _summaryView;
    private readonly IdleMemoryReclaimer _memoryReclaimer;
    private List<DeviceInfo> _devices = new();
    private List<string> _allSites = new();
    private TestItemData? _currentItem;
    private string _currentSite = TestItemSummary.CombinedSite;
    private string _lastConfigPath = "";
    private string _currentCsvPath = "";
    private CsvParseResult _data = new();
    private List<CsvImportSource> _sources = new();
    private readonly List<(WaferMapWindow Window, Func<CsvParseResult, IReadOnlyList<WaferMapPage>> Pages)> _openMaps = new();
    private int _binMode;
    private bool _combineSites;
    private RetestMode _retestMode = RetestMode.All;
    private bool _isBusy;
    private readonly Dictionary<string, string> _columnFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _columnValueFilters = new(StringComparer.OrdinalIgnoreCase);
    private ColumnValueFilterPopup? _valueFilterPopup;
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _detailTimer;
    private bool _imeComposing;
    private CancellationTokenSource? _detailCancellation;
    private readonly SemaphoreSlim _detailGate = new(1, 1);
    private DetailCache _detailCache = new();
    private TestItemData? _displayedItem;
    private string? _displayedSite;
    private ScatterSeries? _displayedScatter;
    private long _detailVersion;
    private bool _closing;
    private bool _detailPending;

    public MainWindow() : this(null) { }

    public MainWindow(LayoutSettings? layoutSettings, RecentFilesService? recentFiles = null, UiPreferencesService? uiPreferences = null)
    {
        _recentFiles = recentFiles ?? RecentFilesService.Current;
        _uiPreferences = uiPreferences ?? UiPreferencesService.Current;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplySearchFilter(_currentItem?.DisplayName, _currentSite);
        };

        _detailTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(35) };
        _detailTimer.Tick += async (_, _) =>
        {
            _detailTimer.Stop();
            await UpdateDetailAsync();
        };

        InitializeComponent();
        ApplyUiPreferences();
        _uiPreferences.Changed += OnUiPreferencesChanged;
        _memoryReclaimer = new IdleMemoryReclaimer(Dispatcher, () =>
            !_closing && !_isBusy && !_detailPending && _detailGate.CurrentCount == 1
            && !DgTestItems.IsScrollActive && !_filterTimer.IsEnabled);
        Closing += (_, e) => { if (_isBusy) e.Cancel = true; };
        // app.ico is optional during development; when present it is embedded as a WPF resource.
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        DgTestItems.ScrollActivityChanged += OnListScrollActivityChanged;

        var settings = layoutSettings ?? LayoutSettings.Current;
        var layout = new WindowLayoutPersistence(this, "Main", settings);
        layout.TrackTable("TestItems", DgTestItems);
        if (settings.Get("Main")?.Tables?.GetValueOrDefault("TestItems")?.Columns?
            .Any(c => c.Key == nameof(TestItemSummary.RunValue)) != true &&
            DgTestItems.Columns.First(c => c.SortMemberPath == "Mean").DisplayIndex is >= 0 and var meanIndex)
            RunValueColumn.DisplayIndex = meanIndex + 1;
        if (settings.Get("Main")?.Tables?.GetValueOrDefault("TestItems")?.Columns?
            .Any(c => c.Key == "Data.Focus.IsFocused") != true) FocusColumn.DisplayIndex = 0;
        layout.TrackColumns("ListAndCharts", MainListColumn, ChartsColumn);
        layout.TrackRows("HistogramAndScatter", HistogramRow, ScatterRow);
        settings.SaveFailed += OnLayoutSaveFailed;
        Closed += (_, _) =>
        {
            _closing = true;
            CloseValueFilter();
            _detailVersion++;
            _detailTimer.Stop();
            _filterTimer.Stop();
            _detailCancellation?.Cancel();
            _memoryReclaimer.Dispose();
            ClearDetail();
            DgTestItems.ItemsSource = null;
            if (_summaryView != null) _summaryView.Filter = null;
            _summaryView = null;
            _currentItem = null;
            _summaries = new();
            _allItems = new();
            _devices = new();
            _data = new();
            _runBySerial = new(StringComparer.Ordinal);
            _runItems = new(StringComparer.Ordinal);
            DgTestItems.ScrollActivityChanged -= OnListScrollActivityChanged;
            settings.SaveFailed -= OnLayoutSaveFailed;
            _uiPreferences.Changed -= OnUiPreferencesChanged;
        };

        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, e) =>
        {
            if (e.TextComposition != null && string.IsNullOrEmpty(e.TextComposition.CompositionText))
                _imeComposing = false;
        });
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) =>
        {
            _imeComposing = false;
            if (!RunTextBox.IsKeyboardFocusWithin) ScheduleFilter();
        });

        TryLoadDefaultConfig();
    }

    #region 配置加载

    private void OnLayoutSaveFailed(string message) => TxtStatus.Text = message;

    private void OnUiPreferencesChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => { if (!_closing) ApplyUiPreferences(); }));
            return;
        }
        ApplyUiPreferences();
    }

    private void ApplyUiPreferences()
    {
        var preferences = _uiPreferences.Value;
        DgTestItems.RowHeight = preferences.CompactTable ? 22 : 28;
        DgTestItems.FontSize = preferences.CompactTable ? 12 : 13;
        DgTestItems.ColumnHeaderHeight = preferences.ShowColumnFilters ? 44 : 28;
        DgTestItems.AlternatingRowBackground = preferences.AlternateRows
            ? (Brush)new BrushConverter().ConvertFromString("#F7F9FC")! : Brushes.Transparent;
        foreach (var column in DgTestItems.Columns)
            if (column.Header is Panel panel)
                foreach (var box in panel.Children.OfType<TextBox>())
                {
                    // A hidden keyword box must not leave an invisible restriction on the list.
                    if (!preferences.ShowColumnFilters) box.Clear();
                    box.Visibility = preferences.ShowColumnFilters ? Visibility.Visible : Visibility.Collapsed;
                }
    }

    private void TryLoadDefaultConfig()
    {
        AppSettings.Load();

        string[] candidates =
        {
            AppSettings.LastConfigCopyPath,
            AppSettings.LastConfigPath ?? "",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configs", "config.ini"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"),
            "config.ini",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_analyzer_config.txt"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configs", "analyzer_config.txt"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "analyzer_config.txt"),
            "analyzer_config.txt"
        };

        foreach (var p in candidates)
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
            {
                LoadConfig(p, remember: false);
                return;
            }
        }

        TxtConfigName.Text = "(未加载配置)";
        TxtStatus.Text = "请先加载配置文件";
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        new SettingsWindow(_config, ApplySettings, OpenCsvAsync, _recentFiles, _uiPreferences) { Owner = this }.ShowDialog();
    }

    private async Task ApplySettings(AnalyzerConfig config)
    {
        await RunBusyAsync(async () =>
        {
            bool reload = !string.IsNullOrEmpty(_currentCsvPath) && ConfigLoader.CsvDefinitionChanged(_config, config);
            var data = reload ? await Task.Run(() => _sources.Count > 0
                ? DatasetService.LoadSources(_sources, config) : DataFileService.Parse(_currentCsvPath, config)) : _data;
            var prepared = await PrepareAnalysisAsync(data, config);
            await Task.Run(() => AppSettings.SaveConfig(config));
            _config = config;
            _lastConfigPath = AppSettings.LastConfigCopyPath;
            UpdateConfigInfo();
            CommitAnalysis(data, prepared);
            RefreshOpenMaps();
            TxtStatus.Text = "设置已保存，统计已重算" + (reload ? "，数据文件已重新解析" : "");
            if (reload) ShowSerialNumberNotice(data);
        });
    }

    private void UpdateConfigInfo()
    {
        TxtConfigName.Text = _config.ConfigFileName;
        TxtBottomInfo.Text = $"TestItemRow={_config.TestItemRow}  DataStartRow={_config.DataStartRow}  StartCol={_config.TestItemStartColumn}";
        CmbBinMode.SelectedIndex = _config.BinMode.ToUpperInvariant() switch
        {
            "SPEC" or "SPECZONE" => 1,
            "MEAN" or "MEANCENTERED" or "MEDIAN" or "MEDIANCENTERED" => 2,
            _ => 0
        };
    }

    private void LoadConfig(string path, bool remember = false)
    {
        try
        {
            _config = ConfigLoader.Load(path);
            _lastConfigPath = path;
            UpdateConfigInfo();
            TxtStatus.Text = $"配置已加载 | 编码={_config.EncodingName} 分隔符='{_config.Delimiter}'";
            TxtBottomInfo.Text = $"TestItemRow={_config.TestItemRow}  DataStartRow={_config.DataStartRow}  StartCol={_config.TestItemStartColumn}";

            if (remember)
            {
                AppSettings.SaveLastConfig(path);
                TxtStatus.Text += "  | 已保存为默认配置";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "配置加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region 打开 CSV

    private async void BtnOpenCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dlg = new OpenFileDialog
        {
            Filter = DataFileService.OpenFilter,
            Title = "打开 CSV / STDF"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            await OpenCsvAsync(dlg.FileName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "数据解析失败", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "解析失败";
        }
    }

    private async Task OpenCsvAsync(string path)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据，请稍后重试。");
        var mode = SelectStdfMode(path);
        if (!mode.HasValue) throw new OperationCanceledException();
        await OpenDataAsync(path, mode.Value);
    }

    private StdfImportMode? SelectStdfMode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到数据文件：" + path);
        if (!DataFileService.IsStdf(path)) return StdfImportMode.Standard;
        var options = new StdfImportWindow(path) { Owner = this };
        if (_uiPreferences.Value.DefaultStdfMode == StdfImportMode.Advantest93K)
            ((RadioButton)options.FindName("Mode93K")).IsChecked = true;
        return options.ShowDialog() == true ? options.SelectedMode : null;
    }

    private async Task OpenDataAsync(string path, StdfImportMode mode)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据，请稍后重试。");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到数据文件：" + path);
        if (string.IsNullOrEmpty(_lastConfigPath) && !DataFileService.IsStdf(path) && !IntegratedCsvService.IsIntegrated(path))
            throw new InvalidOperationException("普通 CSV 需要先在设置中加载行列配置；STDF 可直接打开。");
        await RunBusyAsync(async () =>
        {
            TxtStatus.Text = "处理中…";
            var result = await Task.Run(() => DataFileService.Parse(path, _config, stdfMode: mode));
            var prepared = await PrepareAnalysisAsync(result, _config);
            _columnValueFilters.Clear();
            UpdateValueFilterHeaders();
            CommitAnalysis(result, prepared);
            _sources = new List<CsvImportSource> { new(path, StdfMode: mode) };
            _currentCsvPath = path;
            TxtCsvName.Text = Path.GetFileName(path);
            RefreshOpenMaps();
            TxtStatus.Text = _recentFiles.RememberCsv(path) ? "解析完成" : "解析完成（最近文件记录未能保存）";
            if (result.SourceDescription.Length > 0) TxtStatus.Text += " | " + result.SourceDescription;
            ShowSerialNumberNotice(result);
        });
    }

    #endregion

    private async void BtnImportData_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0)
        { MessageBox.Show("请先打开基础数据。", "导入新数据", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var file = new OpenFileDialog { Filter = DataFileService.OpenFilter, Title = "选择要导入的新数据" };
        if (file.ShowDialog(this) != true) return;
        var choices = new ImportDataWindow(Path.GetFileName(file.FileName), _data.Wafers) { Owner = this };
        if (choices.ShowDialog() != true) return;
        try
        {
            var mode = SelectStdfMode(file.FileName);
            if (!mode.HasValue) return;
            await RunBusyAsync(async () =>
            {
                var merged = await Task.Run(() => DatasetService.Merge(_data, DataFileService.Parse(file.FileName, _config, stdfMode: mode.Value), choices.Options!));
                var prepared = await PrepareAnalysisAsync(merged, _config);
                CommitAnalysis(merged, prepared);
                _sources.Add(new CsvImportSource(file.FileName, choices.Options, mode.Value));
                TxtCsvName.Text = $"整合数据（{_sources.Count} 个文件 / {_data.Wafers.Count} 个 wafer）";
                RefreshOpenMaps();
                _recentFiles.RememberCsv(file.FileName);
                TxtStatus.Text = $"已导入 {Path.GetFileName(file.FileName)}，原始测试记录全部保留";
                ShowSerialNumberNotice(merged);
            });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnExportData_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0)
        { MessageBox.Show("没有可导出的数据。", "导出数据", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var options = new ExportDataWindow(_data.Wafers, _config.OutlierSigma) { Owner = this };
        if (options.ShowDialog() != true) return;
        var wafers = options.SelectedWafers;
        var mode = options.Mode;
        var outlierSigma = options.OutlierSigma;
        var file = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true,
            FileName = "integrated.csv", Title = "导出数据" };
        if (file.ShowDialog(this) != true) return;
        try
        {
            await RunBusyAsync(async () =>
            {
                int count = await Task.Run(() => IntegratedCsvService.Save(file.FileName, _data, wafers, mode, outlierSigma));
                TxtStatus.Text = $"已导出 {wafers.Length} 个 wafer、{count} 条测试记录（含异常原文），可直接重新打开" +
                    (outlierSigma.HasValue ? $"；已剔除均值 ± {outlierSigma.Value:G}σ 以外的测项值" : "");
            });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0)
        { MessageBox.Show("请先打开 CSV / STDF 数据。", "导出报告", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var focused = _data.Items.Where(i => i.Focus.IsFocused).Select(i => i.Id).ToArray();
        var options = new ExportDataWindow(_data.Wafers, _config.OutlierSigma, report: true,
            histogramMode: _binMode, combineSites: _combineSites, focusedCount: focused.Length) { Owner = this };
        options.HistogramModeChanged += mode => CmbBinMode.SelectedIndex = mode;
        if (options.ShowDialog() != true) return;
        var file = new SaveFileDialog { Filter = "Excel 报告 (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", AddExtension = true,
            FileName = "测试分析报告.xlsx", Title = "导出 XLSX 报告" };
        if (file.ShowDialog(this) != true) return;
        var report = new XlsxReportOptions(options.SelectedWafers, options.Mode, options.OutlierSigma,
            options.HistogramMode, options.CombineSites, focused, options.ShowBinNumbers, options.ShowSiteBorders);
        try
        {
            await RunBusyAsync(async () =>
            {
                await XlsxReportService.SaveAsync(file.FileName, _data, _config, report, TxtCsvName.Text);
                TxtStatus.Text = $"报告已导出：4 个工作表，{focused.Length} 个关注测项";
            });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "报告导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    #region 测试项列表 / Combine Site / 列头筛选

    private async void BtnCombineSite_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        bool previous = _combineSites;
        _combineSites = BtnCombineSite.IsChecked == true;
        try { await RunBusyAsync(RefreshTestItemList); }
        catch (Exception ex)
        {
            _combineSites = previous; BtnCombineSite.IsChecked = previous;
            MessageBox.Show(ex.Message, "重算失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RetestMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _isBusy || LstRetestMode.SelectedIndex < 0) return;
        var mode = (RetestMode)LstRetestMode.SelectedIndex;
        if (mode == _retestMode) return;
        var previous = _retestMode;
        _retestMode = mode;
        try { await RunBusyAsync(RefreshTestItemList); }
        catch (Exception ex)
        {
            _retestMode = previous; LstRetestMode.SelectedIndex = (int)previous;
            MessageBox.Show(ex.Message, "重算失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record PreparedAnalysis(CsvParseResult Data, List<string> Sites, List<TestItemSummary> Summaries);

    private Task<PreparedAnalysis> PrepareAnalysisAsync(CsvParseResult data, AnalyzerConfig config)
    {
        var mode = _retestMode;
        bool combine = _combineSites;
        return Task.Run(() =>
        {
            var analysis = DatasetService.SelectRetests(data, mode);
            var sites = CollectSites(analysis.Items);
            return new PreparedAnalysis(analysis, sites, SummaryService.Build(analysis.Items, sites, combine, config));
        });
    }

    private async Task RefreshTestItemList()
    {
        var prepared = await PrepareAnalysisAsync(_data, _config);
        CommitAnalysis(_data, prepared);
    }

    private void CommitAnalysis(CsvParseResult data, PreparedAnalysis prepared)
    {
        string? keepName = _currentItem?.DisplayName;
        string keepSite = _combineSites ? TestItemSummary.CombinedSite : _currentSite;
        ClearDetail();
        bool changedData = !ReferenceEquals(_data, data);
        _data = data;
        _allItems = prepared.Data.Items;
        _devices = data.Devices;
        _allSites = prepared.Sites;
        _summaries = prepared.Summaries;
        InitializeRun(changedData);
        ApplySearchFilter(keepName, keepSite);
        UpdateBottomInfo();
        ScheduleDetail();
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理，请稍后再试。");
        SetBusy(true);
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            await operation();
        }
        finally { SetBusy(false); _memoryReclaimer.Request(); }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MainToolbar.IsEnabled = MainContent.IsEnabled = !busy;
        if (busy)
        {
            CloseValueFilter();
            _detailVersion++;
            _detailTimer.Stop();
            _detailCancellation?.Cancel();
        }
        else if (_currentItem != null) ScheduleDetail();
    }

    private void ColumnFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is string key)
        {
            _columnFilters[key] = tb.Text ?? "";
            if (_imeComposing) return;
            ScheduleFilter();
        }
    }

    private void ScheduleFilter()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void CloseValueFilter()
    {
        if (_valueFilterPopup != null) _valueFilterPopup.IsOpen = false;
        _valueFilterPopup = null;
    }

    private static string? FilterKey(DataGridColumn column) => column.Header is Panel panel
        ? panel.Children.OfType<TextBox>().Select(t => t.Tag as string).FirstOrDefault(k => k != null)
        : null;

    private void TestItems_HeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node is not DataGridColumnHeader)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (_isBusy || node is not DataGridColumnHeader { Column: { } column } header || FilterKey(column) is not { } key) return;
        e.Handled = true;
        OpenValueFilter(header, column, key);
    }

    private void OpenValueFilter(FrameworkElement target, DataGridColumn column, string key)
    {
        CloseValueFilter();
        string title = column.Header is Panel panel ? panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? key : key;
        _columnValueFilters.TryGetValue(key, out var selected);
        var popup = new ColumnValueFilterPopup(title, _summaries.Select(s => ColumnText(s, key)), selected, values =>
        {
            if (values == null) _columnValueFilters.Remove(key);
            else _columnValueFilters[key] = values;
            UpdateValueFilterHeaders();
            ScheduleFilter();
        }) { PlacementTarget = target };
        popup.Closed += (_, _) => { if (ReferenceEquals(_valueFilterPopup, popup)) _valueFilterPopup = null; };
        _valueFilterPopup = popup;
        popup.IsOpen = true;
    }

    private void UpdateValueFilterHeaders()
    {
        foreach (var column in DgTestItems.Columns)
        {
            if (FilterKey(column) is not { } key || column.Header is not Panel panel) continue;
            bool active = _columnValueFilters.ContainsKey(key);
            panel.Background = active ? Brushes.LightBlue : Brushes.Transparent;
        }
    }

    private void HeaderFilter_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            tb.Focus();
            e.Handled = true;
        }
    }

    private void ApplySearchFilter(string? keepName = null, string? keepSite = null)
    {
        if (_summaryView == null || !ReferenceEquals(_summaryView.SourceCollection, _summaries))
        {
            if (_summaryView != null) _summaryView.Filter = null;
            _summaryView = new ListCollectionView(_summaries);
            DgTestItems.ItemsSource = _summaryView;
        }

        var view = _summaryView;
        if (view != null)
        {
            view.Filter = o => o is TestItemSummary s && MatchesColumnFilters(s);
        }

        TestItemSummary? match = null;
        if (!string.IsNullOrEmpty(keepName) && view != null)
        {
            foreach (TestItemSummary s in view)
            {
                if (s.DisplayName == keepName && s.Site == keepSite)
                {
                    match = s;
                    break;
                }
                match ??= s.DisplayName == keepName ? s : null;
            }
        }

        if (match != null)
        {
            if (!ReferenceEquals(DgTestItems.SelectedItem, match))
                DgTestItems.SelectedItem = match;
        }
        else if (view != null && !view.IsEmpty && DgTestItems.SelectedItem == null)
        {
            view.MoveCurrentToFirst();
        }
    }

    private bool MatchesColumnFilters(TestItemSummary s)
    {
        foreach (var kv in _columnValueFilters)
            if (!kv.Value.Contains(ColumnText(s, kv.Key))) return false;
        foreach (var kv in _columnFilters)
        {
            string needle = kv.Value.Trim();
            if (needle.Length == 0) continue;

            string cell = ColumnText(s, kv.Key);
            if (!cell.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string ColumnText(TestItemSummary s, string key) => key switch
    {
        "TestNumber" => s.TestNumberValue.ToString(),
        "DisplayName" => s.DisplayName ?? "",
        "Site" => s.Site ?? "",
        "SBin" => s.SBin ?? "",
        "HBin" => s.HBin ?? "",
        "Unit" => s.Unit ?? "",
        "Lsl" => s.LslText ?? "",
        "Usl" => s.UslText ?? "",
        "Count" => s.Count.ToString(),
        "Eorr" => s.Eorr.ToString(),
        "SiteSigma" => s.SiteSigma?.ToString("G6") ?? "-",
        "Mean" => s.Mean.ToString("G6"),
        "RunValue" => s.RunValue?.ToString("G9") ?? "-",
        "Median" => s.Median?.ToString("G6") ?? "-",
        "Sigma" => s.Sigma.ToString("G6"),
        "RobustMean" => s.RobustMean?.ToString("G6") ?? "-",
        "RobustSigma" => s.RobustSigma?.ToString("G6") ?? "-",
        "Cpk" => s.Cpk.HasValue ? s.Cpk.Value.ToString("F3") : "-",
        "InSpec" => s.InSpecPercent.ToString("F1"),
        _ => ""
    };

    private static List<string> CollectSites(IEnumerable<TestItemData> items)
    {
        var sites = items
            .SelectMany(i => i.Values.Select(v => SiteKey(v.Site)).Concat(i.Errors.Select(r => SiteKey(r.Site))))
            .Distinct()
            .OrderBy(NumericText.SortKey)
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sites.Count == 0)
            sites.Add("(空)");

        return sites;
    }

    private static string SiteKey(string? site)
        => string.IsNullOrWhiteSpace(site) ? "(空)" : site;

    private void UpdateBottomInfo()
    {
        if (_allItems.Count == 0) return;
        TxtBottomInfo.Text =
            $"测试项 {_allItems.Count} 个 | Site {_allSites.Count} 个 | 表格 {_summaries.Count} 行 | 唯一芯片 {_devices.Count} 颗";
    }

    #endregion

    #region 选中测试项 / 直方图 / 散点图

    private void DgTestItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DgTestItems.SelectedItem is TestItemSummary summary)
        {
            if (ReferenceEquals(_currentItem, summary.Data) && _currentSite == summary.Site)
                return;

            _currentItem = summary.Data;
            _currentSite = summary.Site;
            ScheduleDetail();
        }
        else
        {
            _currentItem = null;
            ClearDetail();
        }
    }

    private void CmbBinMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _binMode = CmbBinMode.SelectedIndex;
        _config.BinMode = _binMode switch { 1 => "SpecZone", 2 => "MedianCentered", _ => "Fixed" };
        ScheduleDetail();
    }

    private void ScheduleDetail()
    {
        _detailPending = _currentItem != null;
        _detailVersion++;
        _detailCancellation?.Cancel();
        _detailTimer.Stop();
        if (!_closing && !_isBusy && _detailPending && !DgTestItems.IsScrollActive) _detailTimer.Start();
    }

    private void OnListScrollActivityChanged(object? sender, EventArgs e)
    {
        if (_closing || _isBusy || !_detailPending) return;
        _detailTimer.Stop();
        if (DgTestItems.IsScrollActive)
        {
            // List scrolling remains immediate; only obsolete chart work is postponed.
            _detailVersion++;
            _detailCancellation?.Cancel();
        }
        else _detailTimer.Start();
    }

    private IReadOnlyList<TestValue> CurrentFilteredValues()
    {
        if (_currentItem == null)
            return Array.Empty<TestValue>();

        if (_combineSites || _currentSite == TestItemSummary.CombinedSite)
            return _currentItem.Values;

        return IndexedTestValues.ForSite(_currentItem.Values, _currentSite);
    }

    private async Task UpdateDetailAsync()
    {
        if (_currentItem == null || Histogram == null || _closing || DgTestItems.IsScrollActive) return;
        long version = _detailVersion;
        var request = new DetailRequest(_currentItem,
            _combineSites || _currentSite == TestItemSummary.CombinedSite ? null : _currentSite,
            _binMode, _config.BinCount, _config.IncludeOutOfLimitData,
            _config.ShowLowLimit, _config.ShowHighLimit, _config.ShowMean, _config.ShowThreeSigma);
        using var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        string siteLabel = request.Site == null ? "ALL Sites" : $"Site {request.Site}";
        TxtItemTitle.Text = $"{request.Item.DisplayName}  ·  {siteLabel}";
        if (!ReferenceEquals(_displayedItem, request.Item) || _displayedSite != request.Site)
        {
            TxtUnit.Text = "正在计算图表…";
            Histogram.Apply(null, null, null, null, null, true, true, true, true);
            ScatterPlot.SetSeries(ScatterSeries.Empty, null, null, "");
            _displayedScatter = null;
        }
        bool entered = false;
        try
        {
            // One worker at a time prevents obsolete requests competing with the newest selection.
            await _detailGate.WaitAsync(cancellation.Token);
            entered = true;
            var cache = _detailCache;
            var result = await Task.Run(() => cache.Calculate(request, cancellation.Token), cancellation.Token);
            if (_closing || cancellation.IsCancellationRequested || version != _detailVersion ||
                DgTestItems.IsScrollActive) return;
            var stats = result.Statistics;
            TxtUnit.Text = string.IsNullOrWhiteSpace(request.Item.Unit)
                ? $"Count={stats.Count}  InSpec%={stats.InSpecPercent:F1}"
                : $"Unit：{request.Item.Unit}    Count={stats.Count}  InSpec%={stats.InSpecPercent:F1}";
            Histogram.Apply(result.Bins, request.Item.LowLimit, request.Item.HighLimit,
                result.Mean, result.Sigma, request.ShowLsl, request.ShowUsl, request.ShowMean,
                request.ShowThreeSigma, request.Item.Unit);
            if (!ReferenceEquals(_displayedScatter, result.Scatter))
                ScatterPlot.SetSeries(result.Scatter, request.Item.LowLimit, request.Item.HighLimit, request.Item.Unit);
            _displayedItem = request.Item;
            _displayedSite = request.Site;
            _displayedScatter = result.Scatter;
            _detailPending = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && version == _detailVersion)
            {
                TxtUnit.Text = "图表计算失败：" + ex.Message;
                _detailPending = false;
            }
        }
        finally
        {
            if (entered) _detailGate.Release();
            if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null;
        }
    }
    private void ClearDetail()
    {
        _detailPending = false;
        _displayedItem = null;
        _displayedScatter = null;
        _detailVersion++;
        _detailTimer.Stop();
        _detailCancellation?.Cancel();
        // Drop the old item/row references even when filtering leaves no selection.
        // An already running worker finishes against its own cache instance.
        _detailCache = new DetailCache();
        if (TxtItemTitle != null) TxtItemTitle.Text = "选择测试项查看分布";
        if (TxtUnit != null) TxtUnit.Text = "";
        Histogram?.Apply(null, null, null, null, null, true, true, true, true);
        ScatterPlot?.SetSeries(ScatterSeries.Empty, null, null, "");
    }

    #endregion

    #region Value 表弹窗

    private void BtnShowValues_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem == null)
        {
            MessageBox.Show("请先打开数据并选择一个测试项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var filtered = CurrentFilteredValues();
        if (filtered.Count > _config.MaxTableRows)
            filtered = filtered.Take(_config.MaxTableRows).ToList();

        string siteLabel = _combineSites || _currentSite == TestItemSummary.CombinedSite
            ? "ALL Sites"
            : $"Site {_currentSite}";
        string title = $"Value 表  {_currentItem.DisplayName}  ({siteLabel})";

        var window = new ValuesWindow(title, filtered) { Owner = this };
        window.Closed += (_, _) => _memoryReclaimer.Request();
        window.Show();
    }

    #endregion

    #region 复制

    private void BtnCopyAllStats_Click(object sender, RoutedEventArgs e)
    {
        var list = DgTestItems.Items.OfType<TestItemSummary>().ToList();
        if (list.Count == 0)
        {
            TxtStatus.Text = "没有可复制的数据";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("序号\t测试项\tSite\tSBIN\tHBIN\t单位\tLSL\tUSL\tCount\tEorr\tSite σ\tMean\tValue\t中位数\tσ\tRobust Mean\tRobust Sigma\tCpk\tInSpec%");

        foreach (var s in list)
        {
            sb.Append(s.TestNumberValue).Append('\t')
              .Append(s.DisplayName).Append('\t')
              .Append(s.Site).Append('\t')
              .Append(s.SBin).Append('\t')
              .Append(s.HBin).Append('\t')
              .Append(s.Unit).Append('\t')
              .Append(s.LslText).Append('\t')
              .Append(s.UslText).Append('\t')
              .Append(s.Count).Append('\t')
              .Append(s.Eorr).Append('\t')
              .Append(s.SiteSigma?.ToString("G6") ?? "-").Append('\t')
              .Append(s.Mean.ToString("G6")).Append('\t')
              .Append(s.RunValue?.ToString("G9") ?? "-").Append('\t')
              .Append(s.Median?.ToString("G6") ?? "-").Append('\t')
              .Append(s.Sigma.ToString("G6")).Append('\t')
              .Append(s.RobustMean?.ToString("G6") ?? "-").Append('\t')
              .Append(s.RobustSigma?.ToString("G6") ?? "-").Append('\t')
              .Append(s.Cpk.HasValue ? s.Cpk.Value.ToString("F3") : "-").Append('\t')
              .Append(s.InSpecPercent.ToString("F1"))
              .AppendLine();
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            TxtStatus.Text = $"已复制 {list.Count} 行统计数据到剪贴板（可直接粘贴到 Excel）";
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DgTestItems_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject origin)
        {
            for (var node = origin; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is Thumb) break; // Keep column-edge auto sizing intact.
                if (node is DataGridColumnHeader header)
                {
                    if (header.Column == FocusColumn && !_isBusy)
                    {
                        ToggleVisibleFocus();
                        e.Handled = true;
                    }
                    return;
                }
            }
        }
        if (e.OriginalSource is DependencyObject source)
        {
            for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is CheckBox) return;
        }
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridCell)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is not DataGridCell cell) return;

        string? text = cell.Content switch
        {
            TextBlock tb => tb.Text,
            TextBox tbox => tbox.Text,
            { } content => content.ToString(),
            _ => null
        };

        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
            TxtStatus.Text = $"已复制单元格：{text}";
        }
        catch
        {
            // ignore clipboard errors
        }

        e.Handled = true;
    }

    private void Focus_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    #endregion

    #region SBIN / HBIN 统计与 Map

    private void BtnYieldSimulation_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0) { MessageBox.Show("请先打开 CSV / STDF 文件。", "良率预测"); return; }
        try { new YieldSimulationWindow(_data, _config, _retestMode) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "良率预测"); }
        finally { _memoryReclaimer.Request(); }
    }

    private async void BtnBinStats_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0)
        {
            MessageBox.Show("请先打开 CSV / STDF 文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            BinStatisticsWindow? window = null;
            await RunBusyAsync(async () =>
            {
                var result = await Task.Run(() => (
                    First: BinStatisticsService.Build(_data.Records, RetestMode.First, _config),
                    Last: BinStatisticsService.Build(_data.Records, RetestMode.Last, _config)));
                window = new BinStatisticsWindow(result.First, result.Last) { Owner = this };
            });
            window!.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Bin 统计失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _memoryReclaimer.Request(); }
    }
    private async void BtnWaferTable_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0) { MessageBox.Show("请先打开 CSV / STDF 文件", "Wafer Table"); return; }
        try
        {
            WaferTableWindow? window = null;
            await RunBusyAsync(async () =>
            {
                var result = await Task.Run(() => (
                    First: WaferStatisticsService.Build(_data.Records, RetestMode.First, _config),
                    Last: WaferStatisticsService.Build(_data.Records, RetestMode.Last, _config)));
                window = new WaferTableWindow(result.First, result.Last) { Owner = this };
            });
            window!.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Wafer Table 统计失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _memoryReclaimer.Request(); }
    }

    private void BtnSbinMap_Click(object sender, RoutedEventArgs e)
    {
        ShowBinMap("SBIN", d => string.IsNullOrWhiteSpace(d.SBin) ? "(空)" : d.SBin);
    }

    private void BtnHbinMap_Click(object sender, RoutedEventArgs e)
    {
        ShowBinMap("HBIN", d => string.IsNullOrWhiteSpace(d.HBin) ? "(空)" : d.HBin);
    }

    private void BtnPfMap_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem == null)
        {
            MessageBox.Show("请先选择一个测试项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? site = _combineSites || _currentSite == TestItemSummary.CombinedSite ? null : _currentSite;
        var mapItem = _data.Items.FirstOrDefault(i => i.Id == _currentItem.Id) ?? _currentItem;
        var pages = MapDataService.PfPages(_data, mapItem, site);
        if (pages.All(p => p.Dies.Count == 0))
        {
            MessageBox.Show("当前测试项没有有效的 X/Y 坐标", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string siteLabel = _combineSites || _currentSite == TestItemSummary.CombinedSite ? "ALL Sites" : $"Site {_currentSite}";
        // Keep only the matching keys; retaining the old item would also retain its
        // entire raw snapshot after a new dataset refreshes this still-open Map.
        string selectedId = _currentItem.Id, selectedName = _currentItem.Name, selectedNumber = _currentItem.TestNumber;
        ShowMapWindow(new WaferMapWindow($"P/F Map  {_currentItem.DisplayName}  ({siteLabel})", pages), data =>
        {
            var item = data.Items.FirstOrDefault(i => i.Id == selectedId) ??
                data.Items.FirstOrDefault(i => i.Name == selectedName && i.TestNumber == selectedNumber);
            return item != null ? MapDataService.PfPages(data, item, site) :
                data.Wafers.Select(w => new WaferMapPage(w, Array.Empty<WaferDie>())).ToList();
        });
    }

    private void ShowBinMap(string binName, Func<DeviceInfo, string> selector)
    {
        if (_devices.Count == 0)
        {
            MessageBox.Show("请先打开 CSV / STDF 文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pages = MapDataService.BinPages(_data, selector);
        if (pages.All(p => p.Dies.Count == 0))
        {
            MessageBox.Show("没有有效的 X/Y 坐标，无法绘制 Map", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowMapWindow(new WaferMapWindow($"{binName} Map  （按 wafer 分开，同坐标取最新）", pages, binName),
            data => MapDataService.BinPages(data, selector));
    }

    private void ShowMapWindow(WaferMapWindow window, Func<CsvParseResult, IReadOnlyList<WaferMapPage>> pages)
    {
        _openMaps.Add((window, pages));
        window.Closed += (_, _) =>
        {
            _openMaps.RemoveAll(m => ReferenceEquals(m.Window, window));
            _memoryReclaimer.Request();
        };
        window.Owner = this;
        window.Show();
    }

    private void RefreshOpenMaps()
    {
        foreach (var map in _openMaps) map.Window.SetPages(map.Pages(_data));
    }

    private static bool IsInSpec(double value, double? lsl, double? usl)
    {
        if (lsl.HasValue && value < lsl.Value) return false;
        if (usl.HasValue && value > usl.Value) return false;
        return true;
    }

    #endregion
}



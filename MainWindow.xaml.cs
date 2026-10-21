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
    private SummaryRows _summaries = new();
    private CollectionView? _summaryView;
    private readonly IdleMemoryReclaimer _memoryReclaimer;
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
    private List<string> _allSites = new();
    private TestItemData? _currentItem;
    private string _currentSite = TestItemSummary.CombinedSite;
    private string? _currentWafer;
    private string _lastConfigPath = "";
    private string _currentCsvPath = "";
    private string _currentDataName = "尚未打开文件";
    private bool _managedConfigLoaded;
    private CsvParseResult _data = new();
    private List<CsvImportSource> _sources = new();
    private readonly List<(WaferMapWindow Window, Func<CsvParseResult, IReadOnlyList<WaferMapPage>> Pages)> _openMaps = new();
    private int _binMode;
    private bool _combineSites;
    private bool _combineWafers;
    private ScatterOrder _scatterOrder;
    private RetestMode _retestMode = RetestMode.All;
    private bool _isBusy;
    private CancellationTokenSource? _operationCancellation;
    private DatasetLease? _datasetLease;
    private IDisposable? _summaryLease;
    private bool _closeAfterOperation;
    private bool _operationCommitting;
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
    private string? _displayedWafer;
    private List<HistogramBin>? _displayedBins;
    private ScatterSeries? _displayedScatter;
    private long _detailVersion;
    private bool _closing;
    private bool _detailPending;
    internal bool CanReclaimMemory => !_closing && !_isBusy && !_detailPending && _detailGate.CurrentCount == 1
        && DgTestItems != null && !DgTestItems.IsScrollActive && !_filterTimer.IsEnabled;

    public MainWindow() : this(null) { }

    public MainWindow(LayoutSettings? layoutSettings, RecentFilesService? recentFiles = null, UiPreferencesService? uiPreferences = null)
    {
        _recentFiles = recentFiles ?? RecentFilesService.Current;
        _uiPreferences = uiPreferences ?? UiPreferencesService.Current;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplySearchFilter(_currentItem?.DisplayName, _currentSite, _currentWafer);
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
        _memoryReclaimer = new IdleMemoryReclaimer(Dispatcher, () => CanReclaimMemory);
        Loaded += (_, _) => _memoryReclaimer.RequestStartup();
        Closing += (_, e) =>
        {
            if (!_isBusy) return;
            e.Cancel = true; _closeAfterOperation = true; CancelOperation();
        };
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
        if (settings.Get("Main")?.Tables?.GetValueOrDefault("TestItems")?.Columns?
            .Any(c => c.Key == nameof(TestItemSummary.Wafer)) != true)
            DgTestItems.Columns.First(c => c.SortMemberPath == "Wafer").DisplayIndex =
                DgTestItems.Columns.First(c => c.SortMemberPath == "SiteSort").DisplayIndex + 1;
        layout.TrackColumns("ListAndCharts", MainListColumn, ChartsColumn);
        layout.TrackRows("HistogramAndScatter", HistogramRow, ScatterRow);
        settings.SaveFailed += OnLayoutSaveFailed;
        Closed += (_, _) =>
        {
            _closing = true;
            _sampleFilterWindow?.Close(); _sampleFilterWindow = null;
            CloseValueFilter();
            _detailVersion++;
            _detailTimer.Stop();
            _filterTimer.Stop();
            _detailCancellation?.Cancel();
            _memoryReclaimer.Dispose();
            ClearDetail();
            ScatterPlot.ClearChipMarks();
            DgTestItems.ItemsSource = null;
            if (_summaryView is PagedSummaryView oldView) oldView.ReleaseIndex();
            _summaryView = null;
            _currentItem = null;
            _summaryLease?.Dispose(); _summaryLease = null; _summaries = new();
            _allItems = new();
            _devices = Array.Empty<DeviceInfo>();
            _data = new(); _scopeData = _scopeSource = null; _sampleRanges = _scopeRanges = Array.Empty<SampleRange>(); (_sampleIncludedIds as IDisposable)?.Dispose(); _sampleIncludedIds = null;
            _sources.Clear();
            _datasetLease?.Dispose(); _datasetLease = null;
            _runIndex = -1;
            _runBySerial = new(StringComparer.Ordinal);
            _runItems = new(StringComparer.Ordinal);
            ClearCoordinateRunIndex();
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
            if (!RunSelector.IsKeyboardFocusWithin) ScheduleFilter();
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
        ApplyRunSelectionMode(preferences.RunSelection);
    }

    private void TryLoadDefaultConfig()
    {
        AppSettings.Load();

        string[] candidates =
        {
            AppSettings.LastConfigCopyPath,
            AppSettings.LastConfigCopyPath + ".bak",
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
                if (LoadConfig(p, remember: false))
                {
                    if (p == AppSettings.LastConfigCopyPath + ".bak") TxtStatus.Text = "默认配置读取失败，已使用上一份备份。";
                    else if (AppSettings.LoadNotice.Length > 0) TxtStatus.Text = AppSettings.LoadNotice;
                    return;
                }
            }
        }

        UpdateWindowTitle();
        TxtStatus.Text = "请先加载配置文件";
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        new SettingsWindow(_config, ApplySettings, OpenCsvAsync, _recentFiles, _uiPreferences, _data, CancelOperation, _currentCsvPath) { Owner = this }.ShowDialog();
    }

    private async Task ApplySettings(AnalyzerConfig config)
    {
        await RunBusyAsync(async () =>
        {
            bool reload = !string.IsNullOrEmpty(_currentCsvPath) && ConfigLoader.CsvDefinitionChanged(_config, config);
            var data = reload ? await Task.Run(() => _sources.Count > 0
                ? DatasetService.LoadSources(_sources, config) : DataFileService.Parse(_currentCsvPath, config)) : _data;
            var prepared = await PrepareAnalysisAsync(data, config);
            AnalysisOperation.CheckCancellation();
            _operationCommitting = true; BtnCancelOperation.IsEnabled = false;
            await Task.Run(() => AppSettings.SaveConfig(config));
            _config = config;
            _lastConfigPath = AppSettings.LastConfigCopyPath;
            UpdateConfigInfo();
            CommitAnalysis(data, prepared);
            RefreshOpenMaps();
            TxtStatus.Text = "设置已保存，统计已重算" + (reload ? "，数据文件已重新解析" : "");
            if (reload) ShowSerialNumberNotice(_data);
            else ShowRetestIdentityNotice(_data);
        });
    }

    private void UpdateWindowTitle()
    {
        string configName = string.IsNullOrWhiteSpace(_config.ConfigFileName) ? "未加载配置" : _config.ConfigFileName;
        Title = $"CSV Analyzer · V2.1.0 | 当前数据：{_currentDataName} | 配置：{configName}";
    }

    private void UpdateConfigInfo()
    {
        UpdateWindowTitle();
        TxtBottomInfo.Text = $"TestItemRow={_config.TestItemRow}  DataStartRow={_config.DataStartRow}  StartCol={_config.TestItemStartColumn}";
        CmbBinMode.SelectedIndex = _config.BinMode.ToUpperInvariant() switch
        {
            "SPEC" or "SPECZONE" => 1,
            "MEAN" or "MEANCENTERED" or "MEDIAN" or "MEDIANCENTERED" => 2,
            _ => 0
        };
    }

    private bool LoadConfig(string path, bool remember = false)
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
            return true;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "配置加载失败：" + ex.Message;
            return false;
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
            Title = "打开 CSV / STDF（支持批量选择）",
            Multiselect = true
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            await OpenSelectedFilesAsync(dlg.FileNames);
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

    private StdfImportMode? SelectStdfMode(string path, int fileCount = 1)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到数据文件：" + path);
        if (!DataFileService.IsStdf(path)) return StdfImportMode.Standard;
        var options = new StdfImportWindow(path, fileCount) { Owner = this };
        if (_uiPreferences.Value.DefaultStdfMode == StdfImportMode.Advantest93K)
            ((RadioButton)options.FindName("Mode93K")).IsChecked = true;
        return options.ShowDialog() == true ? options.SelectedMode : null;
    }

    private async Task OpenDataAsync(string path, StdfImportMode mode)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据，请稍后重试。");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到数据文件：" + path);
        if (!_managedConfigLoaded && string.IsNullOrEmpty(_lastConfigPath) && !DataFileService.IsStdf(path) && !IntegratedCsvService.IsIntegrated(path))
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
            RefreshStdfInformationSources();
            _currentCsvPath = path;
            _currentDataName = Path.GetFileName(path);
            UpdateWindowTitle();
            RefreshOpenMaps();
            TxtStatus.Text = _recentFiles.RememberCsv(path) ? "解析完成" : "解析完成（最近文件记录未能保存）";
            if (result.SourceDescription.Length > 0) TxtStatus.Text += " | " + result.SourceDescription;
            ShowSerialNumberNotice(_data);
        });
    }

    #endregion

    public async Task OpenManagedFileAsync(string path, AnalyzerConfig config, StdfImportMode mode, RetestMode retestMode)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据。");
        var previousConfig = _config; var previousMode = _retestMode; bool previousManaged = _managedConfigLoaded;
        _config = ConfigLoader.Parse(ConfigLoader.Serialize(config), config.ConfigFileName);
        _managedConfigLoaded = true;
        _retestMode = retestMode; LstRetestMode.SelectedIndex = (int)retestMode;
        UpdateConfigInfo();
        try { await OpenDataAsync(path, mode); }
        catch
        {
            _config = previousConfig; _retestMode = previousMode; _managedConfigLoaded = previousManaged;
            LstRetestMode.SelectedIndex = (int)previousMode; UpdateConfigInfo(); throw;
        }
    }

    public async Task OpenManagedFilesAsync(IReadOnlyList<CsvImportSource> sources, AnalyzerConfig config, RetestMode retestMode)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理数据。");
        if (sources.Count == 0) throw new InvalidOperationException("未选择数据文件。");
        await RunBusyAsync(async () =>
        {
            var activeConfig = ConfigLoader.Parse(ConfigLoader.Serialize(config), config.ConfigFileName);
            var result = await Task.Run(() => DatasetService.LoadSources(sources, activeConfig));
            var oldRetestMode = _retestMode;
            _retestMode = retestMode;
            PreparedAnalysis prepared;
            try { prepared = await PrepareAnalysisAsync(result, activeConfig); }
            catch { _retestMode = oldRetestMode; throw; }
            _config = activeConfig; _managedConfigLoaded = true;
            LstRetestMode.SelectedIndex = (int)retestMode;
            _columnValueFilters.Clear(); UpdateValueFilterHeaders();
            CommitAnalysis(result, prepared);
            _sources = sources.ToList(); _currentCsvPath = sources[0].Path;
            RefreshStdfInformationSources();
            _currentDataName = sources.Count == 1 ? Path.GetFileName(sources[0].Path) : $"整合数据（{sources.Count} 个文件 / {result.Wafers.Count} 个 wafer）";
            UpdateConfigInfo(); RefreshOpenMaps();
            TxtStatus.Text = "已按登记顺序打开，保留全部原始测试记录；复测显示沿用产品设置。";
            ShowSerialNumberNotice(_data);
        });
    }

    private async void BtnImportData_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_data.Records.Count == 0)
        { BtnOpenCsv_Click(sender, e); return; }
        var file = new OpenFileDialog { Filter = DataFileService.OpenFilter, Title = "选择要追加的数据（支持多选 / 批量导入）", Multiselect = true };
        if (file.ShowDialog(this) != true) return;
        var choices = new ImportDataWindow(file.FileNames, _data.Wafers) { Owner = this };
        if (choices.ShowDialog() != true) return;
        try
        {
            var paths = choices.SelectedFiles;
            var stdfFiles = paths.Where(DataFileService.IsStdf).ToArray();
            var mode = stdfFiles.Length == 0 ? StdfImportMode.Standard : SelectStdfMode(stdfFiles[0], stdfFiles.Length);
            if (!mode.HasValue) return;
            var sources = paths.Select(path => new CsvImportSource(path, choices.Options, mode.Value)).ToArray();
            await RunBusyAsync(async () =>
            {
                var merged = await Task.Run(() => DatasetService.AppendSources(_data, sources, _config));
                var prepared = await PrepareAnalysisAsync(merged, _config);
                CommitAnalysis(merged, prepared);
                _sources.AddRange(sources);
                RefreshStdfInformationSources();
                _currentDataName = $"整合数据（{_sources.Count} 个文件 / {_data.Wafers.Count} 个 wafer）";
                UpdateWindowTitle();
                RefreshOpenMaps();
                foreach (string path in paths) _recentFiles.RememberCsv(path);
                TxtStatus.Text = $"已导入 {paths.Length} 个文件，原始测试记录全部保留";
                ShowSerialNumberNotice(_data);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnExportData_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (ActiveData.Records.Count == 0)
        { MessageBox.Show("没有可导出的数据，请先取消或调整数据筛选。", "导出数据", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var options = new ExportDataWindow(ActiveData.Wafers, _config.OutlierSigma) { Owner = this };
        if (options.ShowDialog() != true) return;
        var wafers = options.SelectedWafers;
        var mode = options.Mode;
        var outlierSigma = options.OutlierSigma;
        bool failOnly = options.FailChipsOnly;
        var file = new SaveFileDialog { Filter = failOnly ? "Excel 文件 (*.xlsx)|*.xlsx" : "CSV 文件 (*.csv)|*.csv",
            DefaultExt = failOnly ? ".xlsx" : ".csv", AddExtension = true,
            FileName = failOnly ? "Fail芯片数据.xlsx" : "integrated.csv", Title = "导出数据" };
        if (file.ShowDialog(this) != true) return;
        try
        {
            await RunBusyAsync(async () =>
            {
                if (failOnly)
                {
                    var exported = await ExportWorker.SaveFailAsync(file.FileName, ActiveData, _config, wafers, mode, outlierSigma);
                    TxtStatus.Text = $"已导出 2 个工作表：{exported.FailChips} 颗 Fail 芯片、{exported.RetestGoodChips} 颗复测良品芯片，共 {exported.Records} 条记录" +
                        (exported.FailChips == 0 ? "" : exported.ReferenceSerial == null ? "；没有可用的良品参考。" : $"；Fail 表良品参考 SN：{exported.ReferenceSerial}");
                    return;
                }
                int count = await Task.Run(() => IntegratedCsvService.Save(file.FileName, ActiveData, wafers, mode, outlierSigma));
                TxtStatus.Text = $"已导出 {wafers.Length} 个 wafer、{count} 条测试记录（含异常原文），可直接重新打开" +
                    (outlierSigma.HasValue ? $"；已剔除均值 ± {outlierSigma.Value:G}σ 以外的测项值" : "");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (ActiveData.Records.Count == 0)
        { MessageBox.Show("没有可导出的数据，请先打开文件或调整数据筛选。", "导出报告", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var focused = _data.Items.Where(i => i.Focus.IsFocused).Select(i => i.Id).ToArray();
        var options = new ExportDataWindow(ActiveData.Wafers, _config.OutlierSigma, report: true,
            histogramMode: _binMode, combineSites: _combineSites, focusedCount: focused.Length,
            combineWafers: _combineWafers) { Owner = this };
        options.HistogramModeChanged += mode => CmbBinMode.SelectedIndex = mode;
        if (options.ShowDialog() != true) return;
        var file = new SaveFileDialog { Filter = "Excel 报告 (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", AddExtension = true,
            FileName = "测试分析报告.xlsx", Title = "导出 XLSX 报告" };
        if (file.ShowDialog(this) != true) return;
        var report = new XlsxReportOptions(options.SelectedWafers, options.Mode, options.OutlierSigma,
            options.HistogramMode, options.CombineSites, focused, options.ShowBinNumbers, options.ShowSiteBorders,
            options.CombineWafers, _scatterOrder);
        try
        {
            await RunBusyAsync(async () =>
            {
                await ExportWorker.SaveReportAsync(file.FileName, ActiveData, _config, report, _currentDataName + (_sampleRanges.Count > 0 ? "（已应用数据筛选）" : ""));
                TxtStatus.Text = $"报告已导出：4 个工作表，{focused.Length} 个关注测项";
            });
        }
        catch (OperationCanceledException) { }
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
            if (ex is not OperationCanceledException) MessageBox.Show(ex.Message, "重算失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (ex is not OperationCanceledException) MessageBox.Show(ex.Message, "重算失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnCombineWafer_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        bool previous = _combineWafers;
        _combineWafers = BtnCombineWafer.IsChecked == true;
        try { await RunBusyAsync(RefreshTestItemList); }
        catch (Exception ex)
        {
            _combineWafers = previous; BtnCombineWafer.IsChecked = previous;
            if (ex is not OperationCanceledException) MessageBox.Show(ex.Message, "重算失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record PreparedAnalysis(CsvParseResult Data, List<string> Sites, SummaryRows Summaries,
        CsvParseResult Source, CsvParseResult Scope, IReadOnlyList<SampleRange> Ranges, SampleMatchMode Match, DiskKeySet? Included);

    private Task<PreparedAnalysis> PrepareAnalysisAsync(CsvParseResult data, AnalyzerConfig config)
    {
        var mode = _retestMode;
        bool combine = _combineSites;
        bool combineWafers = _combineWafers;
        var ranges = ReferenceEquals(data, _data) ? _sampleRanges.ToArray() : Array.Empty<SampleRange>();
        var match = ReferenceEquals(data, _data) ? _sampleMatch : SampleMatchMode.Intersection;
        var cachedScope = ReferenceEquals(data, _scopeSource) && _scopeMode == mode && _scopeMatch == match && _scopeRanges.SequenceEqual(ranges) ? _scopeData : null;
        return Task.Run(() =>
        {
            AnalysisOperation.CheckCancellation();
            data = RetestIdentityService.Prepare(data, config);
            var scope = ReferenceEquals(data, _scopeSource) && cachedScope != null ? cachedScope : SampleFilterService.Apply(data, ranges, mode, match);
            var analysis = DatasetService.SelectRetests(scope, mode);
            var sites = CollectSites(scope.Items, scope.Records);
            return new PreparedAnalysis(analysis, sites,
                SummaryService.Build(analysis.Items, sites, scope.Wafers, combine, combineWafers, config), data, scope, ranges, match, ranges.Length == 0 ? null : DiskKeySet.From(scope.Records.Select(r => r.Id)));
        });
    }

    private async Task RefreshTestItemList()
    {
        var previousScope = ActiveData;
        var prepared = await PrepareAnalysisAsync(_data, _config);
        CommitAnalysis(_data, prepared);
        if (!ReferenceEquals(previousScope, ActiveData)) RefreshOpenMaps();
    }

    private void CommitAnalysis(CsvParseResult data, PreparedAnalysis prepared)
    {
        AnalysisOperation.CheckCancellation();
        data = prepared.Source;
        var lease = new DatasetLease(data.Records, prepared.Scope.Records, prepared.Data.Records);
        var summaryLease = prepared.Summaries.AcquireLease();
        string? keepName = _currentItem?.DisplayName;
        string keepSite = _combineSites ? TestItemSummary.CombinedSite : _currentSite;
        string? keepWafer = _combineWafers ? null : _currentWafer;
        ClearDetail();
        bool changedData = !ReferenceEquals(_data, data);
        if (changedData) ScatterPlot.ClearChipMarks();
        _data = data;
        _sampleRanges = prepared.Ranges;
        _scopeData = prepared.Scope;
        _scopeSource = data; _scopeMode = _retestMode; _scopeRanges = prepared.Ranges;
        _sampleMatch = _scopeMatch = prepared.Match;
        (_sampleIncludedIds as IDisposable)?.Dispose(); _sampleIncludedIds = prepared.Included;
        TxtSampleFilterAction.Text = _sampleRanges.Count == 0 ? "数据筛选" : $"数据筛选（{_sampleRanges.Count}）";
        var previousLease = _datasetLease; _datasetLease = lease; previousLease?.Dispose();
        var previousSummaryLease = _summaryLease; _summaryLease = summaryLease;
        _allItems = prepared.Data.Items;
        _devices = prepared.Scope.Devices;
        _allSites = prepared.Sites;
        _summaries = prepared.Summaries;
        InitializeRun(changedData);
        ApplySearchFilter(keepName, keepSite, keepWafer);
        previousSummaryLease?.Dispose();
        UpdateBottomInfo();
        ScheduleDetail();
        _sampleFilterWindow?.UpdateItems(data.Items, _retestMode);
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_isBusy) throw new InvalidOperationException("正在处理，请稍后再试。");
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _operationCommitting = false;
        using var scope = new AnalysisOperation(cancellation.Token);
        SetBusy(true);
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            await operation();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { TxtStatus.Text = "已取消，保留上次有效结果。"; throw; }
        finally
        {
            _operationCancellation = null;
            _operationCommitting = false;
            SetBusy(false); _memoryReclaimer.Request();
            if (_closeAfterOperation) _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Close));
        }
    }

    private void CancelOperation()
    {
        if (_operationCommitting) { TxtStatus.Text = "正在完成配置保存…"; return; }
        _operationCancellation?.Cancel();
        BtnCancelOperation.IsEnabled = false;
        TxtStatus.Text = _closeAfterOperation ? "正在停止处理并关闭…" : "正在取消，保留上次结果…";
    }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => CancelOperation();

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MainToolbar.IsEnabled = MainContent.IsEnabled = !busy;
        BtnCancelOperation.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnCancelOperation.IsEnabled = busy;
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

    private void ApplySearchFilter(string? keepName = null, string? keepSite = null, string? keepWafer = null)
    {
        if (_summaryView == null || !ReferenceEquals(_summaryView.SourceCollection, _summaries))
        {
            if (_summaryView is PagedSummaryView oldView) oldView.ReleaseIndex();
            _summaryView = new PagedSummaryView(_summaries);
            DgTestItems.ItemsSource = _summaryView;
        }

        var view = _summaryView;
        if (view != null)
        {
            view.Filter = o => o is TestItemSummary s && MatchesColumnFilters(s);
        }

        TestItemSummary? match = null;
        int matchScore = -1;
        if (!string.IsNullOrEmpty(keepName) && view != null)
        {
            foreach (TestItemSummary s in view)
            {
                if (s.DisplayName == keepName && s.Site == keepSite &&
                    (s.IsWaferCombined ? null : s.Wafer) == keepWafer)
                {
                    match = s;
                    break;
                }
                if (s.DisplayName == keepName)
                {
                    int score = (s.Site == keepSite ? 1 : 0) + ((s.IsWaferCombined ? null : s.Wafer) == keepWafer ? 2 : 0);
                    if (score > matchScore) { match = s; matchScore = score; }
                }
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
        "Wafer" => s.Wafer ?? "",
        "SBin" => s.SBin ?? "",
        "HBin" => s.HBin ?? "",
        "Unit" => s.Unit ?? "",
        "Lsl" => s.LslText ?? "",
        "Usl" => s.UslText ?? "",
        "Count" => s.Count.ToString(),
        "Fails" => s.Fails.ToString(),
        "Max" => s.Max?.ToString("G6") ?? "-",
        "Min" => s.Min?.ToString("G6") ?? "-",
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

    private static List<string> CollectSites(IReadOnlyList<TestItemData> items, IReadOnlyList<CsvRecord>? records = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int maxSites = int.MaxValue;
        if (records != null && records.Count > 0)
        {
            var candidateSites = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
                candidateSites.Add(SiteKey(records[i].Site));
            maxSites = candidateSites.Count;
        }

        for (int i = 0; i < items.Count && seen.Count < maxSites; i++)
        {
            var item = items[i];
            var values = item.Values;
            for (int j = 0; j < values.Count; j++)
            {
                if (seen.Add(SiteKey(values.GetRecordAt(j).Site)) && seen.Count >= maxSites)
                    break;
            }
            if (seen.Count >= maxSites) break;
            var errors = item.Errors;
            for (int j = 0; j < errors.Count; j++)
            {
                if (seen.Add(SiteKey(errors[j].Site)) && seen.Count >= maxSites)
                    break;
            }
        }

        var sites = seen
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
            $"测试项 {_allItems.Count} 个 | Site {_allSites.Count} 个 | Wafer {ActiveData.Wafers.Count} 个 | 表格 {_summaries.Count} 行 | 唯一芯片 {_devices.Count} 颗" +
            (_sampleRanges.Count > 0 ? $" | 筛选记录 {ActiveData.Records.Count:N0}/{_data.Records.Count:N0}" : "");
    }

    #endregion

    #region 选中测试项 / 直方图 / 散点图

    private void DgTestItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DgTestItems.SelectedItem is TestItemSummary summary)
        {
            string? wafer = summary.IsWaferCombined ? null : summary.Wafer;
            if (ReferenceEquals(_currentItem, summary.Data) && _currentSite == summary.Site && _currentWafer == wafer)
                return;

            _currentItem = summary.Data;
            _currentSite = summary.Site;
            _currentWafer = wafer;
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

    private void ScatterSortMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScatterSortMode?.SelectedIndex is not (>= 0 and <= 2)) return;
        var order = (ScatterOrder)ScatterSortMode.SelectedIndex;
        if (_scatterOrder == order) return;
        _scatterOrder = order;
        ScheduleDetail();
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

        string? site = _combineSites || _currentSite == TestItemSummary.CombinedSite ? null : _currentSite;
        if (site == null && _currentWafer == null)
            return _currentItem.Values;

        return IndexedTestValues.ForScope(_currentItem.Values, site, _currentWafer);
    }

    private async Task UpdateDetailAsync()
    {
        if (_currentItem == null || Histogram == null || _closing || DgTestItems.IsScrollActive) return;
        long version = _detailVersion;
        var request = new DetailRequest(_currentItem,
            _combineSites || _currentSite == TestItemSummary.CombinedSite ? null : _currentSite,
            _binMode, _config.BinCount, _config.IncludeOutOfLimitData,
            _config.ShowLowLimit, _config.ShowHighLimit, _config.ShowMean, _config.ShowThreeSigma,
            _currentWafer, _scatterOrder, _data.Wafers);
        using var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        string siteLabel = request.Site == null ? "ALL Sites" : $"Site {request.Site}";
        string waferLabel = request.Wafer == null ? "ALL Wafers" : $"Wafer {request.Wafer}";
        string itemTitle = $"{request.Item.DisplayName} · {waferLabel} · {siteLabel}";
        if (!ReferenceEquals(_displayedItem, request.Item) || _displayedSite != request.Site || _displayedWafer != request.Wafer)
        {
            // Keep the last complete pair until the newest result can replace it in one UI turn.
            TxtUnit.Text = $"正在计算：{request.Item.DisplayName}…";
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
            TxtItemTitle.Text = itemTitle;
            TxtUnit.Text = string.IsNullOrWhiteSpace(request.Item.ShownUnit)
                ? $"Count={stats.Count}  InSpec%={stats.InSpecPercent:F1}"
                : $"Unit：{request.Item.ShownUnit}    Count={stats.Count}  InSpec%={stats.InSpecPercent:F1}";
            if (!ReferenceEquals(_displayedBins, result.Bins))
                Histogram.Apply(result.Bins, request.Item.DisplayLowLimit, request.Item.DisplayHighLimit,
                    result.Mean, result.Sigma, request.ShowLsl, request.ShowUsl, request.ShowMean,
                    request.ShowThreeSigma, request.Item.ShownUnit);
            if (!ReferenceEquals(_displayedScatter, result.Scatter))
                ScatterPlot.SetSeries(result.Scatter, request.Item.DisplayLowLimit, request.Item.DisplayHighLimit, request.Item.ShownUnit);
            _displayedItem = request.Item;
            _displayedSite = request.Site;
            _displayedWafer = request.Wafer;
            _displayedBins = result.Bins;
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
        _displayedBins = null;
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
        string title = $"Value 表  {_currentItem.DisplayName}  ({(_currentWafer == null ? "ALL Wafers" : "Wafer " + _currentWafer)} · {siteLabel})";

        var window = new ValuesWindow(title + " · " + _currentItem.ShownUnit, filtered, _currentItem.DisplayFactor) { Owner = this };
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
        sb.AppendLine("Test Num\tTest Item\tSite\tWafer\tSBIN\tHBIN\tUnit\tLSL\tUSL\tCount\tFails\tError\tSite σ\tMean\tMax\tMin\tValue\tMedian\tσ\tRobust Mean\tRobust Sigma\tCpk\tInSpec%");

        foreach (var s in list)
        {
            sb.Append(s.TestNumberValue).Append('\t')
              .Append(s.DisplayName).Append('\t')
              .Append(s.Site).Append('\t')
              .Append(s.Wafer).Append('\t')
              .Append(s.SBin).Append('\t')
              .Append(s.HBin).Append('\t')
              .Append(s.Unit).Append('\t')
              .Append(s.LslText).Append('\t')
              .Append(s.UslText).Append('\t')
              .Append(s.Count).Append('\t')
              .Append(s.Fails).Append('\t')
              .Append(s.Eorr).Append('\t')
              .Append(s.SiteSigma?.ToString("G6") ?? "-").Append('\t')
              .Append(s.Mean.ToString("G6")).Append('\t')
              .Append(s.Max?.ToString("G6") ?? "-").Append('\t')
              .Append(s.Min?.ToString("G6") ?? "-").Append('\t')
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
        try { new YieldSimulationWindow(ActiveData, _config, _retestMode) { Owner = this }.ShowDialog(); }
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
                    First: BinStatisticsService.Build(ActiveData.Records, RetestMode.First, _config),
                    Last: BinStatisticsService.Build(ActiveData.Records, RetestMode.Last, _config)));
                window = new BinStatisticsWindow(result.First, result.Last) { Owner = this };
            });
            window!.ShowDialog();
        }
        catch (OperationCanceledException) { }
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
                    First: WaferStatisticsService.Build(ActiveData.Records, RetestMode.First, _config),
                    Last: WaferStatisticsService.Build(ActiveData.Records, RetestMode.Last, _config)));
                window = new WaferTableWindow(result.First, result.Last) { Owner = this };
            });
            window!.ShowDialog();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Wafer Table 统计失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _memoryReclaimer.Request(); }
    }

    private void BtnSbinMap_Click(object sender, RoutedEventArgs e)
    {
        ShowBinMap("SBIN", true);
    }

    private void BtnHbinMap_Click(object sender, RoutedEventArgs e)
    {
        ShowBinMap("HBIN", false);
    }

    private void BtnPfMap_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem == null)
        {
            MessageBox.Show("请先选择一个测试项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? site = _combineSites || _currentSite == TestItemSummary.CombinedSite ? null : _currentSite;
        var mapItem = ActiveData.Items.FirstOrDefault(i => i.Id == _currentItem.Id) ?? _currentItem;
        var pages = MapDataService.PfPages(ActiveData, mapItem, site);
        if (pages.All(p => p.Dies.Count == 0))
        {
            MessageBox.Show("当前测试项没有有效的 X/Y 坐标", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string siteLabel = _combineSites || _currentSite == TestItemSummary.CombinedSite ? "ALL Sites" : $"Site {_currentSite}";
        // Keep only the matching keys; retaining the old item would also retain its
        // entire raw snapshot after a new dataset refreshes this still-open Map.
        string selectedId = _currentItem.Id, selectedName = _currentItem.Name, selectedNumber = _currentItem.TestNumber;
        var mapWindow = new WaferMapWindow($"P/F Map  {_currentItem.DisplayName}  ({siteLabel})", pages);
        if (_currentWafer != null) mapWindow.SelectWafer(_currentWafer);
        ShowMapWindow(mapWindow, data =>
        {
            var item = data.Items.FirstOrDefault(i => i.Id == selectedId) ??
                data.Items.FirstOrDefault(i => i.Name == selectedName && i.TestNumber == selectedNumber);
            return item != null ? MapDataService.PfPages(data, item, site) :
                data.Wafers.Select(w => new WaferMapPage(w, Array.Empty<WaferDie>())).ToList();
        });
    }

    private void ShowBinMap(string binName, bool soft)
    {
        if (_devices.Count == 0)
        {
            MessageBox.Show("请先打开 CSV / STDF 文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pages = MapDataService.BinPages(ActiveData, soft);
        if (pages.All(p => p.Dies.Count == 0))
        {
            MessageBox.Show("没有有效的 X/Y 坐标，无法绘制 Map", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowMapWindow(new WaferMapWindow($"{binName} Map  （按 wafer 分开，同坐标取最新）", pages, binName),
            data => MapDataService.BinPages(data, soft));
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
        foreach (var map in _openMaps) map.Window.SetPages(map.Pages(ActiveData));
    }

    private static bool IsInSpec(double value, double? lsl, double? usl)
    {
        if (lsl.HasValue && value < lsl.Value) return false;
        if (usl.HasValue && value > usl.Value) return false;
        return true;
    }

    #endregion
}



using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class YieldSimulationWindow : Window
{
    private readonly CsvParseResult _data;
    private readonly AnalyzerConfig _config;
    private readonly LatestComputation _work = new();
    private readonly DispatcherTimer _debounce;
    private SimulationSnapshot? _snapshot;
    private RetestMode _snapshotMode;
    private List<SimulationRuleRow> _rows = new();
    private SimulationResult? _result;
    private SimulationRule[]? _appliedRules;
    private bool _ready, _loading, _confirmed, _closed, _preparing;

    public YieldSimulationWindow(CsvParseResult data, AnalyzerConfig config, RetestMode retest = RetestMode.Last)
    {
        _data = data;
        _config = ConfigLoader.Parse(ConfigLoader.Serialize(config));
        InitializeComponent();
        RetestFirst.IsChecked = retest == RetestMode.First;
        RetestLast.IsChecked = retest != RetestMode.First;
        ClassificationInfo.Text = $"HBIN：Pass = {string.Join(",", config.PassHBins)}；PartialPass = {string.Join(",", config.PartialPassHBins)}；其余（含空 HBIN）为不良。可在设置的 INI 配置中修改。每颗芯片只计一次，部分复测按测项继承。";
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += Debounce_Tick;
        Map.HoverChanged += Map_Hover;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    { if (_ready) return; _ready = true; await PrepareAsync(); }
    private RetestMode Mode => RetestFirst.IsChecked == true ? RetestMode.First : RetestMode.Last;
    private bool Combined => TargetCombined.IsChecked == true;
    private void ClearResult(string message)
    {
        _result = null; _appliedRules = null;
        foreach (var row in _rows) row.ApplyStatistics(null);
        ChipsGrid.ItemsSource = DetailsGrid.ItemsSource = GroupsGrid.ItemsSource = null;
        Map.Dies = null; MapInfo.Text = "";
        Summary.Text = message;
    }
    private void Invalidate(bool unconfirm)
    {
        _debounce.Stop(); _work.Invalidate();
        if (unconfirm) _confirmed = false;
        ClearResult(_confirmed ? "规则已变化，等待最新计算…" : "请核对有效项、冲突及阶段，点击“确认有效项 / 建立基准”。");
        Busy.Visibility = Visibility.Collapsed;
    }
    private PredictionOptions Options()
    {
        if (!int.TryParse(MinimumReferences.Text, out int minimum) || !int.TryParse(MaximumReferences.Text, out int maximum) ||
            !double.TryParse(MaximumDistance.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double distance) ||
            minimum < 2 || maximum < minimum || maximum > 10000 || !double.IsFinite(distance) || distance <= 0)
            throw new ArgumentException("参考样本数需满足 2 ≤ 最少 ≤ 最多 ≤ 10000，距离应为有限正数。");
        return new(Combined, minimum, maximum, distance);
    }

    private async Task PrepareAsync(SimulationScheme? scheme = null, bool reset = false)
    {
        if (_closed) return;
        Invalidate(true);
        _preparing = true;
        Busy.Visibility = Visibility.Visible; Status.Text = "正在后台识别有效项建议…";
        var mode = Mode; bool combined = Combined;
        var cached = _snapshotMode == mode ? _snapshot : null;
        var previous = _rows.ToDictionary(r => r.Item);
        RulesGrid.IsEnabled = false;
        try
        {
            await _work.RunAsync(token =>
            {
                var snapshot = cached ?? SimulationSnapshot.Build(_data, _config, mode, token);
                return (Snapshot: snapshot, Rows: YieldSimulationService.Recommend(snapshot, combined, token));
            }, prepared =>
            {
                _loading = true;
                foreach (var row in _rows) row.PropertyChanged -= Rule_Changed;
                _snapshot = prepared.Snapshot; _snapshotMode = mode; _rows = prepared.Rows;
                foreach (var row in _rows)
                {
                    var saved = scheme?.Rules.First(r => r.Item == row.Item);
                    if (saved != null)
                    {
                        row.Included = saved.Included; row.Enabled = saved.Enabled;
                        row.Lower = SimulationRuleRow.Number(saved.Lower); row.Upper = SimulationRuleRow.Number(saved.Upper);
                        row.Stage = saved.Stage.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (!reset && previous.TryGetValue(row.Item, out var old))
                    {
                        row.Included = old.Included; row.Enabled = old.Enabled;
                        row.Lower = old.Lower; row.Upper = old.Upper; row.Stage = old.Stage;
                    }
                    row.PropertyChanged += Rule_Changed;
                }
                RulesGrid.ItemsSource = new ListCollectionView(_rows);
                string? wafer = Wafers.SelectedItem as string;
                var wafers = _snapshot.Chips.Select(r => r.WaferId).Distinct().ToList();
                Wafers.ItemsSource = new ListCollectionView(wafers);
                Wafers.SelectedItem = wafers.Contains(wafer ?? "") ? wafer : wafers.FirstOrDefault();
                _loading = false;
                _preparing = false; Busy.Visibility = Visibility.Collapsed; RulesGrid.IsEnabled = true;
                Status.Text = $"建议已更新：{_rows.Count} 项、{_snapshot.Count} 颗芯片。建议仅供核查；确认后才执行基准重算和模拟。";
            });
        }
        catch (Exception ex) { _preparing = false; ShowError(ex); RulesGrid.IsEnabled = true; }
    }
    private async void Context_Changed(object sender, RoutedEventArgs e)
    { if (_ready && !_loading) await PrepareAsync(); }
    private async void Recommend_Click(object sender, RoutedEventArgs e)
    { if (_ready) await PrepareAsync(); }
    private void Rule_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || !_ready || _preparing) return;
        if (e.PropertyName is not (nameof(SimulationRuleRow.Included) or nameof(SimulationRuleRow.Enabled)
            or nameof(SimulationRuleRow.Lower) or nameof(SimulationRuleRow.Upper) or nameof(SimulationRuleRow.Stage))) return;
        bool membership = e.PropertyName is nameof(SimulationRuleRow.Included) or nameof(SimulationRuleRow.Stage);
        Invalidate(membership);
        if (_confirmed) Schedule();
        else Status.Text = "有效项或阶段已改变，请重新确认。全通过项不会自动关闭；冲突项需人工核对。";
    }
    private void Options_Changed(object sender, TextChangedEventArgs e)
    { if (_ready && !_loading && !_preparing) { Invalidate(false); if (_confirmed) Schedule(); } }
    private void Schedule()
    {
        if (_snapshot == null || !_confirmed || _closed) return;
        _debounce.Stop(); _debounce.Start();
        Busy.Visibility = Visibility.Visible; Status.Text = "等待输入结束，后台计算最新规则…";
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot == null || !RulesGrid.IsEnabled) return;
        try
        {
            RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true); RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rules = _rows.Select(r => r.Capture()).ToArray(); Options();
            if (!rules.Any(r => r.Included)) throw new ArgumentException("请至少确认一个有效项。");
            _confirmed = true; Invalidate(false); Schedule();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Debounce_Tick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        if (!_confirmed || _snapshot == null || _closed) return;
        try
        {
            var rules = _rows.Select(r => r.Capture()).ToArray(); var options = Options(); var snapshot = _snapshot;
            Status.Text = "正在后台联合判定与缺测预测…";
            await _work.RunAsync(token => YieldSimulationService.Calculate(snapshot, rules, options, token), result =>
            {
                _result = result; _appliedRules = rules;
                foreach (var row in _rows) row.ApplyStatistics(result.Items[row.Item]);
                ChipsGrid.ItemsSource = new ListCollectionView(result.Chips.ToList());
                GroupsGrid.ItemsSource = new ListCollectionView(result.Groups.ToList());
                Summary.Text = result.Summary;
                Busy.Visibility = Visibility.Collapsed;
                Status.Text = "当前结果已更新。概率结果始终属于待预测；参考仍缺测或样本不足时不提供点估计。分项计数仅统计本次启用的有效项。";
                DrawMap();
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!RulesGrid.IsEnabled) return;
        _loading = true;
        foreach (var row in _rows)
        { row.Enabled = true; row.Lower = SimulationRuleRow.Number(row.OriginalLower); row.Upper = SimulationRuleRow.Number(row.OriginalUpper); }
        _loading = false; Invalidate(false); if (_confirmed) Schedule();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!RulesGrid.IsEnabled || _snapshot == null) return;
        try
        {
            var scheme = new SimulationScheme(1, SimulationSchemeService.Signature(_data, _config), Mode, Options(), _rows.Select(r => r.Capture()).ToArray());
            var dialog = new SaveFileDialog { Filter = "良率模拟方案 (*.json)|*.json", DefaultExt = ".json", FileName = "yield-scheme.json", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            SimulationSchemeService.Save(dialog.FileName, scheme);
            Status.Text = "方案已保存；载入后仍需确认有效项。原始数据未修改。";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "良率模拟方案 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var scheme = SimulationSchemeService.Load(dialog.FileName, _data, _config);
            _loading = true;
            TargetCombined.IsChecked = scheme.Prediction.IncludePartial; TargetPass.IsChecked = !scheme.Prediction.IncludePartial;
            RetestFirst.IsChecked = scheme.Retest == RetestMode.First; RetestLast.IsChecked = scheme.Retest == RetestMode.Last;
            MinimumReferences.Text = scheme.Prediction.MinimumReferences.ToString(); MaximumReferences.Text = scheme.Prediction.MaximumReferences.ToString();
            MaximumDistance.Text = scheme.Prediction.MaximumDistance.ToString("R", CultureInfo.InvariantCulture);
            _loading = false;
            await PrepareAsync(scheme);
        }
        catch (Exception ex) { _loading = false; ShowError(ex); }
    }
    private void ShowError(Exception ex)
    { if (_closed) return; Busy.Visibility = Visibility.Collapsed; Status.Text = ex.Message; }

    private void Chip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_snapshot == null || _appliedRules == null || ChipsGrid.SelectedItem is not SimulationChipResult chip)
        { DetailsGrid.ItemsSource = null; return; }
        var details = _appliedRules.Select(r =>
        {
            var state = _snapshot.States[r.Item][chip.Index];
            double value = _snapshot.Value(chip.Index, r.Item);
            string decision = !r.Included ? "未纳入有效项" : !r.Enabled ? "模拟关闭" : state == MeasurementState.Measured
                ? YieldSimulationService.Passes(value, r.Lower, r.Upper) ? "通过" : "失败"
                : state == MeasurementState.NotApplicable ? "不参与此芯片" : "未知";
            return new { Name = _data.Items[r.Item].DisplayName,
                State = state switch { MeasurementState.Measured => "已测", MeasurementState.Unmeasured => r.Included && r.Enabled ? "应测未测" : "未测（未启用）", MeasurementState.NotApplicable => "不适用", _ => "无效数据" },
                Value = double.IsFinite(value) ? value.ToString("G10", CultureInfo.InvariantCulture) : "—",
                Rule = $"{SimulationRuleRow.Number(r.Lower)} ～ {SimulationRuleRow.Number(r.Upper)}", Decision = decision, r.Stage };
        }).ToList();
        DetailsGrid.ItemsSource = new ListCollectionView(details);
    }
    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    { if (_ready && ReferenceEquals(e.OriginalSource, Tabs)) { if (MapTab.IsSelected) DrawMap(); else Map.Dies = null; } }
    private void Wafer_Changed(object sender, SelectionChangedEventArgs e)
    { if (_ready && !_loading) DrawMap(); }
    private void DrawMap()
    {
        if (!MapTab.IsSelected || _result == null || Wafers.SelectedItem is not string wafer) return;
        var rows = _result.Chips.Where(c => c.Wafer == wafer).ToArray();
        Map.Dies = rows.Where(c => c.Record.X.HasValue && c.Record.Y.HasValue).Select(c => new WaferDie
        {
            WaferId = wafer, X = c.Record.X!.Value, Y = c.Record.Y!.Value, SerialNumber = c.SN, Site = c.Site,
            SBin = c.Record.SBin, HBin = c.HBIN, Category = c.Status, AdditionalInfo = "通过概率 " + c.Probability + "；" + c.Note,
            Color = c.Simulated == ChipVerdict.Good ? c.OriginalGood ? Brushes.SeaGreen : Brushes.DodgerBlue
                : c.Simulated == ChipVerdict.Bad ? c.OriginalGood ? Brushes.Red : Brushes.Firebrick
                : c.Evidence?.Probability != null ? Brushes.DarkOrange : Brushes.Gray
        }).ToList();
        MapInfo.Text = $"绘制 {Map.Dies.Count}/{rows.Length} 颗（无完整 XY 的芯片只显示在结果表）；橙色表示待预测，即使概率为 100% 也不标为确认良品。";
    }
    private void Map_Hover(string text, Point point)
    { if (text.Length > 0) MapInfo.Text = text.Replace('\n', ' '); }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true; _ready = false;
        _debounce.Stop(); _debounce.Tick -= Debounce_Tick; _work.Dispose();
        Map.HoverChanged -= Map_Hover; Map.Dies = null;
        foreach (var row in _rows) row.PropertyChanged -= Rule_Changed;
        RulesGrid.ItemsSource = ChipsGrid.ItemsSource = GroupsGrid.ItemsSource = DetailsGrid.ItemsSource = Wafers.ItemsSource = null;
        _rows.Clear(); _snapshot = null; _result = null; _appliedRules = null;
    }
}

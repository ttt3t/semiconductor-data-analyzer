using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ManagedImportWindow : Window
{
    public List<ManagedDataFile> Files { get; }
    public ManagedFilenameRule FilenameRule { get; private set; }
    private string _configText = "";
    private string _commonLot = "";
    private readonly ManagedProduct _product;
    private readonly bool _editingExisting;
    private readonly DateTime _importTime = DateTime.Now;
    private readonly Dictionary<string, DateTime> _fallbackTimes = new();
    private bool _metadataStarted, _readingMetadata, _closed;
    private CancellationTokenSource? _metadataCancellation;
    public Task MetadataLoading { get; private set; } = Task.CompletedTask;
    public ManagedImportWindow(ManagedProduct product, IEnumerable<string> paths, bool specified = false, ManagedDataFile? existing = null)
    {
        InitializeComponent();
        _product = product;
        _editingExisting = existing != null;
        FilenameRule = ProductCatalogStore.Clone(product.FilenameRule);
        Files = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new ManagedDataFile { FilePath = Path.GetFullPath(path), Lot = product.Files.LastOrDefault()?.Lot ?? "" }).ToList();
        if (Files.Count == 0) Files.Add(new() { Lot = product.Files.LastOrDefault()?.Lot ?? "" });
        if (existing != null) { Files.Clear(); Files.Add(ProductCatalogStore.Clone(existing)); }
        DgFiles.ItemsSource = Files; StageList.ItemsSource = product.Flow; StageList.SelectedIndex = 0;
        _commonLot = Files.FirstOrDefault()?.Lot ?? ""; TxtLot.Text = _commonLot;
        SpecifiedMode.IsChecked = specified; SpecifiedMode_Changed(this, new RoutedEventArgs());
        TxtBasis.Text = "当前良品口径：" + (product.IncludePartialPass ? "Pass + PartialPass" : "Pass");
        if (existing != null)
        {
            StageList.SelectedItem = StageList.Items.OfType<ProductTestStage>().FirstOrDefault(s => s.Id == existing.StageId);
            _configText = existing.ConfigText; TxtOrigin.Text = existing.SpecifiedOrigin;
            Mode93K.IsChecked = existing.StdfMode == StdfImportMode.Advantest93K;
            if (existing.ConfigText.Length > 0) TxtConfigName.Text = "使用该登记保存的 INI 配置";
        }
        ExtractWafer.IsChecked = FilenameRule.ExtractWafer; WaferStart.Text = FilenameRule.WaferStart.ToString(); WaferLength.Text = FilenameRule.WaferLength.ToString();
        ExtractTime.IsChecked = FilenameRule.ExtractTime; TimeStart.Text = FilenameRule.TimeStart.ToString(); TimeLength.Text = FilenameRule.TimeLength.ToString(); TimeFormat.Text = FilenameRule.TimeFormat;
        TxtAllTime.Text = _importTime.ToString("yyyy-MM-dd HH:mm:ss");
        if (existing == null) Extract(false);
        Loaded += (_, _) => BeginMetadataRead();
        Closed += (_, _) => { _closed = true; _metadataCancellation?.Cancel(); };
        DgFiles.PreparingCellForEdit += (_, args) =>
        {
            if (args.Row.Item is ManagedDataFile file && args.Column is DataGridBoundColumn { Binding: System.Windows.Data.Binding { Path.Path: "TestTimeText" } })
                file.UseStdfTestTime = false;
        };
    }
    private void BeginMetadataRead()
    {
        if (_metadataStarted || _editingExisting || _closed) return;
        _metadataStarted = true;
        var files = Files.Where(f => f.FilePath.EndsWith(".std", StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.EndsWith(".stdf", StringComparison.OrdinalIgnoreCase) || f.FilePath.EndsWith(".std.gz", StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.EndsWith(".stdf.gz", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length > 0) MetadataLoading = ReadMetadataAsync(files);
    }
    private async Task ReadMetadataAsync(ManagedDataFile[] files)
    {
        var cancellation = new CancellationTokenSource(); _metadataCancellation = cancellation;
        _readingMetadata = true;
        ImportInputs.IsEnabled = BtnSave.IsEnabled = false; DgFiles.IsReadOnly = true; MetadataBusy.Visibility = Visibility.Visible;
        TxtMetadataStatus.Text = "正在读取 STDF 批次、晶圆与测试时间…";
        var original = files.ToDictionary(f => f.Id, f => (f.Lot, f.WaferOverride, f.TestTime));
        try
        {
            var progress = new Progress<string>(text => { if (!_closed && _readingMetadata) TxtMetadataStatus.Text = text; });
            var loaded = await Task.Run(() =>
            {
                var result = new List<(ManagedDataFile File, StdfFileMetadata? Metadata, string Error)>();
                foreach (var file in files)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); ((IProgress<string>)progress).Report("读取 STDF 基本信息：" + file.FileName);
                    try { result.Add((file, StdfMetadataService.Read(file.FilePath, cancellation.Token), "")); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                    { result.Add((file, null, ex.Message)); }
                }
                return result;
            }, cancellation.Token);
            if (_closed) return;
            cancellation.Token.ThrowIfCancellationRequested();
            var notes = new List<string>(); int success = 0;
            foreach (var entry in loaded)
            {
                if (entry.Metadata is not { } metadata) { notes.Add(entry.File.FileName + "：" + entry.Error); continue; }
                var file = entry.File; var before = original[file.Id]; success++;
                if (file.Lot == before.Lot && metadata.LotId.Length > 0) file.Lot = metadata.LotId;
                string[] wafers = metadata.WaferIds;
                if (file.WaferOverride == before.WaferOverride)
                {
                    if (wafers.Length == 1 && metadata.Wafers.All(w => w.WaferId.Length > 0)) file.WaferOverride = wafers[0];
                    else if (metadata.Wafers.Count > 1) file.WaferOverride = "";
                }
                if (file.TestTime == before.TestTime && metadata.TestTime is DateTime time)
                {
                    file.TestTime = file.DetectedStdfTestTime = time; file.UseStdfTestTime = true; _fallbackTimes.Remove(file.Id);
                }
                if (wafers.Length > 1) notes.Add(file.FileName + $"：保留 {wafers.Length} 个晶圆，按各片测试时间统计");
                if (metadata.LotId.Length == 0 || wafers.Length == 0 || !metadata.TestTime.HasValue)
                    notes.Add(file.FileName + "：部分基本信息缺失，缺失项沿用文件名识别或登记默认值");
            }
            DgFiles.Items.Refresh();
            TxtMetadataStatus.Text = $"已读取 {success} / {files.Length} 个 STDF。优先填入文件内信息；可手动修改，统一修改时间会覆盖逐晶圆时间。" +
                (notes.Count > 0 ? " " + string.Join("；", notes.Take(2)) : "");
            var lots = Files.Select(f => f.Lot).Distinct(StringComparer.Ordinal).ToArray();
            if (lots.Length == 1) { _commonLot = lots[0]; TxtLot.Text = lots[0]; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) TxtMetadataStatus.Text = "STDF 基本信息未读取完成：" + ex.Message + "。可手动核对填写。"; }
        finally
        {
            _readingMetadata = false;
            if (ReferenceEquals(_metadataCancellation, cancellation)) _metadataCancellation = null;
            cancellation.Dispose();
            if (!_closed) { ImportInputs.IsEnabled = BtnSave.IsEnabled = true; DgFiles.IsReadOnly = false; MetadataBusy.Visibility = Visibility.Collapsed; }
        }
    }
    private void ReadFilenameRule()
    {
        if (!int.TryParse(WaferStart.Text, out int ws) || ws < 1 || !int.TryParse(WaferLength.Text, out int wl) || wl < 1 ||
            !int.TryParse(TimeStart.Text, out int ts) || ts < 1 || !int.TryParse(TimeLength.Text, out int tl) || tl < 0)
            throw new InvalidDataException("起始位置、片号长度需为正整数；时间长度可填 0 表示自动识别。");
        FilenameRule = new() { ExtractWafer = ExtractWafer.IsChecked == true, WaferStart = ws, WaferLength = wl,
            ExtractTime = ExtractTime.IsChecked == true, TimeStart = ts, TimeLength = tl, TimeFormat = TimeFormat.Text.Trim() };
    }
    private void Extract(bool overwrite)
    {
        try
        {
            if (!DgFiles.CommitEdit(DataGridEditingUnit.Cell, true) || !DgFiles.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidDataException("请先修正测试时间输入。");
            ReadFilenameRule();
            var warnings = Files.Select(f => (f.FileName, Message: Identify(f, overwrite))).Where(r => r.Message.Length > 0).ToArray();
            TxtNamingStatus.Text = warnings.Length == 0 ? "识别完成" :
                $"{warnings.Length} 条需核对：" + string.Join("；", warnings.Take(2).Select(w => w.FileName + "：" + w.Message));
            DgFiles.Items.Refresh(); TxtError.Text = "";
        }
        catch (Exception ex) { TxtError.Text = ex.Message; }
    }
    private void ExtractMissing_Click(object sender, RoutedEventArgs e) => Extract(false);
    private void ExtractAll_Click(object sender, RoutedEventArgs e) => Extract(true);
    private string Identify(ManagedDataFile file, bool overwrite)
    {
        if (file.UseStdfTestTime && file.TestTime != file.DetectedStdfTestTime) file.UseStdfTestTime = false;
        if (overwrite && FilenameRule.ExtractTime) file.UseStdfTestTime = false;
        // An automatic fallback must not prevent a later filename recipe from finding the real date.
        if (_fallbackTimes.TryGetValue(file.Id, out var previous) && file.TestTime == previous) file.TestTime = null;
        _fallbackTimes.Remove(file.Id);
        if (overwrite && FilenameRule.ExtractTime) file.TestTime = null;
        string warning = ManagedFilenameService.Apply(file, FilenameRule, overwrite);
        if (!file.TestTime.HasValue)
        {
            file.TestTime = _importTime; _fallbackTimes[file.Id] = _importTime;
            warning = warning.Replace("未识别时间，可手动填写", "未识别时间，已填当前系统时间");
            if (!warning.Contains("当前系统时间")) warning += (warning.Length > 0 ? "；" : "") + "未识别时间，已填当前系统时间";
        }
        return warning;
    }
    private void ApplyTime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!DgFiles.CommitEdit(DataGridEditingUnit.Cell, true) || !DgFiles.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidDataException("请先修正表格中的输入。");
            DateTime time = ManagedFilenameService.ParseTime(TxtAllTime.Text) ?? throw new InvalidDataException("请填写统一测试时间。");
            foreach (var file in Files) { file.TestTime = time; file.UseStdfTestTime = false; _fallbackTimes.Remove(file.Id); }
            DgFiles.Items.Refresh(); TxtError.Text = ""; TxtNamingStatus.Text = $"已统一修改 {Files.Count} 条记录的测试时间。";
        }
        catch (Exception ex) { TxtError.Text = ex.Message; }
    }
    private void CurrentTime_Click(object sender, RoutedEventArgs e)
    { TxtAllTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); ApplyTime_Click(sender, e); }
    private void ApplyLot_Click(object sender, RoutedEventArgs e)
    { foreach (var file in Files) file.Lot = TxtLot.Text.Trim(); DgFiles.Items.Refresh(); }
    private void Lot_Changed(object sender, TextChangedEventArgs e)
    {
        if (DgFiles?.ItemsSource is not List<ManagedDataFile> rows) return;
        string lot = TxtLot.Text.Trim();
        foreach (var row in rows) if (row.Lot == _commonLot || row.Lot.Trim().Length == 0) row.Lot = lot;
        _commonLot = lot; DgFiles.Items.Refresh();
    }
    private void ChooseConfig_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Filter = "INI 配置|*.ini|所有文件|*.*" };
        if (file.ShowDialog() != true) return;
        try { _configText = ConfigLoader.Serialize(ConfigLoader.Load(file.FileName)); TxtConfigName.Text = Path.GetFileName(file.FileName); }
        catch (Exception ex) { TxtError.Text = ex.Message; }
    }
    private void SpecifiedMode_Changed(object sender, RoutedEventArgs e)
    {
        bool specified = SpecifiedMode.IsChecked == true;
        string? keep = (StageList.SelectedItem as ProductTestStage)?.Id;
        StageList.ItemsSource = _product.Flow.Concat(specified ? _product.Flow.Select(s => s.Kind).Distinct()
            .Select(kind => new ProductTestStage { Id = "COMBINED:" + kind, Kind = kind, Name = "综合良率" }) : Array.Empty<ProductTestStage>()).ToList();
        StageList.SelectedItem = StageList.Items.OfType<ProductTestStage>().FirstOrDefault(s => s.Id == keep) ?? StageList.Items.OfType<ProductTestStage>().FirstOrDefault();
        TotalColumn.Visibility = GoodColumn.Visibility = YieldColumn.Visibility = SpecifiedHelp.Visibility = specified ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BeginMetadataRead();
            if (_readingMetadata) throw new InvalidDataException("正在读取 STDF 基本信息，请等待完成后再登记。");
            if (!DgFiles.CommitEdit(DataGridEditingUnit.Cell, true) || !DgFiles.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidDataException("请修正测试时间、总数、良品数或良率输入。");
            ReadFilenameRule();
            if (StageList.SelectedItem is not ProductTestStage stage) throw new InvalidDataException("请选择测试步骤。");
            if (SpecifiedMode.IsChecked != true && stage.Kind == "FT" && RetestIdentityService.ParseTestNumbers(_product.UuidTestNumbers).Count == 0)
                throw new InvalidDataException("FT 自动计算请先在产品与流程中填写 UUID Test Num；只有汇总结果时可勾选指定良率。");
            foreach (var file in Files)
            {
                // Populate empty fields with the current recipe; explicitly typed values always win.
                if (!_editingExisting) Identify(file, false);
                if (file.TestTime != file.DetectedStdfTestTime) file.UseStdfTestTime = false;
                if (file.Lot.Trim().Length == 0) file.Lot = TxtLot.Text.Trim();
                if (file.Lot.Trim().Length == 0) throw new InvalidDataException("请为每个文件指定批次。");
                file.Lot = file.Lot.Trim(); file.WaferOverride = file.WaferOverride.Trim();
                file.StageId = stage.Id; file.ConfigText = _configText;
                file.StdfMode = Mode93K.IsChecked == true ? StdfImportMode.Advantest93K : StdfImportMode.Standard;
                file.UseSpecifiedYield = SpecifiedMode.IsChecked == true; file.SpecifiedKind = stage.Kind;
                file.SpecifiedOrigin = TxtOrigin.Text.Trim(); file.SpecifiedIncludePartial = _product.IncludePartialPass;
                if (file.UseSpecifiedYield) ProductYieldService.ValidateSpecified(_product, file);
                else if (!File.Exists(file.FilePath)) throw new FileNotFoundException("找不到原文件：" + file.FilePath);
            }
            DialogResult = true;
        }
        catch (Exception ex) { TxtError.Text = ex.Message; }
    }
}

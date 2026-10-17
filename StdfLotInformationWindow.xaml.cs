using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class StdfLotInformationWindow : Window
{
    private sealed record SourceFile(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    }
    private sealed record WaferEntry(string Name, StdfWaferMetadata Metadata);
    private sealed record CachedInformation(StdfFileMetadata Metadata, long Length, DateTime Modified);
    private readonly Dictionary<string, CachedInformation> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _sourceCancellation, _readCancellation;
    private StdfFileMetadata? _displayed;
    private string _displayedPath = "";
    private long _sourceVersion, _readVersion;
    private bool _closed;
    public Task SourcesLoading { get; private set; } = Task.CompletedTask;
    public Task InformationLoading { get; private set; } = Task.CompletedTask;

    public StdfLotInformationWindow(IEnumerable<string> paths)
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _closed = true; _sourceVersion++; _readVersion++;
            _sourceCancellation?.Cancel(); _readCancellation?.Cancel();
            _cache.Clear(); ClearInformation(); LstFiles.ItemsSource = null;
        };
        UpdateSources(paths);
    }

    public void UpdateSources(IEnumerable<string> paths)
    {
        if (_closed) return;
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? selected = (LstFiles.SelectedItem as SourceFile)?.Path;
        _sourceCancellation?.Cancel(); _readCancellation?.Cancel(); _readVersion++;
        var cancel = new CancellationTokenSource(); _sourceCancellation = cancel;
        long version = ++_sourceVersion;
        ClearInformation(); LstFiles.ItemsSource = null; TxtSourceCount.Text = "STDF 文件";
        TxtStatus.Text = "正在查找当前数据中的 STDF 文件…";
        SourcesLoading = DiscoverSourcesAsync(distinct, selected, version, cancel);
    }

    private async Task DiscoverSourcesAsync(string[] paths, string? selected, long version, CancellationTokenSource cancel)
    {
        try
        {
            var files = await Task.Run(() => paths.Where(path =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                // Known extensions remain selectable even if the original file has moved.
                string name = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
                if (name.EndsWith(".std", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".stdf", StringComparison.OrdinalIgnoreCase)) return true;
                try { return DataFileService.IsStdf(path); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }).Select(p => new SourceFile(p)).ToArray(), cancel.Token);
            if (_closed || version != _sourceVersion || cancel.IsCancellationRequested) return;
            var kept = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string key in _cache.Keys.Where(k => !kept.Contains(k)).ToArray()) _cache.Remove(key);
            TxtSourceCount.Text = $"STDF 文件（{files.Length}）";
            LstFiles.ItemsSource = files;
            if (files.Length == 0)
            {
                TxtStatus.Text = "当前数据中没有 STDF 源文件。请先在分析器中打开或追加导入 STDF。";
                return;
            }
            LstFiles.SelectedItem = files.FirstOrDefault(f => string.Equals(f.Path, selected, StringComparison.OrdinalIgnoreCase)) ?? files[0];
            await InformationLoading;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && version == _sourceVersion) TxtStatus.Text = "无法读取文件列表：" + ex.Message; }
        finally { if (ReferenceEquals(_sourceCancellation, cancel)) _sourceCancellation = null; cancel.Dispose(); }
    }

    private void File_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LstFiles.SelectedItem is SourceFile file) StartRead(file.Path, false);
    }
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (LstFiles.SelectedItem is SourceFile file) StartRead(file.Path, true);
    }
    private void StartRead(string path, bool force)
    {
        _readCancellation?.Cancel();
        var cancel = new CancellationTokenSource(); _readCancellation = cancel;
        long version = ++_readVersion;
        _cache.TryGetValue(path, out var cached);
        ClearInformation(); TxtPath.Text = path; TxtSummary.Text = Path.GetFileName(path);
        TxtStatus.Text = "正在读取 Lot Information…"; BtnRefresh.IsEnabled = true;
        InformationLoading = ReadInformationAsync(path, force ? null : cached, version, cancel);
    }
    private async Task ReadInformationAsync(string path, CachedInformation? cached, long version, CancellationTokenSource cancel)
    {
        try
        {
            var info = await Task.Run(() =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                var file = new FileInfo(path); long length = file.Length; DateTime modified = file.LastWriteTimeUtc;
                return cached != null && cached.Length == length && cached.Modified == modified ? cached :
                    new CachedInformation(StdfMetadataService.Read(path, cancel.Token), length, modified);
            }, cancel.Token);
            if (_closed || version != _readVersion || cancel.IsCancellationRequested) return;
            _cache[path] = info; _displayed = info.Metadata; _displayedPath = path;
            TxtSummary.Text = $"Lot: {(_displayed.LotId.Length > 0 ? _displayed.LotId : "未提供")}  ·  {_displayed.Wafers.Count} 个晶圆记录";
            DgLot.ItemsSource = _displayed.LotFields;
            LstWafers.ItemsSource = _displayed.Wafers.Select((w, i) => new WaferEntry($"{i + 1}. {(w.WaferId.Length > 0 ? w.WaferId : "未命名晶圆")}", w)).ToArray();
            LstWafers.SelectedIndex = _displayed.Wafers.Count > 0 ? 0 : -1;
            WaferTab.Header = $"Wafer / 晶圆信息（{_displayed.Wafers.Count}）";
            BtnCopy.IsEnabled = true;
            TxtStatus.Text = $"读取完成 · {info.Length / 1024d:N1} KB · 文件修改时间 {info.Modified.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
                (_displayed.Wafers.Count == 0 ? " · 文件未提供 WIR/WRR 晶圆记录" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && version == _readVersion)
            { _cache.Remove(path); TxtStatus.Text = "读取失败：" + ex.Message; }
        }
        finally { if (ReferenceEquals(_readCancellation, cancel)) _readCancellation = null; cancel.Dispose(); }
    }
    private void Wafer_Changed(object sender, SelectionChangedEventArgs e)
        => DgWafer.ItemsSource = (LstWafers.SelectedItem as WaferEntry)?.Metadata.Fields;
    private void ClearInformation()
    {
        _displayed = null; _displayedPath = "";
        DgLot.ItemsSource = null; DgWafer.ItemsSource = null; LstWafers.ItemsSource = null;
        BtnCopy.IsEnabled = false; BtnRefresh.IsEnabled = false;
        TxtSummary.Text = "尚未选择 STDF"; TxtPath.Text = "";
        WaferTab.Header = "Wafer / 晶圆信息";
    }
    public string BuildCopyText()
    {
        if (_displayed == null) return "";
        var text = new StringBuilder().AppendLine("STDF Lot Information").AppendLine(_displayedPath);
        void Append(IEnumerable<StdfInformationField> fields)
        {
            text.AppendLine("记录\t字段\t说明\t原始值");
            foreach (var f in fields) text.AppendLine(string.Join('\t', new[] { f.Record, f.Name, f.Description, f.Value }.Select(s => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '))));
        }
        Append(_displayed.LotFields);
        for (int i = 0; i < _displayed.Wafers.Count; i++)
        { var w = _displayed.Wafers[i]; text.AppendLine().AppendLine($"Wafer {i + 1}: {w.WaferId}"); Append(w.Fields); }
        return text.ToString();
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { string text = BuildCopyText(); if (text.Length > 0) { Clipboard.SetText(text); TxtStatus.Text = "已复制当前文件的批次及全部晶圆信息。"; } }
        catch (System.Runtime.InteropServices.ExternalException) { TxtStatus.Text = "剪贴板暂不可用，请重试。"; }
    }
}

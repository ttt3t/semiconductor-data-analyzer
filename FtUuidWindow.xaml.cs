using System.Windows;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class FtUuidWindow : Window
{
    private readonly CsvParseResult? _data;
    private readonly AnalyzerConfig _config;
    private readonly CancellationTokenSource _cancel = new();
    private bool _closed;
    public string TestNumbers { get; private set; } = "";
    public sealed record PreviewRow(int Row, string SerialNumber, string Uuid, string Status);
    public FtUuidWindow(string numbers, CsvParseResult? data, AnalyzerConfig config)
    {
        InitializeComponent(); _data = data; _config = config; TxtNumbers.Text = numbers;
        Closed += (_, _) => { _closed = true; _cancel.Cancel(); DgPreview.ItemsSource = null; _cancel.Dispose(); };
        TxtNotice.Text = data == null || data.Records.Count == 0 ? "未加载数据" : $"当前 {data.Records.Count:N0} 条记录";
    }
    private RetestIdentityService.Reader ReadDefinition()
    {
        var numbers = RetestIdentityService.ParseTestNumbers(TxtNumbers.Text);
        return new(_data?.Items ?? new(), new AnalyzerConfig { UuidTestNumbers = numbers,
            MissingTokens = _config.MissingTokens, NotApplicableTokens = _config.NotApplicableTokens });
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var reader = ReadDefinition(); BtnPreview.IsEnabled = false; TxtNotice.Text = "读取预览…";
            using var lease = new DatasetLease(_data?.Records.AsEnumerable() ?? Enumerable.Empty<CsvRecord>());
            var token = _cancel.Token;
            var rows = await Task.Run(() => (_data?.Records.Take(100) ?? Enumerable.Empty<CsvRecord>()).Select((record, index) =>
            {
                token.ThrowIfCancellationRequested(); string uuid = reader.Read(record);
                return new PreviewRow(index + 1, record.ChipSerialNumber, uuid.Length > 0 ? uuid : "–", uuid.Length > 0 ? "可识别" : "缺失，独立保留");
            }).ToArray(), token);
            if (_closed) return;
            DgPreview.ItemsSource = rows;
            TxtNotice.Text = $"按 {reader.Recipe} 顺序拼接；预览 {rows.Length} 条。" +
                (reader.MissingTests.Length > 0 ? " 未找到 Test Num：" + string.Join(", ", reader.MissingTests) : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) TxtNotice.Text = ex.Message; }
        finally { if (!_closed) BtnPreview.IsEnabled = true; }
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try { TestNumbers = ReadDefinition().Recipe; DialogResult = true; }
        catch (Exception ex) { TxtNotice.Text = ex.Message; }
    }
}

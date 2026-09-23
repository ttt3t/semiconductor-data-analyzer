using System.Globalization;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Assigns an unambiguous run selector without changing physical-chip identity or raw measurements.</summary>
public static class SerialNumberService
{
    public static bool Normalize(CsvParseResult data, bool serialColumnMissing = false)
    {
        if (data.Records.Count == 0) return false;
        int empty = 0, duplicates = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in data.Records)
        {
            string serial = record.SerialNumber.Trim();
            if (serial.Length == 0) empty++;
            else if (!seen.Add(serial)) duplicates++;
        }
        if (!serialColumnMissing && empty == 0 && duplicates == 0) return false;

        for (int i = 0; i < data.Records.Count; i++)
        {
            var record = data.Records[i];
            record.OriginalSerialNumber ??= record.SerialNumber;
            record.SerialNumber = (i + 1).ToString(CultureInfo.InvariantCulture);
        }
        // Rebuild only lightweight identity rows, not the numeric columns or disk-backed raw snapshot.
        data.Devices = DatasetService.BuildDevices(data.Records);
        var reasons = new List<string>();
        if (serialColumnMissing) reasons.Add("未配置 SerialNumber 列");
        if (empty > 0) reasons.Add($"{empty} 条 SerialNumber 为空");
        if (duplicates > 0) reasons.Add($"{duplicates} 条 SerialNumber 在全部 wafer 中重复");
        data.ReassignedSerialNumberCount = data.Records.Count;
        data.SerialNumberNotice = $"{string.Join("；", reasons)}。已按输入顺序将全部 {data.Records.Count} 条记录的 SerialNumber 重编为 1～{data.Records.Count}。原始文件与复测芯片身份保持不变。";
        return true;
    }
}

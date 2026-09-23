using System.Globalization;
using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ImportOptions(bool SameWafer, string TargetWafer, bool MatchByNumber, bool UseNewLimits);
public sealed record CsvImportSource(string Path, ImportOptions? Options = null,
    StdfImportMode StdfMode = StdfImportMode.Standard);

public static class DatasetService
{
    public static CsvParseResult SelectRetests(CsvParseResult data, RetestMode mode)
    {
        if (mode == RetestMode.All) return data;
        var records = SelectRetestRecords(data.Records, mode);
        if (ReferenceEquals(records, data.Records)) return data;
        var result = Build(data.Items.Select(CloneItem).ToList(), records);
        result.SourceDescription = data.SourceDescription;
        result.SerialNumberNotice = data.SerialNumberNotice;
        result.ReassignedSerialNumberCount = data.ReassignedSerialNumberCount;
        return result;
    }

    internal static (string Wafer, int? X, int? Y, string Fallback) ChipKey(CsvRecord record)
    {
        bool coordinates = record.X.HasValue && record.Y.HasValue;
        string fallback = coordinates ? "" : !string.IsNullOrWhiteSpace(record.ChipSerialNumber)
            ? "SN:" + record.ChipSerialNumber.Trim() : "ROW:" + record.Id;
        return (record.WaferId, coordinates ? record.X : null, coordinates ? record.Y : null, fallback);
    }

    // Export needs raw record selection only, without rebuilding every numeric column.
    public static List<CsvRecord> SelectRetestRecords(List<CsvRecord> source, RetestMode mode)
    {
        if (mode == RetestMode.All) return source;
        if (mode != RetestMode.First && mode != RetestMode.Last) throw new ArgumentOutOfRangeException(nameof(mode));
        var groups = new Dictionary<(string Wafer, int? X, int? Y, string Fallback), List<CsvRecord>>();
        foreach (var record in source)
        {
            var key = ChipKey(record);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new();
            group.Add(record);
        }
        if (groups.Count == source.Count) return source;
        // Select per measurement: a partial retest must not remove other tests on the same chip.
        // Work on one chip at a time instead of indexing every cell in the whole file.
        var retained = new Dictionary<CsvRecord, HashSet<string>>();
        var unique = new HashSet<CsvRecord>();
        foreach (var group in groups.Values)
        {
            if (group.Count == 1) { unique.Add(group[0]); continue; }
            var chosen = new Dictionary<string, CsvRecord>();
            foreach (var record in group)
                foreach (string test in record.Measurements.Keys)
                {
                    if (mode == RetestMode.Last) chosen[test] = record;
                    else chosen.TryAdd(test, record);
                }
            foreach (var entry in chosen)
            {
                if (!retained.TryGetValue(entry.Value, out var measurements))
                    retained[entry.Value] = measurements = new HashSet<string>();
                measurements.Add(entry.Key);
            }
        }
        var records = new List<CsvRecord>();
        foreach (var record in source)
            if (unique.Contains(record)) records.Add(record);
            else if (retained.TryGetValue(record, out var measurements))
                records.Add(new CsvRecord
                {
                    Id = record.Id, WaferId = record.WaferId, SerialNumber = record.SerialNumber,
                    OriginalSerialNumber = record.OriginalSerialNumber,
                    X = record.X, Y = record.Y, Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin,
                    Product = record.Product, Program = record.Program, Lot = record.Lot,
                    Measurements = record.Measurements.SelectKeys(measurements)
                });
        return records;
    }

    public static bool TryValue(string text, out double value) =>
        TryValue(text.AsSpan(), out value);

    public static bool TryValue(ReadOnlySpan<char> text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static CsvParseResult Build(List<TestItemData> items, List<CsvRecord> records)
    {
        var byId = items.ToDictionary(i => i.Id);
        // Manual/imported records can start as dictionaries; loaded datasets never
        // retain those raw strings. All test columns share this one row table.
        RawMeasurementStore? rawStore = null;
        try
        {
            var schema = new MeasurementSchema(items.Select(i => i.Id));
            foreach (var record in records)
                if (!record.Measurements.IsOnDisk)
                {
                    if (record.Measurements.Keys.Any(key => !byId.ContainsKey(key)))
                        throw new InvalidDataException("测量记录引用了不存在的测试项。");
                    rawStore ??= new RawMeasurementStore();
                    record.Measurements = rawStore.Append(schema,
                        schema.Keys.Select(key => record.Measurements.TryGetValue(key, out var text) ? text : null).ToArray());
                }
            rawStore?.Seal();
        }
        catch { rawStore?.Dispose(); throw; }
        foreach (var item in items) { item.Values.Initialize(records, item.Id); item.Errors.Clear(); }
        var devices = new Dictionary<(string Wafer, int? X, int? Y, string Record), DeviceInfo>();
        for (int row = 0; row < records.Count; row++)
        {
            var record = records[row];
            bool rawBlank = true;
            foreach (string measurementKey in record.Measurements.Keys)
            {
                if (!byId.TryGetValue(measurementKey, out var item))
                    throw new InvalidDataException("测量记录引用了不存在的测试项。");
                record.Measurements.TryGetText(measurementKey, out var raw);
                rawBlank &= raw.IsWhiteSpace();
                if (TryValue(raw, out double value))
                    item.Values.SetRow(row, value);
                else if (!raw.SequenceEqual(StdfParser.NotApplicableValue)) item.Errors.Add(record);
            }
            // A completely blank CSV row is an error, not a physical device.
            bool blank = !record.X.HasValue && !record.Y.HasValue &&
                new[] { record.ChipSerialNumber, record.Site, record.SBin, record.HBin }.All(string.IsNullOrWhiteSpace) &&
                rawBlank;
            if (blank) continue;
            var key = (record.WaferId, record.X, record.Y, record.X.HasValue && record.Y.HasValue ? "" : record.Id);
            devices[key] = new DeviceInfo
            {
                WaferId = record.WaferId, SerialNumber = record.SerialNumber, X = record.X, Y = record.Y,
                Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin
            };
        }
        foreach (var item in items) item.Values.Complete();
        return new CsvParseResult { Items = items, Records = records, Devices = devices.Values.ToList() };
    }

    public static CsvParseResult LoadSources(IReadOnlyList<CsvImportSource> sources, AnalyzerConfig config)
    {
        if (sources.Count == 0) throw new InvalidDataException("没有可重新读取的数据文件。");
        var result = DataFileService.Parse(sources[0].Path, config, stdfMode: sources[0].StdfMode);
        foreach (var source in sources.Skip(1))
        {
            string priorNotice = result.SerialNumberNotice;
            result = Merge(result, DataFileService.Parse(source.Path, config, stdfMode: source.StdfMode), source.Options!);
            if (priorNotice.Length > 0)
            {
                result.SerialNumberNotice = priorNotice + (result.SerialNumberNotice.Length > 0 ? "\n\n" + result.SerialNumberNotice : "");
                result.ReassignedSerialNumberCount = result.Records.Count(r => r.OriginalSerialNumber != null);
            }
        }
        return result;
    }

    public static CsvParseResult Merge(CsvParseResult existing, CsvParseResult incoming, ImportOptions options)
    {
        if (incoming.Records.Count == 0) throw new InvalidDataException("新文件没有可导入的测试记录。");
        if (options.SameWafer && (!existing.Wafers.Contains(options.TargetWafer) || incoming.Wafers.Count != 1))
            throw new InvalidDataException("同 wafer 导入需要选择已有 wafer，且新文件只能包含一个 wafer。");
        string Key(TestItemData item)
        {
            string key = (options.MatchByNumber ? item.TestNumber : item.Name).Trim();
            if (key.Length == 0) throw new InvalidDataException("匹配依据存在空值，请改用另一种测试项匹配方式。");
            return options.MatchByNumber && long.TryParse(key, out long number) ? number.ToString(CultureInfo.InvariantCulture) : key;
        }
        void CheckUnique(IEnumerable<TestItemData> items)
        {
            var duplicate = items.GroupBy(Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new InvalidDataException($"匹配依据存在重复值“{duplicate.Key}”，请改用另一种匹配方式。");
        }
        CheckUnique(existing.Items); CheckUnique(incoming.Items);
        var items = existing.Items.Select(CloneItem).ToList();
        var byKey = items.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var itemMap = new Dictionary<string, string>();
        foreach (var source in incoming.Items)
        {
            if (byKey.TryGetValue(Key(source), out var target))
            {
                if (!string.Equals(source.Unit.Trim(), target.Unit.Trim(), StringComparison.Ordinal))
                    throw new InvalidDataException($"测试项“{source.DisplayName}”单位不一致（{target.Unit} / {source.Unit}），无法直接整合。");
                if (options.UseNewLimits) { target.LowLimit = source.LowLimit; target.HighLimit = source.HighLimit; }
            }
            else
            {
                target = CloneItem(source); target.Id = Guid.NewGuid().ToString("N");
                string displayName = target.DisplayName;
                int suffix = 2;
                while (items.Any(i => i.DisplayName == target.DisplayName)) target.DisplayName = $"{displayName} ({suffix++})";
                items.Add(target); byKey.Add(Key(source), target);
            }
            itemMap.Add(source.Id, target.Id);
        }
        var waferMap = new Dictionary<string, string>();
        var taken = existing.Wafers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string wafer in incoming.Wafers)
        {
            string name = options.SameWafer ? options.TargetWafer : wafer;
            if (!options.SameWafer)
            {
                int suffix = 2;
                while (!taken.Add(name)) name = $"{wafer} ({suffix++})";
            }
            waferMap.Add(wafer, name);
        }
        // A subsequent serial normalization must never edit records held by the active dataset.
        var records = existing.Records.Select(CloneRecord).ToList();
        var schemas = new Dictionary<MeasurementSchema, MeasurementSchema>();
        MeasurementCollection Remap(MeasurementCollection raw)
        {
            if (raw.Schema is not { } schema) return raw.ToDictionary(p => itemMap[p.Key], p => p.Value);
            if (!schemas.TryGetValue(schema, out var mapped))
                schemas[schema] = mapped = new MeasurementSchema(schema.Keys.Select(key => itemMap[key]));
            return raw.Remap(mapped);
        }
        foreach (var record in incoming.Records)
            records.Add(new CsvRecord
            {
                WaferId = waferMap[record.WaferId], SerialNumber = record.SerialNumber, X = record.X, Y = record.Y,
                OriginalSerialNumber = record.OriginalSerialNumber,
                Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin,
                Product = record.Product, Program = record.Program, Lot = record.Lot,
                Measurements = Remap(record.Measurements)
            });
        var merged = Build(items, records);
        string incomingNotice = incoming.SerialNumberNotice;
        if (SerialNumberService.Normalize(merged))
            merged.SerialNumberNotice = "整合数据：" + merged.SerialNumberNotice;
        if (incomingNotice.Length > 0)
        {
            merged.SerialNumberNotice = incomingNotice + (merged.SerialNumberNotice.Length > 0 ? "\n\n" + merged.SerialNumberNotice : "");
            if (merged.ReassignedSerialNumberCount == 0) merged.ReassignedSerialNumberCount = incoming.ReassignedSerialNumberCount;
        }
        return merged;
    }

    internal static CsvRecord CloneRecord(CsvRecord record) => new()
    {
        Id = record.Id, WaferId = record.WaferId, SerialNumber = record.SerialNumber,
        OriginalSerialNumber = record.OriginalSerialNumber,
        X = record.X, Y = record.Y, Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin,
        Product = record.Product, Program = record.Program, Lot = record.Lot,
        Measurements = record.Measurements
    };

    internal static List<DeviceInfo> BuildDevices(IReadOnlyList<CsvRecord> records)
    {
        var devices = new Dictionary<(string Wafer, int? X, int? Y, string Record), DeviceInfo>();
        foreach (var record in records)
        {
            if (!record.X.HasValue && !record.Y.HasValue &&
                new[] { record.ChipSerialNumber, record.Site, record.SBin, record.HBin }.All(string.IsNullOrWhiteSpace))
            {
                bool rawBlank = true;
                foreach (string key in record.Measurements.Keys)
                {
                    record.Measurements.TryGetText(key, out var raw);
                    if (!raw.IsWhiteSpace()) { rawBlank = false; break; }
                }
                if (rawBlank) continue;
            }
            var keyForDevice = (record.WaferId, record.X, record.Y, record.X.HasValue && record.Y.HasValue ? "" : record.Id);
            devices[keyForDevice] = new DeviceInfo
            {
                WaferId = record.WaferId, SerialNumber = record.SerialNumber, X = record.X, Y = record.Y,
                Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin
            };
        }
        return devices.Values.ToList();
    }

    internal static TestItemData CloneItem(TestItemData item) => new()
    {
        Id = item.Id, Focus = item.Focus, ColumnIndex = item.ColumnIndex, Name = item.Name, DisplayName = item.DisplayName,
        TestNumber = item.TestNumber, Unit = item.Unit, SBin = item.SBin, HBin = item.HBin,
        LowLimit = item.LowLimit, HighLimit = item.HighLimit
    };
}

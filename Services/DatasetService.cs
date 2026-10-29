using System.Buffers;
using System.Globalization;
using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ImportOptions(bool SameWafer, string TargetWafer, bool MatchByNumber, bool UseNewLimits,
    bool PreserveWaferNames = false, bool AutoMatch = false);
public sealed record CsvImportSource(string Path, ImportOptions? Options = null,
    StdfImportMode StdfMode = StdfImportMode.Standard, string ConfigText = "", string? WaferOverride = null,
    string? LotOverride = null, bool ShowLotInWafer = false);

public static partial class DatasetService
{
    public static CsvParseResult SelectRetests(CsvParseResult data, RetestMode mode, AnalyzerConfig? config = null)
    {
        AnalysisOperation.CheckCancellation();
        if (mode == RetestMode.All) return data;
        var records = SelectRetestRecords(data.Records, mode, config);
        if (ReferenceEquals(records, data.Records)) return data;
        var result = CreateView(data, records);
        result.SourceDescription = data.SourceDescription;
        result.SerialNumberNotice = data.SerialNumberNotice;
        result.ReassignedSerialNumberCount = data.ReassignedSerialNumberCount;
        return result;
    }

    internal static (string Wafer, int? X, int? Y, string Fallback) ChipKey(CsvRecord record)
    {
        if (record.IsFtIdentity)
            return ("", null, null, !string.IsNullOrEmpty(record.RetestUuid) ? $"LOT:{record.Lot.Length}:{record.Lot}|UUID:" + record.RetestUuid : "ROW:" + record.Id);
        bool coordinates = record.X.HasValue && record.Y.HasValue;
        string fallback = coordinates ? "" : !string.IsNullOrWhiteSpace(record.ChipSerialNumber)
            ? "SN:" + record.ChipSerialNumber.Trim() : "ROW:" + record.Id;
        return (record.WaferId, coordinates ? record.X : null, coordinates ? record.Y : null, fallback);
    }

    // Export needs raw record selection only, without rebuilding every numeric column.
    public static List<CsvRecord> SelectRetestRecords(List<CsvRecord> source, RetestMode mode, AnalyzerConfig? config = null)
    {
        if (mode == RetestMode.Last && config?.PreferGoodRetest == true)
            return RetestPreference.SelectWhole(source, mode, config);
        if (mode == RetestMode.All) return source;
        if (mode != RetestMode.First && mode != RetestMode.Last && mode != RetestMode.OnlyRetests)
            throw new ArgumentOutOfRangeException(nameof(mode));
        var groups = new Dictionary<(string Wafer, int? X, int? Y, string Fallback), List<int>>();
        for (int row = 0; row < source.Count; row++)
        {
            AnalysisOperation.CheckCancellation();
            var key = ChipKey(source[row]);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new();
            group.Add(row);
        }
        if (groups.Count == source.Count)
            return mode == RetestMode.OnlyRetests ? new List<CsvRecord>() : source;
        // Resolve one chip at a time. Only compact presence masks survive the group;
        // never retain a string HashSet for every selected record.
        var selected = new MeasurementCollection?[source.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.Values)
        {
            AnalysisOperation.CheckCancellation();
            if (group.Count == 1)
            {
                if (mode != RetestMode.OnlyRetests) selected[group[0]] = source[group[0]].Measurements;
                continue;
            }
            seen.Clear();
            for (int i = 0; i < group.Count; i++)
            {
                AnalysisOperation.CheckCancellation();
                int row = group[mode == RetestMode.Last ? group.Count - 1 - i : i];
                var measurements = source[row].Measurements.SelectKeys(key =>
                {
                    bool first = seen.Add(key);
                    return mode == RetestMode.OnlyRetests ? !first : first;
                });
                if (measurements.Count > 0) selected[row] = measurements;
            }
        }
        var records = new List<CsvRecord>();
        for (int row = 0; row < source.Count; row++)
        {
            if ((row & 1023) == 0) AnalysisOperation.CheckCancellation();
            if (selected[row] is not { } measurements) continue;
            var record = source[row];
            if (!ReferenceEquals(measurements, record.Measurements))
            {
                record = CloneRecord(record);
                record.Measurements = measurements;
            }
            records.Add(record);
        }
        return records;
    }

    public static bool TryValue(string text, out double value) =>
        TryValue(text.AsSpan(), out value);

    public static bool TryValue(ReadOnlySpan<char> text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static CsvParseResult Build(List<TestItemData> items, List<CsvRecord> records)
    {
        AnalysisOperation.CheckCancellation();
        var byId = new Dictionary<string, TestItemData>(items.Count, StringComparer.Ordinal);
        foreach (var item in items) byId.Add(item.Id, item);
        // Manual/imported records can start as dictionaries; loaded datasets never
        // retain those raw strings. All test columns share this one row table.
        RawMeasurementStore? rawStore = null;
        try
        {
            MeasurementSchema? fallbackSchema = null;
            string?[]? tempValues = null;
            foreach (var record in records)
                if (!record.Measurements.IsOnDisk)
                {
                    if (record.Measurements.Keys.Any(key => !byId.ContainsKey(key)))
                        throw new InvalidDataException("测量记录引用了不存在的测试项。");
                    rawStore ??= new RawMeasurementStore();
                    fallbackSchema ??= new MeasurementSchema(items.Select(i => i.Id));
                    tempValues ??= new string?[fallbackSchema.Keys.Length];
                    for (int i = 0; i < fallbackSchema.Keys.Length; i++)
                        tempValues[i] = record.Measurements.TryGetValue(fallbackSchema.Keys[i], out var text) ? text : null;
                    record.Measurements = rawStore.Append(fallbackSchema, tempValues);
                }
            rawStore?.Seal();
        }
        catch { rawStore?.Dispose(); throw; }

        foreach (var item in items) { item.Values.Initialize(records, item.Id, item.IsStdfMeasurement); item.Errors.Clear(); }
        var devices = new Dictionary<(string Wafer, int? X, int? Y, string Record), DeviceInfo>(records.Count);
        TestItemData[]? defaultItemsArray = null;
        MeasurementSchema? lastSchema = null;
        TestItemData[]? lastSchemaItems = null;
        Dictionary<MeasurementSchema, TestItemData[]>? schemaCache = null;
        byte[]? rowBuffer = null;

        try
        {
            for (int row = 0; row < records.Count; row++)
            {
                AnalysisOperation.CheckCancellation();
                var record = records[row];
                bool rawBlank = true;
                var measurements = record.Measurements;
                if (measurements.Count > 0)
                {
                    var recordSchema = measurements.Schema!;
                    if (!ReferenceEquals(recordSchema, lastSchema))
                    {
                        lastSchema = recordSchema;
                        if (schemaCache != null && schemaCache.TryGetValue(recordSchema, out var mapped))
                        {
                            lastSchemaItems = mapped;
                        }
                        else
                        {
                            bool matchesIdentityOrder = recordSchema.Keys.Length == items.Count;
                            if (matchesIdentityOrder)
                            {
                                for (int i = 0; i < items.Count; i++)
                                    if (recordSchema.Keys[i] != items[i].Id) { matchesIdentityOrder = false; break; }
                            }
                            if (matchesIdentityOrder)
                            {
                                lastSchemaItems = defaultItemsArray ??= items.ToArray();
                            }
                            else
                            {
                                var resolved = new TestItemData[recordSchema.Keys.Length];
                                for (int i = 0; i < recordSchema.Keys.Length; i++)
                                {
                                    if (!byId.TryGetValue(recordSchema.Keys[i], out var targetItem))
                                        throw new InvalidDataException("测量记录引用了不存在的测试项。");
                                    resolved[i] = targetItem;
                                }
                                lastSchemaItems = resolved;
                            }
                            (schemaCache ??= new())[recordSchema] = lastSchemaItems;
                        }
                    }

                    var rawRow = measurements.ReadRow(ref rowBuffer);
                    var schemaItems = lastSchemaItems!;
                    for (int col = 0; col < schemaItems.Length; col++)
                    {
                        if (!measurements.PresentAt(col)) continue;
                        var item = schemaItems[col];
                        if (rawRow.IsNumeric(col))
                        {
                            rawBlank = false;
                            item.Values.SetRow(row, rawRow.ReadNumeric(col));
                        }
                        else
                        {
                            var raw = rawRow.RawTextSpan(col);
                            rawBlank &= raw.IsWhiteSpace();
                            if (TryValue(raw, out double value))
                                item.Values.SetRow(row, value);
                            else if (!raw.SequenceEqual(StdfParser.NotApplicableValue))
                                item.Errors.Add(record);
                        }
                    }
                }

                // A completely blank CSV row is an error, not a physical device.
                bool blank = rawBlank && !record.X.HasValue && !record.Y.HasValue &&
                    string.IsNullOrWhiteSpace(record.ChipSerialNumber) &&
                    string.IsNullOrWhiteSpace(record.Site) &&
                    string.IsNullOrWhiteSpace(record.SBin) &&
                    string.IsNullOrWhiteSpace(record.HBin);
                if (blank) continue;
                var key = record.IsFtIdentity ? ChipKey(record) :
                    (record.WaferId, record.X, record.Y, record.X.HasValue && record.Y.HasValue ? "" : record.Id);
                devices[key] = new DeviceInfo
                {
                    WaferId = record.WaferId, SerialNumber = record.SerialNumber, X = record.X, Y = record.Y,
                    Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin
                };
            }
        }
        finally
        {
            if (rowBuffer != null) ArrayPool<byte>.Shared.Return(rowBuffer);
        }

        foreach (var item in items) item.Values.Complete();
        return new CsvParseResult { Items = items, Records = records, Devices = devices.Values.ToList() };
    }

    public static CsvParseResult LoadSources(IReadOnlyList<CsvImportSource> sources, AnalyzerConfig config)
    {
        if (sources.Count == 0) throw new InvalidDataException("没有可重新读取的数据文件。");
        CsvParseResult result;
        try { result = ParseSource(sources[0], config); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new InvalidDataException($"文件“{sources[0].Path}”导入失败：{ex.Message}", ex); }
        return sources.Count == 1 ? result : AppendSources(result, sources.Skip(1).ToArray(), config);
    }

    /// <summary>Append in explicit order, then build columns once. The active dataset is never mutated.</summary>
    public static CsvParseResult AppendSources(CsvParseResult existing, IReadOnlyList<CsvImportSource> sources,
        AnalyzerConfig config)
    {
        if (sources.Count == 0) throw new InvalidDataException("请至少选择一个追加文件。");
        var result = existing;
        foreach (var source in sources)
        {
            AnalysisOperation.CheckCancellation();
            try
            {
                var incoming = ParseSource(source, config);
                result = MergeDefinitions(result, incoming, source.Options ??
                    throw new InvalidDataException("追加文件缺少导入选项。"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new InvalidDataException($"文件“{Path.GetFileName(source.Path)}”导入失败：{ex.Message}", ex); }
        }
        return CompleteMerge(result);
    }

    public static CsvParseResult Merge(CsvParseResult existing, CsvParseResult incoming, ImportOptions options)
        => CompleteMerge(MergeDefinitions(existing, incoming, options));

    private static CsvParseResult MergeDefinitions(CsvParseResult existing, CsvParseResult incoming, ImportOptions options)
    {
        if (options.AutoMatch)
        {
            bool uniqueNumbers = new[] { existing.Items, incoming.Items }.All(items => items.All(i => i.TestNumber.Trim().Length > 0) &&
                items.Select(i => long.TryParse(i.TestNumber.Trim(), out long n) ? n.ToString(CultureInfo.InvariantCulture) : i.TestNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count);
            options = options with { MatchByNumber = uniqueNumbers };
        }
        if (incoming.Records.Count == 0) throw new InvalidDataException("新文件没有可导入的测试记录。");
        if (options.SameWafer && options.TargetWafer.Length == 0)
        {
            if (existing.Wafers.Count != 1)
                throw new InvalidDataException("合并到首个文件时，首个文件必须仅包含一片晶圆；请改选不同 wafer。");
            options = options with { TargetWafer = existing.Wafers[0] };
        }
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
        var itemMap = new Dictionary<string, string>(StringComparer.Ordinal);
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
        var waferMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = existing.Wafers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string wafer in incoming.Wafers)
        {
            string name = options.SameWafer ? options.TargetWafer : wafer;
            if (!options.SameWafer && !options.PreserveWaferNames)
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
                IsFtIdentity = record.IsFtIdentity, RetestUuid = record.RetestUuid, UuidTestNumbers = record.UuidTestNumbers,
                Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin,
                Product = record.Product, Program = record.Program, Lot = record.Lot,
                StdfTestTime = record.StdfTestTime,
                Measurements = Remap(record.Measurements)
            });
        return new CsvParseResult
        {
            Items = items, Records = records,
            SerialNumberNotice = string.Join("\n\n", new[] { existing.SerialNumberNotice, incoming.SerialNumberNotice }
                .Where(notice => notice.Length > 0)),
            ReassignedSerialNumberCount = records.Count(r => r.OriginalSerialNumber != null)
        };
    }

    private static CsvParseResult CompleteMerge(CsvParseResult definitions)
    {
        var merged = Build(definitions.Items, definitions.Records);
        MeasurementDisplay.Configure(merged.Items);
        string priorNotice = definitions.SerialNumberNotice;
        merged.ReassignedSerialNumberCount = definitions.ReassignedSerialNumberCount;
        if (SerialNumberService.Normalize(merged))
            merged.SerialNumberNotice = "整合数据：" + merged.SerialNumberNotice;
        if (priorNotice.Length > 0)
            merged.SerialNumberNotice = priorNotice + (merged.SerialNumberNotice.Length > 0 ? "\n\n" + merged.SerialNumberNotice : "");
        return merged;
    }

    private static CsvParseResult ParseSource(CsvImportSource source, AnalyzerConfig config)
    {
        var layout = source.ConfigText.Length > 0 ? ConfigLoader.Parse(source.ConfigText) : config;
        if (source.ConfigText.Length > 0)
        {
            layout.TestMode = config.TestMode; layout.UuidTestNumbers = config.UuidTestNumbers;
            layout.PassHBins = config.PassHBins; layout.PartialPassHBins = config.PartialPassHBins;
            layout.MissingTokens = config.MissingTokens; layout.NotApplicableTokens = config.NotApplicableTokens;
        }
        var data = DataFileService.Parse(source.Path, layout, cancellationToken: AnalysisOperation.Token, stdfMode: source.StdfMode);
        if (source.WaferOverride is { Length: > 0 } && data.Devices.Select(d => d.WaferId).Distinct().Skip(1).Any())
            throw new InvalidDataException("含多晶圆的文件不能整体覆盖为一个片号，请在登记中清空晶圆覆盖值。");
        if (source.WaferOverride != null || source.LotOverride != null || source.ShowLotInWafer)
        {
            foreach (var record in data.Records)
            {
                if (source.LotOverride != null) record.Lot = source.LotOverride;
                if (source.WaferOverride is { Length: > 0 }) record.WaferId = source.WaferOverride;
                if (source.ShowLotInWafer) record.WaferId = record.Lot + " / " + record.WaferId;
            }
            data.Records = data.Records.ToList(); // Invalidate any previously materialized wafer-name cache.
            data.Devices = BuildDevices(data.Records);
        }
        return data;
    }

    internal static CsvRecord CloneRecord(CsvRecord record) => new()
    {
        Id = record.Id, WaferId = record.WaferId, SerialNumber = record.SerialNumber,
        OriginalSerialNumber = record.OriginalSerialNumber,
        IsFtIdentity = record.IsFtIdentity, RetestUuid = record.RetestUuid, UuidTestNumbers = record.UuidTestNumbers,
        X = record.X, Y = record.Y, Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin,
        Product = record.Product, Program = record.Program, Lot = record.Lot,
        StdfTestTime = record.StdfTestTime,
        Measurements = record.Measurements
    };

    internal static List<DeviceInfo> BuildDevices(IReadOnlyList<CsvRecord> records)
    {
        var devices = new Dictionary<(string Wafer, int? X, int? Y, string Record), DeviceInfo>(records.Count);
        byte[]? rowBuffer = null;
        try
        {
            foreach (var record in records)
            {
                if (!record.X.HasValue && !record.Y.HasValue &&
                    string.IsNullOrWhiteSpace(record.ChipSerialNumber) &&
                    string.IsNullOrWhiteSpace(record.Site) &&
                    string.IsNullOrWhiteSpace(record.SBin) &&
                    string.IsNullOrWhiteSpace(record.HBin))
                {
                    bool rawBlank = true;
                    var measurements = record.Measurements;
                    if (measurements.IsOnDisk && measurements.Schema is { } schema)
                    {
                        if (measurements.Count > 0)
                        {
                            var rawRow = measurements.ReadRow(ref rowBuffer);
                            for (int col = 0; col < schema.Keys.Length; col++)
                            {
                                if (!measurements.PresentAt(col)) continue;
                                if (rawRow.IsNumeric(col) || !rawRow.RawTextSpan(col).IsWhiteSpace())
                                {
                                    rawBlank = false;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (string key in measurements.Keys)
                        {
                            measurements.TryGetText(key, out var raw);
                            if (!raw.IsWhiteSpace()) { rawBlank = false; break; }
                        }
                    }
                    if (rawBlank) continue;
                }
                var keyForDevice = record.IsFtIdentity ? ChipKey(record) :
                    (record.WaferId, record.X, record.Y, record.X.HasValue && record.Y.HasValue ? "" : record.Id);
                devices[keyForDevice] = new DeviceInfo
                {
                    WaferId = record.WaferId, SerialNumber = record.SerialNumber, X = record.X, Y = record.Y,
                    Site = record.Site, Result = record.Result, SBin = record.SBin, HBin = record.HBin
                };
            }
        }
        finally
        {
            if (rowBuffer != null) ArrayPool<byte>.Shared.Return(rowBuffer);
        }
        return devices.Values.ToList();
    }

    internal static TestItemData CloneItem(TestItemData item) => new()
    {
        Id = item.Id, Focus = item.Focus, ColumnIndex = item.ColumnIndex, Name = item.Name, DisplayName = item.DisplayName,
        TestNumber = item.TestNumber, Unit = item.Unit, SBin = item.SBin, HBin = item.HBin,
        IsStdfMeasurement = item.IsStdfMeasurement, DisplayFactor = item.DisplayFactor, DisplayUnit = item.DisplayUnit,
        LowLimit = item.LowLimit, HighLimit = item.HighLimit
    };
}

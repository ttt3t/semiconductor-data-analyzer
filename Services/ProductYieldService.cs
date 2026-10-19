using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class ProductYieldService
{
    private readonly record struct DeviceKey(string Kind, string Lot, string Wafer, string Identity);
    private sealed record Observation(bool KnownIdentity, string HBin, string SBin, OriginalDisposition Disposition, DateTime? TestTime);

    public static void Validate(ManagedProduct product)
    {
        if (string.IsNullOrWhiteSpace(product.Name)) throw new InvalidDataException("请填写产品名称。");
        if (product.Flow.Count == 0) throw new InvalidDataException("请至少添加一个测试步骤。");
        if (product.Files.Select(f => f.Id).Distinct().Count() != product.Files.Count) throw new InvalidDataException("登记记录 ID 重复，请修正产品目录。");
        if (product.Flow.Select(s => s.Id).Distinct().Count() != product.Flow.Count ||
            product.Flow.Any(s => s.Kind is not ("CP" or "FT") || string.IsNullOrWhiteSpace(s.Name) || !double.IsFinite(s.Temperature)))
            throw new InvalidDataException("测试步骤名称、CP/FT 类型或温度无效。");
        foreach (var group in product.Flow.GroupBy(s => s.Kind))
            if (!group.Any(s => s.Required)) throw new InvalidDataException($"{group.Key} 至少要有一个必测步骤。");
        _ = RetestIdentityService.ParseTestNumbers(product.UuidTestNumbers);
        if (product.RetestMode is not (RetestMode.First or RetestMode.Last)) throw new InvalidDataException("管理统计仅支持首测或末次整条记录。");
        _ = ConfigLoader.Parse(product.ConfigText);
    }

    public static AnalyzerConfig FileConfig(ManagedProduct product, ManagedDataFile file)
    {
        string kind = file.StageId.StartsWith("COMBINED:") ? file.SpecifiedKind : product.Flow.Single(s => s.Id == file.StageId).Kind;
        var original = ConfigLoader.Parse(product.ConfigText, product.Name + ".ini");
        var config = file.ConfigText.Length > 0 ? ConfigLoader.Parse(file.ConfigText, product.Name + "-文件配置.ini") : original;
        config.PassHBins = original.PassHBins; config.PartialPassHBins = original.PartialPassHBins;
        config.SBinCategories = original.SBinCategories;
        config.SBinDescriptions = original.SBinDescriptions;
        config.TestMode = kind == "FT" ? TestAnalysisMode.FT : TestAnalysisMode.CP;
        config.UuidTestNumbers = RetestIdentityService.ParseTestNumbers(product.UuidTestNumbers);
        return config;
    }

    public static string Signature(ManagedProduct product) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Schema = 2, product.ConfigText, product.Flow, product.UuidTestNumbers, product.RetestMode,
            product.IncludePartialPass, product.Files }))));

    public static bool IsCurrent(ManagedProduct product)
    {
        if (product.Snapshot is not { } snapshot || snapshot.DefinitionSignature != Signature(product)) return false;
        foreach (var entry in product.Files)
        {
            if (entry.UseSpecifiedYield) continue;
            var prior = snapshot.Files.FirstOrDefault(f => f.FileId == entry.Id);
            try
            {
                var file = new FileInfo(entry.FilePath);
                if (prior == null || !file.Exists || file.Length != prior.Length || file.LastWriteTimeUtc != prior.LastWriteUtc) return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }
        return true;
    }

    public static ManagementSnapshot Calculate(ManagedProduct product, CancellationToken token = default, IProgress<string>? progress = null)
    {
        if (!token.CanBeCanceled) token = AnalysisOperation.Token;
        using var operation = new AnalysisOperation(token);
        Validate(product);
        var result = new ManagementSnapshot { DefinitionSignature = Signature(product) };
        var devices = new Dictionary<DeviceKey, Dictionary<string, Observation>>();
        for (int i = 0; i < product.Files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = product.Files[i];
            progress?.Report($"读取 {i + 1} / {product.Files.Count}：{file.FileName}");
            var state = new ManagedFileResult { FileId = file.Id }; result.Files.Add(state);
            try
            {
                if (file.UseSpecifiedYield) { ValidateSpecified(product, file); continue; }
                var stage = product.Flow.SingleOrDefault(s => s.Id == file.StageId) ?? throw new InvalidDataException("文件的测试步骤已不存在。");
                if (string.IsNullOrWhiteSpace(file.Lot)) throw new InvalidDataException("未指定批次。");
                ReadFile(product, file, stage, state, devices, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
            { state.Error = ex.Message; }
        }
        token.ThrowIfCancellationRequested();
        var config = ConfigLoader.Parse(product.ConfigText);
        var categories = config.SBinCategories.SelectMany(c => c.Value.Select(bin => (Bin: BinStatisticsService.NormalizeBin(bin), Name: c.Key)))
            .ToDictionary(c => c.Bin, c => c.Name);
        foreach (var group in devices.GroupBy(d => (d.Key.Kind, d.Key.Lot, d.Key.Wafer)))
        {
            token.ThrowIfCancellationRequested();
            var stages = product.Flow.Where(s => s.Kind == group.Key.Kind).ToArray();
            var population = group.ToArray();
            foreach (var stage in stages)
            {
                var selected = population.Where(d => d.Value.ContainsKey(stage.Id)).Select(d => d.Value[stage.Id]).ToArray();
                void AddStage(Observation[] observations, string period, List<ManagedYieldRow> yields, List<ManagedCategoryCount> counts)
                {
                    var row = Row(group.Key, stage.Id, stage.Label, false); row.Period = period;
                    row.Total = observations.Length;
                    row.Pass = observations.Count(o => o.HBin.Length > 0 && o.Disposition == OriginalDisposition.Pass);
                    row.PartialPass = observations.Count(o => o.HBin.Length > 0 && o.Disposition == OriginalDisposition.PartialPass);
                    row.Good = row.Pass + (product.IncludePartialPass ? row.PartialPass : 0);
                    row.Pending = observations.Count(o => o.HBin.Length == 0);
                    row.Failed = row.Total - row.Good - row.Pending;
                    row.MissingIdentity = observations.Count(o => !o.KnownIdentity);
                    yields.Add(row);
                    int valid = observations.Count(o => o.SBin.Length > 0);
                    var byCategory = observations.Where(o => o.SBin.Length > 0).GroupBy(o => categories.GetValueOrDefault(BinStatisticsService.NormalizeBin(o.SBin), "未分类"))
                        .ToDictionary(g => g.Key, g => g.Count());
                    foreach (string name in config.SBinCategories.Keys.Concat(byCategory.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                        counts.Add(new() { Kind = row.Kind, Lot = row.Lot, Wafer = row.Wafer, StageId = stage.Id, Period = period,
                            Category = name, Count = byCategory.GetValueOrDefault(name), Total = valid });
                }
                AddStage(selected, "", result.Yields, result.Categories);
                foreach (var month in selected.GroupBy(o => Month(o.TestTime)))
                    AddStage(month.ToArray(), month.Key, result.MonthlyYields, result.MonthlyCategories);
            }
            var required = stages.Where(s => s.Required).Select(s => s.Id).ToArray();
            var combined = Row(group.Key, "COMBINED:" + group.Key.Kind, group.Key.Kind + " 综合", true);
            var monthly = new Dictionary<string, ManagedYieldRow>();
            foreach (var device in population.Where(d => required.Any(d.Value.ContainsKey)))
            {
                var observations = device.Value;
                // Match all temperatures first, then assign the selected chip to its latest measured required step.
                // Missing timestamps stay explicitly unassigned; registration time is never substituted.
                var tested = required.Where(observations.ContainsKey).Select(s => observations[s]).ToArray();
                string month = tested.All(o => o.TestTime.HasValue) ? Month(tested.Max(o => o.TestTime)) : "";
                if (!monthly.TryGetValue(month, out var periodRow))
                    monthly[month] = periodRow = Row(group.Key, combined.StageId, combined.StageName, true);
                periodRow.Period = month;
                bool known = observations.Values.All(o => o.KnownIdentity);
                bool sufficientIdentity = known || required.Length == 1; // One required step needs no cross-temperature matching.
                bool missingStage = required.Any(s => !observations.ContainsKey(s));
                bool missing = required.Any(s => !observations.TryGetValue(s, out var o) || o.HBin.Length == 0);
                bool failed = required.Any(s => observations.TryGetValue(s, out var o) && o.HBin.Length > 0 && !HBinClassifier.IsGood(o.Disposition, product.IncludePartialPass));
                foreach (var target in new[] { combined, periodRow })
                {
                    target.Total++;
                    if (missingStage) target.MissingTemperature++;
                    if (!known) target.MissingIdentity++;
                    if (failed) target.Failed++;
                    else if (!missing && sufficientIdentity) target.Good++;
                    else target.Pending++;
                    if (!missing && sufficientIdentity && required.All(s => observations[s].Disposition == OriginalDisposition.Pass)) target.Pass++;
                    else if (!missing && sufficientIdentity && required.All(s => HBinClassifier.IsGood(observations[s].Disposition, true))) target.PartialPass++;
                }
            }
            result.Yields.Add(combined);
            result.MonthlyYields.AddRange(monthly.Values);
        }
        ApplySpecified(product, result);
        foreach (var file in product.Files.Where(f => !f.UseSpecifiedYield))
        {
            token.ThrowIfCancellationRequested();
            var state = result.Files.Single(s => s.FileId == file.Id);
            if (state.Error.Length > 0) continue;
            var current = new FileInfo(file.FilePath);
            if (!current.Exists || current.Length != state.Length || current.LastWriteTimeUtc != state.LastWriteUtc)
                throw new IOException($"文件“{file.FileName}”在计算期间又发生变化，本次结果未保存，请重新计算。");
        }
        result.CalculatedUtc = DateTime.UtcNow;
        return result;
    }

    public static void ValidateSpecified(ManagedProduct product, ManagedDataFile file)
    {
        string kind = file.StageId.StartsWith("COMBINED:") ? file.SpecifiedKind : product.Flow.SingleOrDefault(s => s.Id == file.StageId)?.Kind ?? "";
        if (kind is not ("CP" or "FT") || !product.Flow.Any(s => s.Kind == kind)) throw new InvalidDataException("指定良率的测试步骤无效。");
        if (kind != file.SpecifiedKind || file.StageId.StartsWith("COMBINED:") && file.StageId != "COMBINED:" + kind)
            throw new InvalidDataException("指定良率的 CP / FT 类型与步骤不一致。");
        if (string.IsNullOrWhiteSpace(file.Lot) || kind == "CP" && string.IsNullOrWhiteSpace(file.WaferOverride))
            throw new InvalidDataException("指定 CP 良率需要填写批次和晶圆号；FT 至少填写批次。");
        if (file.SpecifiedIncludePartial != product.IncludePartialPass) throw new InvalidDataException("指定良率的 Pass / PartialPass 口径已变化，请核实并重新登记该指定值。");
        if (file.SpecifiedTotal.HasValue != file.SpecifiedGood.HasValue || file.SpecifiedTotal is <= 0 || file.SpecifiedGood is < 0 || file.SpecifiedGood > file.SpecifiedTotal)
            throw new InvalidDataException("总数和良品数需同时填写，且总数 > 0、0 ≤ 良品数 ≤ 总数；也可同时留空只指定百分比。");
        if (!file.SpecifiedTotal.HasValue && (file.SpecifiedPercent is not double value || !double.IsFinite(value) || value < 0 || value > 100))
            throw new InvalidDataException("请填写 0–100 的良率百分比，或同时填写总数与良品数。");
    }

    private static void ApplySpecified(ManagedProduct product, ManagementSnapshot snapshot)
    {
        var valid = product.Files.Where(f => f.UseSpecifiedYield && snapshot.Files.Single(r => r.FileId == f.Id).Error.Length == 0).ToArray();
        var selected = valid.GroupBy(f => (f.Lot, Wafer: f.SpecifiedKind == "FT" ? "" : f.WaferOverride, f.StageId))
            .Select(g => product.RetestMode == RetestMode.First ? g.First() : g.Last()).ToArray();
        foreach (var file in selected)
        {
            string kind = file.SpecifiedKind, wafer = kind == "FT" ? "" : file.WaferOverride;
            bool combined = file.StageId.StartsWith("COMBINED:");
            var row = new ManagedYieldRow
            {
                Kind = kind, Lot = file.Lot, Wafer = wafer, StageId = file.StageId, IsCombined = combined,
                Period = Month(file.TestTime),
                StageName = combined ? kind + " 综合" : product.Flow.Single(s => s.Id == file.StageId).Label,
                Source = combined ? "指定综合良率" : "指定良率", Note = file.SpecifiedOrigin,
                Total = file.SpecifiedTotal ?? 0, Good = file.SpecifiedGood ?? 0,
                Failed = file.SpecifiedTotal.HasValue ? file.SpecifiedTotal.Value - file.SpecifiedGood!.Value : 0,
                HasSampleCount = file.SpecifiedTotal.HasValue, HasDispositionCounts = false,
                SpecifiedYield = file.SpecifiedTotal.HasValue ? 100.0 * file.SpecifiedGood!.Value / file.SpecifiedTotal.Value : file.SpecifiedPercent
            };
            snapshot.Yields.RemoveAll(r => r.Kind == kind && r.Lot == file.Lot && r.Wafer == wafer && r.StageId == file.StageId);
            snapshot.Yields.Add(row);
            snapshot.MonthlyYields.RemoveAll(r => r.Kind == kind && r.Lot == file.Lot && r.Wafer == wafer && r.StageId == file.StageId);
            snapshot.MonthlyYields.Add(ProductCatalogStore.Clone(row));
        }
        foreach (var group in selected.Where(f => !f.StageId.StartsWith("COMBINED:"))
            .GroupBy(f => (Kind: f.SpecifiedKind, f.Lot, Wafer: f.SpecifiedKind == "FT" ? "" : f.WaferOverride)))
        {
            var required = product.Flow.Where(s => s.Kind == group.Key.Kind && s.Required).ToArray();
            if (!group.Any(f => required.Any(s => s.Id == f.StageId))) continue;
            string id = "COMBINED:" + group.Key.Kind;
            if (selected.Any(f => f.StageId == id && f.Lot == group.Key.Lot && (f.SpecifiedKind == "FT" ? "" : f.WaferOverride) == group.Key.Wafer)) continue;
            snapshot.Yields.RemoveAll(r => r.Kind == group.Key.Kind && r.Lot == group.Key.Lot && r.Wafer == group.Key.Wafer && r.IsCombined);
            snapshot.MonthlyYields.RemoveAll(r => r.Kind == group.Key.Kind && r.Lot == group.Key.Lot && r.Wafer == group.Key.Wafer && r.IsCombined);
            if (required.Length == 1 && group.Any(f => f.StageId == required[0].Id))
            {
                var stageRow = snapshot.Yields.Single(r => r.Kind == group.Key.Kind && r.Lot == group.Key.Lot && r.Wafer == group.Key.Wafer && r.StageId == required[0].Id);
                var combined = ProductCatalogStore.Clone(stageRow); combined.StageId = id; combined.StageName = group.Key.Kind + " 综合"; combined.IsCombined = true;
                combined.Note += "；仅一个必测步骤，综合与该步骤相同。"; snapshot.Yields.Add(combined);
                snapshot.MonthlyYields.Add(ProductCatalogStore.Clone(combined));
            }
            else
            {
                var unavailable = new ManagedYieldRow { Kind = group.Key.Kind, Lot = group.Key.Lot, Wafer = group.Key.Wafer,
                StageId = id, StageName = group.Key.Kind + " 综合", IsCombined = true, CanCalculateYield = false, HasSampleCount = false,
                HasDispositionCounts = false, Source = "无法自动综合", Note = "存在指定的阶段良率，缺少逐颗芯片跨温对应关系；可另行登记物理分 Bin 得到的综合良率。" };
                snapshot.Yields.Add(unavailable);
                // The true joint population/date is unknown; retain an explicit unknown-time result.
                snapshot.MonthlyYields.Add(ProductCatalogStore.Clone(unavailable));
            }
        }
    }

    public static double? AggregateYield(IEnumerable<ManagedYieldRow> source)
    {
        var rows = source.ToArray();
        if (rows.Length == 0 || rows.Any(r => !r.CanCalculateYield)) return null;
        if (rows.Length == 1) return rows[0].Yield;
        if (rows.Any(r => !r.HasSampleCount)) return null;
        int total = rows.Sum(r => r.Total); return total == 0 ? null : 100.0 * rows.Sum(r => r.Good) / total;
    }

    private static ManagedYieldRow Row((string Kind, string Lot, string Wafer) key, string id, string name, bool combined) => new()
    { Kind = key.Kind, Lot = key.Lot, Wafer = key.Wafer, StageId = id, StageName = name, IsCombined = combined };

    private static string Month(DateTime? time) => time?.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static void ReadFile(ManagedProduct product, ManagedDataFile file, ProductTestStage stage, ManagedFileResult state,
        Dictionary<DeviceKey, Dictionary<string, Observation>> devices, CancellationToken token)
    {
        using var operation = new AnalysisOperation(token);
        var info = new FileInfo(file.FilePath);
        if (!info.Exists) throw new FileNotFoundException("原文件不存在，请重新定位路径。", file.FilePath);
        long length = info.Length; DateTime modified = info.LastWriteTimeUtc;
        state.Length = length; state.LastWriteUtc = modified;
        var config = FileConfig(product, file);
        if (stage.Kind == "FT" && config.UuidTestNumbers.Count == 0) throw new InvalidDataException("FT 自动计算需要在产品流程中指定 UUID Test Num；指定汇总良率可不提供 UUID。");
        var data = DataFileService.Parse(file.FilePath, config, cancellationToken: token, stdfMode: file.StdfMode);
        token.ThrowIfCancellationRequested();
        if (file.WaferOverride.Length > 0 && stage.Kind == "CP" && data.Devices.Select(d => d.WaferId).Distinct().Skip(1).Any())
            throw new InvalidDataException("含多个晶圆的文件不能整体指定为一个晶圆，请清空晶圆覆盖值并配置 Wafer 列。");
        var classifier = new HBinClassifier(config);
        // Prepare a file transaction first: failed/changed files must not contribute a partial population.
        var readings = new List<(DeviceKey Key, Observation Observation)>(data.Records.Count);
        for (int r = 0; r < data.Records.Count; r++)
        {
            if ((r & 1023) == 0) token.ThrowIfCancellationRequested();
            var record = data.Records[r];
            if (!record.X.HasValue && !record.Y.HasValue && string.IsNullOrWhiteSpace(record.ChipSerialNumber) &&
                string.IsNullOrWhiteSpace(record.Site) && string.IsNullOrWhiteSpace(record.HBin) && string.IsNullOrWhiteSpace(record.SBin) &&
                record.Measurements.All(m => string.IsNullOrWhiteSpace(m.Value))) continue;
            bool known = stage.Kind == "CP" ? record.X.HasValue && record.Y.HasValue : !string.IsNullOrEmpty(record.RetestUuid);
            string identity = !known ? $"UNKNOWN:{file.Id}:{r}" : stage.Kind == "CP" ? $"XY:{record.X},{record.Y}" : "UUID:" + record.RetestUuid!;
            string wafer = stage.Kind == "FT" ? "" : file.WaferOverride.Length > 0 ? file.WaferOverride : record.WaferId;
            if (stage.Kind == "CP" && string.IsNullOrWhiteSpace(wafer)) throw new InvalidDataException("CP 数据缺少晶圆名称，请在登记时指定真实晶圆号。");
            string hbin = record.HBin.Trim(), sbin = record.SBin.Trim();
            DateTime? time = file.UseStdfTestTime && file.TestTime == file.DetectedStdfTestTime
                ? record.StdfTestTime ?? data.StdfMetadata?.TimeForWafer(record.WaferId) ?? file.TestTime : file.TestTime;
            readings.Add((new(stage.Kind, file.Lot.Trim(), wafer, identity), new(known, hbin, sbin, classifier.Classify(hbin), time)));
        }
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified) throw new IOException("文件在计算期间被修改，请重新计算。");
        token.ThrowIfCancellationRequested();
        foreach (var reading in readings)
        {
            if (!devices.TryGetValue(reading.Key, out var stages)) devices[reading.Key] = stages = new(StringComparer.Ordinal);
            if (product.RetestMode == RetestMode.First) stages.TryAdd(stage.Id, reading.Observation);
            else stages[stage.Id] = reading.Observation;
        }
        state.Length = length; state.LastWriteUtc = modified; state.Records = readings.Count;
        state.MissingIdentity = readings.Count(r => !r.Observation.KnownIdentity);
    }
}

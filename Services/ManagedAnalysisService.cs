using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class ManagedAnalysisService
{
    public static ManagedDataFile[] YieldFiles(ManagedProduct product, ManagedYieldRow row)
    {
        var files = product.Files.Where(f => row.SourceFileIds.Contains(f.Id) && !string.IsNullOrWhiteSpace(f.FilePath)).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("该行没有关联的原始数据文件；仅登记指定良率时无法打开原文件。");
        return files;
    }

    public static CsvImportSource[][] YieldSources(ManagedProduct product, ManagedYieldRow row) =>
        YieldFiles(product, row).GroupBy(f => f.StageId)
            .Select(group => Sources(product, group.Select(f => f.Id))).ToArray();

    public static CsvImportSource[] Sources(ManagedProduct product, IEnumerable<string> selectedIds)
    {
        var ids = selectedIds.ToHashSet(StringComparer.Ordinal);
        // Selection click order must not change first/last retest semantics.
        var selected = product.Files.Where(f => ids.Contains(f.Id)).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("请选择需要打开的数据文件。");
        if (selected.Any(f => f.FilePath.Length == 0)) throw new InvalidDataException("选中项包含无原文件的指定良率，请只选择实际数据文件。");
        if (selected.Select(f => f.StageId).Distinct().Count() != 1 || selected.Any(f => !product.Flow.Any(s => s.Id == f.StageId) &&
            !(f.UseSpecifiedYield && f.StageId == "COMBINED:" + f.SpecifiedKind)))
            throw new InvalidDataException("请合并同一测试步骤的数据；不同温度或 CP/FT 步骤请分别打开，综合结果请查看管理良率明细。");
        bool multipleLots = selected.Select(f => f.Lot).Distinct(StringComparer.Ordinal).Skip(1).Any();
        foreach (var file in selected) if (!File.Exists(file.FilePath)) throw new FileNotFoundException("找不到原文件，请重新定位：" + file.FileName);
        return selected.Select((file, i) => new CsvImportSource(file.FilePath,
            i == 0 ? null : new(false, "", true, false, PreserveWaferNames: true, AutoMatch: true), file.StdfMode,
            ConfigLoader.Serialize(ProductYieldService.FileConfig(product, file)), file.WaferOverride, file.Lot, multipleLots)).ToArray();
    }
}

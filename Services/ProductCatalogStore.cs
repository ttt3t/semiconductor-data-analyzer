using System.IO;
using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Paths, configuration snapshots and small aggregates only. No raw measurements.</summary>
public sealed class ProductCatalogStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; }
    public ProductCatalog Catalog { get; private set; }
    public ProductCatalogStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SemiconductorCsvAnalyzer", "ProductLibrary", "catalog.json");
        Catalog = File.Exists(FilePath) ? Read(FilePath) : new();
    }
    private static ProductCatalog Read(string path)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<ProductCatalog>(File.ReadAllText(path))
                ?? throw new InvalidDataException("产品目录为空。");
            if (catalog.Version != 1 || catalog.Products == null || string.IsNullOrEmpty(catalog.Revision))
                throw new InvalidDataException("产品目录格式或版本不受支持。");
            if (catalog.Products.Any(p => p == null || p.Flow == null || p.Files == null) ||
                catalog.Products.Select(p => p.Id).Distinct().Count() != catalog.Products.Count)
                throw new InvalidDataException("产品目录包含无效或重复产品。");
            return catalog;
        }
        catch (JsonException ex) { throw new InvalidDataException("产品目录损坏，请从 catalog.json.bak 恢复；不会覆盖原目录。", ex); }
    }
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public void DeleteProduct(string productId)
    {
        var next = Clone(Catalog);
        if (next.Products.RemoveAll(p => p.Id == productId) == 0) throw new InvalidDataException("产品不存在。");
        Save(next); // Registration metadata only. Original source paths are never deleted.
    }
    public void RemoveFiles(string productId, IEnumerable<string> fileIds)
    {
        var ids = fileIds.ToHashSet(StringComparer.Ordinal);
        var next = Clone(Catalog);
        var product = next.Products.Single(p => p.Id == productId);
        product.Files.RemoveAll(f => ids.Contains(f.Id));
        product.Snapshot = null; // Never display removed sources as current statistics, even if recalculation is cancelled.
        Save(next);
    }
    public void Save(ProductCatalog catalog)
    {
        string? temporary = null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        using var gate = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(FilePath) && Read(FilePath).Revision != Catalog.Revision)
            throw new IOException("产品目录已被另一个程序更新，请重新打开管理窗口后再保存。");
        var next = Clone(catalog);
        next.Revision = Guid.NewGuid().ToString("N");
        try
        {
            temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Options));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
            temporary = null; Catalog = next;
        }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }
}

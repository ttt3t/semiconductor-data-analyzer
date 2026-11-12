using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ImportWorksheet(string Name, string Part, bool Hidden);

/// <summary>Read worksheet XML one row at a time. Shared strings and the intermediate CSV live on disk.</summary>
public static class SpreadsheetImportService
{
    private static XmlReader Reader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });

    private static string Part(string origin, string target)
    {
        if (target.Contains(':') || target.Contains('?') || target.Contains('#')) throw new InvalidDataException("Excel 内部关系路径无效。");
        string full = target.StartsWith('/') ? target[1..] : origin[..(origin.LastIndexOf('/') + 1)] + target;
        var parts = new List<string>();
        foreach (string piece in Uri.UnescapeDataString(full).Split('/'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..")
            { if (parts.Count == 0) throw new InvalidDataException("Excel 内部路径超出工作簿。"); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    private sealed record Relationship(string Id, string Type, string Target);
    private static List<Relationship> Relationships(ZipArchive zip, string part)
    {
        string relations = part.Length == 0 ? "_rels/.rels" : part[..(part.LastIndexOf('/') + 1)] + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
        var entry = zip.GetEntry(relations);
        if (entry == null) return new();
        using var stream = entry.Open(); using var reader = Reader(stream);
        var result = new List<Relationship>();
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("TargetMode") != "External")
                result.Add(new(reader.GetAttribute("Id") ?? "", reader.GetAttribute("Type") ?? "", Part(part, reader.GetAttribute("Target") ?? "")));
        return result;
    }

    private static (ImportWorksheet[] Sheets, string? Strings) Definition(ZipArchive zip, CancellationToken token)
    {
        string workbook = Relationships(zip, "").FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal))?.Target ?? "xl/workbook.xml";
        var relations = Relationships(zip, workbook);
        var worksheets = relations.Where(r => r.Type.EndsWith("/worksheet", StringComparison.Ordinal)).ToDictionary(r => r.Id, r => r.Target);
        using var input = (zip.GetEntry(workbook) ?? throw new InvalidDataException("文件中没有有效的 Excel 工作簿。")).Open();
        using var reader = Reader(input);
        var sheets = new List<ImportWorksheet>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet") continue;
            string? id = null;
            if (reader.MoveToFirstAttribute())
            {
                do { if (reader.LocalName == "id") id = reader.Value; } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            if (id != null && worksheets.TryGetValue(id, out var part))
                sheets.Add(new(reader.GetAttribute("name") ?? "Sheet", part, reader.GetAttribute("state") is "hidden" or "veryHidden"));
        }
        if (sheets.Count == 0) throw new InvalidDataException("Excel 文件中没有可读取的数据工作表。");
        if (sheets.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Count)
            throw new InvalidDataException("Excel 工作表名称重复，无法唯一选择。");
        return (sheets.ToArray(), relations.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal))?.Target);
    }

    public static ImportWorksheet[] Worksheets(CsvImportSource source, CancellationToken token = default)
    {
        using var resolved = ImportTemporaryFile.Resolve(source.Path, source.ArchiveEntry, token);
        using var input = SourceFileReader.Open(resolved.Path); var stamp = new SourceFileStamp(input);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var sheets = Definition(zip, token).Sheets;
        stamp.Validate(input);
        return sheets;
    }

    private static string RichText(XElement element) => XmlConvert.DecodeName(string.Concat(element.Descendants()
        .Where(e => e.Name.LocalName == "t" && !e.Ancestors().Any(a => a.Name.LocalName == "rPh")).Select(e => e.Value)));

    internal sealed class Converted : IDisposable
    {
        public ImportTemporaryFile File { get; }
        public string Worksheet { get; }
        public int UncachedFormulas { get; }
        public Converted(ImportTemporaryFile file, string worksheet, int uncached) { File = file; Worksheet = worksheet; UncachedFormulas = uncached; }
        public void Dispose() => File.Dispose();
    }

    internal static Converted Convert(string path, string? worksheet, CancellationToken token)
    {
        using var input = SourceFileReader.Open(path); var stamp = new SourceFileStamp(input);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var definition = Definition(zip, token);
        var selected = worksheet == null ? definition.Sheets.FirstOrDefault(s => !s.Hidden) ?? definition.Sheets[0]
            : definition.Sheets.FirstOrDefault(s => s.Name == worksheet) ?? throw new InvalidDataException($"Excel 工作表已不存在：{worksheet}");
        string name = System.IO.Path.GetFileNameWithoutExtension(path) + (definition.Sheets.Length > 1 ? " [" + selected.Name + "]" : "") + ".csv";
        var output = ImportTemporaryFile.Create(name);
        try
        {
            using var stringsFile = ImportTemporaryFile.Create("shared-strings.bin");
            using var strings = new FileStream(stringsFile.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.RandomAccess);
            using var stringWriter = new BinaryWriter(strings, Encoding.UTF8, leaveOpen: true);
            using var stringReader = new BinaryReader(strings, Encoding.UTF8, leaveOpen: true);
            var offsets = new List<long>();
            using (ImportProgress.Stage(0, .2, "读取 Excel 共享文本"))
            if (definition.Strings is { } stringPart && zip.GetEntry(stringPart) is { } stringEntry)
            {
                using var stream = stringEntry.Open(); using var reader = Reader(stream);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
                    using var subtree = reader.ReadSubtree();
                    string text = RichText(XElement.Load(subtree, LoadOptions.PreserveWhitespace));
                    offsets.Add(strings.Position); stringWriter.Write(text);
                }
                stringWriter.Flush();
            }
            var textCache = new Dictionary<int, string>();
            var textOrder = new Queue<int>();
            int cachedCharacters = 0;
            string Shared(string raw)
            {
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || (uint)index >= offsets.Count)
                    throw new InvalidDataException("Excel 共享文本索引无效。");
                if (textCache.TryGetValue(index, out string? cached)) return cached;
                strings.Position = offsets[index]; string text = stringReader.ReadString();
                if (text.Length <= 4096)
                {
                    while (textCache.Count >= 2048 || cachedCharacters + text.Length > 131072)
                    {
                        int oldest = textOrder.Dequeue(); cachedCharacters -= textCache[oldest].Length; textCache.Remove(oldest);
                    }
                    textCache[index] = text; textOrder.Enqueue(index); cachedCharacters += text.Length;
                }
                return text;
            }
            int formulas = 0, previousRow = 0, estimatedRows = 0;
            using (ImportProgress.Stage(.2, 1, "读取 Excel 工作表 " + selected.Name))
            using (var sheet = (zip.GetEntry(selected.Part) ?? throw new InvalidDataException("Excel 工作表内容缺失。")).Open())
            using (var reader = Reader(sheet))
            using (var writer = new StreamWriter(output.Path, false, new UTF8Encoding(false), 65536))
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "dimension")
                    {
                        string last = (reader.GetAttribute("ref") ?? "").Split(':').Last();
                        int.TryParse(new string(last.SkipWhile(c => !char.IsDigit(c)).ToArray()), out estimatedRows);
                    }
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                    int row = int.TryParse(reader.GetAttribute("r"), out int n) ? n : previousRow + 1;
                    if (row <= previousRow || row > 1_048_576) throw new InvalidDataException("Excel 行号无效或顺序异常。");
                    while (++previousRow < row) { if ((previousRow & 255) == 0) token.ThrowIfCancellationRequested(); writer.WriteLine(); }
                    using var subtree = reader.ReadSubtree();
                    var cells = XElement.Load(subtree, LoadOptions.PreserveWhitespace).Elements().Where(e => e.Name.LocalName == "c");
                    int column = 0, sourceColumn = 0;
                    foreach (var cell in cells)
                    {
                        token.ThrowIfCancellationRequested();
                        string reference = (string?)cell.Attribute("r") ?? "";
                        int target = 0;
                        foreach (char c in reference.TakeWhile(char.IsLetter)) target = checked(target * 26 + char.ToUpperInvariant(c) - 'A' + 1);
                        if (target == 0) target = sourceColumn + 1;
                        if (target <= sourceColumn || target > 16_384) throw new InvalidDataException("Excel 列号无效或顺序异常。");
                        sourceColumn = target;
                        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                        var inline = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                        bool formula = cell.Elements().Any(e => e.Name.LocalName == "f");
                        if (formula && value == null) formulas++;
                        if (value == null && inline == null && !formula) continue; // Formatting-only cells do not widen the table.
                        if (column > 0) writer.Write(',');
                        while (++column < target) writer.Write(',');
                        string raw = (string?)cell.Attribute("t") switch
                        {
                            "s" => Shared(value?.Value ?? ""),
                            "inlineStr" => inline == null ? "" : RichText(inline),
                            "str" => XmlConvert.DecodeName(value?.Value ?? ""),
                            _ => value?.Value ?? ""
                        };
                        writer.Write('"'); writer.Write(raw.Replace("\"", "\"\"")); writer.Write('"');
                    }
                    writer.WriteLine();
                    if ((row & 255) == 0) ImportProgress.Report(estimatedRows > 0 ? (double)row / estimatedRows : (double)row / (row + 1024));
                }
            }
            stamp.Validate(input);
            return new(output, selected.Name, formulas);
        }
        catch { output.Dispose(); throw; }
    }
}

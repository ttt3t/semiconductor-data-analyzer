using System.Globalization;
using System.IO;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>
/// Reads V4 PTR/MPR/FTR into the existing column/disk-backed dataset. Pass one discovers
/// definitions; pass two holds only the currently active parts before flushing to disk.
/// </summary>
public static class StdfParser
{
    // Reserved import marker, also recognized after an integrated CSV round trip.
    public const string NotApplicableValue = "STDF_NOT_APPLICABLE";

    public static CsvParseResult Parse(string path, AnalyzerConfig config, Action<string>? progress = null,
        CancellationToken cancellationToken = default, StdfImportMode mode = StdfImportMode.Standard)
        => ParseCore(path, config, progress, cancellationToken, mode, false);

    internal static CsvParseResult ParseCore(string path, AnalyzerConfig config, Action<string>? progress,
        CancellationToken cancellationToken, StdfImportMode mode, bool yieldOnly)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = AnalysisOperation.Token;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var file = SourceFileReader.Open(path);
        var stamp = new SourceFileStamp(file);
        string name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        name = Path.GetFileNameWithoutExtension(name);
        var catalog = new Catalog(mode);
        progress?.Invoke("正在读取 STDF 测试定义…");
        using (ImportProgress.Stage(0, .3, "扫描 STDF 测试定义"))
            new Pass(catalog, config, name, null, cancellationToken).Read(file);
        stamp.Validate(file);
        catalog.FinishNames();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("正在读取 STDF 芯片与测量值…");
        stamp.Validate(file);
        var projection = yieldOnly ? new YieldReadProjection(catalog.Items, config) : null;
        var store = yieldOnly ? null : new RawMeasurementStore();
        try
        {
            var pass = new Pass(catalog, config, name, store, cancellationToken, projection);
            using (ImportProgress.Stage(.3, .8, "读取 STDF 芯片与测量值"))
                pass.Read(file);
            stamp.Validate(file);
            store?.Seal();
            if (pass.Records.Count == 0 && catalog.Items.Count == 0)
                throw new InvalidDataException("STDF 没有可恢复的芯片记录或测项定义。\n" + string.Join("\n", catalog.Warnings.Messages));
            if (pass.Records.Count == 0) catalog.Warnings.Add("definitions-only", "只恢复了测项定义，没有可读芯片记录，暂无可统计的芯片数据。");
            var orderedRecords = new List<CsvRecord>(pass.Records.Count);
            bool inOrder = true;
            for (int i = 1; i < pass.Records.Count; i++)
                if (pass.Records[i].Order < pass.Records[i - 1].Order) { inOrder = false; break; }
            if (inOrder)
            {
                for (int i = 0; i < pass.Records.Count; i++) orderedRecords.Add(pass.Records[i].Record);
            }
            else
            {
                pass.Records.Sort((a, b) => a.Order.CompareTo(b.Order));
                for (int i = 0; i < pass.Records.Count; i++) orderedRecords.Add(pass.Records[i].Record);
            }
            CsvParseResult result;
            using (ImportProgress.Stage(.8, 1, "建立数值列与芯片索引"))
                result = projection != null ? projection.Complete(orderedRecords) : DatasetService.Build(catalog.Items, orderedRecords);
            MeasurementDisplay.Configure(result.Items);
            result.StdfMetadata = catalog.Metadata.Complete();
            result.ImportWarning = catalog.Warnings.Messages.Length == 0 ? "" :
                $"文件“{Path.GetFileName(path)}”已尽量恢复可读数据：\n" + string.Join("\n", catalog.Warnings.Messages);
            result.StdfWaferOrigins = StdfWaferMerge.ReadOrigins(result.StdfMetadata, result.Wafers);
            result.SourceDescription = "STDF V4" + (mode == StdfImportMode.Advantest93K ? "（93k：TSR 测试名称）" : "") +
                (catalog.HasFunctional ? "；FTR：0=Pass、1=Fail" : "") +
                (catalog.Families.Values.Any(f => f.Variants.Count > 1) ? "；不同阈值/单位已拆分测项" : "") +
                (result.ImportWarning.Length > 0 ? "；警告：部分记录缺失或异常，已尽量恢复" : "");
            progress?.Invoke($"解析完成，共 {result.Items.Count} 个测试项，{result.Records.Count} 条测试记录");
            return result;
        }
        catch { store?.Dispose(); throw; }
    }

    private readonly record struct TestKey(byte Kind, uint Number, byte Head = 0, byte Site = 0)
    {
        public TestKey Unscoped => new(Kind, Number);
    }
    private readonly record struct FamilyKey(TestKey Test, string Channel, int Occurrence, string Name = "");
    private readonly record struct Limits(double? Lower, double? Upper, string Unit);
    private struct Defaults
    {
        public Defaults() { }
        public double? Lower, Upper;
        public double? StartInput, IncrementInput;
        public string Unit = "", Description = "", InputUnit = "";
        public ushort[] Pins = Array.Empty<ushort>();
    }
    private sealed class Family
    {
        public required FamilyKey Key;
        public string Description = "";
        public Dictionary<Limits, TestItemData> Variants = new();
    }
    private sealed class WaferScope
    {
        public required string Name;
        public DateTime? StartTime, FinishTime;
        public bool Inferred;
    }
    private sealed class Catalog
    {
        public Catalog(StdfImportMode mode) { Mode = mode; Metadata = new(Warnings); }
        public StdfImportMode Mode { get; }
        public readonly List<TestItemData> Items = new();
        public readonly Dictionary<FamilyKey, Family> Families = new();
        public readonly Dictionary<TestKey, string> TestNames = new();
        public readonly Dictionary<TestKey, string> ScopedTestNames = new();
        public readonly HashSet<TestKey> AmbiguousTsrNames = new();
        public readonly HashSet<(byte Head, byte Site)> Sites = new();
        public readonly Dictionary<(byte Head, byte Site), int[]> NotApplicableColumns = new();
        public readonly Dictionary<FamilyKey, (Limits Limits, string Description)> Declarations = new();
        public readonly Dictionary<(TestKey Test, int Occurrence, Limits Limits, string Name), string> EmptyMprs = new();
        public readonly List<WaferScope> Wafers = new();
        public readonly HashSet<byte> Heads = new();
        public bool HasFunctional;
        public readonly StdfWarnings Warnings = new();
        public readonly StdfMetadataBuilder Metadata;

        public TestKey Test(byte kind, uint number, byte head, byte site) =>
            Mode == StdfImportMode.Advantest93K ? new(kind, number, head, site) : new(kind, number);

        private string ResolveTsrName(TestKey test)
        {
            // 93k writes both per-site TSRs and a final HEAD_NUM=255 / SITE_NUM=0
            // summary. Exact site metadata wins; never borrow a different site's name.
            foreach (var key in new[] { test, test with { Site = 0 }, test with { Site = 255 },
                test with { Head = 255 }, test with { Head = 255, Site = 0 }, test with { Head = 255, Site = 255 } })
            {
                if (AmbiguousTsrNames.Contains(key)) return "";
                if (ScopedTestNames.TryGetValue(key, out var name)) return name;
            }
            return "";
        }

        private void ConsolidateSiteNames()
        {
            var combined = new Dictionary<(TestKey Test, string Channel, int Occurrence, string Name), Family>();
            // A single TSR label cannot replace several distinct PTR/MPR names on one site.
            var useRecordNames = Families.Keys.GroupBy(k => k.Test)
                .Where(g => g.Select(k => k.Name).Distinct(StringComparer.Ordinal).Skip(1).Any())
                .Select(g => g.Key.Unscoped).ToHashSet();
            Items.Clear();
            foreach (var (key, family) in Families.ToArray())
            {
                string tsrName = ResolveTsrName(key.Test);
                string description = !useRecordNames.Contains(key.Test.Unscoped) && tsrName.Length > 0 ? tsrName : family.Description;
                var identity = (key.Test.Unscoped, key.Channel, key.Occurrence, description);
                if (!combined.TryGetValue(identity, out var target))
                {
                    target = new Family { Key = key, Description = description };
                    combined.Add(identity, target);
                }
                foreach (var (limits, item) in family.Variants)
                    if (target.Variants.TryAdd(limits, item))
                    {
                        item.ColumnIndex = Items.Count;
                        Items.Add(item);
                    }
                Families[key] = target;
            }
            // When the same TEST_NUM means different tests on different sites, keep
            // separate columns and mark the other site's rule as inapplicable.
            foreach (var site in Sites)
            {
                var excluded = new List<int>();
                foreach (var group in combined.Values.GroupBy(f => f.Key.Test.Unscoped))
                {
                    if (group.Select(f => f.Description).Distinct(StringComparer.Ordinal).Take(2).Count() < 2) continue;
                    var observed = Families.Where(f => f.Key.Test.Unscoped == group.Key &&
                        f.Key.Test.Head == site.Head && f.Key.Test.Site == site.Site)
                        .Select(f => f.Value.Description).ToHashSet(StringComparer.Ordinal);
                    if (observed.Count == 0 && ResolveTsrName(group.Key with { Head = site.Head, Site = site.Site }) is { Length: > 0 } name)
                        observed.Add(name);
                    if (observed.Count == 0) continue;
                    excluded.AddRange(group.Where(f => !observed.Contains(f.Description)).SelectMany(f => f.Variants.Values).Select(i => i.ColumnIndex));
                }
                if (excluded.Count > 0) NotApplicableColumns[site] = excluded.ToArray();
            }
        }

        public TestItemData Item(FamilyKey key, Limits limits, string description, bool discovering, out bool hasMultipleVariants)
        {
            if (!Families.TryGetValue(key, out var family))
            {
                if (!discovering) throw new InvalidDataException("STDF 两次读取的测项定义不一致。");
                Families.Add(key, family = new Family { Key = key });
            }
            if (discovering && family.Description.Length == 0 && description.Length > 0) family.Description = description;
            if (!family.Variants.TryGetValue(limits, out var item))
            {
                if (!discovering) throw new InvalidDataException("STDF 两次读取的测试阈值不一致。");
                item = new TestItemData { ColumnIndex = Items.Count, TestNumber = key.Test.Number.ToString(CultureInfo.InvariantCulture), StdfChannel = key.Channel,
                    Unit = limits.Unit, LowLimit = limits.Lower, HighLimit = limits.Upper, IsStdfMeasurement = true };
                family.Variants.Add(limits, item); Items.Add(item);
            }
            hasMultipleVariants = family.Variants.Count > 1;
            return item;
        }

        public void FinishNames()
        {
            foreach (var (empty, description) in EmptyMprs)
            {
                var keys = Families.Keys.Where(k => k.Test == empty.Test && k.Occurrence == empty.Occurrence && k.Name == empty.Name).ToArray();
                if (keys.Length == 0 && Mode == StdfImportMode.Advantest93K)
                {
                    string tsrName = ResolveTsrName(empty.Test);
                    keys = Families.Keys.Where(k => k.Test.Unscoped == empty.Test.Unscoped && k.Occurrence == empty.Occurrence &&
                        (k.Name == empty.Name || tsrName.Length > 0 && ResolveTsrName(k.Test) == tsrName))
                        .Select(k => k with { Test = empty.Test, Name = empty.Name }).Distinct().ToArray();
                }
                if (keys.Length == 0) keys = new[] { new FamilyKey(empty.Test, " 无结果", empty.Occurrence, empty.Name) };
                foreach (var key in keys) Item(key, empty.Limits, description, true, out _);
            }
            foreach (var (key, declaration) in Declarations)
                if (!Families.ContainsKey(key) && (Mode != StdfImportMode.Advantest93K ||
                    !Families.Keys.Any(k => k.Test.Unscoped == key.Test.Unscoped && k.Channel == key.Channel && k.Occurrence == key.Occurrence && k.Name == key.Name)) &&
                    (key.Channel != " 无结果" || !Families.Keys.Any(k => k.Test == key.Test && k.Name == key.Name)))
                    Item(key, declaration.Limits, declaration.Description, true, out _);
            // TSR may explicitly identify tests that were never executed at all.
            if (Mode == StdfImportMode.Advantest93K)
            {
                foreach (var test in ScopedTestNames.Keys.Select(t => t.Unscoped).Distinct())
                    foreach (var site in Sites)
                    {
                        var scoped = test with { Head = site.Head, Site = site.Site };
                        string name = ResolveTsrName(scoped);
                        if (name.Length > 0 && !Families.Keys.Any(k => k.Test == scoped) &&
                            !Families.Any(k => k.Key.Test.Unscoped == test && ResolveTsrName(k.Key.Test) == name))
                            Item(new(scoped, test.Kind == 15 ? " 未测结果" : "", 1),
                                test.Kind == 20 ? new(0, 0, "0=Pass; 1=Fail") : new(null, null, ""), name, true, out _);
                    }
                ConsolidateSiteNames();
            }
            else foreach (var (test, name) in TestNames)
                    if (!Families.Keys.Any(k => k.Test == test))
                        Item(new(test, test.Kind == 15 ? " 未测结果" : "", 1),
                            test.Kind == 20 ? new(0, 0, "0=Pass; 1=Fail") : new(null, null, ""), name, true, out _);
            foreach (var family in Families.Values.Distinct())
            {
                string description = family.Description.Length > 0 ? family.Description :
                    TestNames.GetValueOrDefault(family.Key.Test, "");
                string name = description.Length > 0 ? description : $"T{family.Key.Test.Number}";
                int variant = 0;
                foreach (var item in family.Variants.Values)
                {
                    variant++;
                    string suffix = family.Variants.Count > 1 ? $" [规则 {variant}]" : "";
                    item.Name = item.DisplayName = name + suffix;
                }
            }
            TestNumberLabel.Disambiguate(Items);
            foreach (var group in Items.GroupBy(i => i.Name).Where(g => g.Count() > 1))
                foreach (var item in group) item.DisplayName = $"{item.TestNumber}_{item.Name}";
        }
    }

    private sealed class Part
    {
        public long Order;
        public WaferScope Wafer = null!;
        public string Product = "", Program = "", Lot = "";
        public bool HasTests;
        public readonly Dictionary<(TestKey Test, string Name), int> Occurrences = new();
        public readonly byte[] States;
        public readonly double[] Numbers;
        public Dictionary<int, string>? InvalidTexts;
        public readonly HashSet<FamilyKey> Families = new();

        public Part(int itemCount)
        {
            States = itemCount > 0 ? new byte[itemCount] : Array.Empty<byte>();
            Numbers = itemCount > 0 ? new double[itemCount] : Array.Empty<double>();
        }

        public void Reset()
        {
            HasTests = false;
            Occurrences.Clear();
            if (States.Length > 0) Array.Clear(States, 0, States.Length);
            InvalidTexts?.Clear();
            Families.Clear();
        }
    }

    private sealed class Pass
    {
        private readonly Catalog _catalog;
        private readonly AnalyzerConfig _config;
        private readonly string _fileName;
        private readonly RawMeasurementStore? _store;
        private readonly YieldReadProjection? _projection;
        private readonly CancellationToken _token;
        private readonly MeasurementSchema? _schema;
        private readonly Dictionary<TestKey, Defaults> _defaults = new();
        private readonly Dictionary<(byte Head, byte Site), Part> _parts = new();
        private readonly Stack<Part> _partPool = new();
        private readonly Dictionary<(byte Head, byte Site), byte> _siteGroups = new();
        private readonly Dictionary<(byte Head, byte Group), WaferScope> _wafers = new();
        private string _product = "", _program = "", _lot = "";
        private long _order;
        private int _waferIndex;
        private bool _mir, _mrr;
        private bool Discovering => _store == null && _projection == null;
        public readonly List<(long Order, CsvRecord Record)> Records = new();

        public Pass(Catalog catalog, AnalyzerConfig config, string fileName, RawMeasurementStore? store, CancellationToken token,
            YieldReadProjection? projection = null)
        {
            _catalog = catalog; _config = config; _fileName = fileName; _store = store; _token = token;
            _product = config.Product; _program = config.Program; _lot = config.Lot;
            _projection = projection;
            if (store != null) _schema = new MeasurementSchema(catalog.Items.Select(i => i.Id));
        }

        private Part RentPart(long order, WaferScope wafer, string product, string program, string lot)
        {
            var part = _partPool.Count > 0 ? _partPool.Pop() : new Part(Discovering ? 0 : _catalog.Items.Count);
            part.Order = order;
            part.Wafer = wafer;
            part.Product = product;
            part.Program = program;
            part.Lot = lot;
            return part;
        }

        private void ReleasePart(Part part)
        {
            part.Reset();
            _partPool.Push(part);
        }

        public void Read(FileStream file)
        {
            using var reader = new StdfReader(file, Discovering ? _catalog.Warnings : null);
            int progressRecords = 0;
            while (reader.Next())
            {
                _token.ThrowIfCancellationRequested();
                // For gzip, use compressed bytes consumed; no decompression or extra scan for progress.
                if ((++progressRecords & 4095) == 0) ImportProgress.Report((double)file.Position / Math.Max(1, file.Length));
                var fields = reader.Fields;
                try
                {
                    if (_mrr)
                    {
                        Warn("after-mrr", "MRR 之后仍有记录，继续读取可识别部分，请核对文件是否含多个测试段。");
                        _mrr = false;
                    }
                    if (Discovering) _catalog.Metadata.Read(reader.Type, reader.Subtype, fields, reader.Offset);
                    switch ((reader.Type, reader.Subtype))
                    {
                        case (0, 10): break; // The reader already checked that the encoding remains compatible.
                        case (1, 10): Mir(ref fields); break;
                        case (1, 20): _mrr = true; fields.OptionalU4(); fields.OptionalU1(); break;
                        case (1, 80): Sdr(ref fields); break;
                        case (2, 10): Wir(ref fields); break;
                        case (2, 20): Wrr(ref fields); break;
                        case (5, 10): Pir(ref fields); break;
                        case (5, 20): Prr(ref fields); break;
                        case (10, 30): Tsr(ref fields); break;
                        case (15, 10): Ptr(ref fields); break;
                        case (15, 15): Mpr(ref fields); break;
                        case (15, 20): Ftr(ref fields); break;
                        case (15, _): Warn($"unsupported:15/{reader.Subtype}", $"暂不支持测试记录 15/{reader.Subtype}，已跳过该类记录，其他可读结果继续导入。"); break;
                        // Summary/pin/pattern/annotation records are not individual measurements.
                    }
                }
                catch (InvalidDataException ex)
                {
                    string key = $"record:{reader.Type}/{reader.Subtype}:{ex.Message}";
                    string message = $"记录 {reader.Type}/{reader.Subtype}（截至字节 {reader.Offset}）异常，已略过不可读部分：{ex.Message}";
                    if (Discovering) _catalog.Warnings.Add(key, message);
                    else _catalog.Warnings.AddOnce(key, message);
                }
            }
            foreach (var entry in _parts.ToArray())
            {
                _token.ThrowIfCancellationRequested();
                _parts.Remove(entry.Key);
                RecoverPart(entry.Key.Head, entry.Key.Site, entry.Value);
            }
        }

        private void Warn(string key, string message)
        { if (Discovering) _catalog.Warnings.Add(key, message); }

        private Part StartPart(byte head, byte site)
        {
            _catalog.Heads.Add(head); _catalog.Sites.Add((head, site));
            return RentPart(_order++, Wafer(head, site), _product, _program, _lot);
        }

        private void RecoverPart(byte head, byte site, Part part)
        {
            if (!part.HasTests) { ReleasePart(part); return; }
            Warn("missing-prr", $"芯片缺少可读 PRR（如 Head {head} / Site {site}），已保留测量值；HBIN、SBIN、坐标等未提供字段留空，不能作为已知良品。");
            CompletePart(head, site, part);
        }

        private void CloseParts(WaferScope scope)
        {
            foreach (var entry in _parts.Where(p => ReferenceEquals(p.Value.Wafer, scope)).ToArray())
            {
                _parts.Remove(entry.Key); RecoverPart(entry.Key.Head, entry.Key.Site, entry.Value);
            }
        }

        private void Mir(ref StdfFields f)
        {
            if (!_mir && _order > 0) Warn("late-mir", "MIR 晚于芯片记录，已保留此前结果，缺失批次等信息沿用配置。");
            if (_mir)
            {
                foreach (var entry in _parts.ToArray()) { _parts.Remove(entry.Key); RecoverPart(entry.Key.Head, entry.Key.Site, entry.Value); }
                _wafers.Clear(); _siteGroups.Clear(); _defaults.Clear();
            }
            _mir = true;
            _lot = _config.Lot; _product = _config.Product; _program = _config.Program;
            f.Skip(15);
            string lot = (f.OptionalCn() ?? "").Trim(), product = (f.OptionalCn() ?? "").Trim(); f.OptionalSkipCn(); f.OptionalSkipCn();
            string job = (f.OptionalCn() ?? "").Trim(), revision = (f.OptionalCn() ?? "").Trim();
            _lot = lot.Length > 0 ? lot : _config.Lot;
            _product = product.Length > 0 ? product : _config.Product;
            _program = job.Length > 0 ? job + (revision.Length > 0 ? " @ " + revision : "") : _config.Program;
            _mir = true;
        }

        private void Sdr(ref StdfFields f)
        {
            byte head = f.U1(), group = f.U1(), count = f.U1();
            for (int i = 0; i < count; i++) _siteGroups[(head, f.U1())] = group;
        }

        private WaferScope NewWafer(byte head, byte group, string name, bool inferred = false)
        {
            WaferScope scope;
            if (Discovering)
            {
                scope = new WaferScope { Name = name.Length > 0 ? name : $"{_fileName} (wafer {_waferIndex + 1})", Inferred = inferred };
                _catalog.Wafers.Add(scope);
            }
            else scope = _catalog.Wafers[_waferIndex]; // WRR may have corrected the WIR name in pass one.
            _waferIndex++;
            _wafers[(head, group)] = scope;
            return scope;
        }

        private void Wir(ref StdfFields f)
        {
            byte head = f.U1(), group = f.U1(); var start = StdfMetadataBuilder.Time(f.U4()); string name = (f.OptionalCn() ?? "").Trim();
            if (group != 255 && _wafers.TryGetValue((head, 255), out var inferred) && inferred.Inferred)
            {
                Warn("late-wir", "部分芯片早于 WIR，已保留在原临时晶圆，新 WIR 后的芯片使用实际晶圆归属。");
                CloseParts(inferred); _wafers.Remove((head, 255));
            }
            if (_wafers.TryGetValue((head, group), out var previous))
            { Warn("missing-wrr", "缺少 WRR 便出现新 WIR，已保留前一片可读数据并开始下一片。"); CloseParts(previous); }
            var scope = NewWafer(head, group, name);
            if (Discovering) scope.StartTime = start;
        }

        private void Wrr(ref StdfFields f)
        {
            byte head = f.U1(), group = f.U1(); var finish = StdfMetadataBuilder.Time(f.U4()); f.U4();
            for (int i = 0; i < 4; i++) f.OptionalU4();
            string name = (f.OptionalCn() ?? "").Trim();
            if (!_wafers.Remove((head, group), out var scope))
            {
                Warn("missing-wir", "WRR 没有对应 WIR，按已有芯片和 WRR 尽量恢复晶圆；无法确定的片号沿用文件名。");
                var candidates = _wafers.Where(w => w.Key.Head == head).ToArray();
                if (candidates.Length == 1 && candidates[0].Value.Inferred)
                { scope = candidates[0].Value; _wafers.Remove(candidates[0].Key); }
                else { scope = NewWafer(head, group, name); _wafers.Remove((head, group)); }
            }
            CloseParts(scope);
            if (Discovering && name.Length > 0) scope.Name = name;
            if (Discovering) scope.FinishTime = finish;
        }

        private WaferScope Wafer(byte head, byte site)
        {
            if (_siteGroups.TryGetValue((head, site), out var group) && _wafers.TryGetValue((head, group), out var scope)) return scope;
            if (_wafers.TryGetValue((head, 255), out scope)) return scope;
            var candidates = _wafers.Where(w => w.Key.Head == head).Select(w => w.Value).ToArray();
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1)
            {
                Warn("ambiguous-wafer", "缺少可确定 Site Group 的 SDR，无法确定归属的芯片放入独立未知晶圆，未猜测其片号。");
                return NewWafer(head, 255, $"{_fileName} (Head {head} 未知晶圆)", inferred: true);
            }
            return NewWafer(head, 255, _fileName, inferred: true); // Final test commonly has no WIR/WRR or XY.
        }

        private void Pir(ref StdfFields f)
        {
            byte head = f.U1(), site = f.U1();
            if (_parts.Remove((head, site), out var previous)) RecoverPart(head, site, previous);
            _parts.Add((head, site), StartPart(head, site));
        }

        private void Prr(ref StdfFields f)
        {
            byte head = f.U1(), site = f.U1();
            ushort? hbin = null, sbin = null; short? x = null, y = null; string sn = "";
            try
            {
                f.OptionalU1(); f.OptionalU2(); hbin = f.OptionalU2();
                sbin = f.OptionalU2(); x = f.OptionalI2(); y = f.OptionalI2(); f.OptionalU4();
                sn = (f.OptionalCn() ?? "").Trim(); f.OptionalSkipCn(); f.OptionalBytes();
            }
            catch (InvalidDataException ex) { Warn("partial-prr", "PRR 字段不完整，已保留可读的 BIN、坐标和芯片测量值：" + ex.Message); }
            if (!hbin.HasValue) Warn("prr-no-hbin", "部分 PRR 缺少 HBIN，保留芯片记录，HBIN 留空。");
            if (!_parts.Remove((head, site), out var part))
            {
                Warn("prr-no-pir", "PRR 没有对应 PIR，按 PRR 恢复芯片身份与 BIN，缺少的测量值留空。");
                part = StartPart(head, site);
            }
            if (part.Wafer.Inferred && (x is not null and not short.MinValue || y is not null and not short.MinValue))
                Warn("coordinates-no-wir", "有坐标芯片缺少可确定归属的 WIR，临时晶圆号沿用文件名；若后续有对应 WRR，则采用 WRR 片号。");
            CompletePart(head, site, part, hbin, sbin, x, y, sn);
        }

        private void CompletePart(byte head, byte site, Part part, ushort? hbin = null, ushort? sbin = null,
            short? x = null, short? y = null, string sn = "")
        {
            if (Discovering)
            {
                ReleasePart(part);
                return;
            }
            if (_catalog.NotApplicableColumns.TryGetValue((head, site), out var excluded))
                foreach (int column in excluded)
                    if (part.States[column] == RawMeasurementStore.StateAbsent)
                        part.States[column] = RawMeasurementStore.StateNotApplicable;
            // A test executed with one rule does not require all alternate rules too.
            foreach (var key in part.Families)
                foreach (var item in _catalog.Families[key].Variants.Values)
                    if (part.States[item.ColumnIndex] == RawMeasurementStore.StateAbsent)
                        part.States[item.ColumnIndex] = RawMeasurementStore.StateNotApplicable;
            var record = new CsvRecord
            {
                WaferId = part.Wafer.Name, SerialNumber = sn, Site = _catalog.Heads.Count > 1 ? $"{head}:{site}" : site.ToString(CultureInfo.InvariantCulture),
                X = x is null or short.MinValue ? null : x, Y = y is null or short.MinValue ? null : y,
                HBin = hbin is null or ushort.MaxValue ? "" : hbin.Value.ToString(CultureInfo.InvariantCulture), SBin = sbin is null or ushort.MaxValue ? "" : sbin.Value.ToString(CultureInfo.InvariantCulture),
                Product = part.Product, Program = part.Program, Lot = part.Lot,
                StdfTestTime = part.Wafer.StartTime ?? _catalog.Metadata.LotStart ?? part.Wafer.FinishTime ?? _catalog.Metadata.LotFinish
            };
            if (_projection != null) _projection.CaptureStdf(record, part.States, part.Numbers, part.InvalidTexts);
            else record.Measurements = _store!.AppendStdf(_schema!, part.States, part.Numbers, part.InvalidTexts, NotApplicableValue);
            Records.Add((part.Order, record));
            ReleasePart(part);
        }

        private void Tsr(ref StdfFields f)
        {
            byte head = f.U1(), site = f.U1(), type = f.U1(); uint number = f.U4();
            for (int i = 0; i < 3; i++) f.OptionalU4();
            string name = (f.OptionalCn() ?? "").Trim();
            byte kind = type switch { (byte)'P' => 10, (byte)'M' => 15, (byte)'F' => 20, _ => 0 };
            if (Discovering && name.Length > 0 && kind != 0)
            {
                _catalog.TestNames.TryAdd(new(kind, number), name);
                var key = new TestKey(kind, number, head, site);
                if (_catalog.Mode == StdfImportMode.Advantest93K &&
                    _catalog.ScopedTestNames.TryGetValue(key, out var previous) && previous != name)
                    _catalog.AmbiguousTsrNames.Add(key);
                _catalog.ScopedTestNames.TryAdd(key, name);
            }
        }

        private (Part? Part, int Occurrence) Execution(TestKey key, byte head, byte site, byte flags, byte parameterFlags = 0, string name = "")
        {
            if (!_parts.TryGetValue((head, site), out var part))
            {
                if ((flags & 0x10) != 0 && parameterFlags == 0) return (null, 1); // Definition-only record.
                Warn("test-no-pir", "测试结果缺少对应 PIR，按 Head / Site 建立芯片记录；仍使用后续 PRR 提供的身份和 BIN。");
                _parts[(head, site)] = part = StartPart(head, site);
            }
            var identity = (key, name);
            int count = part.Occurrences.GetValueOrDefault(identity) + 1;
            part.Occurrences[identity] = count;
            return (part, count);
        }

        private static string Inherit(string? text, string fallback) => string.IsNullOrEmpty(text) ? fallback : text == "\0" ? "" : text.Trim();
        private Defaults Default(TestKey key) => _defaults.TryGetValue(key.Unscoped, out var value) ? value : new Defaults();

        private static double? Limit(float? value, byte? optional, int invalidBit, int noneBit, double? fallback)
        {
            if (optional.HasValue && (optional.Value & (1 << noneBit)) != 0) return null;
            if (!optional.HasValue || (optional.Value & (1 << invalidBit)) != 0 || value == null) return fallback;
            if (!float.IsFinite(value.Value)) throw new InvalidDataException("有效上下限不是有限数值。");
            return value.Value;
        }

        private static Limits Bounds(Defaults data, byte parm)
        {
            // The common pipeline uses inclusive comparisons. One double ULP maps
            // STDF's strict boundary to that convention without changing any R*4 value.
            double? low = data.Lower, high = data.Upper;
            if (low.HasValue && (parm & 0x40) == 0) low = Math.BitIncrement(low.Value);
            if (high.HasValue && (parm & 0x80) == 0) high = Math.BitDecrement(high.Value);
            if (low > high) throw new InvalidDataException("测试上下限及边界包含标志定义了空区间。");
            return new(low, high, data.Unit);
        }

        private static Defaults ReadLimits(ref StdfFields f, Defaults initial, bool mpr, int pins)
        {
            byte? optional = f.OptionalU1();
            // RESULT and limits are already in base UNITS. *_SCAL are display-only.
            f.OptionalU1(); f.OptionalU1(); f.OptionalU1();
            float? low = f.OptionalR4(), high = f.OptionalR4();
            ushort[] indices = initial.Pins;
            double? start = initial.StartInput, increment = initial.IncrementInput;
            if (mpr)
            {
                float? suppliedStart = f.OptionalR4(), suppliedIncrement = f.OptionalR4();
                if (optional.HasValue && (optional.Value & 2) == 0)
                {
                    start = suppliedStart ?? start; increment = suppliedIncrement ?? increment;
                    if (start.HasValue && !double.IsFinite(start.Value) || increment.HasValue && !double.IsFinite(increment.Value))
                        throw new InvalidDataException("MPR 输入条件不是有限数值。");
                }
                var supplied = f.Indices(pins); if (supplied.Length > 0) indices = supplied;
            }
            string unit = Inherit(f.OptionalCn(), initial.Unit);
            string inputUnit = mpr ? Inherit(f.OptionalCn(), initial.InputUnit) : "";
            f.OptionalSkipCn(); f.OptionalSkipCn(); f.OptionalSkipCn(); f.OptionalR4(); f.OptionalR4();
            return new Defaults { Lower = Limit(low, optional, 4, 6, initial.Lower), Upper = Limit(high, optional, 5, 7, initial.Upper),
                Unit = unit, Pins = indices, Description = initial.Description,
                StartInput = start, IncrementInput = increment, InputUnit = inputUnit };
        }

        private void Value(Part? part, FamilyKey family, Limits limits, string description, byte flags, byte parm, float? value, bool functional = false)
        {
            if (part == null)
            {
                if (Discovering) _catalog.Declarations.TryAdd(family, (limits, description));
                return;
            }
            var item = _catalog.Item(family, limits, description, Discovering, out bool hasMultipleVariants);
            part.HasTests = true;
            if (Discovering) return;
            int col = item.ColumnIndex;
            if (part.States[col] != RawMeasurementStore.StateAbsent)
                throw new ArgumentException("同一芯片内测试项列索引重复。");
            if ((flags & 0x10) != 0)
            {
                part.States[col] = RawMeasurementStore.StateEmpty; // Explicitly untested; never insert zero.
            }
            else if ((flags & (functional ? 0x6d : 0x2f)) != 0 || (parm & 7) != 0 || value == null || !float.IsFinite(value.Value))
            {
                part.States[col] = RawMeasurementStore.StateText;
                (part.InvalidTexts ??= new())[col] =
                    $"STDF_INVALID(TEST_FLG=0x{flags:X2},PARM_FLG=0x{parm:X2},RESULT={value?.ToString("R", CultureInfo.InvariantCulture) ?? "absent"})";
            }
            else
            {
                part.States[col] = RawMeasurementStore.StateNumeric;
                part.Numbers[col] = value.Value;
            }
            if (hasMultipleVariants) part.Families.Add(family);
        }

        private void Ptr(ref StdfFields f)
        {
            uint number = f.U4(); byte head = f.U1(), site = f.U1(), flags = f.U1(), parm = f.U1();
            var key = _catalog.Test(10, number, head, site);
            float? result = f.OptionalR4();
            if (result == null && (flags & 0x12) == 0) Warn("ptr-no-result", "PTR 缺少 RESULT，保留测项并标为无效测量，不填零。");
            string? text = f.OptionalCn(); f.OptionalSkipCn();
            var initial = Default(key); var definition = ReadLimits(ref f, initial, false, 0);
            definition.Description = Inherit(text, initial.Description);
            _defaults.TryAdd(key.Unscoped, definition); // Overrides apply only to this record, never replace the defaults.
            var execution = Execution(key, head, site, flags, parm, definition.Description);
            Value(execution.Part, new(key, "", execution.Occurrence, definition.Description), Bounds(definition, execution.Part == null ? (byte)0xc0 : parm), definition.Description, flags, parm, result);
        }

        private void Mpr(ref StdfFields f)
        {
            uint number = f.U4(); byte head = f.U1(), site = f.U1(), flags = f.U1(), parm = f.U1();
            var key = _catalog.Test(15, number, head, site);
            int pins = f.OptionalU2() ?? 0, count = f.OptionalU2() ?? 0;
            f.Skip((pins + 1) / 2);
            if (count * 4 > f.Remaining) throw new InvalidDataException("MPR RTN_RSLT 数组不完整。");
            Span<float> results = count <= 64 ? stackalloc float[count] : new float[count];
            for (int i = 0; i < count; i++) results[i] = f.R4();
            string? text = f.OptionalCn(); f.OptionalSkipCn();
            var initial = Default(key); var definition = ReadLimits(ref f, initial, true, pins);
            definition.Description = Inherit(text, initial.Description);
            _defaults.TryAdd(key.Unscoped, definition);
            var execution = Execution(key, head, site, flags, parm, definition.Description);
            var limits = Bounds(definition, execution.Part == null ? (byte)0xc0 : parm);
            if (count == 0 && execution.Part != null)
            {
                if (Discovering) _catalog.EmptyMprs.TryAdd((key, execution.Occurrence, limits, definition.Description), definition.Description);
                else
                    foreach (var family in _catalog.Families.Keys.Where(k => k.Test == key && k.Occurrence == execution.Occurrence && k.Name == definition.Description))
                        Value(execution.Part, family, limits, definition.Description, flags, parm, null);
                return;
            }
            bool usePins = count > 0 && pins == count && definition.Pins.Length == pins;
            Dictionary<ushort, int>? pinCounts = usePins ? new Dictionary<ushort, int>(pins) : null;
            for (int i = 0; i < Math.Max(1, count); i++)
            {
                string channel = $" 结果 {i + 1}";
                if (usePins)
                {
                    ushort pin = definition.Pins[i]; int repeat = pinCounts!.GetValueOrDefault(pin) + 1; pinCounts![pin] = repeat;
                    channel = $" PMR {pin}" + (repeat > 1 ? $" #{repeat}" : "");
                }
                else if (count == 0) channel = " 无结果";
                else if (definition.StartInput is double start && definition.IncrementInput is double increment)
                    channel += " @ " + (start + i * increment).ToString("R", CultureInfo.InvariantCulture) + " " + definition.InputUnit;
                Value(execution.Part, new(key, channel, execution.Occurrence, definition.Description), limits, definition.Description, flags, parm,
                    count == 0 ? null : results[i]);
            }
        }

        private void Ftr(ref StdfFields f)
        {
            uint number = f.U4(); byte head = f.U1(), site = f.U1(), flags = f.U1(); f.OptionalU1();
            var key = _catalog.Test(20, number, head, site);
            for (int i = 0; i < 6; i++) f.OptionalU4();
            f.OptionalI2(); int returns = f.OptionalU2() ?? 0, programs = f.OptionalU2() ?? 0;
            if (returns > 0) { f.Skip(returns * 2); f.Skip((returns + 1) / 2); }
            if (programs > 0) { f.Skip(programs * 2); f.Skip((programs + 1) / 2); }
            f.OptionalBits();
            string? vector = f.OptionalCn(); f.OptionalSkipCn(); f.OptionalSkipCn(); string? text = f.OptionalCn();
            f.OptionalSkipCn(); f.OptionalSkipCn(); f.OptionalSkipCn(); f.OptionalU1(); f.OptionalBits();
            var initial = Default(key);
            string description = Inherit(text, Inherit(vector, initial.Description));
            _defaults.TryAdd(key.Unscoped, new Defaults { Description = description });
            var execution = Execution(key, head, site, flags, name: description);
            _catalog.HasFunctional = true;
            Value(execution.Part, new(key, "", execution.Occurrence, description), new Limits(0, 0, "0=Pass; 1=Fail"),
                description, flags, 0, (flags & 0x80) != 0 ? 1 : 0, functional: true);
        }
    }
}

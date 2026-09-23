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
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        string name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        name = Path.GetFileNameWithoutExtension(name);
        var catalog = new Catalog(mode);
        progress?.Invoke("正在读取 STDF 测试定义…");
        new Pass(catalog, config, name, null, cancellationToken).Read(file);
        catalog.FinishNames();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("正在读取 STDF 芯片与测量值…");
        var store = new RawMeasurementStore();
        try
        {
            var pass = new Pass(catalog, config, name, store, cancellationToken);
            pass.Read(file);
            store.Seal();
            if (pass.Records.Count == 0) throw new InvalidDataException("STDF 不包含完成的芯片记录（PRR）。");
            var result = DatasetService.Build(catalog.Items, pass.Records.OrderBy(r => r.Order).Select(r => r.Record).ToList());
            result.SourceDescription = "STDF V4" + (mode == StdfImportMode.Advantest93K ? "（93k：TSR 测试名称）" : "") +
                (catalog.HasFunctional ? "；FTR：0=Pass、1=Fail" : "") +
                (catalog.Families.Values.Any(f => f.Variants.Count > 1) ? "；不同阈值/单位已拆分测项" : "");
            progress?.Invoke($"解析完成，共 {result.Items.Count} 个测试项，{result.Records.Count} 条测试记录");
            return result;
        }
        catch { store.Dispose(); throw; }
    }

    private readonly record struct TestKey(byte Kind, uint Number, byte Head = 0, byte Site = 0)
    {
        public TestKey Unscoped => new(Kind, Number);
    }
    private readonly record struct FamilyKey(TestKey Test, string Channel, int Occurrence);
    private readonly record struct Limits(double? Lower, double? Upper, string Unit);
    private sealed class Defaults
    {
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
    }
    private sealed class Catalog
    {
        public Catalog(StdfImportMode mode) => Mode = mode;
        public StdfImportMode Mode { get; }
        public readonly List<TestItemData> Items = new();
        public readonly Dictionary<FamilyKey, Family> Families = new();
        public readonly Dictionary<TestKey, string> TestNames = new();
        public readonly Dictionary<TestKey, string> ScopedTestNames = new();
        public readonly HashSet<(byte Head, byte Site)> Sites = new();
        public readonly Dictionary<(byte Head, byte Site), int[]> NotApplicableColumns = new();
        public readonly Dictionary<FamilyKey, (Limits Limits, string Description)> Declarations = new();
        public readonly Dictionary<(TestKey Test, int Occurrence, Limits Limits), string> EmptyMprs = new();
        public readonly List<WaferScope> Wafers = new();
        public readonly HashSet<byte> Heads = new();
        public bool HasFunctional;

        public TestKey Test(byte kind, uint number, byte head, byte site) =>
            Mode == StdfImportMode.Advantest93K ? new(kind, number, head, site) : new(kind, number);

        private string ResolveTsrName(TestKey test)
        {
            // 93k writes both per-site TSRs and a final HEAD_NUM=255 / SITE_NUM=0
            // summary. Exact site metadata wins; never borrow a different site's name.
            foreach (var key in new[] { test, test with { Site = 0 }, test with { Site = 255 },
                test with { Head = 255 }, test with { Head = 255, Site = 0 }, test with { Head = 255, Site = 255 } })
                if (ScopedTestNames.TryGetValue(key, out var name)) return name;
            return "";
        }

        private void ConsolidateSiteNames()
        {
            var combined = new Dictionary<(TestKey Test, string Channel, int Occurrence, string Name), Family>();
            Items.Clear();
            foreach (var (key, family) in Families.ToArray())
            {
                string tsrName = ResolveTsrName(key.Test);
                string description = tsrName.Length > 0 ? tsrName : family.Description;
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
                    string name = ResolveTsrName(group.Key with { Head = site.Head, Site = site.Site });
                    if (name.Length == 0)
                    {
                        var observed = Families.Where(f => f.Key.Test.Unscoped == group.Key &&
                            f.Key.Test.Head == site.Head && f.Key.Test.Site == site.Site)
                            .Select(f => f.Value.Description).Distinct(StringComparer.Ordinal).ToArray();
                        if (observed.Length == 1) name = observed[0];
                    }
                    if (name.Length == 0) continue;
                    excluded.AddRange(group.Where(f => f.Description != name).SelectMany(f => f.Variants.Values).Select(i => i.ColumnIndex));
                }
                if (excluded.Count > 0) NotApplicableColumns[site] = excluded.ToArray();
            }
        }

        public TestItemData Item(FamilyKey key, Limits limits, string description, bool discovering)
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
                item = new TestItemData { ColumnIndex = Items.Count, TestNumber = key.Test.Number.ToString(CultureInfo.InvariantCulture),
                    Unit = limits.Unit, LowLimit = limits.Lower, HighLimit = limits.Upper };
                family.Variants.Add(limits, item); Items.Add(item);
            }
            return item;
        }

        public void FinishNames()
        {
            foreach (var (empty, description) in EmptyMprs)
            {
                var keys = Families.Keys.Where(k => k.Test == empty.Test && k.Occurrence == empty.Occurrence).ToArray();
                if (keys.Length == 0 && Mode == StdfImportMode.Advantest93K)
                    keys = Families.Keys.Where(k => k.Test.Unscoped == empty.Test.Unscoped && k.Occurrence == empty.Occurrence &&
                        ResolveTsrName(k.Test) == ResolveTsrName(empty.Test))
                        .Select(k => k with { Test = empty.Test }).Distinct().ToArray();
                if (keys.Length == 0) keys = new[] { new FamilyKey(empty.Test, " 无结果", empty.Occurrence) };
                foreach (var key in keys) Item(key, empty.Limits, description, true);
            }
            foreach (var (key, declaration) in Declarations)
                if (!Families.ContainsKey(key) && (Mode != StdfImportMode.Advantest93K ||
                    !Families.Keys.Any(k => k.Test.Unscoped == key.Test.Unscoped && k.Channel == key.Channel && k.Occurrence == key.Occurrence)) &&
                    (key.Channel != " 无结果" || !Families.Keys.Any(k => k.Test == key.Test)))
                    Item(key, declaration.Limits, declaration.Description, true);
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
                                test.Kind == 20 ? new(0, 0, "0=Pass; 1=Fail") : new(null, null, ""), name, true);
                    }
                ConsolidateSiteNames();
            }
            else foreach (var (test, name) in TestNames)
                    if (!Families.Keys.Any(k => k.Test == test))
                        Item(new(test, test.Kind == 15 ? " 未测结果" : "", 1),
                            test.Kind == 20 ? new(0, 0, "0=Pass; 1=Fail") : new(null, null, ""), name, true);
            foreach (var family in Families.Values.Distinct())
            {
                string kind = family.Key.Test.Kind switch { 10 => "PTR", 15 => "MPR", _ => "FTR" };
                string description = family.Description.Length > 0 ? family.Description :
                    TestNames.GetValueOrDefault(family.Key.Test, "");
                string name = description.Length > 0 ? description : $"T{family.Key.Test.Number}";
                if (family.Key.Test.Kind != 10) name += $" [{kind}{family.Key.Channel}]";
                if (family.Key.Occurrence > 1) name += $" [重复 {family.Key.Occurrence}]";
                int variant = 0;
                foreach (var item in family.Variants.Values)
                {
                    variant++;
                    string suffix = family.Variants.Count > 1 ? $" [规则 {variant}]" : "";
                    item.Name = item.DisplayName = name + suffix;
                }
            }
            foreach (var group in Items.GroupBy(i => i.Name).Where(g => g.Count() > 1))
                foreach (var item in group) item.DisplayName = $"{item.TestNumber}_{item.Name}";
        }
    }

    private sealed class Part
    {
        public required long Order;
        public required WaferScope Wafer;
        public required string Product, Program, Lot;
        public readonly Dictionary<TestKey, int> Occurrences = new();
        public readonly Dictionary<int, string> Values = new();
        public readonly HashSet<FamilyKey> Families = new();
    }

    private sealed class Pass
    {
        private readonly Catalog _catalog;
        private readonly AnalyzerConfig _config;
        private readonly string _fileName;
        private readonly RawMeasurementStore? _store;
        private readonly CancellationToken _token;
        private readonly MeasurementSchema? _schema;
        private readonly Dictionary<TestKey, Defaults> _defaults = new();
        private readonly Dictionary<(byte Head, byte Site), Part> _parts = new();
        private readonly Dictionary<(byte Head, byte Site), byte> _siteGroups = new();
        private readonly Dictionary<(byte Head, byte Group), WaferScope> _wafers = new();
        private string _product = "", _program = "", _lot = "";
        private long _order;
        private int _waferIndex;
        private bool _mir, _mrr;
        private bool Discovering => _store == null;
        public readonly List<(long Order, CsvRecord Record)> Records = new();

        public Pass(Catalog catalog, AnalyzerConfig config, string fileName, RawMeasurementStore? store, CancellationToken token)
        {
            _catalog = catalog; _config = config; _fileName = fileName; _store = store; _token = token;
            if (store != null) _schema = new MeasurementSchema(catalog.Items.Select(i => i.Id));
        }

        public void Read(FileStream file)
        {
            using var reader = new StdfReader(file);
            while (reader.Next())
            {
                _token.ThrowIfCancellationRequested();
                var fields = reader.Fields;
                try
                {
                    if (_mrr) throw new InvalidDataException("MRR 之后仍有记录，文件不是单个完整的 STDF 数据流。");
                    switch ((reader.Type, reader.Subtype))
                    {
                        case (0, 10): throw new InvalidDataException("文件中出现重复 FAR，不支持直接拼接二进制 STDF 文件。");
                        case (1, 10): Mir(ref fields); break;
                        case (1, 20): fields.U4(); fields.OptionalU1(); _mrr = true; break;
                        case (1, 80): Sdr(ref fields); break;
                        case (2, 10): Wir(ref fields); break;
                        case (2, 20): Wrr(ref fields); break;
                        case (5, 10): Pir(ref fields); break;
                        case (5, 20): Prr(ref fields); break;
                        case (10, 30): Tsr(ref fields); break;
                        case (15, 10): Ptr(ref fields); break;
                        case (15, 15): Mpr(ref fields); break;
                        case (15, 20): Ftr(ref fields); break;
                        case (15, _): throw new InvalidDataException($"尚不支持测试记录 15/{reader.Subtype}（例如扫描扩展 STR），为避免漏算测试结果，未导入此文件。");
                        // Summary/pin/pattern/annotation records are not individual measurements.
                    }
                }
                catch (InvalidDataException ex)
                { throw new InvalidDataException($"STDF 记录 {reader.Type}/{reader.Subtype}（截至字节 {reader.Offset}）：{ex.Message}", ex); }
            }
            if (!_mir || !_mrr) throw new InvalidDataException("STDF 缺少 MIR/MRR，文件可能仍在写入或已截断，未导入不完整结果。");
            if (_parts.Count > 0) throw new InvalidDataException("STDF 有 PIR 尚未对应 PRR，未导入不完整芯片记录。");
        }

        private void Mir(ref StdfFields f)
        {
            if (_mir) throw new InvalidDataException("同一数据流包含多个 MIR。");
            f.Skip(15);
            string lot = f.Cn().Trim(), product = f.Cn().Trim(); f.Cn(); f.Cn();
            string job = f.Cn().Trim(), revision = (f.OptionalCn() ?? "").Trim();
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

        private WaferScope NewWafer(byte head, byte group, string name)
        {
            WaferScope scope;
            if (Discovering)
            {
                scope = new WaferScope { Name = name.Length > 0 ? name : $"{_fileName} (wafer {_waferIndex + 1})" };
                _catalog.Wafers.Add(scope);
            }
            else scope = _catalog.Wafers[_waferIndex]; // WRR may have corrected the WIR name in pass one.
            _waferIndex++;
            _wafers[(head, group)] = scope;
            return scope;
        }

        private void Wir(ref StdfFields f)
        {
            byte head = f.U1(), group = f.U1(); f.U4(); string name = (f.OptionalCn() ?? "").Trim();
            if (_wafers.ContainsKey((head, group))) throw new InvalidDataException("WIR 尚未结束便出现同一 Head/Site Group 的新 WIR。");
            NewWafer(head, group, name);
        }

        private void Wrr(ref StdfFields f)
        {
            byte head = f.U1(), group = f.U1(); f.U4(); f.U4();
            for (int i = 0; i < 4; i++) f.OptionalU4();
            string name = (f.OptionalCn() ?? "").Trim();
            if (!_wafers.Remove((head, group), out var scope)) throw new InvalidDataException("WRR 没有对应 WIR。");
            if (_parts.Values.Any(p => ReferenceEquals(p.Wafer, scope))) throw new InvalidDataException("WRR 出现时仍有未完成的芯片。");
            if (Discovering && name.Length > 0) scope.Name = name;
        }

        private WaferScope Wafer(byte head, byte site)
        {
            if (_siteGroups.TryGetValue((head, site), out var group) && _wafers.TryGetValue((head, group), out var scope)) return scope;
            if (_wafers.TryGetValue((head, 255), out scope)) return scope;
            var candidates = _wafers.Where(w => w.Key.Head == head).Select(w => w.Value).ToArray();
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1) throw new InvalidDataException("同一 Head 有多个晶圆，缺少可确定 Site Group 的 SDR。");
            return NewWafer(head, 255, _fileName); // Final test commonly has no WIR/WRR or XY.
        }

        private void Pir(ref StdfFields f)
        {
            if (!_mir) throw new InvalidDataException("PIR 必须位于 MIR 之后。");
            byte head = f.U1(), site = f.U1();
            _catalog.Heads.Add(head);
            _catalog.Sites.Add((head, site));
            if (!_parts.TryAdd((head, site), new Part { Order = _order++, Wafer = Wafer(head, site),
                    Product = _product, Program = _program, Lot = _lot }))
                throw new InvalidDataException($"Head {head} / Site {site} 的上一个 PIR 尚未结束。");
        }

        private void Prr(ref StdfFields f)
        {
            byte head = f.U1(), site = f.U1(); f.U1(); f.U2(); ushort hbin = f.U2();
            ushort? sbin = f.OptionalU2(); short? x = f.OptionalI2(), y = f.OptionalI2(); f.OptionalU4();
            string sn = (f.OptionalCn() ?? "").Trim(); f.OptionalCn(); f.OptionalBytes();
            if (!_parts.Remove((head, site), out var part)) throw new InvalidDataException($"Head {head} / Site {site} 的 PRR 没有对应 PIR。");
            if (Discovering) return;
            var values = new string?[_catalog.Items.Count];
            if (_catalog.NotApplicableColumns.TryGetValue((head, site), out var excluded))
                foreach (int column in excluded) values[column] = NotApplicableValue;
            foreach (var (column, text) in part.Values) values[column] = text;
            // A test executed with one rule does not require all alternate rules too.
            foreach (var key in part.Families)
                foreach (var item in _catalog.Families[key].Variants.Values)
                    values[item.ColumnIndex] ??= NotApplicableValue;
            Records.Add((part.Order, new CsvRecord
            {
                WaferId = part.Wafer.Name, SerialNumber = sn, Site = _catalog.Heads.Count > 1 ? $"{head}:{site}" : site.ToString(CultureInfo.InvariantCulture),
                X = x is null or short.MinValue ? null : x, Y = y is null or short.MinValue ? null : y,
                HBin = hbin.ToString(CultureInfo.InvariantCulture), SBin = sbin is null or ushort.MaxValue ? "" : sbin.Value.ToString(CultureInfo.InvariantCulture),
                Product = part.Product, Program = part.Program, Lot = part.Lot,
                Measurements = _store!.Append(_schema!, values)
            }));
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
                    throw new InvalidDataException($"TSR 测试 {number} 的 Head {head} / Site {site} 存在冲突名称，无法安全匹配 93k 测试项。");
                _catalog.ScopedTestNames.TryAdd(key, name);
            }
        }

        private (Part? Part, int Occurrence) Execution(TestKey key, byte head, byte site, byte flags, byte parameterFlags = 0)
        {
            if (!_parts.TryGetValue((head, site), out var part))
            {
                if ((flags & 0x10) == 0 || parameterFlags != 0) throw new InvalidDataException("测试结果不在对应 Head/Site 的 PIR/PRR 内。");
                return (null, 1); // Definition-only PTR/MPR outside a part.
            }
            int count = part.Occurrences.GetValueOrDefault(key) + 1;
            part.Occurrences[key] = count;
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
            f.OptionalCn(); f.OptionalCn(); f.OptionalCn(); f.OptionalR4(); f.OptionalR4();
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
            var item = _catalog.Item(family, limits, description, Discovering);
            if (Discovering) return;
            string raw;
            if ((flags & 0x10) != 0) raw = ""; // Explicitly untested; never insert zero.
            else if ((flags & (functional ? 0x6d : 0x2f)) != 0 || (parm & 7) != 0 || value == null || !float.IsFinite(value.Value))
                raw = $"STDF_INVALID(TEST_FLG=0x{flags:X2},PARM_FLG=0x{parm:X2},RESULT={value?.ToString("R", CultureInfo.InvariantCulture) ?? "absent"})";
            else raw = ((double)value.Value).ToString("R", CultureInfo.InvariantCulture);
            part.Values.Add(item.ColumnIndex, raw);
            part.Families.Add(family);
        }

        private void Ptr(ref StdfFields f)
        {
            uint number = f.U4(); byte head = f.U1(), site = f.U1(), flags = f.U1(), parm = f.U1();
            var key = _catalog.Test(10, number, head, site);
            float? result = f.OptionalR4();
            if (result == null && (flags & 0x12) == 0) throw new InvalidDataException("PTR 的有效 RESULT 缺失。");
            string? text = f.OptionalCn(); f.OptionalCn();
            var initial = Default(key); var definition = ReadLimits(ref f, initial, false, 0);
            definition.Description = Inherit(text, initial.Description);
            _defaults.TryAdd(key.Unscoped, definition); // Overrides apply only to this record, never replace the defaults.
            var execution = Execution(key, head, site, flags, parm);
            Value(execution.Part, new(key, "", execution.Occurrence), Bounds(definition, execution.Part == null ? (byte)0xc0 : parm), definition.Description, flags, parm, result);
        }

        private void Mpr(ref StdfFields f)
        {
            uint number = f.U4(); byte head = f.U1(), site = f.U1(), flags = f.U1(), parm = f.U1();
            var key = _catalog.Test(15, number, head, site);
            int pins = f.OptionalU2() ?? 0, count = f.OptionalU2() ?? 0;
            f.Skip((pins + 1) / 2);
            if (count * 4 > f.Remaining) throw new InvalidDataException("MPR RTN_RSLT 数组不完整。");
            var results = new float[count]; for (int i = 0; i < count; i++) results[i] = f.R4();
            string? text = f.OptionalCn(); f.OptionalCn();
            var initial = Default(key); var definition = ReadLimits(ref f, initial, true, pins);
            definition.Description = Inherit(text, initial.Description);
            _defaults.TryAdd(key.Unscoped, definition);
            var execution = Execution(key, head, site, flags, parm);
            var limits = Bounds(definition, execution.Part == null ? (byte)0xc0 : parm);
            if (count == 0 && execution.Part != null)
            {
                if (Discovering) _catalog.EmptyMprs.TryAdd((key, execution.Occurrence, limits), definition.Description);
                else
                    foreach (var family in _catalog.Families.Keys.Where(k => k.Test == key && k.Occurrence == execution.Occurrence))
                        Value(execution.Part, family, limits, definition.Description, flags, parm, null);
                return;
            }
            var pinCounts = new Dictionary<ushort, int>();
            for (int i = 0; i < Math.Max(1, count); i++)
            {
                string channel = $" 结果 {i + 1}";
                if (count > 0 && pins == count && definition.Pins.Length == pins)
                {
                    ushort pin = definition.Pins[i]; int repeat = pinCounts.GetValueOrDefault(pin) + 1; pinCounts[pin] = repeat;
                    channel = $" PMR {pin}" + (repeat > 1 ? $" #{repeat}" : "");
                }
                else if (count == 0) channel = " 无结果";
                else if (definition.StartInput is double start && definition.IncrementInput is double increment)
                    channel += " @ " + (start + i * increment).ToString("R", CultureInfo.InvariantCulture) + " " + definition.InputUnit;
                Value(execution.Part, new(key, channel, execution.Occurrence), limits, definition.Description, flags, parm,
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
            string? vector = f.OptionalCn(); f.OptionalCn(); f.OptionalCn(); string? text = f.OptionalCn();
            f.OptionalCn(); f.OptionalCn(); f.OptionalCn(); f.OptionalU1(); f.OptionalBits();
            var initial = Default(key);
            string description = Inherit(text, Inherit(vector, initial.Description));
            _defaults.TryAdd(key.Unscoped, new Defaults { Description = description });
            var execution = Execution(key, head, site, flags);
            _catalog.HasFunctional = true;
            Value(execution.Part, new(key, "", execution.Occurrence), new Limits(0, 0, "0=Pass; 1=Fail"),
                description, flags, 0, (flags & 0x80) != 0 ? 1 : 0, functional: true);
        }
    }
}

using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public static class YieldSimulationService
{
    public static bool Passes(double value, double? lower, double? upper) =>
        double.IsFinite(value) && (!lower.HasValue || value >= lower) && (!upper.HasValue || value <= upper);

    public static List<SimulationRuleRow> Recommend(SimulationSnapshot snapshot, bool includePartial, CancellationToken token)
    {
        var rows = new List<SimulationRuleRow>();
        for (int i = 0; i < snapshot.Data.Items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = snapshot.Data.Items[i];
            int good = 0, goodFail = 0, badFail = 0, tested = 0, fail = 0;
            for (int c = 0; c < snapshot.Count; c++)
            {
                if (snapshot.States[i][c] != MeasurementState.Measured) continue;
                tested++;
                bool outside = !Passes(snapshot.Value(c, i), item.LowLimit, item.HighLimit);
                if (outside) fail++;
                if (HBinClassifier.IsGood(snapshot.Original[c], includePartial)) { good++; if (outside) goodFail++; }
                else if (outside) badFail++;
            }
            bool limits = item.LowLimit.HasValue || item.HighLimit.HasValue;
            bool readout = new[] { "debug", "readout", "读值", "调试" }.Any(w => item.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
            string advice = !limits ? "无原始上下限，候选读值项；如需参与，请设置阈值并勾选"
                : goodFail > 0 ? $"冲突：原始目标良品超限 {goodFail}/{good}，请核对程序/阈值"
                : tested == 0 ? "无有效测量证据，需人工确认"
                : fail == 0 ? "全通过，保留参与；不能据此自动关闭"
                : badFail > 0 && good > 0 ? $"建议参与：良品未超限，不良超限 {badFail}"
                : "证据不足，需人工确认";
            if (readout) advice += "；名称疑似 debug/读值项，建议核查（未自动关闭）";
            rows.Add(new() { Item = i, Name = item.DisplayName, Unit = item.Unit, OriginalLower = item.LowLimit,
                OriginalUpper = item.HighLimit, Included = limits, Enabled = true,
                Lower = SimulationRuleRow.Number(item.LowLimit), Upper = SimulationRuleRow.Number(item.HighLimit),
                Stage = (i + 1).ToString(), Recommendation = advice, GoodViolations = goodFail, BadViolations = badFail,
                TestCount = tested, OriginalYield = limits && tested > 0 ? (double)(tested - fail) / tested : null });
        }
        return rows;
    }

    private sealed record Evaluation(ChipVerdict Verdict, int Measured, int Missing, int NA, int Invalid, int[] MissingItems);

    private static Evaluation Evaluate(SimulationSnapshot s, int chip, SimulationRule[] rules, bool baseline, CancellationToken token,
        int[] tested, int[] originalPassed, int[] simulatedPassed)
    {
        int measured = 0, missing = 0, na = 0, invalid = 0;
        bool fail = false;
        List<int>? missingItems = null;
        foreach (var r in rules)
        {
            if ((r.Item & 255) == 0) token.ThrowIfCancellationRequested();
            // Piggyback on the existing baseline traversal, including non-participating items.
            // This adds only three counters per item, with no extra scan or per-chip objects.
            bool originalPass = false;
            if (baseline && s.States[r.Item][chip] == MeasurementState.Measured)
            {
                double value = s.Value(chip, r.Item);
                var item = s.Data.Items[r.Item];
                originalPass = Passes(value, item.LowLimit, item.HighLimit);
                tested[r.Item]++;
                if (originalPass) originalPassed[r.Item]++;
            }
            if (!r.Included || !baseline && !r.Enabled) continue;
            switch (s.States[r.Item][chip])
            {
                case MeasurementState.Measured:
                    measured++;
                    bool passes = baseline ? originalPass : Passes(s.Value(chip, r.Item), r.Lower, r.Upper);
                    if (!baseline && passes) simulatedPassed[r.Item]++;
                    if (!passes)
                    { fail = true; missingItems = null; }
                    break;
                case MeasurementState.Unmeasured:
                    missing++; if (!baseline && !fail) (missingItems ??= new()).Add(r.Item); break;
                case MeasurementState.NotApplicable: na++; break;
                default: invalid++; break;
            }
        }
        var verdict = fail ? ChipVerdict.Bad : s.ContextConflict[chip] || invalid > 0 || measured + missing == 0
            ? ChipVerdict.Unknown : missing > 0 ? ChipVerdict.Pending : ChipVerdict.Good;
        return new(verdict, measured, missing, na, invalid, missingItems?.ToArray() ?? Array.Empty<int>());
    }

    public static SimulationResult Calculate(SimulationSnapshot s, SimulationRule[] rules, PredictionOptions options, CancellationToken token)
    {
        Validate(s, rules, options);
        token.ThrowIfCancellationRequested();
        var predictor = new JointPredictor(s, rules, options, token);
        var rows = new List<SimulationChipResult>(s.Count);
        var tested = new int[rules.Length];
        var originalPassed = new int[rules.Length];
        var simulatedPassed = new int[rules.Length];
        // Intern missing sets, which are frequently shared by thousands of fail-stop chips.
        var sets = new Dictionary<string, int[]>();
        var groups = new Dictionary<string, List<SimulationChipResult>>();
        for (int c = 0; c < s.Count; c++)
        {
            token.ThrowIfCancellationRequested();
            var baseline = Evaluate(s, c, rules, true, token, tested, originalPassed, simulatedPassed);
            var simulation = Evaluate(s, c, rules, false, token, tested, originalPassed, simulatedPassed);
            string key = string.Join(",", simulation.MissingItems);
            if (!sets.TryGetValue(key, out var missingSet)) sets[key] = missingSet = simulation.MissingItems;
            var evidence = simulation.Verdict == ChipVerdict.Pending ? predictor.Predict(c, missingSet) : null;
            var row = new SimulationChipResult(c, s.Chips[c], s.Original[c], baseline.Verdict, simulation.Verdict,
                simulation.Measured, simulation.Missing, simulation.NA, simulation.Invalid, missingSet, evidence,
                HBinClassifier.IsGood(s.Original[c], options.IncludePartial));
            rows.Add(row);
            if (simulation.Verdict is ChipVerdict.Pending or ChipVerdict.Unknown)
            {
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                list.Add(row);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(rows, groups.Select(g => new MissingGroupRow(
            sets[g.Key].Length == 0 ? "无缺测项（无效值 / 无判定依据 / 上下文冲突）" :
                string.Join("；", sets[g.Key].Select(i => s.Data.Items[i].DisplayName)), g.Value.Count,
            g.Value.Count(c => c.Evidence?.Probability != null), g.Value.Count(c => c.Evidence?.Probability == null),
            g.Value.Sum(c => c.Evidence?.Probability ?? 0))).ToArray())
        {
            Items = rules.OrderBy(r => r.Item).Select(r => new SimulationItemStatistics(r.Item, tested[r.Item],
                originalPassed[r.Item], simulatedPassed[r.Item],
                s.Data.Items[r.Item].LowLimit.HasValue || s.Data.Items[r.Item].HighLimit.HasValue,
                r.Included, r.Enabled)).ToArray()
        };
    }

    private static void Validate(SimulationSnapshot s, SimulationRule[] rules, PredictionOptions o)
    {
        if (rules.Length != s.Data.Items.Count || rules.Select(r => r.Item).Distinct().Count() != rules.Length ||
            rules.Any(r => r.Item < 0 || r.Item >= rules.Length || r.Stage < 1 || r.Lower > r.Upper ||
                r.Lower.HasValue && !double.IsFinite(r.Lower.Value) || r.Upper.HasValue && !double.IsFinite(r.Upper.Value)))
            throw new ArgumentException("模拟规则不完整或上下限 / 阶段无效。");
        if (o.MinimumReferences < 2 || o.MaximumReferences < o.MinimumReferences || o.MaximumReferences > 10000 ||
            !double.IsFinite(o.MaximumDistance) || o.MaximumDistance <= 0)
            throw new ArgumentException("参考样本数应满足 2 ≤ 最少 ≤ 最多 ≤ 10000；相近距离必须为正数。");
    }

    private sealed class JointPredictor
    {
        private readonly SimulationSnapshot _s;
        private readonly SimulationRule[] _rules;
        private readonly PredictionOptions _options;
        private readonly CancellationToken _token;
        private readonly Dictionary<string, string> _programGroups;
        private readonly Dictionary<(string, string), int[]> _pools;
        private readonly Dictionary<((string, string), int), double> _scales = new();

        public JointPredictor(SimulationSnapshot s, SimulationRule[] rules, PredictionOptions options, CancellationToken token)
        {
            _s = s; _rules = rules.OrderBy(r => r.Item).ToArray(); _options = options; _token = token;
            _programGroups = s.Config.ProgramCompatibility.SelectMany(g => g.Value.Select(p => (Program: p, Group: "GROUP:" + g.Key.ToUpperInvariant())))
                .ToDictionary(p => p.Program, p => p.Group, StringComparer.OrdinalIgnoreCase);
            _pools = Enumerable.Range(0, s.Count).Where(c => !s.ContextConflict[c] && s.Products[c].Length > 0 && s.Programs[c].Length > 0)
                .GroupBy(ContextKey).ToDictionary(g => g.Key, g => g.ToArray());
        }
        private (string, string) ContextKey(int chip) => (_s.Products[chip].ToUpperInvariant(),
            _programGroups.GetValueOrDefault(_s.Programs[chip], "PROGRAM:" + _s.Programs[chip].ToUpperInvariant()));

        private double Scale((string, string) context, int item, int[] pool)
        {
            if (_scales.TryGetValue((context, item), out double scale)) return scale;
            int n = 0; double mean = 0, m2 = 0;
            foreach (int c in pool)
            {
                if ((n & 255) == 0) _token.ThrowIfCancellationRequested();
                if (_s.States[item][c] != MeasurementState.Measured) continue;
                double v = _s.Value(c, item), delta = v - mean;
                n++; mean += delta / n; m2 += delta * (v - mean);
            }
            scale = n > 1 ? Math.Sqrt(Math.Max(0, m2 / n)) : 0;
            if (!double.IsFinite(scale)) scale = 0;
            _scales[(context, item)] = scale;
            return scale;
        }

        public PredictionEvidence Predict(int target, int[] missing)
        {
            bool extrapolated = _rules.Any(r => r.Included && _s.States[r.Item][target] == MeasurementState.Measured &&
                !Passes(_s.Value(target, r.Item), _s.Data.Items[r.Item].LowLimit, _s.Data.Items[r.Item].HighLimit)) ||
                missing.Any(i => (_s.Data.Items[i].LowLimit is double low && (!_rules[i].Lower.HasValue || _rules[i].Lower < low)) ||
                    (_s.Data.Items[i].HighLimit is double high && (!_rules[i].Upper.HasValue || _rules[i].Upper > high)));
            PredictionEvidence None(string reason, string features = "") => new(null, 0, 0, 0, 0, 0, 0, extrapolated, reason, features);
            if (_s.Products[target].Length == 0 || _s.Programs[target].Length == 0)
                return None("无法估计：产品或程序未知；请在配置中定义上下文");
            var context = ContextKey(target);
            if (!_pools.TryGetValue(context, out var pool)) return None("无法估计：没有同产品兼容程序的参考");
            int firstStage = missing.Min(i => _rules[i].Stage);
            var features = _rules.Where(r => r.Included && r.Stage < firstStage && _s.States[r.Item][target] == MeasurementState.Measured)
                .OrderByDescending(r => r.Stage).ThenBy(r => r.Item).Take(8).Select(r => r.Item).ToArray();
            if (features.Length == 0) return None("无法估计：缺测之前没有可匹配的已测项；请核对阶段定义");
            string names = string.Join(", ", features.Select(i => _s.Data.Items[i].DisplayName));
            var scales = features.Select(i => Scale(context, i, pool)).ToArray();
            // Select by context and observed upstream values BEFORE examining missing outcomes or final HBIN.
            // Keep only the nearest K candidates. Do not allocate/sort an entire pool for every missing chip.
            var candidates = new PriorityQueue<(int Chip, bool SameLot, double Distance), (int LotRank, double Distance, int Chip)>(_options.MaximumReferences);
            foreach (int c in pool)
            {
                _token.ThrowIfCancellationRequested();
                if (c == target) continue;
                double squared = 0; bool valid = true;
                for (int f = 0; f < features.Length; f++)
                {
                    int item = features[f];
                    if (_s.States[item][c] != MeasurementState.Measured) { valid = false; break; }
                    double delta = _s.Value(c, item) - _s.Value(target, item);
                    if (scales[f] == 0) { if (Math.Abs(delta) > 1e-12) { valid = false; break; } }
                    else squared += Math.Pow(delta / scales[f], 2);
                }
                double distance = Math.Sqrt(squared / features.Length);
                if (valid && double.IsFinite(distance) && distance <= _options.MaximumDistance)
                {
                    bool same = _s.Lots[target].Length > 0 && string.Equals(_s.Lots[target], _s.Lots[c], StringComparison.OrdinalIgnoreCase);
                    var entry = (c, same, distance);
                    var priority = (same ? 1 : 0, -distance, -c); // smallest = worst candidate
                    if (candidates.Count < _options.MaximumReferences) candidates.Enqueue(entry, priority);
                    else candidates.EnqueueDequeue(entry, priority);
                }
            }
            var neighbors = candidates.UnorderedItems.Select(n => n.Element).ToArray();
            int pass = 0, fail = 0, unknown = 0, bad = 0, sameLot = 0;
            foreach (var reference in neighbors)
            {
                _token.ThrowIfCancellationRequested();
                if (_s.Original[reference.Chip] == OriginalDisposition.Fail) bad++;
                if (reference.SameLot) sameLot++;
                bool failed = false, unresolved = false;
                foreach (int i in missing)
                {
                    if (_s.States[i][reference.Chip] != MeasurementState.Measured) unresolved = true;
                    else if (!Passes(_s.Value(reference.Chip, i), _rules[i].Lower, _rules[i].Upper)) { failed = true; break; }
                }
                // A known failure resolves the joint event even when other items remain unmeasured.
                if (failed) fail++; else if (unresolved) unknown++; else pass++;
            }
            string reason = neighbors.Length < _options.MinimumReferences ? "无法估计：相近参考样本不足"
                : bad == 0 ? "无法估计：相近参考未覆盖原始不良品"
                : unknown > 0 ? "无法估计：参考的联合结果仍有未知，未删除这些参考"
                : "经验联合概率（同产品、兼容程序；前段相近；优先同批次）";
            double? probability = neighbors.Length >= _options.MinimumReferences && bad > 0 && unknown == 0
                ? (double)pass / neighbors.Length : null;
            return new(probability, neighbors.Length, pass, fail, unknown, bad, sameLot, extrapolated, reason, names);
        }
    }
}

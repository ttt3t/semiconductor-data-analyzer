using System.Collections;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed partial class SimulationSnapshot
{
    private sealed class Projection<T>(RecordList records, Func<CsvRecord, T> select) : IReadOnlyList<T>
    {
        public int Count => records.Count;
        public T this[int index] => select(records[index]);
        public IEnumerator<T> GetEnumerator() { foreach (var record in records) yield return select(record); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static SimulationSnapshot BuildDisk(CsvParseResult data, AnalyzerConfig config, RetestMode mode, CancellationToken token)
    {
        int itemCount = data.Items.Count;
        var sources = Enumerable.Range(0, itemCount).Select(_ => new PagedVector<int>()).ToArray();
        var states = Enumerable.Range(0, itemCount).Select(_ => new PagedVector<MeasurementState>()).ToArray();
        var conflicts = new PagedVector<bool>();
        var chips = new RecordList(0, disk: true); chips.Inherit(data.Records);
        string Context(string value, string fallback) => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim();
        var na = config.NotApplicableTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = config.MissingTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var order = data.Records.Store!.GroupRows();
        for (int cursor = 0; cursor < order.Count;)
        {
            token.ThrowIfCancellationRequested();
            int count = order[cursor].Count;
            var selectedRows = new int[itemCount]; Array.Fill(selectedRows, -1);
            var selectedStates = new MeasurementState[itemCount];
            CsvRecord? chosen = null; bool conflict = false;
            string? product = null, program = null;
            for (int n = 0; n < count; n++)
            {
                int row = order[cursor++].Slot; var record = data.Records[row];
                bool blank = record.X == null && record.Y == null && string.IsNullOrWhiteSpace(record.ChipSerialNumber) &&
                    string.IsNullOrWhiteSpace(record.Site) && string.IsNullOrWhiteSpace(record.SBin) && string.IsNullOrWhiteSpace(record.HBin) && !record.HasNonblankMeasurements;
                if (blank) continue;
                string p = Context(record.Product, config.Product), g = Context(record.Program, config.Program);
                if (chosen == null) { product = p; program = g; }
                else conflict |= !string.Equals(product, p, StringComparison.OrdinalIgnoreCase) || !string.Equals(program, g, StringComparison.OrdinalIgnoreCase);
                if (chosen == null || mode == RetestMode.Last) chosen = record;
                for (int item = 0; item < itemCount; item++)
                {
                    if ((item & 255) == 0) token.ThrowIfCancellationRequested();
                    if (mode == RetestMode.First && selectedRows[item] >= 0) continue;
                    var column = data.Items[item];
                    if (double.IsFinite(column.Values.GetSourceRowValue(row)))
                    { selectedRows[item] = row; selectedStates[item] = MeasurementState.Measured; continue; }
                    if (!record.Measurements.ContainsKey(column.Id)) continue;
                    selectedRows[item] = row; record.Measurements.TryGetText(column.Id, out var raw);
                    string text = raw.Trim().ToString();
                    selectedStates[item] = text.Length == 0 || missing.Contains(text) ? MeasurementState.Unmeasured :
                        text == StdfParser.NotApplicableValue || na.Contains(text) ? MeasurementState.NotApplicable : MeasurementState.Invalid;
                }
            }
            if (chosen == null) continue;
            chips.Add(chosen); conflicts.Add(conflict);
            for (int item = 0; item < itemCount; item++) { sources[item].Add(selectedRows[item]); states[item].Add(selectedStates[item]); }
        }
        chips.Flush(); conflicts.Seal();
        foreach (var vector in sources) vector.Seal(); foreach (var vector in states) vector.Seal();
        var classifier = new HBinClassifier(config);
        return new() { Data = data, Config = config, Chips = chips, SourceRows = sources, States = states,
            ContextConflict = conflicts, Original = new Projection<OriginalDisposition>(chips, r => classifier.Classify(r.HBin)),
            Products = new Projection<string>(chips, r => Context(r.Product, config.Product)),
            Programs = new Projection<string>(chips, r => Context(r.Program, config.Program)),
            Lots = new Projection<string>(chips, r => Context(r.Lot, config.Lot)) };
    }
}

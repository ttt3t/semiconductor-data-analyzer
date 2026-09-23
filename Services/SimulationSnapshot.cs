using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Only row indexes and byte states are added; numeric columns remain shared and immutable.</summary>
public sealed class SimulationSnapshot
{
    public required CsvParseResult Data { get; init; }
    public required CsvRecord[] Chips { get; init; }
    public required int[][] SourceRows { get; init; }
    public required MeasurementState[][] States { get; init; }
    public required OriginalDisposition[] Original { get; init; }
    public required string[] Products { get; init; }
    public required string[] Programs { get; init; }
    public required string[] Lots { get; init; }
    public required bool[] ContextConflict { get; init; }
    public required AnalyzerConfig Config { get; init; }
    public int Count => Chips.Length;
    public double Value(int chip, int item) => SourceRows[item][chip] < 0 ? double.NaN : Data.Items[item].Values.GetSourceRowValue(SourceRows[item][chip]);

    public static SimulationSnapshot Build(CsvParseResult data, AnalyzerConfig config, RetestMode mode, CancellationToken token)
    {
        if (mode is not (RetestMode.First or RetestMode.Last)) throw new ArgumentException("芯片联合判定请选择首测或末测；不能重复计算同一芯片。");
        var groups = new Dictionary<(string, int?, int?, string), int>();
        var chips = new List<CsvRecord>();
        var rowChips = new int[data.Records.Count];
        Array.Fill(rowChips, -1);
        for (int row = 0; row < data.Records.Count; row++)
        {
            token.ThrowIfCancellationRequested();
            var r = data.Records[row];
            if (r.X == null && r.Y == null && new[] { r.ChipSerialNumber, r.Site, r.SBin, r.HBin }.All(string.IsNullOrWhiteSpace)
                && r.Measurements.All(kv => string.IsNullOrWhiteSpace(kv.Value))) continue;
            if (!groups.TryGetValue(DatasetService.ChipKey(r), out int chip))
            { chip = chips.Count; groups.Add(DatasetService.ChipKey(r), chip); chips.Add(r); }
            else if (mode == RetestMode.Last) chips[chip] = r;
            rowChips[row] = chip;
        }
        int count = chips.Count, itemCount = data.Items.Count;
        var sources = new int[itemCount][];
        var states = new MeasurementState[itemCount][];
        for (int i = 0; i < itemCount; i++) { sources[i] = new int[count]; Array.Fill(sources[i], -1); states[i] = new MeasurementState[count]; }
        var conflict = new bool[count];
        string Context(string value, string fallback) => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim();
        var products = chips.Select(r => Context(r.Product, config.Product)).ToArray();
        var programs = chips.Select(r => Context(r.Program, config.Program)).ToArray();
        var lots = chips.Select(r => Context(r.Lot, config.Lot)).ToArray();
        var na = config.NotApplicableTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = config.MissingTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int row = 0; row < data.Records.Count; row++)
        {
            token.ThrowIfCancellationRequested();
            int chip = rowChips[row]; if (chip < 0) continue;
            var r = data.Records[row];
            conflict[chip] |= !string.Equals(products[chip], Context(r.Product, config.Product), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(programs[chip], Context(r.Program, config.Program), StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < itemCount; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                if (mode == RetestMode.First && sources[i][chip] >= 0) continue;
                if (!r.Measurements.ContainsKey(data.Items[i].Id)) continue;
                sources[i][chip] = row;
                if (double.IsFinite(data.Items[i].Values.GetSourceRowValue(row))) states[i][chip] = MeasurementState.Measured;
                else
                {
                    r.Measurements.TryGetText(data.Items[i].Id, out var raw);
                    string text = raw.Trim().ToString();
                    states[i][chip] = text == StdfParser.NotApplicableValue ? MeasurementState.NotApplicable
                        : text.Length == 0 || missing.Contains(text) ? MeasurementState.Unmeasured
                        : na.Contains(text) ? MeasurementState.NotApplicable : MeasurementState.Invalid;
                }
            }
        }
        var classifier = new HBinClassifier(config);
        return new() { Data = data, Config = config, Chips = chips.ToArray(), SourceRows = sources, States = states,
            Products = products, Programs = programs, Lots = lots, ContextConflict = conflict,
            Original = chips.Select(c => classifier.Classify(c.HBin)).ToArray() };
    }
}

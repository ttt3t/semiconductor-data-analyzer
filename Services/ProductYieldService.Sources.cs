namespace SemiconductorCsvAnalyzer.Services;

public static partial class ProductYieldService
{
    private readonly record struct SourceScope(string Kind, string Lot, string Wafer, string Stage, string? Month);

    // One association per file/wafer/month, never one set per measurement or chip.
    private sealed class SourceIndex
    {
        private readonly Dictionary<SourceScope, HashSet<string>> _files = new();

        public void Add(Models.ManagedDataFile file, Models.ProductTestStage stage,
            IEnumerable<(DeviceKey Key, Observation Observation)> readings)
        {
            foreach (var scope in readings.Select(r => new SourceScope(stage.Kind, r.Key.Lot, r.Key.Wafer,
                stage.Id, Month(r.Observation.TestTime))).Distinct())
            {
                AddScope(scope, file.Id);
                AddScope(scope with { Month = null }, file.Id);
            }
        }

        private void AddScope(SourceScope scope, string id)
        {
            if (!_files.TryGetValue(scope, out var files)) _files[scope] = files = new(StringComparer.Ordinal);
            files.Add(id);
        }

        public IEnumerable<string> Find((string Kind, string Lot, string Wafer) group, string stage, string? month) =>
            _files.TryGetValue(new(group.Kind, group.Lot, group.Wafer, stage, month), out var files)
                ? files : Array.Empty<string>();
    }
}

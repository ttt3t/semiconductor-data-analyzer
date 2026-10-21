using System.IO;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Exact external merge sort with bounded runs and at most 16 readers per merge.</summary>
internal static class ExternalValueSort
{
    public const int RunValues = 1_048_576;
    private sealed class Run : IDisposable
    {
        private readonly BinaryReader _reader;
        public Run(string path) => _reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536));
        public bool Next(out double value) { if (_reader.BaseStream.Position >= _reader.BaseStream.Length) { value = 0; return false; } value = _reader.ReadDouble(); return true; }
        public void Dispose() => _reader.Dispose();
    }
    public static IEnumerable<double> Sort(IEnumerable<double> values)
    {
        var files = new HashSet<string>();
        try
        {
            var runs = new List<string>(); var buffer = new double[RunValues]; int count = 0;
            void Save()
            {
                Array.Sort(buffer, 0, count); string path = DiskStorage.NewPath(".sort"); files.Add(path); runs.Add(path);
                using var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536));
                for (int i = 0; i < count; i++) w.Write(buffer[i]); count = 0;
            }
            foreach (double value in values) { if ((count & 4095) == 0) AnalysisOperation.CheckCancellation(); buffer[count++] = value; if (count == buffer.Length) Save(); }
            if (count > 0) Save();
            while (runs.Count > 16)
            {
                var next = new List<string>();
                foreach (var batch in runs.Chunk(16))
                {
                    string path = DiskStorage.NewPath(".sort"); files.Add(path); next.Add(path);
                    using (var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536)))
                        foreach (double value in Merge(batch)) writer.Write(value);
                    foreach (string old in batch) { File.Delete(old); files.Remove(old); }
                }
                runs = next;
            }
            foreach (double value in Merge(runs)) yield return value;
        }
        finally { foreach (string file in files) try { File.Delete(file); } catch (IOException) { } }
    }
    private static IEnumerable<double> Merge(IEnumerable<string> files)
    {
        var runs = files.Select(f => new Run(f)).ToArray();
        try
        {
            var queue = new PriorityQueue<Run, double>();
            foreach (var run in runs) if (run.Next(out double value)) queue.Enqueue(run, value);
            int read = 0;
            while (queue.TryDequeue(out var run, out double value))
            {
                if ((read++ & 4095) == 0) AnalysisOperation.CheckCancellation();
                yield return value; if (run.Next(out double next)) queue.Enqueue(run, next);
            }
        }
        finally { foreach (var run in runs) run.Dispose(); }
    }
}

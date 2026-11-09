using System.IO;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record ExplorerLocationPlan(string[] Files, int TotalLocations, int InvalidCount, string[] Errors)
{
    public const int WindowLimit = 5;
    public static ExplorerLocationPlan Create(IEnumerable<string> files)
    {
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>(); int invalid = 0;
        foreach (string file in files)
        {
            try
            {
                string path = Path.GetFullPath(file);
                string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("文件路径没有所在目录。");
                locations.TryAdd(Path.TrimEndingDirectorySeparator(directory), path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { invalid++; if (errors.Count < WindowLimit) errors.Add(file + "：" + ex.Message); }
        }
        return new(locations.Values.Take(WindowLimit).ToArray(), locations.Count, invalid, errors.ToArray());
    }
}

using System.IO;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

public static class AtomicFile
{
    public static void WriteAllText(string path, string text, Encoding? encoding = null)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(file, encoding ?? new UTF8Encoding(false), 4096, leaveOpen: true))
                { writer.Write(text); writer.Flush(); }
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

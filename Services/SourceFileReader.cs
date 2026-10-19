using System.IO;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Read-only input handles coexist with editors that allow shared reading.</summary>
internal static class SourceFileReader
{
    internal static FileStream Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            throw new IOException($"无法读取“{Path.GetFileName(path)}”：其他程序对文件加了独占锁，请允许共享读取或关闭占用此文件的程序后重试。", ex);
        }
    }
}

// Sharing permits edits while reading. Reject detectable changes between passes
// rather than combining headers from one version with measurements from another.
internal readonly struct SourceFileStamp
{
    private readonly long _length;
    private readonly DateTime _modified;

    internal SourceFileStamp(FileStream file)
    {
        _length = file.Length;
        _modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
    }

    internal void Validate(FileStream file)
    {
        var info = new FileInfo(file.Name);
        if (file.Length != _length || File.GetLastWriteTimeUtc(file.SafeFileHandle) != _modified ||
            !info.Exists || info.Length != _length || info.LastWriteTimeUtc != _modified)
            throw new IOException($"读取“{Path.GetFileName(file.Name)}”期间文件发生变化，请保存文件后重新导入。");
    }
}

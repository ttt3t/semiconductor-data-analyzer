using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Process-wide bounded block cache. Heap budgets never imply a limit on Windows' file cache.</summary>
public static class DiskStorage
{
    public const int FormatVersion = 1;
    internal static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static long CacheBudgetBytes { get; private set; } = 96L * 1024 * 1024;
    public static void ConfigureBudget(int dataBudgetMiB)
    {
        if (dataBudgetMiB is not (256 or 512)) dataBudgetMiB = 256;
        lock (Gate) { CacheBudgetBytes = dataBudgetMiB * 1024L * 1024 * 3 / 8; while (_bytes > CacheBudgetBytes && Lru.Last is { } node) Remove(node); }
    }
    public const int WorkBufferBytes = 16 * 1024 * 1024;
    internal static readonly SemaphoreSlim Jobs = new(2);
    internal static IDisposable ReserveWork()
    { Jobs.Wait(AnalysisOperation.Token); return new WorkLease(); }
    private sealed class WorkLease : IDisposable
    { private bool _released; public void Dispose() { if (!_released) { _released = true; Jobs.Release(); } } }
    internal static void ConfigureExportBudget(int dataBudgetMiB)
    {
        ConfigureBudget(dataBudgetMiB);
        lock (Gate) { CacheBudgetBytes /= 4; while (_bytes > CacheBudgetBytes && Lru.Last is { } node) Remove(node); }
    }
    internal static long SmallVectorBytes;
    internal const long SmallVectorBudget = 8L * 1024 * 1024;
    private sealed record Entry(object Value, long Bytes, Action? Flush);
    private static readonly object Gate = new();
    internal static object Sync => Gate;
    private static readonly Dictionary<(Guid, long), LinkedListNode<((Guid, long) Key, Entry Value)>> Cache = new();
    private static readonly LinkedList<((Guid, long) Key, Entry Value)> Lru = new();
    private static long _bytes;
    public static long CachedBytes { get { lock (Gate) return _bytes; } }
    internal static T Get<T>(Guid owner, long page, Func<T> load, Func<T, long> bytes, Action<T>? flush = null) where T : class
    {
        lock (Gate)
        {
            var key = (owner, page);
            if (Cache.TryGetValue(key, out var found)) { Lru.Remove(found); Lru.AddFirst(found); return (T)found.Value.Value.Value; }
            var value = load(); long size = bytes(value);
            while (_bytes + size > CacheBudgetBytes && Lru.Last is { } old) Remove(old);
            var entry = new Entry(value, size, flush == null ? null : () => flush(value));
            var node = Lru.AddFirst((key, entry)); Cache.Add(key, node); _bytes += size;
            return value;
        }
    }
    private static void Remove(LinkedListNode<((Guid, long) Key, Entry Value)> node)
    { node.Value.Value.Flush?.Invoke(); _bytes -= node.Value.Value.Bytes; Cache.Remove(node.Value.Key); Lru.Remove(node); }
    internal static void Evict(Guid owner)
    {
        lock (Gate)
            foreach (var node in Cache.Where(p => p.Key.Item1 == owner).Select(p => p.Value).ToArray()) Remove(node);
    }
    internal static void Evict(Guid owner, long page)
    { lock (Gate) if (Cache.TryGetValue((owner, page), out var node)) Remove(node); }
    public static void Trim() { lock (Gate) while (Lru.Last is { } node) Remove(node); }
    internal static void ReadExactly(Microsoft.Win32.SafeHandles.SafeFileHandle file, Span<byte> buffer, long offset)
    {
        int done = 0;
        while (done < buffer.Length)
        { int read = RandomAccess.Read(file, buffer[done..], offset + done); if (read == 0) throw new EndOfStreamException("磁盘缓存不完整，请重新打开源文件。"); done += read; }
    }
    private static bool _cleaned;
    internal static string NewPath(string extension)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorCsvAnalyzer", "Cache", "v1");
        Directory.CreateDirectory(folder);
        lock (Gate)
        {
            if (!_cleaned)
            {
                _cleaned = true;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    string prefix = Path.GetFileName(file).Split('-')[0];
                    if (!int.TryParse(prefix, out int pid) || pid == Environment.ProcessId) continue;
                    try { using var process = System.Diagnostics.Process.GetProcessById(pid); if (!process.HasExited) continue; }
                    catch (ArgumentException) { }
                    catch (System.ComponentModel.Win32Exception) { continue; }
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        return Path.Combine(folder, $"{Environment.ProcessId}-{Guid.NewGuid():N}{extension}");
    }
    internal static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048; PRAGMA mmap_size=0; PRAGMA temp_store=FILE;";
        command.ExecuteNonQuery();
        connection.CreateCollation("ORDINAL", StringComparer.Ordinal.Compare);
        connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
        return connection;
    }
}

internal abstract class DiskResource : IDisposable
{
    private readonly object _lifetime = new();
    private int _leases;
    private bool _disposed;
    private readonly bool _owned;
    public Guid CacheId { get; } = Guid.NewGuid();
    protected DiskResource(bool track = true) { _owned = track && AnalysisOperation.Track(this); }
    internal void Acquire() { lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); _leases++; } }
    internal void Release() { lock (_lifetime) { if (_leases > 0) _leases--; if (_leases == 0 && _owned) Dispose(); } }
    internal void DisposeIfUnleased() { lock (_lifetime) if (_leases == 0) Dispose(); }
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            try { DiskStorage.Evict(CacheId); } finally { Close(); GC.SuppressFinalize(this); }
        }
    }
    protected abstract void Close();
    ~DiskResource() { try { Dispose(); } catch { /* Unpublished scratch files are never user data. */ } }
}

/// <summary>Small sequences stay in memory. Larger sequences append to a private file and read cached 64-KiB pages.</summary>
public sealed class PagedVector<T> : IReadOnlyList<T>, IDisposable where T : unmanaged
{
    private List<T>? _small = new();
    private VectorFile? _disk;
    private int _count;
    private bool _disposed;
    private const int SmallBytes = 64 * 1024;
    private static readonly int ElementBytes = Unsafe.SizeOf<T>();
    public int Count => _count;
    public int Length => _count;
    public bool IsOnDisk => _disk != null;
    public T this[int index]
    {
        get { if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index)); return _small != null ? _small[index] : _disk!.Get(index); }
        set { if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index)); if (_small != null) _small[index] = value; else _disk!.Set(index, value); }
    }
    public void Add(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_small != null)
        {
            long total = Interlocked.Add(ref DiskStorage.SmallVectorBytes, ElementBytes);
            if ((long)(_count + 1) * ElementBytes > SmallBytes || total > DiskStorage.SmallVectorBudget)
            {
                try { _disk = new VectorFile(); foreach (var item in _small) _disk.Add(item); }
                catch { Interlocked.Add(ref DiskStorage.SmallVectorBytes, -ElementBytes); throw; }
                _small = null; Interlocked.Add(ref DiskStorage.SmallVectorBytes, -(long)(_count + 1) * ElementBytes);
            }
        }
        if (_small != null) _small.Add(value); else _disk!.Add(value);
        _count++;
    }
    internal sealed record Snapshot(string? Path, int Count, T[]? Small);
    internal Snapshot Capture() { Seal(); return _small != null ? new(null, Count, _small.ToArray()) : new(_disk!.StoragePath, Count, null); }
    internal static PagedVector<T> Open(Snapshot snapshot)
    {
        if (snapshot.Small != null) return From(snapshot.Small);
        return new PagedVector<T> { _small = null, _count = snapshot.Count, _disk = new VectorFile(snapshot.Path!, snapshot.Count) };
    }
    public void Seal() => _disk?.Seal();
    public static PagedVector<T> From(IEnumerable<T> values)
    { var result = new PagedVector<T>(); try { foreach (var value in values) { if ((result.Count & 4095) == 0) AnalysisOperation.CheckCancellation(); result.Add(value); } result.Seal(); return result; } catch { result.Dispose(); throw; } }
    public static implicit operator PagedVector<T>(T[] values) => From(values);
    public IEnumerable<T> Range(int start, int count)
    {
        Seal();
        if (_small != null) { for (int i = start; i < start + count; i++) yield return _small[i]; }
        else
        {
            int page = -1; byte[] buffer = Array.Empty<byte>();
            for (int i = start; i < start + count; i++)
            {
                int next = i / VectorFile.PerPage;
                if (page != next) { buffer = _disk!.Page(next); page = next; }
                yield return MemoryMarshal.Read<T>(buffer.AsSpan(i % VectorFile.PerPage * ElementBytes));
            }
        }
    }
    public IEnumerator<T> GetEnumerator() => Range(0, Count).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { if (_disposed) return; _disposed = true; _disk?.Dispose(); _disk = null; if (_small != null) Interlocked.Add(ref DiskStorage.SmallVectorBytes, -(long)_count * ElementBytes); _small = new(); _count = 0; GC.SuppressFinalize(this); }
    ~PagedVector() { if (_small != null) Interlocked.Add(ref DiskStorage.SmallVectorBytes, -(long)_count * ElementBytes); }
    private sealed class VectorFile : DiskResource
    {
        internal static readonly int PerPage = Math.Max(1, 65536 / ElementBytes);
        private readonly string _path = DiskStorage.NewPath(".vector");
        private FileStream _file;
        private bool _dirty;
        private bool _sealed;
        private readonly bool _readerOnly;
        internal string StoragePath => _path;
        public VectorFile() : base(false) => _file = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 2048, FileOptions.RandomAccess);
        public VectorFile(string path, int count) : base(false)
        {
            _readerOnly = _sealed = true; _path = path;
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            if (_file.Length != (long)count * ElementBytes) throw new InvalidDataException("行索引缓存长度校验失败。");
        }
        public void Add(T value) { if (_sealed) _file.Seek(0, SeekOrigin.End); Span<T> one = stackalloc T[1]; one[0] = value; _file.Write(MemoryMarshal.AsBytes(one)); _dirty = true; }
        public void Flush() { if (_dirty) { _file.Flush(); _dirty = false; DiskStorage.Evict(CacheId); } }
        public void Seal()
        {
            Flush(); if (_sealed) return;
            _file.Dispose(); _file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess); _sealed = true;
        }
        public byte[] Page(int page)
        {
            Flush();
            return DiskStorage.Get(CacheId, page, () =>
            {
                long offset = (long)page * PerPage * ElementBytes;
                byte[] buffer = new byte[(int)Math.Min(PerPage * ElementBytes, _file.Length - offset)];
                DiskStorage.ReadExactly(_file.SafeFileHandle, buffer, offset); return buffer;
            }, b => b.Length);
        }
        public T Get(int index) => MemoryMarshal.Read<T>(Page(index / PerPage).AsSpan(index % PerPage * ElementBytes));
        public void Set(int index, T value)
        {
            Flush(); Span<T> one = stackalloc T[1]; one[0] = value;
            RandomAccess.Write(_file.SafeFileHandle, MemoryMarshal.AsBytes(one), (long)index * ElementBytes); DiskStorage.Evict(CacheId);
        }
        protected override void Close() { _file.Dispose(); if (!_readerOnly) try { File.Delete(_path); } catch (IOException) { } }
    }
}

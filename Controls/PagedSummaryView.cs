using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using Microsoft.Data.Sqlite;
using SemiconductorCsvAnalyzer.Services;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Controls;

/// <summary>WPF view stores filter/sort positions on disk instead of ListCollectionView's full object shadow list.</summary>
public sealed class PagedSummaryView : CollectionView
{
    private readonly IList _source;
    private readonly Func<int, object> _read;
    private PagedVector<int>? _index;
    private readonly SortDescriptionCollection _sort = new();
    public PagedSummaryView(SummaryRows source) : this(source, i => source.Read(i)) { }
    public PagedSummaryView(IList source) : this(source, i => source[i]!) { }
    private PagedSummaryView(IList source, Func<int, object> read) : base(source)
    { _source = source; _read = read; ((INotifyCollectionChanged)_sort).CollectionChanged += (_, _) => Refresh(); }
    public override bool CanSort => true;
    public override bool CanFilter => true;
    public override SortDescriptionCollection SortDescriptions => _sort;
    public override int Count => _index?.Count ?? _source?.Count ?? 0;
    public override bool IsEmpty => Count == 0;
    public override object GetItemAt(int index) => _source[_index == null ? index : _index[index]]!;
    public override int IndexOf(object item)
    {
        int source = _source.IndexOf(item); if (_index == null) return source;
        for (int i = 0; i < _index.Count; i++) if (_index[i] == source) return i;
        return -1;
    }
    protected override IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return _read(_index == null ? i : _index[i]); }
    public void ReleaseIndex() { _index?.Dispose(); _index = null; }
    protected override void RefreshOverride()
    {
        if (_source == null) return;
        if (_sort.Count == 0 && Filter == null)
        { _index?.Dispose(); _index = null; OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); return; }
        var next = new PagedVector<int>(); string? path = null;
        try
        {
            if (_sort.Count == 0)
            {
                for (int i = 0; i < _source.Count; i++) if (Filter == null || Filter(_read(i))) next.Add(i);
            }
            else
            {
                path = DiskStorage.NewPath(".summary-order"); using var db = DiskStorage.OpenDatabase(path);
                db.CreateCollation("CULTURE", StringComparer.CurrentCulture.Compare);
                using var create = db.CreateCommand(); create.CommandText = "CREATE TABLE positions(slot INTEGER" + string.Concat(Enumerable.Range(0, _sort.Count).Select(i => ",k" + i)) + ")"; create.ExecuteNonQuery();
                using (var transaction = db.BeginTransaction())
                using (var insert = db.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO positions VALUES($slot" + string.Concat(Enumerable.Range(0, _sort.Count).Select(i => ",$k" + i)) + ")";
                    insert.Parameters.Add(new("$slot", null)); for (int k = 0; k < _sort.Count; k++) insert.Parameters.Add(new("$k" + k, null));
                    for (int i = 0; i < _source.Count; i++)
                    {
                        var row = _read(i); if (Filter != null && !Filter(row)) continue;
                        insert.Parameters[0].Value = i;
                        for (int k = 0; k < _sort.Count; k++)
                        {
                            object? value = row;
                            foreach (string member in _sort[k].PropertyName.Split('.')) value = value?.GetType().GetProperty(member)?.GetValue(value);
                            insert.Parameters[k + 1].Value = value ?? DBNull.Value;
                        }
                        insert.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                using var read = db.CreateCommand(); read.CommandText = "SELECT slot FROM positions ORDER BY " + string.Join(',', Enumerable.Range(0, _sort.Count).Select(k => $"k{k} COLLATE CULTURE " + (_sort[k].Direction == ListSortDirection.Descending ? "DESC" : "ASC"))) + ",slot";
                using var reader = read.ExecuteReader(); while (reader.Read()) next.Add(reader.GetInt32(0));
            }
            next.Seal(); var old = _index; _index = next; old?.Dispose();
            SetCurrent(Count == 0 ? null : GetItemAt(0), Count == 0 ? -1 : 0);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); OnCurrentChanged();
        }
        catch { next.Dispose(); throw; }
        finally { if (path != null) try { File.Delete(path); } catch (IOException) { } }
    }
}

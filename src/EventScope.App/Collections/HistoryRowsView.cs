using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Collections;
using EventScope.App.Search;
using EventScope.App.ViewModels;
using EventScope.Storage.Search;

namespace EventScope.App.Collections;

/// <summary>
/// The grid's item source when browsing rows that live on disk — captured sessions and search
/// result sets. The read-back counterpart to <see cref="MessageRowsView"/>, and the other
/// implementation of <see cref="IGridRowsView"/>.
///
/// <para>
/// <b>Why a second view rather than seeding the live ring.</b> <see cref="MessageRowsView"/> is a
/// fixed 65,536-slot follow ring whose window is always the newest capacity-worth of what has been
/// appended. History is not bounded that way — a capture at the default on-disk cap holds far more
/// rows than the ring has slots — and it is not appended, it is addressed. Seeding the ring would
/// cap history at the ring size and would give its monotonic head sequence a second, conflicting
/// meaning. Splitting the two keeps the live path's carefully-measured behaviour untouched.
/// </para>
///
/// <para>
/// <b><see cref="IDataGridCollectionView"/> is what makes this virtualize at all</b> — see
/// <see cref="IGridRowsView"/>'s remarks and the measurement recorded in
/// <see cref="MessageRowsView"/>'s. Without the marker interface the <c>DataGrid</c> would wrap
/// this in a <c>DataGridCollectionView</c> and eagerly enumerate every historical row at bind
/// time, which for a multi-million-row capture is precisely the failure this design exists to
/// prevent.
/// </para>
///
/// <para>
/// Simpler than the live ring in one important way: nothing shifts underneath it. A row's index is
/// its identity for as long as the source is bound, so there is no follow-window recompute, no
/// in-place repopulation of realized rows, and no sequence drift. Row view models are still pooled
/// and realized only on demand.
/// </para>
///
/// <para>Must be touched only from the UI thread.</para>
/// </summary>
public sealed class HistoryRowsView : IGridRowsView
{
    private readonly Dictionary<int, MessageRowViewModel> _realized = new();
    private readonly Stack<MessageRowViewModel> _pool = new();
    private readonly RingSearchFilter _searchFilter = new();

    private IHistoryPageSource? _source;
    private MessageRowViewModel? _selected;
    private long _indexerReads;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>What is being browsed, for the status bar. Empty when nothing is bound.</summary>
    public string Description => _source?.Description ?? string.Empty;

    /// <summary>Total rows on disk behind this view, before the <see cref="Count"/> clamp.</summary>
    public long TotalRows => _source?.Count ?? 0;

    public long IndexerReads => Interlocked.Read(ref _indexerReads);

    public void ResetIndexerReadCount() => Interlocked.Exchange(ref _indexerReads, 0);

    /// <summary>
    /// Binds a new source and resets the grid. Disposes the previous one, which is what releases
    /// any day-file handles the old browse was holding — retention cannot delete a day directory on
    /// Windows while something still has it open.
    /// </summary>
    public void SetSource(IHistoryPageSource? source)
    {
        if (ReferenceEquals(_source, source)) return;

        _source?.Dispose();
        _source = source;

        _realized.Clear();
        _pool.Clear();
        _selected = null;
        _currentPosition = -1;

        RaiseReset();
    }

    private void PopulateAt(MessageRowViewModel vm, int index)
    {
        if (_source is not null && _source.TryGet(index, out var hit))
        {
            vm.PopulateFromStore(index, hit);

            vm.IsSearchHit = _searchFilter.IsActive &&
                (_searchFilter.Matches(hit.Preview)
                 || _searchFilter.Matches(hit.Subject)
                 || _searchFilter.Matches(hit.CorrelationId));
            return;
        }

        // A row the source cannot produce - a day file deleted mid-browse, or a malformed page.
        // Rendering it as an unavailable row keeps the grid usable; throwing from the indexer
        // would tear down the whole view for one bad row.
        vm.PopulateFromStore(index, UnavailableRow(index));
        vm.IsSearchHit = false;
    }

    private static SearchHit UnavailableRow(int index) => new(
        Day: string.Empty,
        MessageRowId: 0,
        EnqueuedTicks: 0,
        SegmentId: -1,
        Offset: 0,
        Length: 0,
        MessageId: null,
        CorrelationId: null,
        Subject: string.Empty,
        Preview: "(unavailable)",
        Partition: 0,
        Flags: Core.Models.MessageFlags.PayloadEvicted,
        IndexHwm: SearchHit.IndexHwmNotApplicable);

    /// <summary>Sets the instant search query and refreshes every realized row against it, so a
    /// query change shows up immediately rather than waiting for a scroll.</summary>
    public void SetSearchQuery(string? query)
    {
        _searchFilter.SetQuery(query);
        foreach (var (index, vm) in _realized)
        {
            PopulateAt(vm, index);
        }
    }

    /// <summary>Hook this to <c>DataGrid.UnloadingRow</c>. Recycles the row's view model unless it
    /// is the current selection, which must never be handed to a different logical row.</summary>
    public void NotifyRowUnloaded(int index)
    {
        if (!_realized.TryGetValue(index, out var vm)) return;
        _realized.Remove(index);
        if (!ReferenceEquals(vm, _selected))
        {
            _pool.Push(vm);
        }
    }

    public void SetSelected(MessageRowViewModel? vm) => _selected = vm;

    public void ForceReset()
    {
        // Preserve the selection's identity across the reset the same way the live ring does -
        // DataGrid re-resolves SelectedItem by reference, so it must stay reachable at its index.
        var selected = _selected;
        _realized.Clear();
        if (selected is not null)
        {
            var index = (int)selected.Sequence;
            if (index >= 0 && index < Count)
            {
                _realized[index] = selected;
            }
        }

        RaiseReset();
    }

    // ---- IList / IReadOnlyList<T> ----

    /// <summary>Clamped to <see cref="int.MaxValue"/> because <see cref="IList"/> counts in
    /// <see cref="int"/>. At the default on-disk cap a capture is some three orders of magnitude
    /// short of that ceiling, so the clamp is a correctness guard, not a limit anyone reaches.</summary>
    public int Count => (int)Math.Min(_source?.Count ?? 0, int.MaxValue);

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot { get; } = new();

    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            Interlocked.Increment(ref _indexerReads);

            if (_realized.TryGetValue(index, out var vm)) return vm;

            vm = _pool.Count > 0 ? _pool.Pop() : new MessageRowViewModel();
            PopulateAt(vm, index);
            _realized[index] = vm;
            return vm;
        }
        set => throw new NotSupportedException("HistoryRowsView is read-only.");
    }

    MessageRowViewModel IReadOnlyList<MessageRowViewModel>.this[int index] => (MessageRowViewModel)this[index]!;

    public int IndexOf(object? value)
    {
        if (value is not MessageRowViewModel vm) return -1;
        var index = vm.Sequence;
        if (index < 0 || index >= Count) return -1;
        return _realized.TryGetValue((int)index, out var current) && ReferenceEquals(current, vm) ? (int)index : -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<MessageRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return (MessageRowViewModel)this[i]!;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void RaiseReset() =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    int IList.Add(object? value) => throw new NotSupportedException("HistoryRowsView is read-only.");
    void IList.Clear() => throw new NotSupportedException("HistoryRowsView is read-only.");
    void IList.Insert(int index, object? value) => throw new NotSupportedException("HistoryRowsView is read-only.");
    void IList.Remove(object? value) => throw new NotSupportedException("HistoryRowsView is read-only.");
    void IList.RemoveAt(int index) => throw new NotSupportedException("HistoryRowsView is read-only.");
    void ICollection.CopyTo(Array array, int index) => throw new NotSupportedException("HistoryRowsView is read-only.");

    // ---- IDataGridCollectionView ----
    //
    // As on MessageRowsView, the marker interface rather than these members is what does the work.
    // Sorting, filtering and grouping happen upstream against SQLite - a history browse is already
    // an ordered query - so those members report "unsupported" honestly instead of pretending.

    public IEnumerable SourceCollection => this;
    public bool CanFilter => false;
    public bool CanSort => false;
    public bool CanGroup => false;

    public Func<object, bool> Filter
    {
        get => null!;
        set => throw new NotSupportedException("HistoryRowsView does not support filtering; filter upstream against SQLite.");
    }

    public DataGridSortDescriptionCollection SortDescriptions { get; } = [];
    public bool IsGrouping => false;
    public int GroupingDepth => 0;
    public string GetGroupingPropertyNameAtDepth(int level) => null!;
    public IAvaloniaReadOnlyList<object> Groups { get; } = new AvaloniaList<object>();
    public bool IsEmpty => Count == 0;

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public void Refresh() => ForceReset();

    public IDisposable DeferRefresh() => NoopDisposable.Instance;

    private long _currentPosition = -1;

    public object CurrentItem =>
        _currentPosition >= 0 && _currentPosition < Count ? this[(int)_currentPosition]! : null!;

    public int CurrentPosition => (int)_currentPosition;
    public bool IsCurrentAfterLast => _currentPosition >= Count;
    public bool IsCurrentBeforeFirst => _currentPosition < 0;

    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;
    public event EventHandler? CurrentChanged;

    public bool MoveCurrentToPosition(int position)
    {
        if (position < -1 || position > Count) return false;
        var changing = new DataGridCurrentChangingEventArgs();
        CurrentChanging?.Invoke(this, changing);
        if (changing.Cancel) return false;

        _currentPosition = position;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return position >= 0 && position < Count;
    }

    public bool MoveCurrentToFirst() => MoveCurrentToPosition(0);
    public bool MoveCurrentToLast() => MoveCurrentToPosition(Count - 1);
    public bool MoveCurrentToNext() => MoveCurrentToPosition((int)_currentPosition + 1);
    public bool MoveCurrentToPrevious() => MoveCurrentToPosition((int)_currentPosition - 1);

    public bool MoveCurrentTo(object? item)
    {
        var index = IndexOf(item);
        return index >= 0 && MoveCurrentToPosition(index);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

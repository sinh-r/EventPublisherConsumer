using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Collections;
using EventScope.App.Search;
using EventScope.App.ViewModels;
using EventScope.Core.Models;

namespace EventScope.App.Collections;

/// <summary>
/// The DataGrid's item source. Backed by a fixed-capacity ring of <see cref="MessageHeader"/>
/// structs — never an <c>ObservableCollection&lt;MessageRowViewModel&gt;</c>, which would
/// require one live view model per message. Row view models are materialized only for
/// realized (on-screen) rows and recycled through a pool.
///
/// Must be touched only from the UI thread: the ingest coalescer marshals batched headers
/// here once per tick, so no internal locking is needed.
///
/// <para>
/// This implements <see cref="IDataGridCollectionView"/> as well as <see cref="IList"/> —
/// both are load-bearing, and the reason is not the one the original plan assumed.
/// <c>DataGrid.OnItemsSourcePropertyChanged</c> wraps any <c>ItemsSource</c> that is
/// <i>not already</i> an <see cref="IDataGridCollectionView"/> in a <c>DataGridCollectionView</c>
/// via <c>DataGridDataConnection.CreateView</c>, and that type's <c>CopySourceToInternalList</c>
/// eagerly enumerates the entire source — exactly the materialization this class exists to
/// avoid. Implementing the marker interface ourselves is what keeps DataGrid from wrapping us
/// at all. <c>IList</c> is what then makes <c>DataGridDataConnection.GetDataItem</c> resolve
/// through <c>this[int]</c> instead of a full-source enumeration to find <c>Count</c>: its
/// concrete-type check for <c>DataGridCollectionView</c> fails for this class (we aren't one),
/// so it falls through to <c>List = DataSource as IList</c>, which is us. Verified against
/// Avalonia.Controls.DataGrid 11.3.1 source and confirmed by a spike test — see
/// DataGridVirtualizationSpikeTests. Sort/filter/group are not needed here (search and
/// filtering are done upstream, against SQLite — see the build plan's tiered search design)
/// so those members are honest no-ops rather than stubs pretending to support something.
/// </para>
/// </summary>
public sealed class MessageRowsView : IGridRowsView
{
    private readonly int _capacity;
    private readonly MessageHeader[] _ring;
    private readonly string?[] _previews;
    private readonly string[] _subjects;
    private readonly string[] _correlationIds;

    /// <summary>The day directory each row's payload was written under. A ring of references to
    /// the one or two interned day strings live at any moment, not distinct strings — the array
    /// costs a pointer per slot, nothing more.
    ///
    /// <para>Load-bearing for replay: a run that starts from a Kafka backlog files month-old
    /// messages under <i>today</i>, so a row's broker timestamp says nothing about which directory
    /// holds its bytes. Segment ids restart at 0 every day, so inferring the day from the timestamp
    /// does not merely fail to find the payload — it can find a different message's bytes at the
    /// same coordinates.</para></summary>
    private readonly string[] _days;

    /// <summary>Total messages ever appended; also the sequence number of the next append.</summary>
    private long _head;

    private long _windowBaseSeq;
    private int _windowLength;
    private bool _pinned;
    private long _pinnedNewCount;

    private readonly Dictionary<int, MessageRowViewModel> _realized = new();
    private readonly Stack<MessageRowViewModel> _pool = new();
    private MessageRowViewModel? _selected;

    /// <summary>The instant tier of tiered search (build plan §5 M2). Recomputed for every
    /// realized row on every populate, including the steady-state refresh that raises no
    /// collection notification — so a query change only needs a <see cref="ForceReset"/> to
    /// show up immediately on whatever's already realized; it doesn't need its own live
    /// subscription machinery the way the M1-remainder row-styling investigation found
    /// prohibitively expensive for class state (see <c>RowStateClassSync</c>'s remarks) -
    /// <see cref="MessageRowViewModel.IsSearchHit"/> is a plain property, not a Classes.Set
    /// call, so it doesn't carry that cost.</summary>
    private readonly RingSearchFilter _searchFilter = new();

    /// <summary>Test/diagnostic instrumentation only — counts indexer reads.</summary>
    private long _indexerReads;

    public MessageRowsView(int capacity = 65536)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ring = new MessageHeader[capacity];
        _previews = new string?[capacity];
        _subjects = new string[capacity];
        _correlationIds = new string[capacity];
        _days = new string[capacity];
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public bool IsPinned => _pinned;
    public long PinnedNewCount => _pinnedNewCount;
    public long TotalAppended => _head;
    public long IndexerReads => Interlocked.Read(ref _indexerReads);

    public void ResetIndexerReadCount() => Interlocked.Exchange(ref _indexerReads, 0);

    /// <summary>Freezes the window (a row is selected, or the user scrolled off the top).</summary>
    public void Pin()
    {
        if (_pinned) return;
        _pinned = true;
        _pinnedNewCount = 0;
    }

    /// <summary>Releases the freeze and snaps back to following the ring head.</summary>
    public void Unpin()
    {
        if (!_pinned) return;
        _pinned = false;
        _pinnedNewCount = 0;
        RecomputeFollowWindow(forceReset: true);
    }

    /// <summary>
    /// Appends one coalescer tick's worth of headers. In follow mode this recomputes the
    /// window and either raises one <c>Reset</c> (warm-up growth, or the transition to a
    /// full ring) or silently refreshes whatever rows are currently realized (steady
    /// state — the common case, and the reason the UI stays cheap at 10k msg/s).
    /// In pinned mode this only advances the "N new messages" counter.
    /// </summary>
    public void AppendBatch(
        ReadOnlySpan<MessageHeader> headers,
        ReadOnlySpan<string?> previews,
        ReadOnlySpan<string> subjects,
        ReadOnlySpan<string> correlationIds) =>
        AppendBatch(headers, previews, subjects, correlationIds, days: default);

    /// <inheritdoc cref="AppendBatch(ReadOnlySpan{MessageHeader}, ReadOnlySpan{string}, ReadOnlySpan{string}, ReadOnlySpan{string})"/>
    /// <param name="days">The day directory each row's payload was written under, parallel to
    /// <paramref name="headers"/>. An empty span means no day is known and rows are stamped empty,
    /// which is what the overload above passes and what leaves a row's payload to be resolved by
    /// inference — see <see cref="_days"/> for why inference is not good enough once a backlog is
    /// read.</param>
    public void AppendBatch(
        ReadOnlySpan<MessageHeader> headers,
        ReadOnlySpan<string?> previews,
        ReadOnlySpan<string> subjects,
        ReadOnlySpan<string> correlationIds,
        ReadOnlySpan<string> days)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var slot = (int)(_head % _capacity);
            _ring[slot] = headers[i];
            _previews[slot] = previews[i];
            _subjects[slot] = subjects[i];
            _correlationIds[slot] = correlationIds[i];
            _days[slot] = i < days.Length ? days[i] : string.Empty;
            _head++;
        }

        if (headers.Length == 0) return;

        if (_pinned)
        {
            _pinnedNewCount += headers.Length;
            return;
        }

        RecomputeFollowWindow(forceReset: false);
    }

    /// <summary>Test-only convenience for appending a single already-known row.</summary>
    public void Append(MessageHeader header, string? preview, string subject, string correlationId, string day = "") =>
        AppendBatch([header], [preview], [subject], [correlationId], [day]);

    private void RecomputeFollowWindow(bool forceReset)
    {
        var newBase = Math.Max(0, _head - _capacity);
        var newLength = (int)Math.Min(_head, _capacity);
        var lengthChanged = newLength != _windowLength;
        _windowBaseSeq = newBase;
        _windowLength = newLength;

        if (forceReset || lengthChanged)
        {
            // Preserve the selected VM's identity across the reset — DataGrid re-resolves
            // SelectedItem by reference equality, so it must still be reachable through
            // this[int] at its (possibly shifted) index. Every other realized row is
            // dropped; its container will re-request an item after the reset.
            var selected = _selected;
            _realized.Clear();
            if (selected is not null)
            {
                var idx = selected.Sequence - _windowBaseSeq;
                if (idx >= 0 && idx < _windowLength)
                {
                    _realized[(int)idx] = selected;
                }
            }

            RaiseReset();
            return;
        }

        // Steady state: the ring evicted the oldest entries and appended new ones, so
        // every index's underlying sequence shifted. Refresh in place; raise nothing.
        foreach (var (index, vm) in _realized)
        {
            PopulateAt(vm, _windowBaseSeq + index);
        }
    }

    private void PopulateAt(MessageRowViewModel vm, long sequence)
    {
        var slot = (int)(sequence % _capacity);
        vm.Populate(
            sequence, in _ring[slot], _subjects[slot], _correlationIds[slot], _previews[slot], _days[slot] ?? string.Empty);

        vm.IsSearchHit = _searchFilter.IsActive &&
            (_searchFilter.Matches(_previews[slot])
             || _searchFilter.Matches(_subjects[slot])
             || _searchFilter.Matches(_correlationIds[slot]));
    }

    /// <summary>Sets (or clears, with <see langword="null"/> or empty) the instant search
    /// query and immediately refreshes every currently realized row against it — a query
    /// change must show up on-screen right away, not wait for the next ingest tick.</summary>
    public void SetSearchQuery(string? query)
    {
        _searchFilter.SetQuery(query);
        foreach (var (index, vm) in _realized)
        {
            PopulateAt(vm, _windowBaseSeq + index);
        }
    }

    /// <summary>Hook this to <c>DataGrid.UnloadingRow</c>. Recycles the row's view model
    /// unless it is the current selection, which must never be handed to a different
    /// logical row.</summary>
    public void NotifyRowUnloaded(int index)
    {
        if (!_realized.TryGetValue(index, out var vm)) return;
        _realized.Remove(index);
        if (!ReferenceEquals(vm, _selected))
        {
            _pool.Push(vm);
        }
    }

    /// <summary>Hook this to the view model backing <c>DataGrid.SelectedItem</c> changing.</summary>
    public void SetSelected(MessageRowViewModel? vm) => _selected = vm;

    /// <summary>Forces a Reset without changing the window — used by filter changes and
    /// by the DataGrid virtualization spike test.</summary>
    public void ForceReset() => RecomputeFollowWindow(forceReset: true);

    // ---- IList / IReadOnlyList<T> ----

    public int Count => _windowLength;
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot { get; } = new();

    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_windowLength) throw new ArgumentOutOfRangeException(nameof(index));
            Interlocked.Increment(ref _indexerReads);

            if (_realized.TryGetValue(index, out var vm)) return vm;

            vm = _pool.Count > 0 ? _pool.Pop() : new MessageRowViewModel();
            PopulateAt(vm, _windowBaseSeq + index);
            _realized[index] = vm;
            return vm;
        }
        set => throw new NotSupportedException("MessageRowsView is read-only.");
    }

    MessageRowViewModel IReadOnlyList<MessageRowViewModel>.this[int index] => (MessageRowViewModel)this[index]!;

    public int IndexOf(object? value)
    {
        if (value is not MessageRowViewModel vm) return -1;
        var idx = vm.Sequence - _windowBaseSeq;
        if (idx < 0 || idx >= _windowLength) return -1;
        return _realized.TryGetValue((int)idx, out var current) && ReferenceEquals(current, vm) ? (int)idx : -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<MessageRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < _windowLength; i++)
            yield return (MessageRowViewModel)this[i]!;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void RaiseReset() =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    int IList.Add(object? value) => throw new NotSupportedException("MessageRowsView is read-only.");
    void IList.Clear() => throw new NotSupportedException("MessageRowsView is read-only.");
    void IList.Insert(int index, object? value) => throw new NotSupportedException("MessageRowsView is read-only.");
    void IList.Remove(object? value) => throw new NotSupportedException("MessageRowsView is read-only.");
    void IList.RemoveAt(int index) => throw new NotSupportedException("MessageRowsView is read-only.");
    void ICollection.CopyTo(Array array, int index) => throw new NotSupportedException("MessageRowsView is read-only.");

    // ---- IDataGridCollectionView ----
    //
    // The marker interface, not these members, is what does the work (see the class
    // remarks). Sorting, filtering and grouping happen upstream against SQLite, not here,
    // so those members honestly report "unsupported" rather than pretending to implement
    // a feature nothing exercises. Current-item navigation is implemented for real, since
    // DataGrid's own selection plumbing may call it.

    public IEnumerable SourceCollection => this;
    public bool CanFilter => false;
    public bool CanSort => false;
    public bool CanGroup => false;

    public Func<object, bool> Filter
    {
        get => null!;
        set => throw new NotSupportedException("MessageRowsView does not support filtering; filter upstream against SQLite.");
    }

    public DataGridSortDescriptionCollection SortDescriptions { get; } = [];
    public bool IsGrouping => false;
    public int GroupingDepth => 0;
    public string GetGroupingPropertyNameAtDepth(int level) => null!;
    public IAvaloniaReadOnlyList<object> Groups { get; } = new AvaloniaList<object>();
    public bool IsEmpty => _windowLength == 0;

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public void Refresh() => ForceReset();

    public IDisposable DeferRefresh() => NoopDisposable.Instance;

    private long _currentPosition = -1;

    public object CurrentItem =>
        _currentPosition >= 0 && _currentPosition < _windowLength
            ? this[(int)_currentPosition]!
            : null!;

    public int CurrentPosition => (int)_currentPosition;
    public bool IsCurrentAfterLast => _currentPosition >= _windowLength;
    public bool IsCurrentBeforeFirst => _currentPosition < 0;

    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;
    public event EventHandler? CurrentChanged;

    public bool MoveCurrentToPosition(int position)
    {
        if (position < -1 || position > _windowLength) return false;
        var changing = new DataGridCurrentChangingEventArgs();
        CurrentChanging?.Invoke(this, changing);
        if (changing.Cancel) return false;

        _currentPosition = position;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return position >= 0 && position < _windowLength;
    }

    public bool MoveCurrentToFirst() => MoveCurrentToPosition(0);
    public bool MoveCurrentToLast() => MoveCurrentToPosition(_windowLength - 1);
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

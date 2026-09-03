using System.Collections.Concurrent;
using EventScope.Storage.Search;

namespace EventScope.App.Collections;

/// <summary>
/// Random access by row index over messages that live on disk. The seam
/// <see cref="HistoryRowsView"/> binds to, so the view can be tested against a hand-rolled source
/// with no SQLite involved, and so browsing a day and showing a search result set are the same
/// grid over two different sources.
/// </summary>
/// <remarks>
/// <see cref="TryGet"/> is synchronous because the grid's indexer is: <c>DataGrid</c> asks for a
/// row on the UI thread and needs it immediately. Implementations must therefore keep a warm hit
/// cheap and bound what a cold miss costs.
/// </remarks>
public interface IHistoryPageSource : IDisposable
{
    /// <summary>Total rows addressable by <see cref="TryGet"/>.</summary>
    long Count { get; }

    /// <summary>A short label for the status bar — what is being browsed.</summary>
    string Description { get; }

    /// <summary>The row at <paramref name="index"/>, or <see langword="false"/> when it cannot be
    /// read (a day file deleted underneath the browse, a malformed page). Callers render an
    /// unavailable row rather than treating this as fatal.</summary>
    bool TryGet(long index, out SearchHit hit);
}

/// <summary>
/// An already-materialized result set — full-text search hits, or a deep scan's output. Ordered
/// oldest-first like the live grid, so moving between live and history does not silently invert
/// the reading direction.
/// </summary>
public sealed class FixedResultsPageSource(IReadOnlyList<SearchHit> results, string description)
    : IHistoryPageSource
{
    public long Count => results.Count;

    public string Description => description;

    public bool TryGet(long index, out SearchHit hit)
    {
        if (index < 0 || index >= results.Count)
        {
            hit = null!;
            return false;
        }

        hit = results[(int)index];
        return true;
    }

    public void Dispose()
    {
        // Nothing held open - the rows are already in memory.
    }
}

/// <summary>
/// One contiguous run of rows inside a single day file, and where it starts in the browse's
/// global index space.
/// </summary>
/// <param name="MinRowId">The day's lowest <c>messages.id</c>, so a dense day can turn a global
/// index straight into a rowid.</param>
/// <param name="IsDense">Whether ids run contiguously — see <see cref="DaySummary.IsDense"/>.
/// A sparse day pages positionally instead, which is correct but costs an index walk.</param>
public readonly record struct DaySpan(string Day, long StartIndex, long Count, long MinRowId, bool IsDense);

/// <summary>
/// Browses one or more captured days as a single continuous list, oldest first, reading pages out
/// of SQLite on demand.
///
/// <para>
/// Rows are fetched a page at a time and kept in a bounded, approximately-LRU cache — the same
/// shape <c>SegmentReader</c> uses for decompressed blocks, and for the same reason: a scroll is
/// overwhelmingly local, so a small cache turns almost every row request into a dictionary hit,
/// while the bound is what stops browsing a multi-million-row capture from pulling it all into
/// memory. That bound is the whole point of this project, so it is enforced here rather than
/// assumed.
/// </para>
/// </summary>
public sealed class DayRangePageSource : IHistoryPageSource
{
    /// <summary>Rows per fetch. Large enough that a screenful is one query, small enough that a
    /// cold miss stays cheap.</summary>
    public const int PageSize = 256;

    /// <summary>Pages retained — at <see cref="PageSize"/> rows each, roughly 8,000 rows of
    /// previews, far more than any screenful plus its scroll neighbourhood.</summary>
    public const int MaxCachedPages = 32;

    private readonly HistoryQueryService _history;
    private readonly IReadOnlyList<DaySpan> _spans;
    private readonly ConcurrentDictionary<(string Day, long PageStart), IReadOnlyList<SearchHit>> _pages = new();
    private readonly ConcurrentQueue<(string Day, long PageStart)> _pageOrder = new();

    public DayRangePageSource(HistoryQueryService history, IReadOnlyList<DaySummary> days, string description)
    {
        _history = history;
        Description = description;

        var spans = new List<DaySpan>(days.Count);
        var start = 0L;
        foreach (var day in days)
        {
            if (day.RowCount == 0) continue; // an evicted or empty day contributes no rows
            spans.Add(new DaySpan(day.Day, start, day.RowCount, day.MinRowId, day.IsDense));
            start += day.RowCount;
        }

        _spans = spans;
        Count = start;
    }

    public long Count { get; }

    public string Description { get; }

    /// <summary>The days this source spans, in order — for tests and for the status bar.</summary>
    public IReadOnlyList<DaySpan> Spans => _spans;

    public bool TryGet(long index, out SearchHit hit)
    {
        hit = null!;
        if (index < 0 || index >= Count) return false;

        var span = FindSpan(index);
        if (span is not { } found) return false;

        var localIndex = index - found.StartIndex;
        var pageStart = localIndex / PageSize * PageSize;

        var page = GetOrFetchPage(found, pageStart);
        var withinPage = (int)(localIndex - pageStart);
        if (withinPage < 0 || withinPage >= page.Count) return false;

        hit = page[withinPage];
        return true;
    }

    /// <summary>Binary search rather than a scan: a long-running capture accumulates a day per
    /// day, and a scrollbar drag asks for arbitrary indices many times per second.</summary>
    private DaySpan? FindSpan(long index)
    {
        var low = 0;
        var high = _spans.Count - 1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var span = _spans[mid];

            if (index < span.StartIndex) high = mid - 1;
            else if (index >= span.StartIndex + span.Count) low = mid + 1;
            else return span;
        }

        return null;
    }

    private IReadOnlyList<SearchHit> GetOrFetchPage(DaySpan span, long pageStart)
    {
        var key = (span.Day, pageStart);
        if (_pages.TryGetValue(key, out var cached)) return cached;

        var page = span.IsDense
            ? _history.PageFromRowId(span.Day, span.MinRowId + pageStart, PageSize)
            : _history.PageByOffset(span.Day, pageStart, PageSize);

        if (_pages.TryAdd(key, page))
        {
            _pageOrder.Enqueue(key);
            EvictIfOverCapacity();
        }

        return page;
    }

    /// <summary>Approximate LRU — insertion order, not access order. Enough for a read cache whose
    /// access pattern is a scroll, and it avoids the synchronization a true LRU would need.</summary>
    private void EvictIfOverCapacity()
    {
        while (_pages.Count > MaxCachedPages && _pageOrder.TryDequeue(out var oldest))
        {
            _pages.TryRemove(oldest, out _);
        }
    }

    public void Dispose()
    {
        _pages.Clear();
        while (_pageOrder.TryDequeue(out _)) { }
    }
}

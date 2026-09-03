using System.Collections.Concurrent;
using EventScope.Core.Abstractions;
using EventScope.Storage.Sqlite;

namespace EventScope.Storage.Segments;

/// <summary>
/// A day-keyed cache of <see cref="SegmentReader"/>s over an already-captured session root, for
/// reading message bodies with no writer involved. <see cref="ForDay"/> hands out the reader for
/// one day directory; <see cref="SegmentReader"/> is itself an <see cref="IPayloadReader"/>, so
/// the caller gets the ordinary read contract back.
///
/// <para>
/// <b>The day is a parameter, not a derivation — this is the whole point of the type.</b> Segment
/// ids restart at 0 every day, so <c>(segmentId, offset)</c> identifies a different message in
/// every day directory. The live cold-read path
/// (<see cref="EventScope.Storage.Sqlite.SessionLayout.DayFor"/>) infers the day from a message's
/// <c>EnqueuedTicks</c>, which is the <i>broker's</i> timestamp, while the directory a message was
/// written to comes from the <i>writer's</i> clock. Those agree only while the app tails a live
/// topic. Read a backlog — or cross midnight mid-batch — and they diverge, and an inferred day
/// either finds nothing or, worse, finds a different message's bytes at the same coordinates.
/// A history row already knows the directory it was read out of
/// (<see cref="EventScope.Storage.Search.SearchHit.Day"/>), so it passes that rather than
/// guessing.
/// </para>
///
/// <para>
/// Open segment handles keep a day directory alive on Windows, which can make retention's
/// recursive delete fail while a browse is in progress. Dispose this when leaving history mode.
/// </para>
/// </summary>
public sealed class HistoryPayloadReaders(string rootDirectory, int blockCacheCapacity = 16) : IDisposable
{
    private readonly ConcurrentDictionary<string, SegmentReader> _readersByDay = new();
    private bool _disposed;

    /// <summary>Smaller than <see cref="SegmentReader"/>'s own default: history browsing reads one
    /// selected row at a time rather than streaming, and may hold a reader open per day.</summary>
    public int BlockCacheCapacity { get; } = blockCacheCapacity;

    /// <summary>The payload reader for <paramref name="day"/>'s segment files. Safe for a day that
    /// does not exist — <see cref="SegmentReader"/>'s contract is to return empty for a segment it
    /// cannot open, which the detail pane already renders as an unavailable payload.</summary>
    public IPayloadReader ForDay(string day)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _readersByDay.GetOrAdd(
            day,
            static (d, state) => new SegmentReader(SessionLayout.DayDirectory(state.Root, d), state.Capacity),
            (Root: rootDirectory, Capacity: BlockCacheCapacity));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var reader in _readersByDay.Values)
        {
            reader.Dispose();
        }

        _readersByDay.Clear();
    }
}

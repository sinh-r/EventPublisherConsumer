using EventScope.Core.Models;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// Day-file rollover (build plan §5 M2, task 1): both the old and new day's files stay usable
/// across the boundary, driven by <see cref="TimeProvider"/> so the midnight crossing is
/// deterministic rather than something a test has to wait real wall-clock time for.
/// </summary>
public sealed class SessionStoreRolloverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-rollover-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MessageHeader HeaderFor(int segmentId, int offset, int length) =>
        new(sequence: 0, enqueuedTicks: 0, rowId: 0, segmentId: segmentId, offset: offset,
            length: length, subjectId: 0, correlationInternId: 0, partition: 0, flags: MessageFlags.None);

    [Fact]
    public async Task Crossing_midnight_opens_a_new_day_while_the_old_one_stays_readable()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 3, 5, 23, 59, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        Assert.Equal("2026-03-05", store.CurrentDay);

        var dayOnePayload = "day one"u8.ToArray();
        var dayOneCoords = store.SegmentWriter.Append(dayOnePayload);
        store.SegmentWriter.Append(new byte[Segments.SegmentFormat.BlockSize]); // force it to disk, see PROGRESS.md §0.1
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: dayOneCoords.SegmentId, Offset: dayOneCoords.Offset, Length: dayOneCoords.Length,
            MessageId: "m-1", CorrelationId: "c-1", Subject: "orders.created",
            Partition: 0, Flags: 0, Preview: "day one", BodyHead: "day one"));

        var dayOneDb = Path.Combine(store.Directory, "2026-03-05.db");
        await SqliteTestHelpers.WaitForRowCountAsync(dayOneDb, 1, TimeSpan.FromSeconds(2), Ct);

        // Cross midnight.
        time.Set(new DateTimeOffset(2026, 3, 6, 0, 0, 5, TimeSpan.Zero));
        store.EnsureCurrentDay();

        Assert.Equal("2026-03-06", store.CurrentDay);
        Assert.Equal(Path.Combine(_root, "2026-03-06"), store.Directory);

        var dayTwoPayload = "day two"u8.ToArray();
        var dayTwoCoords = store.SegmentWriter.Append(dayTwoPayload);
        store.SegmentWriter.Append(new byte[Segments.SegmentFormat.BlockSize]);
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: dayTwoCoords.SegmentId, Offset: dayTwoCoords.Offset, Length: dayTwoCoords.Length,
            MessageId: "m-2", CorrelationId: "c-2", Subject: "orders.created",
            Partition: 0, Flags: 0, Preview: "day two", BodyHead: "day two"));

        var dayTwoDb = Path.Combine(store.Directory, "2026-03-06.db");
        await SqliteTestHelpers.WaitForRowCountAsync(dayTwoDb, 1, TimeSpan.FromSeconds(2), Ct);

        // The new day's own reads work through the store's current-day properties.
        var readTwo = await store.SegmentReader.ReadAsync(
            HeaderFor(dayTwoCoords.SegmentId, dayTwoCoords.Offset, dayTwoCoords.Length), Ct);
        Assert.Equal(dayTwoPayload, readTwo.ToArray());

        // The old day is not current anymore, but its own reader (opened before rollover)
        // still has to work - a detail-pane read against a pre-rollover row must not fail
        // just because the clock moved on.
        var oldReader = store.GetOrOpenReader("2026-03-05");
        var readOne = await oldReader.ReadAsync(
            HeaderFor(dayOneCoords.SegmentId, dayOneCoords.Offset, dayOneCoords.Length), Ct);
        Assert.Equal(dayOnePayload, readOne.ToArray());

        // Both files are real, on disk, independently.
        Assert.True(File.Exists(dayOneDb));
        Assert.True(File.Exists(dayTwoDb));
        Assert.True(File.Exists(Path.Combine(_root, "2026-03-05", "000000.seg")));
        Assert.True(File.Exists(Path.Combine(_root, "2026-03-06", "000000.seg")));

        // Old day's writer eventually seals and closes on its own background task - wait for
        // that rather than asserting instantly, since it's deliberately asynchronous (see
        // SessionStore's remarks on why rollover must never block ingest into the new day).
        await SqliteTestHelpers.AssertFtsIntegrityAsync(dayOneDb, Ct);
        await SqliteTestHelpers.AssertFtsIntegrityAsync(dayTwoDb, Ct);
    }

    [Fact]
    public void EnsureCurrentDay_is_a_no_op_within_the_same_day()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        var writerBefore = store.Writer;
        var segmentWriterBefore = store.SegmentWriter;

        time.Advance(TimeSpan.FromHours(1));
        store.EnsureCurrentDay();

        Assert.Equal("2026-03-05", store.CurrentDay);
        Assert.Same(writerBefore, store.Writer);
        Assert.Same(segmentWriterBefore, store.SegmentWriter);
    }

    [Fact]
    public async Task GetOrOpenReader_for_a_day_that_never_existed_returns_empty_reads_not_a_throw()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        var reader = store.GetOrOpenReader("2020-01-01");
        var result = await reader.ReadAsync(HeaderFor(segmentId: 0, offset: 0, length: 10), Ct);

        Assert.True(result.IsEmpty);
    }
}

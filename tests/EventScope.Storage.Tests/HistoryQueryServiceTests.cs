using EventScope.Storage.Search;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// <see cref="HistoryQueryService"/>'s day listing and keyset paging — the read-back path that
/// makes an already-captured session browsable. Drives real <see cref="SessionStore"/> day files
/// through real ingest, the same way <see cref="FtsSearchServiceTests"/> does, rather than
/// hand-crafting a schema.
/// </summary>
public sealed class HistoryQueryServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-history-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task WriteMessageAsync(
        SessionStore store, string body, string correlationId, long enqueuedTicks, CancellationToken ct)
    {
        var coords = store.SegmentWriter.Append(System.Text.Encoding.UTF8.GetBytes(body));
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: enqueuedTicks, ReceivedTicks: enqueuedTicks,
            SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
            MessageId: Guid.NewGuid().ToString(), CorrelationId: correlationId, Subject: "orders.created",
            Partition: 3, Flags: 0, Preview: body, BodyHead: body));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private async Task<string> WriteDayAsync(int messageCount)
    {
        using var store = new SessionStore(_root);
        for (var i = 0; i < messageCount; i++)
        {
            await WriteMessageAsync(store, $"body-{i}", $"c-{i}", i, Ct);
        }

        return store.CurrentDay;
    }

    [Fact]
    public async Task Lists_a_captured_day_with_its_row_count_and_timestamp_range()
    {
        using (var store = new SessionStore(_root))
        {
            await WriteMessageAsync(store, "one", "c-1", 1_000, Ct);
            await WriteMessageAsync(store, "two", "c-2", 5_000, Ct);
        }

        var day = Assert.Single(new HistoryQueryService(_root).ListDays(Ct));

        Assert.Equal(2, day.RowCount);
        Assert.Equal(1_000, day.MinEnqueuedTicks);
        Assert.Equal(5_000, day.MaxEnqueuedTicks);
    }

    [Fact]
    public void An_absent_root_lists_nothing_rather_than_throwing()
    {
        Assert.Empty(new HistoryQueryService(Path.Combine(_root, "never-streamed")).ListDays(Ct));
    }

    [Fact]
    public async Task A_normally_written_day_reports_contiguous_row_ids()
    {
        await WriteDayAsync(5);

        var day = Assert.Single(new HistoryQueryService(_root).ListDays(Ct));

        // Nothing in the write path deletes individual rows, so the n-th row of a day is
        // MinRowId + n - which is what lets a scrollbar jump be a rowid seek. Asserted rather
        // than assumed, because the paging fallback depends on knowing when it is false.
        Assert.True(day.IsDense);
        Assert.Equal(5, day.MaxRowId - day.MinRowId + 1);
    }

    [Fact]
    public async Task Pages_a_day_in_capture_order_and_stops_at_the_end()
    {
        var day = await WriteDayAsync(5);
        var history = new HistoryQueryService(_root);

        var first = history.PageFromRowId(day, 0, 3);
        Assert.Equal(["body-0", "body-1", "body-2"], first.Select(h => h.Preview));

        var second = history.PageFromRowId(day, first[^1].MessageRowId + 1, 3);
        Assert.Equal(["body-3", "body-4"], second.Select(h => h.Preview));

        Assert.Empty(history.PageFromRowId(day, second[^1].MessageRowId + 1, 3));
    }

    [Fact]
    public async Task Offset_paging_agrees_with_keyset_paging()
    {
        var day = await WriteDayAsync(6);
        var history = new HistoryQueryService(_root);

        var keyset = history.PageFromRowId(day, 0, 6).Select(h => h.Preview);
        var positional = history.PageByOffset(day, skip: 0, take: 6).Select(h => h.Preview);

        Assert.Equal(keyset, positional);
        Assert.Equal(["body-2", "body-3"], history.PageByOffset(day, skip: 2, take: 2).Select(h => h.Preview));
    }

    [Fact]
    public async Task A_paged_row_carries_the_day_it_was_read_from_and_not_an_inferred_one()
    {
        // The enqueued timestamp here is year-1-shaped - deliberately nothing like the day the
        // writer's clock puts the file under. A row must report the directory it actually came
        // from, because segment ids restart at 0 every day and an inferred day would resolve the
        // same (segmentId, offset) against the wrong file. See HistoryPayloadReaders' remarks.
        using (var store = new SessionStore(_root))
        {
            await WriteMessageAsync(store, "from-the-past", "c-1", enqueuedTicks: 1, Ct);
        }

        var history = new HistoryQueryService(_root);
        var writtenDay = history.ListDays(Ct).Single().Day;

        var hit = Assert.Single(history.PageFromRowId(writtenDay, 0, 10));

        Assert.Equal(writtenDay, hit.Day);
        Assert.NotEqual(SessionLayout.DayFor(hit.EnqueuedTicks), hit.Day);
    }

    [Fact]
    public async Task A_paged_row_carries_its_partition_and_flags()
    {
        var day = await WriteDayAsync(1);

        var hit = Assert.Single(new HistoryQueryService(_root).PageFromRowId(day, 0, 10));

        Assert.Equal(3, hit.Partition);
        Assert.Equal(Core.Models.MessageFlags.None, hit.Flags);
        Assert.Equal(SearchHit.IndexHwmNotApplicable, hit.IndexHwm);
    }

    [Fact]
    public async Task A_history_row_reads_its_body_back_through_the_day_it_names()
    {
        using (var store = new SessionStore(_root))
        {
            await WriteMessageAsync(store, "the body we want back", "c-1", 1, Ct);
        }

        var history = new HistoryQueryService(_root);
        var day = history.ListDays(Ct).Single().Day;
        var hit = Assert.Single(history.PageFromRowId(day, 0, 10));

        using var readers = new HistoryPayloadReaders(_root);
        var header = new Core.Models.MessageHeader(
            sequence: 0, enqueuedTicks: hit.EnqueuedTicks, rowId: hit.MessageRowId,
            segmentId: hit.SegmentId, offset: hit.Offset, length: hit.Length,
            subjectId: 0, correlationInternId: 0, partition: hit.Partition, flags: hit.Flags);

        var bytes = await readers.ForDay(hit.Day).ReadAsync(header, Ct);

        Assert.Equal("the body we want back", System.Text.Encoding.UTF8.GetString(bytes.Span));
    }

    [Fact]
    public async Task Reports_a_zero_count_for_a_day_whose_database_is_gone_rather_than_hiding_it()
    {
        using (var store = new SessionStore(_root))
        {
            await WriteMessageAsync(store, "one", "c-1", 1, Ct);
        }

        var day = SessionLayout.ListDayDirectories(_root).Single();
        File.Delete(SessionLayout.DayDatabasePath(_root, day));

        var summary = Assert.Single(new HistoryQueryService(_root).ListDays(Ct));

        Assert.Equal(day, summary.Day);
        Assert.Equal(0, summary.RowCount);
        Assert.False(summary.IsDense);
    }

    [Fact]
    public void Paging_a_day_that_does_not_exist_returns_nothing()
    {
        Assert.Empty(new HistoryQueryService(_root).PageFromRowId("2019-01-01", 0, 10));
    }

    [Fact]
    public async Task Enumerating_a_day_walks_every_page()
    {
        var day = await WriteDayAsync(7);

        var all = new HistoryQueryService(_root).EnumerateDay(day, pageSize: 2).Select(h => h.Preview);

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"body-{i}"), all);
    }
}

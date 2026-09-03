using EventScope.Storage.Search;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// <see cref="DeepScanner"/> — the last-resort search tier (build plan §5 M2): scans every
/// message's full body, not the 2 KB <c>body_head</c> capped copy FTS indexes, so it can find
/// matches FTS structurally cannot.
/// </summary>
public sealed class DeepScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-deepscan-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task WriteMessageAsync(SessionStore store, byte[] body, CancellationToken ct)
    {
        var coords = store.SegmentWriter.Append(body);
        var bodyHead = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 2048));
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
            MessageId: null, CorrelationId: null, Subject: "orders.created",
            Partition: 0, Flags: 0, Preview: "p", BodyHead: bodyHead));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>A just-written small payload sits in <c>SegmentWriter</c>'s in-memory pending
    /// block until enough accumulates to flush it (see PROGRESS.md §0.1) — <see cref="DeepScanner"/>
    /// reads only from disk via <see cref="Segments.SegmentReader"/>, so a test scanning
    /// small messages right after writing them needs to force that flush first, the same
    /// pattern <c>SessionStoreRolloverTests</c> and others already use.</summary>
    private static void ForceFlushToDisk(SessionStore store) =>
        store.SegmentWriter.Append(new byte[SegmentFormat.BlockSize]);

    private static async Task<List<DeepScanMatch>> ScanAsync(
        string dayDirectory, string query, IProgress<long>? progress, CancellationToken ct)
    {
        var results = new List<DeepScanMatch>();
        await foreach (var match in DeepScanner.ScanDayAsync(dayDirectory, query, progress, ct))
        {
            results.Add(match);
        }

        return results;
    }

    [Fact]
    public async Task Finds_a_match_within_a_normal_length_body()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "the quick brown fox"u8.ToArray(), Ct);
        await WriteMessageAsync(store, "nothing relevant"u8.ToArray(), Ct);
        ForceFlushToDisk(store);

        var matches = await ScanAsync(store.Directory, "fox", progress: null, Ct);

        var match = Assert.Single(matches);
        Assert.Equal(1, match.MessageRowId);
    }

    [Fact]
    public async Task Finds_a_match_past_the_two_kilobyte_body_head_cap_that_fts_could_never_see()
    {
        using var store = new SessionStore(_root);

        // FTS only ever indexes the first 2 KB (body_head) - put the needle well past that,
        // in the full body deep scan reads via SegmentReader but body_fts never captured.
        var padding = new string('x', 3000);
        var body = System.Text.Encoding.UTF8.GetBytes($"{{\"padding\":\"{padding}\",\"needle\":\"findme\"}}");
        Assert.True(body.Length > 2048); // the premise this test depends on
        await WriteMessageAsync(store, body, Ct);
        ForceFlushToDisk(store);

        var matches = await ScanAsync(store.Directory, "findme", progress: null, Ct);

        Assert.Single(matches);
    }

    [Fact]
    public async Task Reports_progress_once_per_message_scanned()
    {
        using var store = new SessionStore(_root);
        for (var i = 0; i < 5; i++)
        {
            await WriteMessageAsync(store, System.Text.Encoding.UTF8.GetBytes($"message {i}"), Ct);
        }
        ForceFlushToDisk(store);

        // A ConcurrentBag, not a List: Progress<T> delivers each report on its own thread-pool
        // work item (see below), so several can be added genuinely concurrently. An unsynchronized
        // List.Add there can lose a report outright rather than merely reorder it - which is what
        // made this test fail intermittently under full-suite load, reporting [2,3,4,5], while
        // passing every time it ran alone.
        var reports = new System.Collections.Concurrent.ConcurrentBag<long>();
        var progress = new Progress<long>(reports.Add);

        await ScanAsync(store.Directory, "nonexistent", progress, Ct);

        // Progress<T> posts each report via SynchronizationContext.Post when one is present;
        // with none installed (this console test host), it falls back to
        // ThreadPool.QueueUserWorkItem per report, which does not preserve call order across
        // separate work items - measured directly, not assumed (an early version of this test
        // asserted exact order and failed with reports arriving as [2,5,4,3,1]). The
        // reordering is Progress<T>'s own documented behavior, not a DeepScanner bug: a real
        // progress-bar consumer only cares about the latest value, not strict ordering.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (reports.Count < 5 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal([1L, 2L, 3L, 4L, 5L], reports.Order());
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var store = new SessionStore(_root);
        for (var i = 0; i < 20; i++)
        {
            await WriteMessageAsync(store, System.Text.Encoding.UTF8.GetBytes($"message {i}"), Ct);
        }
        ForceFlushToDisk(store);

        using var cts = new CancellationTokenSource();
        var seen = 0;

        // ThrowsAnyAsync, not ThrowsAsync: SqliteDataReader.ReadAsync surfaces cancellation as
        // TaskCanceledException, which derives from OperationCanceledException but isn't an
        // exact type match - the contract this test cares about is "some cancellation
        // exception propagates," not which concrete subtype.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in DeepScanner.ScanDayAsync(store.Directory, "message", null, cts.Token))
            {
                seen++;
                if (seen == 3) await cts.CancelAsync();
            }
        });

        Assert.True(seen is >= 3 and < 20, $"expected the scan to stop early, saw {seen} matches");
    }

    [Fact]
    public async Task A_day_with_no_db_file_yields_no_matches_rather_than_throwing()
    {
        var emptyDayDirectory = Directory.CreateDirectory(Path.Combine(_root, "empty-day")).FullName;

        var matches = await ScanAsync(emptyDayDirectory, "anything", null, Ct);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Search_is_case_insensitive()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "The Quick Brown FOX"u8.ToArray(), Ct);
        ForceFlushToDisk(store);

        var matches = await ScanAsync(store.Directory, "fox", null, Ct);

        Assert.Single(matches);
    }
}

using System.Collections.Concurrent;
using System.Text;
using EventScope.Core.Models;
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

    private static async Task WriteMessageAsync(
        SessionStore store,
        byte[] body,
        CancellationToken ct,
        string subject = "orders.created",
        string? messageId = null,
        string? correlationId = null,
        MessageFlags flags = MessageFlags.None,
        string preview = "p")
    {
        var coords = store.SegmentWriter.Append(body);
        var bodyHead = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 2048));
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
            MessageId: messageId, CorrelationId: correlationId, Subject: subject,
            Partition: 0, Flags: (byte)flags, Preview: preview, BodyHead: bodyHead));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>A just-written small payload sits in <c>SegmentWriter</c>'s in-memory pending
    /// block until enough accumulates to flush it (see PROGRESS.md §0.1) — <see cref="DeepScanner"/>
    /// reads only from disk via <see cref="Segments.SegmentReader"/>, so a test scanning
    /// small messages right after writing them needs to force that flush first, the same
    /// pattern <c>SessionStoreRolloverTests</c> and others already use.</summary>
    private static void ForceFlushToDisk(SessionStore store) =>
        store.SegmentWriter.Append(new byte[SegmentFormat.BlockSize]);

    private static async Task<List<SearchHit>> CollectDayAsync(
        string dayDirectory, string query, IProgress<DeepScanProgress>? progress, CancellationToken ct)
    {
        var results = new List<SearchHit>();
        await foreach (var hit in DeepScanner.ScanDayAsync(dayDirectory, query, progress, ct))
        {
            results.Add(hit);
        }

        return results;
    }

    private static async Task<List<SearchHit>> CollectAsync(
        string rootDirectory, string query, int maxResults, CancellationToken ct)
    {
        var results = new List<SearchHit>();
        await foreach (var hit in DeepScanner.ScanAsync(rootDirectory, query, maxResults, null, ct))
        {
            results.Add(hit);
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

        var matches = await CollectDayAsync(store.Directory, "fox", progress: null, Ct);

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
        var body = Encoding.UTF8.GetBytes($"{{\"padding\":\"{padding}\",\"needle\":\"findme\"}}");
        Assert.True(body.Length > 2048); // the premise this test depends on
        await WriteMessageAsync(store, body, Ct);
        ForceFlushToDisk(store);

        var matches = await CollectDayAsync(store.Directory, "findme", progress: null, Ct);

        Assert.Single(matches);
    }

    /// <summary>
    /// A deep-scan hit has to be a fully-populated <see cref="SearchHit"/>, not just coordinates:
    /// it goes straight into the history grid through the same path FTS results use, and the
    /// grid renders subject, identifiers and row-state styling from these fields. Projecting
    /// through <c>MessageRowQuery</c> is what guarantees that, and this is the test that would
    /// notice if deep scan ever grew its own projection.
    /// </summary>
    [Fact]
    public async Task A_hit_carries_the_same_fully_populated_row_shape_an_fts_hit_does()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(
            store, "the quick brown fox"u8.ToArray(), Ct,
            subject: "payments.settled", messageId: "m-1", correlationId: "c-1",
            flags: MessageFlags.IsDeadLettered, preview: "the quick brown fox");
        ForceFlushToDisk(store);

        var hit = Assert.Single(await CollectDayAsync(store.Directory, "fox", progress: null, Ct));

        Assert.Equal(store.CurrentDay, hit.Day);
        Assert.Equal("payments.settled", hit.Subject);
        Assert.Equal("m-1", hit.MessageId);
        Assert.Equal("c-1", hit.CorrelationId);
        Assert.Equal("the quick brown fox", hit.Preview);
        Assert.Equal(MessageFlags.IsDeadLettered, hit.Flags);
        Assert.Equal(19, hit.Length);

        // A deep scan never consults the FTS index, so it has no high-water mark to report -
        // and the question "are these results current" does not apply to a tier that reads
        // past the index by design.
        Assert.Equal(SearchHit.IndexHwmNotApplicable, hit.IndexHwm);
    }

    [Fact]
    public async Task Progress_accumulates_payload_bytes_against_a_total_that_never_moves()
    {
        using var store = new SessionStore(_root);

        var bodies = new List<byte[]>();
        for (var i = 0; i < 5; i++)
        {
            var body = Encoding.UTF8.GetBytes($"message {i} {new string('.', i * 10)}");
            bodies.Add(body);
            await WriteMessageAsync(store, body, Ct);
        }
        ForceFlushToDisk(store);

        var expectedTotal = bodies.Sum(b => (long)b.Length);

        // A ConcurrentBag, not a List: Progress<T> delivers each report on its own thread-pool
        // work item (see below), so several can be added genuinely concurrently. An unsynchronized
        // List.Add there can lose a report outright rather than merely reorder it - which is what
        // made an earlier version of this test fail intermittently under full-suite load while
        // passing every time it ran alone.
        var reports = new ConcurrentBag<DeepScanProgress>();
        var progress = new Progress<DeepScanProgress>(reports.Add);

        await CollectDayAsync(store.Directory, "nonexistent", progress, Ct);

        // Progress<T> posts each report via SynchronizationContext.Post when one is present;
        // with none installed (this console test host), it falls back to
        // ThreadPool.QueueUserWorkItem per report, which does not preserve call order across
        // separate work items - measured directly, not assumed. The reordering is Progress<T>'s
        // own documented behavior, not a DeepScanner bug: a real progress-bar consumer only
        // cares about the latest value, not strict ordering. So this asserts on the set of
        // values, sorted, rather than the sequence they arrived in.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (reports.Count < bodies.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(bodies.Count, reports.Count);

        // The denominator is fixed before the first payload is read, so every report carries
        // the same total - that is what keeps a determinate bar from rebasing mid-scan.
        Assert.All(reports, report => Assert.Equal(expectedTotal, report.TotalBytes));
        Assert.All(reports, report => Assert.Equal(0, report.Matches)); // "nonexistent" matches nothing

        // Rows are read newest-first, so the running totals are the reversed lengths accumulated.
        var expectedRunning = new List<long>();
        var accumulated = 0L;
        foreach (var length in bodies.Select(b => (long)b.Length).Reverse())
        {
            accumulated += length;
            expectedRunning.Add(accumulated);
        }

        Assert.Equal(expectedRunning, reports.Select(r => r.BytesScanned).Order());
        Assert.Equal(1d, reports.MaxBy(r => r.BytesScanned).Fraction);
    }

    [Fact]
    public async Task Progress_counts_matches_as_they_are_found()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "match one"u8.ToArray(), Ct);
        await WriteMessageAsync(store, "nothing here"u8.ToArray(), Ct);
        await WriteMessageAsync(store, "match two"u8.ToArray(), Ct);
        ForceFlushToDisk(store);

        var reports = new ConcurrentBag<DeepScanProgress>();
        var progress = new Progress<DeepScanProgress>(reports.Add);

        await CollectDayAsync(store.Directory, "match", progress, Ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (reports.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(2, reports.Max(r => r.Matches));
    }

    /// <summary>
    /// Days newest-first with an early exit, matching <c>FtsSearchService.SearchAsync</c>'s own
    /// traversal - so "the first N matches" means the same thing in both tiers, and an old day
    /// is never opened once N is already satisfied by newer ones.
    /// </summary>
    [Fact]
    public async Task Scanning_a_whole_root_takes_the_newest_day_first_and_stops_at_max_results()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        await WriteMessageAsync(store, "needle in the older day"u8.ToArray(), Ct);
        ForceFlushToDisk(store);
        var olderDay = store.CurrentDay;

        time.Set(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();
        await SqliteTestHelpers.WaitForRolloverSealAsync(_root, olderDay, Ct);

        await WriteMessageAsync(store, "needle in the newer day"u8.ToArray(), Ct);
        ForceFlushToDisk(store);
        var newerDay = store.CurrentDay;

        Assert.NotEqual(olderDay, newerDay); // the premise: two distinct day files exist

        var capped = await CollectAsync(_root, "needle", maxResults: 1, Ct);
        Assert.Equal(newerDay, Assert.Single(capped).Day);

        var all = await CollectAsync(_root, "needle", maxResults: 100, Ct);
        Assert.Equal([newerDay, olderDay], all.Select(h => h.Day));
    }

    [Fact]
    public async Task Scanning_a_root_that_has_never_streamed_yields_nothing()
    {
        var matches = await CollectAsync(Path.Combine(_root, "never-used"), "anything", 100, Ct);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var store = new SessionStore(_root);
        for (var i = 0; i < 20; i++)
        {
            await WriteMessageAsync(store, Encoding.UTF8.GetBytes($"message {i}"), Ct);
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

        var matches = await CollectDayAsync(emptyDayDirectory, "anything", null, Ct);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task Search_is_case_insensitive()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "The Quick Brown FOX"u8.ToArray(), Ct);
        ForceFlushToDisk(store);

        var matches = await CollectDayAsync(store.Directory, "fox", null, Ct);

        Assert.Single(matches);
    }
}

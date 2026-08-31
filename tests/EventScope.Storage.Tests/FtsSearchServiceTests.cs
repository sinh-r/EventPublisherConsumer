using EventScope.Storage.Search;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// <see cref="FtsSearchService"/>'s day-file iteration and early exit — the FTS tier of
/// tiered search (build plan §5 M2). Drives real <see cref="SessionStore"/> day files
/// through real ingest (via <see cref="SqliteBatchWriter"/>), not hand-crafted schemas, so
/// rollover-produced multi-day layouts are exercised the way the real app produces them.
/// </summary>
public sealed class FtsSearchServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-search-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task WriteMessageAsync(
        SessionStore store, string body, string correlationId, string subject, CancellationToken ct)
    {
        var coords = store.SegmentWriter.Append(System.Text.Encoding.UTF8.GetBytes(body));
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
            MessageId: Guid.NewGuid().ToString(), CorrelationId: correlationId, Subject: subject,
            Partition: 0, Flags: 0, Preview: body, BodyHead: body));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private static async Task WaitForIndexAsync(SessionStore store, long expectedHwm, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Writer.IndexLag == 0) return;
            await Task.Delay(20, ct);
        }

        throw new TimeoutException($"Index did not catch up; lag={store.Writer.IndexLag}");
    }

    [Fact]
    public async Task Finds_a_body_match_and_stamps_the_result_with_the_days_index_hwm()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "the quick brown fox", "c-1", "orders.created", Ct);
        await WriteMessageAsync(store, "nothing relevant", "c-2", "orders.created", Ct);
        await WaitForIndexAsync(store, 2, Ct);

        var search = new FtsSearchService(store);
        var hits = new List<SearchHit>();
        await foreach (var h in search.SearchBodyAsync("fox", maxResults: 10, Ct))
        {
            hits.Add(h);
        }

        var hit = Assert.Single(hits);
        Assert.Equal("c-1", hit.CorrelationId);
        Assert.True(hit.IndexHwm >= 2);
    }

    [Fact]
    public async Task Results_are_newest_first_across_multiple_days_with_early_exit()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        await WriteMessageAsync(store, "match on day one", "c-1", "orders.created", Ct);
        await WaitForIndexAsync(store, 1, Ct);

        time.Set(new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();
        await Task.Delay(200, Ct); // old day's async seal

        await WriteMessageAsync(store, "match on day two", "c-2", "orders.created", Ct);
        await WaitForIndexAsync(store, 1, Ct);

        var search = new FtsSearchService(store);

        // Early exit: only 1 result requested, and the newer day (2026-02-02) has a match,
        // so the older day (2026-02-01) must never even be opened.
        var hits = new List<SearchHit>();
        await foreach (var h in search.SearchBodyAsync("match", maxResults: 1, Ct))
        {
            hits.Add(h);
        }

        var hit = Assert.Single(hits);
        Assert.Equal("2026-02-02", hit.Day);
        Assert.Equal("c-2", hit.CorrelationId);
    }

    [Fact]
    public async Task An_identifier_query_under_three_characters_falls_back_to_a_like_scan()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "body", "ab", "orders.created", Ct);
        await WaitForIndexAsync(store, 1, Ct);

        var search = new FtsSearchService(store);

        // "ab" is under the trigram tokenizer's 3-character floor (build plan §3.4) - this
        // must still find the row via the LIKE fallback, not silently return nothing.
        var hits = new List<SearchHit>();
        await foreach (var h in search.SearchIdentifiersAsync("ab", maxResults: 10, Ct))
        {
            hits.Add(h);
        }

        var hit = Assert.Single(hits);
        Assert.Equal("ab", hit.CorrelationId);
    }

    [Fact]
    public async Task An_identifier_query_of_three_or_more_characters_uses_the_trigram_index()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "body", "c-42", "orders.created", Ct);
        await WaitForIndexAsync(store, 1, Ct);

        var search = new FtsSearchService(store);
        var hits = new List<SearchHit>();
        await foreach (var h in search.SearchIdentifiersAsync("c-42", maxResults: 10, Ct))
        {
            hits.Add(h);
        }

        var hit = Assert.Single(hits);
        Assert.Equal("c-42", hit.CorrelationId);
    }

    [Fact]
    public async Task No_matches_returns_an_empty_sequence_not_a_throw()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, "body", "c-1", "orders.created", Ct);
        await WaitForIndexAsync(store, 1, Ct);

        var search = new FtsSearchService(store);
        var hits = new List<SearchHit>();
        await foreach (var h in search.SearchBodyAsync("nonexistentterm", maxResults: 10, Ct))
        {
            hits.Add(h);
        }

        Assert.Empty(hits);
    }
}

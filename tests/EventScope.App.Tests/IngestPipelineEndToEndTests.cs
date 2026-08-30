using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.App.ViewModels;
using EventScope.Core.Ingest;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// Coalescer &#8594; <see cref="MessageRowsView"/>, driven by a <see cref="ManualTicker"/>
/// standing in for the 60&#160;ms <c>DispatcherTimer</c>. Proves the M1a wiring end to end:
/// no per-message collection notification reaches the grid, only one batched update per
/// tick, and the same bind-time-materialization guarantee
/// <see cref="DataGridVirtualizationSpikeTests"/> proves for the view alone still holds once
/// the coalescer is the one calling <see cref="MessageRowsView.AppendBatch"/>.
/// </summary>
public class IngestPipelineEndToEndTests
{
    public IngestPipelineEndToEndTests() => HeadlessFixture.EnsureInitialized();

    private static MessageHeader Header(long sequence, MessageFlags flags = MessageFlags.None) =>
        new(sequence, DateTime.UtcNow.Ticks + sequence, sequence, 0, 0, 128, 0, 0, 0, flags);

    [Fact]
    public void Coalesced_batches_land_in_the_rows_view_only_on_tick()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        var rows = new MessageRowsView(capacity: 1024);

        coalescer.BatchReady += (headers, previews, subjects, correlationIds) =>
            rows.AppendBatch(headers.Span, previews.Span, subjects.Span, correlationIds.Span);

        for (var i = 0; i < 50; i++)
        {
            coalescer.Enqueue(Header(i), $"preview-{i}", "orders.created", Guid.NewGuid().ToString());
        }

        Assert.Equal(0, rows.TotalAppended); // nothing lands before a tick

        ticker.Fire();

        Assert.Equal(50, rows.TotalAppended);
        Assert.Equal(50, rows.Count);
    }

    [Fact]
    public void Row_state_flags_from_the_pipeline_reach_the_realized_view_model()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        var rows = new MessageRowsView(capacity: 1024);
        coalescer.BatchReady += (headers, previews, subjects, correlationIds) =>
            rows.AppendBatch(headers.Span, previews.Span, subjects.Span, correlationIds.Span);

        coalescer.Enqueue(Header(0, MessageFlags.IsLarge), null, "s", "c");
        coalescer.Enqueue(Header(1, MessageFlags.IsDeadLettered), null, "s", "c");
        coalescer.Enqueue(Header(2, MessageFlags.PayloadEvicted), null, "s", "c");
        ticker.Fire();

        var large = (MessageRowViewModel)rows[0]!;
        var deadLettered = (MessageRowViewModel)rows[1]!;
        var evicted = (MessageRowViewModel)rows[2]!;

        Assert.True(large.IsLarge);
        Assert.True(deadLettered.IsDeadLettered);
        Assert.True(evicted.IsEvicted);
    }

    [Fact]
    public async Task InMemoryPayloadStore_round_trips_a_stored_payload_and_reports_eviction_as_empty()
    {
        var store = new InMemoryPayloadStore(capacity: 4);
        var body = "hello"u8.ToArray();
        store.Store(sequence: 0, body);

        var readBack = await store.ReadAsync(Header(0), CancellationToken.None);
        Assert.Equal(body, readBack.ToArray());

        // Overwrites slot 0 (0 % 4 == 0), evicting sequence 0's payload.
        store.Store(sequence: 4, "other"u8.ToArray());

        var afterEviction = await store.ReadAsync(Header(0), CancellationToken.None);
        Assert.True(afterEviction.IsEmpty);
    }
}

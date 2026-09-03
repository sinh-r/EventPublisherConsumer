using EventScope.Core.Ingest;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.Core.Tests;

public class IngestCoalescerTests
{
    private static MessageHeader Header(long sequence) =>
        new(sequence, sequence, sequence, 0, 0, 0, 0, 0, 0, MessageFlags.None);

    [Fact]
    public void Enqueue_before_a_tick_raises_no_batch()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        var raised = false;
        coalescer.BatchReady += (_, _, _, _, _) => raised = true;

        coalescer.Enqueue(Header(1), "p", "s", "c");

        Assert.False(raised);
        Assert.True(ticker.Started);
    }

    [Fact]
    public void Tick_batches_everything_enqueued_since_the_last_tick()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        var batches = new List<int>();
        coalescer.BatchReady += (headers, _, _, _, _) => batches.Add(headers.Length);

        coalescer.Enqueue(Header(1), "p1", "s1", "c1");
        coalescer.Enqueue(Header(2), "p2", "s2", "c2");
        coalescer.Enqueue(Header(3), "p3", "s3", "c3");
        ticker.Fire();

        Assert.Single(batches);
        Assert.Equal(3, batches[0]);
    }

    [Fact]
    public void Tick_with_nothing_enqueued_raises_no_batch()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        var raised = false;
        coalescer.BatchReady += (_, _, _, _, _) => raised = true;

        ticker.Fire();

        Assert.False(raised);
    }

    [Fact]
    public void Successive_ticks_do_not_replay_an_earlier_batch()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        var batches = new List<int>();
        coalescer.BatchReady += (headers, _, _, _, _) => batches.Add(headers.Length);

        coalescer.Enqueue(Header(1), "p", "s", "c");
        ticker.Fire();
        ticker.Fire(); // nothing new enqueued

        Assert.Single(batches);
    }

    [Fact]
    public void Batch_contents_and_order_are_preserved()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        string[]? subjects = null;
        coalescer.BatchReady += (_, _, s, _, _) => subjects = s.ToArray();

        coalescer.Enqueue(Header(1), null, "first", "c1");
        coalescer.Enqueue(Header(2), null, "second", "c2");
        ticker.Fire();

        Assert.NotNull(subjects);
        Assert.Equal(["first", "second"], subjects);
    }

    [Fact]
    public void Overflowing_the_staging_capacity_in_one_tick_drops_and_counts_instead_of_growing()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker, stagingCapacity: 4);

        for (var i = 0; i < 10; i++)
        {
            coalescer.Enqueue(Header(i), null, "s", "c");
        }

        Assert.Equal(6, coalescer.UiDropped);

        var batchSize = -1;
        coalescer.BatchReady += (headers, _, _, _, _) => batchSize = headers.Length;
        ticker.Fire();

        Assert.Equal(4, batchSize);
    }

    [Fact]
    public void Dispose_stops_the_ticker_and_unsubscribes()
    {
        var ticker = new ManualTicker();
        var coalescer = new IngestCoalescer(ticker);
        var raised = false;
        coalescer.BatchReady += (_, _, _, _, _) => raised = true;

        coalescer.Dispose();

        Assert.False(ticker.Started);

        coalescer.Enqueue(Header(1), null, "s", "c");
        ticker.Fire(); // must be a no-op post-dispose: handler was unsubscribed
        Assert.False(raised);
    }

    [Fact]
    public void Each_message_keeps_its_own_day_when_a_batch_spans_a_rollover()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        string[]? days = null;
        coalescer.BatchReady += (_, _, _, _, d) => days = d.ToArray();

        // Staged either side of the writer's midnight rollover and flushed in one batch. Carrying
        // the day per message rather than per batch is what keeps both halves resolvable: segment
        // ids restart at 0 each day, so a single batch-wide day would point one half at the other
        // day's bytes.
        coalescer.Enqueue(Header(1), null, "s", "c", "2026-03-14");
        coalescer.Enqueue(Header(2), null, "s", "c", "2026-03-15");
        ticker.Fire();

        Assert.NotNull(days);
        Assert.Equal(["2026-03-14", "2026-03-15"], days);
    }

    [Fact]
    public void A_message_enqueued_without_a_day_reports_an_empty_one()
    {
        var ticker = new ManualTicker();
        using var coalescer = new IngestCoalescer(ticker);
        string[]? days = null;
        coalescer.BatchReady += (_, _, _, _, d) => days = d.ToArray();

        coalescer.Enqueue(Header(1), null, "s", "c");
        ticker.Fire();

        Assert.NotNull(days);
        Assert.Equal([string.Empty], days);
    }
}

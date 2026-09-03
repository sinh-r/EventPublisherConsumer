using System.Text;
using System.Threading.Channels;
using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.App.ViewModels;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// The live grid reading a <b>replayed backlog</b> — a Kafka run started from <c>Earliest</c>,
/// <c>Timestamp</c> or an explicit offset, where old messages arrive alongside new ones.
///
/// <para>
/// What makes this its own test class is that a backlog breaks an assumption every other ingest
/// test satisfies for free: that a message's broker timestamp and the directory it was filed under
/// name the same day. They do not. The writer files by <i>its own</i> clock, so a month-old message
/// replayed today lands under today. Since segment ids restart at 0 every day and offsets are
/// dense, resolving such a row by its timestamp does not merely fail to find the payload — it can
/// find a <i>different message's</i> bytes at the same coordinates, which in a tool whose entire
/// job is showing what a message contained is worse than an error. That exact substitution is
/// constructed and asserted below.
/// </para>
/// </summary>
public sealed class BacklogReplayDayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-backlog-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Far enough back that no plausible test-run duration could make it today.</summary>
    private static readonly DateTime BacklogWrittenAt = DateTime.UtcNow.AddDays(-45);

    public BacklogReplayDayTests() => HeadlessFixture.EnsureInitialized();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_replayed_row_carries_the_day_it_was_written_under_not_the_day_it_was_produced()
    {
        using var sessionStore = new SessionStore(_root);
        var rows = new MessageRowsView(capacity: 1024);
        var ticker = new ManualTicker();

        await using (var pipeline = new IngestPipeline(
            new OneShotSource([Encoding.UTF8.GetBytes("replayed")], BacklogWrittenAt.Ticks),
            rows, ticker, sessionStore))
        {
            pipeline.Start();
            await PumpUntilAppendedAsync(rows, ticker, expected: 1);
        }

        var row = (MessageRowViewModel)rows[0]!;

        // The two days that a backlog makes disagree, asserted as disagreeing.
        Assert.Equal(BacklogWrittenAt.ToString("yyyy-MM-dd"), row.Time.ToString("yyyy-MM-dd"));
        Assert.Equal(sessionStore.CurrentDay, row.Day);
        Assert.NotEqual(row.Time.ToString("yyyy-MM-dd"), row.Day);
    }

    /// <summary>
    /// The failure this whole mechanism exists to prevent, staged end to end: a real replayed
    /// payload written under today, and a decoy of the same length sitting at the same
    /// <c>(segment, offset)</c> in the day the row's timestamp points at. Resolving by the stamped
    /// day returns the real bytes; resolving by the timestamp returns the decoy — silently, with no
    /// error anywhere.
    /// </summary>
    [Fact]
    public async Task Resolving_by_timestamp_returns_another_days_bytes_while_the_stamped_day_returns_the_real_ones()
    {
        var real = Encoding.UTF8.GetBytes("the-real-replayed-payload");
        var decoy = Encoding.UTF8.GetBytes("a-different-days-message!"); // same length: same coordinates
        Assert.Equal(real.Length, decoy.Length);

        var rows = new MessageRowsView(capacity: 1024);
        var ticker = new ManualTicker();

        using var sessionStore = new SessionStore(_root);
        await using var pipeline = new IngestPipeline(
            new OneShotSource([real, Flusher()], BacklogWrittenAt.Ticks), rows, ticker, sessionStore);

        pipeline.Start();
        await PumpUntilAppendedAsync(rows, ticker, expected: 2);

        var writeDay = sessionStore.CurrentDay;
        var row = (MessageRowViewModel)rows[0]!;
        var inferredDay = row.Time.ToString("yyyy-MM-dd");
        Assert.NotEqual(writeDay, inferredDay);

        // Plant the decoy at the same coordinates in the day the timestamp points at.
        var decoyDirectory = SessionLayout.DayDirectory(_root, inferredDay);
        Directory.CreateDirectory(decoyDirectory);
        using (var decoyWriter = new SegmentWriter(decoyDirectory))
        {
            var (segmentId, offset, _) = decoyWriter.Append(decoy);
            Assert.Equal(row.SegmentId, segmentId);
            Assert.Equal(row.Offset, offset);
        }

        var header = HeaderFor(row);

        var byStampedDay = await new SessionStorePayloadReader(sessionStore, row.Day).ReadAsync(header, Ct);
        Assert.Equal(real, byStampedDay.ToArray());

        // The bug, demonstrated rather than described: no exception, no empty buffer — just the
        // wrong message's body.
        var byInference = await new SessionStorePayloadReader(sessionStore).ReadAsync(header, Ct);
        Assert.Equal(decoy, byInference.ToArray());
    }

    [Fact]
    public async Task The_pipelines_reader_for_a_stamped_row_reads_the_replayed_payload_back()
    {
        var payload = Encoding.UTF8.GetBytes("replayed-body-read-through-the-pipeline");
        var rows = new MessageRowsView(capacity: 1024);
        var ticker = new ManualTicker();

        using var sessionStore = new SessionStore(_root);
        await using var pipeline = new IngestPipeline(
            new OneShotSource([payload, Flusher()], BacklogWrittenAt.Ticks), rows, ticker, sessionStore,
            // One slot, so the follow-up message evicts the payload under test from the hot ring
            // and the read has to go through the cold, day-addressed path — where a backlog larger
            // than the hot ring puts almost every row anyway.
            hotPayloadCapacity: 1);

        pipeline.Start();
        await PumpUntilAppendedAsync(rows, ticker, expected: 2);

        var row = (MessageRowViewModel)rows[0]!;
        Assert.True(
            (await pipeline.PayloadReader.ReadAsync(HeaderFor(row), Ct)).IsEmpty,
            "the inferring reader should find nothing — otherwise this asserts the hot ring, not the fix");

        var bytes = await pipeline.ReaderFor(row.Day).ReadAsync(HeaderFor(row), Ct);

        Assert.Equal(payload, bytes.ToArray());
    }

    [Fact]
    public async Task A_row_appended_with_no_day_still_resolves_through_the_inferring_reader()
    {
        var rows = new MessageRowsView(capacity: 16);
        rows.Append(Header(0), "p", "s", "c"); // the day-less overload every older call site uses

        var row = (MessageRowViewModel)rows[0]!;

        Assert.Equal(string.Empty, row.Day);

        using var sessionStore = new SessionStore(_root);
        await using var pipeline = new IngestPipeline(
            new OneShotSource([], 0), new MessageRowsView(capacity: 16), new ManualTicker(), sessionStore);

        // An empty day must hand back the inferring reader unchanged, not a reader pinned to "".
        Assert.Same(pipeline.PayloadReader, pipeline.ReaderFor(row.Day));
    }

    [Fact]
    public void Rows_in_one_batch_keep_their_own_days_across_a_rollover()
    {
        var rows = new MessageRowsView(capacity: 16);

        // A batch that spans midnight: the coalescer staged messages either side of the writer's
        // rollover, so a single batch-wide day would misfile one half or the other.
        rows.AppendBatch(
            [Header(0), Header(1), Header(2)],
            ["p0", "p1", "p2"],
            ["s", "s", "s"],
            ["c", "c", "c"],
            ["2026-03-14", "2026-03-14", "2026-03-15"]);

        Assert.Equal("2026-03-14", ((MessageRowViewModel)rows[0]!).Day);
        Assert.Equal("2026-03-14", ((MessageRowViewModel)rows[1]!).Day);
        Assert.Equal("2026-03-15", ((MessageRowViewModel)rows[2]!).Day);
    }

    [Fact]
    public void A_days_span_shorter_than_the_batch_leaves_the_uncovered_rows_empty()
    {
        var rows = new MessageRowsView(capacity: 16);

        rows.AppendBatch(
            [Header(0), Header(1)],
            ["p0", "p1"],
            ["s", "s"],
            ["c", "c"],
            ["2026-03-14"]);

        Assert.Equal("2026-03-14", ((MessageRowViewModel)rows[0]!).Day);
        Assert.Equal(string.Empty, ((MessageRowViewModel)rows[1]!).Day);
    }

    [Fact]
    public void A_recycled_row_view_model_does_not_keep_the_previous_rows_day()
    {
        // Row view models are pooled and repopulated in place, so a stale Day would point a live
        // row at whatever directory the row that previously used this instance came from.
        var rows = new MessageRowsView(capacity: 2);
        rows.Append(Header(0), "p", "s", "c", day: "2026-03-14");
        Assert.Equal("2026-03-14", ((MessageRowViewModel)rows[0]!).Day);

        rows.NotifyRowUnloaded(0);
        rows.Append(Header(1), "p", "s", "c"); // no day this time
        rows.Append(Header(2), "p", "s", "c", day: "2026-03-16");

        Assert.Equal(string.Empty, ((MessageRowViewModel)rows[0]!).Day);
        Assert.Equal("2026-03-16", ((MessageRowViewModel)rows[1]!).Day);
    }

    /// <summary>A payload past the segment format's 1 MB block size, which forces the segment
    /// writer to flush whatever is pending before giving this one its own block. Sending one after
    /// the message under test is what puts that message's bytes on disk while the store stays open
    /// — the alternative, disposing the store to seal the segment, cannot then be followed by
    /// reopening it, because opening a day's writer truncates that day's segment 0. (The constant
    /// is duplicated rather than referenced: <c>SegmentFormat</c> is internal to
    /// EventScope.Storage, and one number in one test is not worth an InternalsVisibleTo.)</summary>
    private static byte[] Flusher() => new byte[(1024 * 1024) + 1];

    private static MessageHeader Header(long sequence, long enqueuedTicks = 0) =>
        new(sequence, enqueuedTicks, sequence, 0, 0, 0, 0, 0, 0, MessageFlags.None);

    /// <summary>Rebuilds the read coordinates the detail pane reconstructs from a selected row.</summary>
    private static MessageHeader HeaderFor(MessageRowViewModel row) =>
        new(
            sequence: row.Sequence,
            enqueuedTicks: row.Time.Ticks,
            rowId: row.Sequence,
            segmentId: row.SegmentId,
            offset: row.Offset,
            length: row.Size,
            subjectId: 0,
            correlationInternId: 0,
            partition: row.Partition,
            flags: MessageFlags.None);

    /// <summary>Drives the UI ticker until the coalescer has handed <paramref name="expected"/>
    /// rows to the view. The drain loop is asynchronous, so the batch is not ready on the first
    /// tick — polling the ticker is what makes this deterministic without sleeping a fixed time.</summary>
    private static async Task PumpUntilAppendedAsync(MessageRowsView rows, ManualTicker ticker, long expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (rows.TotalAppended < expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Expected {expected} appended rows, saw {rows.TotalAppended}.");
            }

            ticker.Fire();
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Emits a fixed set of bodies, all stamped with one <paramref name="enqueuedTicks"/>
    /// — a replayed backlog's defining property is that its broker timestamps are old, so the test
    /// sets them explicitly rather than letting them default to now.</summary>
    private sealed class OneShotSource(IReadOnlyList<byte[]> bodies, long enqueuedTicks) : IEventSource
    {
        public SourceCapabilities Capabilities { get; } = new()
        {
            CanPeekNonDestructively = true,
            SupportsPartitions = true,
            SupportsSubscriptions = false,
            SupportsSessions = false,
            SupportsDeadLetterQueue = false,
            SupportsReplay = true,
            SupportsOffsetCommit = true,
        };

        public string DisplayName => "Backlog test source";

        public event Action<SourceError>? ErrorOccurred { add { } remove { } }

        public async Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken)
        {
            foreach (var body in bodies)
            {
                await destination.WriteAsync(new RawMessage
                {
                    Body = body,
                    EnqueuedTicks = enqueuedTicks,
                    ReceivedTicks = DateTime.UtcNow.Ticks,
                    Subject = "orders.created",
                    CorrelationId = "corr-1",
                    Partition = 0,
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

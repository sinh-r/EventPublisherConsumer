using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-session-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Opens_a_day_directory_with_both_the_segment_writer_and_the_db_file()
    {
        var fakeNow = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(fakeNow);

        string dbPath;
        using (var store = new SessionStore(_root, timeProvider))
        {
            Assert.Equal(Path.Combine(_root, "2026-03-05"), store.Directory);

            var body = "hello"u8.ToArray();
            var coords = store.SegmentWriter.Append(body);

            // A lone small payload stays purely in the segment writer's in-memory pending
            // buffer (see PROGRESS.md §0.1) — force it out to disk before reading it back
            // through the cold SegmentReader path this test exercises.
            store.SegmentWriter.Append(new byte[EventScope.Storage.Segments.SegmentFormat.BlockSize]);

            store.Writer.Enqueue(new WriteOp.InsertMessage(
                EnqueuedTicks: 0, ReceivedTicks: 0,
                SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
                MessageId: "m-1", CorrelationId: "c-1", Subject: "orders.created",
                Partition: 0, Flags: 0, Preview: "hello", BodyHead: "hello"));

            dbPath = Path.Combine(store.Directory, "2026-03-05.db");
            await SqliteTestHelpers.WaitForRowCountAsync(dbPath, 1, TimeSpan.FromSeconds(1), Ct);

            var read = await store.SegmentReader.ReadAsync(
                new EventScope.Core.Models.MessageHeader(0, 0, 0, coords.SegmentId, coords.Offset, coords.Length, 0, 0, 0, EventScope.Core.Models.MessageFlags.None),
                Ct);
            Assert.Equal(body, read.ToArray());
        }

        Assert.True(File.Exists(dbPath));
        Assert.True(File.Exists(Path.Combine(_root, "2026-03-05", "000000.seg")));

        await SqliteTestHelpers.AssertFtsIntegrityAsync(dbPath, Ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

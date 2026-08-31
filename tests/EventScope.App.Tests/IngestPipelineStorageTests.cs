using System.Text;
using System.Threading.Channels;
using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// The end-to-end M1b path: an <see cref="IEventSource"/> &#8594; <see cref="IngestPipeline"/>
/// &#8594; real segment files + a real SQLite day file in a temp directory. Proves the two
/// things the build plan's M1 acceptance criteria actually require: zero messages lost from
/// disk, and every row's on-disk coordinates read back the exact original bytes through the
/// same <see cref="IPayloadReader"/> the detail pane uses.
/// </summary>
public sealed class IngestPipelineStorageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-pipeline-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public IngestPipelineStorageTests() => HeadlessFixture.EnsureInitialized();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Every_emitted_message_lands_on_disk_and_reads_back_byte_for_byte()
    {
        const int messageCount = 500;

        string directory;
        string dbPath;
        List<byte[]> payloads;

        using (var sessionStore = new SessionStore(_root))
        {
            var rows = new MessageRowsView(capacity: 4096);
            var source = new FiniteEventSource(messageCount, seed: 1);
            payloads = source.Payloads;
            var ticker = new ManualTicker();

            var pipeline = new IngestPipeline(
                source, rows, ticker, sessionStore,
                hotPayloadCapacity: 64); // small on purpose: forces most reads through the cold segment path

            pipeline.Start();
            await source.Completed.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            directory = sessionStore.Directory;
            dbPath = Directory.GetFiles(directory, "*.db")[0];
            await WaitForRowCountAsync(dbPath, messageCount, TimeSpan.FromSeconds(5));

            await pipeline.DisposeAsync();

            // Zero messages lost from disk under saturation (build plan §6) — measured directly.
            Assert.Equal(messageCount, await CountRowsAsync(dbPath));

            // 500 small payloads (well under one 1 MB block) may still be sitting in the
            // segment writer's in-memory pending buffer at this point — see PROGRESS.md §0.1.
            // Disposing sessionStore seals the segment (an unconditional flush), so what's
            // verified below is what's actually durable on disk, not a flush-timing race.
        }

        // A fresh reader against the now-sealed segment files — exactly how a restarted app
        // (or the detail pane's cold path on a miss) would read this data back.
        using var segmentReader = new EventScope.Storage.Segments.SegmentReader(directory);

        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT segment_id, offset, length FROM messages ORDER BY id";
        await using var reader = await query.ExecuteReaderAsync(Ct);

        var checkedRows = 0;
        while (await reader.ReadAsync(Ct))
        {
            var header = new MessageHeader(
                sequence: 0, enqueuedTicks: 0, rowId: 0,
                segmentId: reader.GetInt32(0), offset: reader.GetInt32(1), length: reader.GetInt32(2),
                subjectId: 0, correlationInternId: 0, partition: 0, flags: MessageFlags.None);

            var bytes = await segmentReader.ReadAsync(header, Ct);
            Assert.False(bytes.IsEmpty, $"row {checkedRows}: payload missing from disk");
            Assert.Equal(payloads[checkedRows], bytes.ToArray());
            checkedRows++;
        }

        Assert.Equal(messageCount, checkedRows);
    }

    private static async Task WaitForRowCountAsync(string dbPath, long expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var count = await CountRowsAsync(dbPath);
            if (count >= expected) return;
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Expected {expected} rows within {timeout}, saw {count}.");
            }

            await Task.Delay(50, Ct);
        }
    }

    private static async Task<long> CountRowsAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Emits an exact, known count of messages as fast as the channel accepts them,
    /// then completes — unlike <see cref="EventScope.Core.Ingest.FakeEventSource"/>, which
    /// paces indefinitely at a target rate and never stops on its own. Deterministic message
    /// count is what this test needs; realistic pacing is not.</summary>
    private sealed class FiniteEventSource(int count, int seed) : IEventSource
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;
        public List<byte[]> Payloads { get; } = [];

        public SourceCapabilities Capabilities { get; } = new()
        {
            CanPeekNonDestructively = true,
            SupportsPartitions = true,
            SupportsSubscriptions = false,
            SupportsSessions = false,
            SupportsDeadLetterQueue = false,
            SupportsReplay = false,
            SupportsOffsetCommit = true,
        };

        public async Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken)
        {
            var random = new Random(seed);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var body = Encoding.UTF8.GetBytes($$"""{"i":{{i}},"pad":"{{new string('x', random.Next(16, 512))}}"}""");
                    Payloads.Add(body);

                    await destination.WriteAsync(new RawMessage
                    {
                        Body = body,
                        EnqueuedTicks = DateTime.UtcNow.Ticks,
                        ReceivedTicks = DateTime.UtcNow.Ticks,
                        Subject = "orders.created",
                        CorrelationId = Guid.NewGuid().ToString(),
                        Partition = i % 4,
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _completed.TrySetResult();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

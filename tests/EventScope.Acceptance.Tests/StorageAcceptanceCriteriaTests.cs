using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.Acceptance.Tests;

/// <summary>
/// Measures two of the build plan's five M1 acceptance criteria (§6) against real code paths
/// rather than assuming them from correctness tests alone. Deliberately its own project, not
/// part of <c>EventScope.App.Tests</c> — see this project's .csproj remarks for why: a
/// process where Avalonia's headless dispatcher has been set up can hang on the first real
/// async file I/O depending on test execution order, and these tests need none of Avalonia.
///
/// The remaining three M1 criteria live elsewhere: scrolling frame time in
/// <c>EventScope.App.Tests.AcceptanceCriteriaTests</c> (needs a real DataGrid); UI frame time
/// at 10k msg/s and heap growth over a 60s run need a real windowed process and are measured
/// by <c>build/Measure-M1Acceptance.ps1</c> instead.
///
/// Gated behind <c>EVENTSCOPE_SOAK=1</c> so the normal fast suite stays fast — these
/// intentionally run larger volumes than a unit test needs to, to get a real number rather
/// than a toy one. Each writes its measurement to a CSV under
/// <c>tests/EventScope.Bench/baselines/acceptance/</c> so the numbers are reviewable, not
/// just asserted — see that directory's README for machine details.
/// </summary>
public sealed class StorageAcceptanceCriteriaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool SoakEnabled => Environment.GetEnvironmentVariable("EVENTSCOPE_SOAK") == "1";

    // --- Criterion: selecting a row renders its body in under 100 ms ---

    [Fact(Skip = "Set EVENTSCOPE_SOAK=1 to run — larger volumes than a unit test needs.",
        SkipUnless = nameof(SoakEnabled))]
    public async Task Cold_segment_reads_complete_well_under_the_hundred_millisecond_budget()
    {
        // This measures the real bottleneck behind "row selection renders body < 100 ms":
        // the disk-bound SegmentReader.ReadAsync call. DetailPaneViewModel wraps this in a
        // 50 ms spinner-delay *race* (Task.WhenAny against a Task.Delay), which never adds
        // latency to a read that's already fast — it only decides whether a spinner shows —
        // so timing the reader directly is a faithful, simpler measurement of the same cost.
        const int payloadCount = 5_000;
        const int sampleCount = 500;

        var directory = Directory.CreateTempSubdirectory("eventscope-acceptance-cold-read-").FullName;
        try
        {
            var random = new Random(42);
            var coords = new (int SegmentId, int Offset, int Length)[payloadCount];

            using (var writer = new SegmentWriter(directory))
            {
                var payload = new byte[2048];
                for (var i = 0; i < payloadCount; i++)
                {
                    random.NextBytes(payload);
                    coords[i] = writer.Append(payload);
                }
            } // disposing seals the segment — every read below goes through disk, not a pending buffer.

            using var reader = new SegmentReader(directory);
            var elapsedMs = new List<double>(sampleCount);
            var sw = new Stopwatch();

            for (var i = 0; i < sampleCount; i++)
            {
                var (segmentId, offset, length) = coords[random.Next(payloadCount)];
                var header = new MessageHeader(0, 0, 0, segmentId, offset, length, 0, 0, 0, MessageFlags.None);

                sw.Restart();
                var bytes = await reader.ReadAsync(header, Ct);
                sw.Stop();

                Assert.Equal(length, bytes.Length);
                elapsedMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            elapsedMs.Sort();
            var p50 = elapsedMs[elapsedMs.Count / 2];
            var p99 = elapsedMs[(int)((elapsedMs.Count - 1) * 0.99)];
            var max = elapsedMs[^1];

            WriteAcceptanceCsv("cold-segment-read-latency.csv",
                ["payload_count", "sample_count", "p50_ms", "p99_ms", "max_ms"],
                [payloadCount.ToString(), sampleCount.ToString(), $"{p50:F3}", $"{p99:F3}", $"{max:F3}"]);

            Assert.True(max < 100.0,
                $"a cold segment read took {max:F1} ms (p50={p50:F1}, p99={p99:F1}), over the 100 ms acceptance budget");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- Criterion: zero messages lost from disk under saturation ---

    [Fact(Skip = "Set EVENTSCOPE_SOAK=1 to run — larger volumes than a unit test needs.",
        SkipUnless = nameof(SoakEnabled))]
    public async Task Every_message_lands_on_disk_even_under_a_deliberately_starved_byte_budget()
    {
        const int messageCount = 20_000;
        const int byteBudgetLimit = 16 * 1024;

        var root = Directory.CreateTempSubdirectory("eventscope-acceptance-saturation-").FullName;
        try
        {
            string dbPath;

            using (var sessionStore = new SessionStore(root))
            {
                var rows = new MessageRowsView(capacity: 4096);
                var source = new BurstEventSource(messageCount, seed: 3);
                var ticker = new ManualTicker();

                // A deliberately tiny byte budget (16 KB against ~20k messages averaging
                // ~280 bytes each, i.e. room for only ~50 in flight) forces the writer to
                // genuinely saturate the channel rather than breezing through it — this is
                // the actual back-pressure path the build plan's §3.2 byte budget exists for,
                // not just a large N. Measured directly: a much smaller budget (64 KB against
                // 200k messages, near-constant park/release churn) turned this into a
                // multi-minute run for no added correctness signal — this ratio still
                // saturates without being pathologically slow.
                var pipeline = new IngestPipeline(
                    source, rows, ticker,
                    sessionStore.SegmentWriter, sessionStore.Writer, sessionStore.SegmentReader,
                    byteBudgetLimit: byteBudgetLimit,
                    hotPayloadCapacity: 64);

                pipeline.Start();
                await source.Completed.WaitAsync(TimeSpan.FromSeconds(30), Ct);

                dbPath = Directory.GetFiles(sessionStore.Directory, "*.db")[0];
                await WaitForRowCountAsync(dbPath, messageCount, TimeSpan.FromSeconds(30));

                await pipeline.DisposeAsync();

                var actual = await CountRowsAsync(dbPath);
                Assert.Equal(messageCount, actual);

                WriteAcceptanceCsv("saturation-zero-loss.csv",
                    ["messages_emitted", "messages_on_disk", "byte_budget_bytes"],
                    [messageCount.ToString(), actual.ToString(), byteBudgetLimit.ToString()]);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    /// <summary>Emits an exact, known count of messages as fast as the channel (and its byte
    /// budget) admit, then completes — like <c>IngestPipelineStorageTests.FiniteEventSource</c>
    /// in EventScope.App.Tests, deliberately not <see cref="EventScope.Core.Ingest.FakeEventSource"/>,
    /// which paces indefinitely and never stops on its own. A bounded, naturally-completing
    /// source keeps this test's "zero loss" measurement about ingestion correctness under
    /// saturation, not entangled with <see cref="IngestPipeline.DisposeAsync"/>'s
    /// cancellation-based shutdown path, which is a different question this test isn't
    /// asking.</summary>
    private sealed class BurstEventSource(int count, int seed) : IEventSource
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

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
                    var body = Encoding.UTF8.GetBytes(
                        $$"""{"i":{{i}},"pad":"{{new string('x', random.Next(16, 512))}}"}""");

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

    private static void WriteAcceptanceCsv(string fileName, string[] header, string[] row)
    {
        var directory = Path.Combine(FindRepoRoot(), "tests", "EventScope.Bench", "baselines", "acceptance");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Join(',', header) + Environment.NewLine + string.Join(',', row) + Environment.NewLine);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EventScope.slnx")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root (EventScope.slnx) from " + AppContext.BaseDirectory);
    }
}

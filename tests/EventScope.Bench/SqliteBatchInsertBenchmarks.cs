using BenchmarkDotNet.Attributes;
using EventScope.Storage.Sqlite;

namespace EventScope.Bench;

/// <summary>Batch insert rate (build plan §7). One fresh day file per iteration, since the
/// batch writer owns the only write connection to it for its whole lifetime.</summary>
[MemoryDiagnoser]
public class SqliteBatchInsertBenchmarks
{
    private string _directory = null!;
    private int _iteration;

    [Params(5_000, 50_000)]
    public int RowCount;

    [IterationSetup]
    public void IterationSetup() => _directory = Directory.CreateTempSubdirectory("eventscope-bench-sqlite-").FullName;

    [IterationCleanup]
    public void IterationCleanup() => Directory.Delete(_directory, recursive: true);

    [Benchmark]
    public async Task InsertRows()
    {
        var path = Path.Combine(_directory, $"bench-{_iteration++}.db");
        using var writer = new SqliteBatchWriter(path);

        for (var i = 0; i < RowCount; i++)
        {
            writer.Enqueue(new WriteOp.InsertMessage(
                EnqueuedTicks: i,
                ReceivedTicks: i,
                SegmentId: 0,
                Offset: i * 64,
                Length: 64,
                MessageId: null,
                CorrelationId: null,
                Subject: "orders.created",
                Partition: i % 4,
                Flags: 0,
                Preview: "preview",
                BodyHead: null));
        }

        await writer.FlushAsync();
    }
}

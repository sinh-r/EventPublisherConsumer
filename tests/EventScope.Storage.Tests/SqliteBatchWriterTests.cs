using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

public sealed class SqliteBatchWriterTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("eventscope-sqlite-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string DbPath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(_directory, $"{name}.db");

    private static WriteOp.InsertMessage Row(int i, string subject = "orders.created") => new(
        EnqueuedTicks: DateTime.UtcNow.Ticks,
        ReceivedTicks: DateTime.UtcNow.Ticks,
        SegmentId: 0,
        Offset: i * 64,
        Length: 64,
        MessageId: $"m-{i}",
        CorrelationId: Guid.NewGuid().ToString(),
        Subject: subject,
        Partition: i % 4,
        Flags: 0,
        Preview: $"preview-{i}",
        BodyHead: $"{{\"i\":{i}}}");

    [Fact]
    public async Task Applying_the_schema_enables_wal_and_creates_the_fts_tables()
    {
        var path = DbPath();
        using (new SqliteBatchWriter(path))
        {
            // Constructor applies the schema synchronously before starting its thread.
        }

        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(Ct);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode";
            var mode = (string)(await pragma.ExecuteScalarAsync(Ct))!;
            Assert.Equal("wal", mode, ignoreCase: true);
        }

        await using (var tables = connection.CreateCommand())
        {
            tables.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('messages','subjects','index_state')";
            await using var reader = await tables.ExecuteReaderAsync(Ct);
            var names = new HashSet<string>();
            while (await reader.ReadAsync(Ct)) names.Add(reader.GetString(0));
            Assert.Equal(["index_state", "messages", "subjects"], names.Order());
        }

        await SqliteTestHelpers.AssertFtsIntegrityAsync(path, Ct);
    }

    [Fact]
    public async Task Five_hundred_rows_commit_without_waiting_for_the_time_boundary()
    {
        var path = DbPath();
        using (var writer = new SqliteBatchWriter(path))
        {
            for (var i = 0; i < SqliteBatchWriter.BatchRowLimit; i++)
            {
                writer.Enqueue(Row(i));
            }

            // The row-count threshold, not the 200 ms clock, should be what flushes this —
            // give it a generous but well-under-a-second window to prove that.
            await SqliteTestHelpers.WaitForRowCountAsync(
                path, SqliteBatchWriter.BatchRowLimit, TimeSpan.FromMilliseconds(500), Ct);
        }

        await SqliteTestHelpers.AssertFtsIntegrityAsync(path, Ct);
    }

    [Fact]
    public async Task Fewer_rows_than_the_limit_still_commit_on_the_time_boundary()
    {
        var path = DbPath();
        using (var writer = new SqliteBatchWriter(path))
        {
            for (var i = 0; i < 10; i++)
            {
                writer.Enqueue(Row(i));
            }

            await SqliteTestHelpers.WaitForRowCountAsync(path, 10, TimeSpan.FromSeconds(1), Ct);
        }

        await SqliteTestHelpers.AssertFtsIntegrityAsync(path, Ct);
    }

    [Fact]
    public async Task Subject_interning_is_stable_within_a_file_and_independent_across_files()
    {
        var pathA = DbPath();
        var pathB = Path.Combine(_directory, "SecondFile.db");

        using (var writerA = new SqliteBatchWriter(pathA))
        {
            writerA.Enqueue(Row(0, subject: "orders.created"));
            writerA.Enqueue(Row(1, subject: "orders.created")); // same subject, must reuse the id
            writerA.Enqueue(Row(2, subject: "orders.cancelled")); // different subject, new id
            await SqliteTestHelpers.WaitForRowCountAsync(pathA, 3, TimeSpan.FromSeconds(1), Ct);
        }

        using (var writerB = new SqliteBatchWriter(pathB))
        {
            writerB.Enqueue(Row(0, subject: "orders.created")); // a different file — id space restarts
            await SqliteTestHelpers.WaitForRowCountAsync(pathB, 1, TimeSpan.FromSeconds(1), Ct);
        }

        await using var connectionA = new SqliteConnection($"Data Source={pathA};Pooling=False");
        await connectionA.OpenAsync(Ct);
        await using var subjectQueryA = connectionA.CreateCommand();
        subjectQueryA.CommandText = "SELECT subject_id FROM messages ORDER BY id";
        await using var subjectReaderA = await subjectQueryA.ExecuteReaderAsync(Ct);
        var perRowSubjectIdsA = new List<long>();
        while (await subjectReaderA.ReadAsync(Ct)) perRowSubjectIdsA.Add(subjectReaderA.GetInt64(0));

        Assert.Equal(perRowSubjectIdsA[0], perRowSubjectIdsA[1]); // reused
        Assert.NotEqual(perRowSubjectIdsA[0], perRowSubjectIdsA[2]); // distinct

        await using var connectionB = new SqliteConnection($"Data Source={pathB};Pooling=False");
        await connectionB.OpenAsync(Ct);
        await using var subjectQueryB = connectionB.CreateCommand();
        subjectQueryB.CommandText = "SELECT subject_id FROM messages";
        var subjectIdB = (long)(await subjectQueryB.ExecuteScalarAsync(Ct))!;

        // Same subject text, a different file: nothing says the ids must differ, but they
        // also must not be assumed shared — this file's interner seeded from its own
        // (empty) table, so its first id is independently 1, same as file A's first id.
        Assert.Equal(1, subjectIdB);
        Assert.Equal(1, perRowSubjectIdsA[0]);

        await SqliteTestHelpers.AssertFtsIntegrityAsync(pathA, Ct);
        await SqliteTestHelpers.AssertFtsIntegrityAsync(pathB, Ct);
    }

    [Fact]
    public async Task FlushAsync_completes_only_after_every_previously_enqueued_row_commits()
    {
        var path = DbPath();
        using var writer = new SqliteBatchWriter(path);

        for (var i = 0; i < 5; i++)
        {
            writer.Enqueue(Row(i));
        }

        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.Equal(5, await SqliteTestHelpers.CountRowsAsync(path, Ct));
    }
}

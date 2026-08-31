using EventScope.Storage.Search;
using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// The real end-to-end path: <see cref="SqliteBatchWriter"/> automatically runs
/// <see cref="FtsIndexer"/> catch-up batches on its own thread once the queue goes idle,
/// with no separate call needed from ingest. <see cref="FtsIndexerTests"/> covers the
/// indexer's SQL directly; this covers the scheduling around it.
/// </summary>
public sealed class SqliteBatchWriterIndexingTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("eventscope-batchwriter-indexing-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string DbPath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(_directory, $"{name}.db");

    private static WriteOp.InsertMessage Row(int i) => new(
        EnqueuedTicks: 0, ReceivedTicks: 0, SegmentId: 0, Offset: i * 64, Length: 64,
        MessageId: $"m-{i}", CorrelationId: $"c-{i}", Subject: "orders.created",
        Partition: i % 4, Flags: 0, Preview: $"preview-{i}", BodyHead: $"body {i}");

    private static async Task<long> ScalarAsync(string dbPath, string sql, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Counts rowids matching a full-text query — see <c>FtsIndexerTests</c>' remarks
    /// on why <c>COUNT(*)</c> without a <c>MATCH</c> clause is not a reliable way to check
    /// what is actually indexed in an external-content FTS5 table.</summary>
    private static async Task<long> MatchingCountAsync(
        string dbPath, string ftsTable, string matchQuery, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {ftsTable} WHERE {ftsTable} MATCH $query";
        // See FtsIndexerTests' MatchingRowidsAsync remarks: FTS5 treats a bare "-" as its NOT
        // operator, so a hyphenated value like "c-1" needs to be quoted as a literal phrase.
        command.Parameters.AddWithValue("$query", $"\"{matchQuery}\"");
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task WaitUntilAsync(Func<Task<long>> query, long expected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        long last = -1;
        while (DateTime.UtcNow < deadline)
        {
            last = await query();
            if (last == expected) return;
            await Task.Delay(20, ct);
        }

        throw new TimeoutException($"Expected {expected}, last saw {last}.");
    }

    [Fact]
    public async Task Inserted_rows_are_indexed_automatically_once_the_writer_goes_idle()
    {
        var path = DbPath();
        using var writer = new SqliteBatchWriter(path);

        for (var i = 0; i < 10; i++) writer.Enqueue(Row(i));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await WaitUntilAsync(
            () => ScalarAsync(path, "SELECT value FROM index_state WHERE name='fts_hwm'", Ct),
            expected: 10, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(10, await MatchingCountAsync(path, "body_fts", "body", Ct));
        // ident_fts is trigram-tokenized: no `*` wildcard, and queries under 3 characters
        // match nothing - spot-check individual rows instead of one blanket query.
        Assert.Equal(1, await MatchingCountAsync(path, "ident_fts", "c-0", Ct));
        Assert.Equal(1, await MatchingCountAsync(path, "ident_fts", "c-9", Ct));
        Assert.Equal(0, writer.IndexLag);

        await SqliteTestHelpers.AssertFtsIntegrityAsync(path, Ct);
    }

    [Fact]
    public async Task IndexLag_reflects_a_deliberately_large_backlog_until_it_catches_up()
    {
        var path = DbPath();
        using var writer = new SqliteBatchWriter(path);

        // Comfortably more than one catch-up batch, so lag is observably nonzero at least
        // momentarily rather than always resolving within a single indexing pass.
        var rowCount = FtsIndexer.CatchUpBatchSize + 200;
        for (var i = 0; i < rowCount; i++) writer.Enqueue(Row(i));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await WaitUntilAsync(
            () => ScalarAsync(path, "SELECT value FROM index_state WHERE name='fts_hwm'", Ct),
            expected: rowCount, TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(0, writer.IndexLag);
        Assert.Equal(1, await MatchingCountAsync(path, "ident_fts", "c-0", Ct));
        Assert.Equal(1, await MatchingCountAsync(path, "ident_fts", $"c-{rowCount - 1}", Ct));
    }
}

using EventScope.Storage.Search;
using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// <see cref="FtsIndexer"/>'s catch-up batch in isolation, against a raw connection — no
/// <see cref="SqliteBatchWriter"/> involved, so these test the SQL itself rather than its
/// scheduling. <see cref="SqliteBatchWriterIndexingTests"/> covers the real end-to-end path.
/// </summary>
public sealed class FtsIndexerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Directory.CreateTempSubdirectory("eventscope-fts-tests-").FullName, "test.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
    }

    private SqliteConnection OpenSchema()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        SqliteSchema.Apply(connection);

        // messages.subject_id has a foreign key to subjects(id) - Microsoft.Data.Sqlite
        // enforces foreign keys by default, so every row inserted by these tests (which
        // always uses subject_id = 0, since the indexer doesn't care about subject content)
        // needs a matching subjects row to exist first.
        using var seedSubject = connection.CreateCommand();
        seedSubject.CommandText = "INSERT INTO subjects (id, name) VALUES (0, 'test')";
        seedSubject.ExecuteNonQuery();

        return connection;
    }

    private static void InsertMessage(
        SqliteConnection connection, string? messageId, string? correlationId, string? bodyHead)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages
                (enqueued_ticks, received_ticks, segment_id, offset, length,
                 message_id, correlation_id, subject_id, partition, flags, preview, body_head)
            VALUES
                (0, 0, 0, 0, 0, $messageId, $correlationId, 0, 0, 0, 'p', $bodyHead)
            """;
        command.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bodyHead", (object?)bodyHead ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Rowids matching a full-text query — the reliable way to check what's actually
    /// indexed in an external-content FTS5 table. <c>SELECT COUNT(*)</c> without a
    /// <c>MATCH</c> clause is <b>not</b> reliable for this: measured directly, it reflects
    /// the underlying content table's (<c>messages</c>) row count rather than the number of
    /// rowids actually present in the fts5 index — so it would report the same count whether
    /// a row was indexed or deliberately skipped (e.g. a null <c>body_head</c>). A `MATCH`
    /// query is what the index is actually built for, and is what search (M2 later) will use
    /// for real, so it is also the more representative thing to test against.</summary>
    private static async Task<List<long>> MatchingRowidsAsync(
        SqliteConnection connection, string ftsTable, string matchQuery, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid FROM {ftsTable} WHERE {ftsTable} MATCH $query ORDER BY rowid";
        // FTS5's query syntax treats a bare "-" as its NOT operator (e.g. "c-1" parses as
        // "c NOT 1", which then fails since a lone numeral there isn't a valid column
        // reference) - double-quoting makes the whole value a literal phrase instead,
        // exactly like quoting a phrase with a hyphen in any other FTS5 query would need.
        command.Parameters.AddWithValue("$query", $"\"{matchQuery}\"");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rowids = new List<long>();
        while (await reader.ReadAsync(ct)) rowids.Add(reader.GetInt64(0));
        return rowids;
    }

    [Fact]
    public async Task A_batch_indexes_every_row_and_advances_the_high_water_mark_to_the_last_id()
    {
        using var connection = OpenSchema();
        for (var i = 0; i < 5; i++)
        {
            InsertMessage(connection, $"m-{i}", $"c-{i}", $"body {i}");
        }

        var advanced = FtsIndexer.RunOneBatch(connection);

        Assert.Equal(5, advanced);
        Assert.Equal(5, FtsIndexer.ReadHwm(connection));
        Assert.Equal(0, FtsIndexer.GetLagRows(connection));
        Assert.Equal([1, 2, 3, 4, 5], await MatchingRowidsAsync(connection, "body_fts", "body", Ct));

        // ident_fts is trigram-tokenized: no `*` prefix wildcard support, and queries under
        // 3 characters match nothing (build plan §3.4) - "c-0".."c-4" are each exactly 3
        // characters, so check each row's own correlation id individually rather than one
        // query for "all of them".
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal([i + 1L], await MatchingRowidsAsync(connection, "ident_fts", $"c-{i}", Ct));
        }
    }

    [Fact]
    public async Task The_high_water_mark_advances_past_rows_with_a_null_body_head_even_though_body_fts_skips_them()
    {
        using var connection = OpenSchema();
        InsertMessage(connection, "m-1", "c-1", bodyHead: null); // e.g. an empty payload
        InsertMessage(connection, "m-2", "c-2", bodyHead: "has a body");

        var advanced = FtsIndexer.RunOneBatch(connection);

        Assert.Equal(2, advanced);
        Assert.Equal(2, FtsIndexer.ReadHwm(connection));
        // Only the second row has a body_head, so only it should be reachable through
        // body_fts - rowid 1 was never a candidate for the insert at all.
        Assert.Equal([2], await MatchingRowidsAsync(connection, "body_fts", "body", Ct));
        // ident_fts indexes message_id/correlation_id regardless of body_head, so both rows
        // are reachable there.
        Assert.Equal([1], await MatchingRowidsAsync(connection, "ident_fts", "c-1", Ct));
        Assert.Equal([2], await MatchingRowidsAsync(connection, "ident_fts", "c-2", Ct));
    }

    [Fact]
    public void Running_a_batch_again_with_nothing_new_is_a_no_op_and_creates_no_duplicates()
    {
        using var connection = OpenSchema();
        InsertMessage(connection, "m-1", "c-1", "body");

        var first = FtsIndexer.RunOneBatch(connection);
        var second = FtsIndexer.RunOneBatch(connection);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // already caught up
        Assert.Equal(1, FtsIndexer.ReadHwm(connection));
    }

    [Fact]
    public async Task A_batch_larger_than_the_catch_up_limit_needs_more_than_one_call_to_fully_catch_up()
    {
        using var connection = OpenSchema();
        var rowCount = FtsIndexer.CatchUpBatchSize + 500;
        for (var i = 0; i < rowCount; i++)
        {
            InsertMessage(connection, $"m-{i}", $"c-{i}", $"body {i}");
        }

        var firstBatch = FtsIndexer.RunOneBatch(connection);
        Assert.Equal(FtsIndexer.CatchUpBatchSize, firstBatch);
        Assert.Equal(500, FtsIndexer.GetLagRows(connection));

        var secondBatch = FtsIndexer.RunOneBatch(connection);
        Assert.Equal(500, secondBatch);
        Assert.Equal(0, FtsIndexer.GetLagRows(connection));

        // Spot-check the first row (indexed by the first batch) and the last (indexed only
        // by the second) - trigram has no `*` wildcard, so an exhaustive single query isn't
        // available; hwm/lag above already prove the full range was covered.
        Assert.Equal([1], await MatchingRowidsAsync(connection, "ident_fts", "c-0", Ct));
        Assert.Equal([rowCount], await MatchingRowidsAsync(connection, "ident_fts", $"c-{rowCount - 1}", Ct));
    }

    [Fact]
    public void Optimize_and_merge_do_not_throw_against_a_real_schema()
    {
        using var connection = OpenSchema();
        InsertMessage(connection, "m-1", "c-1", "body");
        FtsIndexer.RunOneBatch(connection);

        FtsIndexer.Merge(connection);
        FtsIndexer.Optimize(connection);
    }
}

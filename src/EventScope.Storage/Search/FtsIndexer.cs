using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Search;

/// <summary>
/// The catch-up indexer that populates <c>body_fts</c>/<c>ident_fts</c> from
/// <c>messages</c>, outside the ingest transaction so indexing never stalls a write (build
/// plan §3.4). Runs on the same thread and connection as <c>SqliteBatchWriter</c>'s inserts —
/// a second write connection would mean <c>BEGIN IMMEDIATE</c> contention and
/// <c>SQLITE_BUSY</c> storms, exactly what same-thread execution avoids by construction.
///
/// <para>
/// <b>Why the high-water mark advances in the same transaction as the inserts.</b> FTS5 does
/// not dedupe rows by rowid — re-running a catch-up batch after a crash between the inserts
/// and the hwm update would insert the same rowids twice. One transaction makes that
/// impossible: either the whole batch (both index inserts and the hwm advance) commits, or
/// none of it does.
/// </para>
///
/// <para>
/// <b>Why the window is computed once, separately, rather than taken from the inserted
/// rowset.</b> <c>body_fts</c> skips rows whose <c>body_head</c> is <c>NULL</c> (an empty
/// payload), but the high-water mark must still advance past them — otherwise those rows
/// would be retried forever, never actually skipped. Computing <c>newHwm</c> as "the id of
/// the last row in this batch of up to <see cref="CatchUpBatchSize"/>" first, then filtering
/// each insert independently against that same window, keeps both tables indexing an
/// identical row range while still allowing one to skip individual rows within it.
/// </para>
/// </summary>
internal static class FtsIndexer
{
    /// <summary>Rows considered per catch-up transaction — matches the build plan's §3.4
    /// worked example exactly.</summary>
    public const int CatchUpBatchSize = 2000;

    /// <summary>Indexes up to <see cref="CatchUpBatchSize"/> newly-inserted rows and advances
    /// <c>index_state.fts_hwm</c>. Returns the number of ids the high-water mark advanced by
    /// (0 if already caught up — nothing to do).</summary>
    public static long RunOneBatch(SqliteConnection connection)
    {
        var hwm = ReadHwm(connection);

        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            long newHwm;
            using (var selectMax = connection.CreateCommand())
            {
                selectMax.CommandText = """
                    SELECT COALESCE(MAX(id), $hwm) FROM (
                        SELECT id FROM messages WHERE id > $hwm ORDER BY id LIMIT $batchSize)
                    """;
                selectMax.Parameters.AddWithValue("$hwm", hwm);
                selectMax.Parameters.AddWithValue("$batchSize", CatchUpBatchSize);
                newHwm = Convert.ToInt64(selectMax.ExecuteScalar());
            }

            if (newHwm == hwm)
            {
                Execute(connection, "ROLLBACK;"); // nothing new - no point committing an empty txn
                return 0;
            }

            using (var insertBody = connection.CreateCommand())
            {
                insertBody.CommandText = """
                    INSERT INTO body_fts(rowid, body_head)
                    SELECT id, body_head FROM messages
                     WHERE id > $hwm AND id <= $newHwm AND body_head IS NOT NULL
                    """;
                insertBody.Parameters.AddWithValue("$hwm", hwm);
                insertBody.Parameters.AddWithValue("$newHwm", newHwm);
                insertBody.ExecuteNonQuery();
            }

            using (var insertIdent = connection.CreateCommand())
            {
                insertIdent.CommandText = """
                    INSERT INTO ident_fts(rowid, message_id, correlation_id)
                    SELECT id, message_id, correlation_id FROM messages
                     WHERE id > $hwm AND id <= $newHwm
                    """;
                insertIdent.Parameters.AddWithValue("$hwm", hwm);
                insertIdent.Parameters.AddWithValue("$newHwm", newHwm);
                insertIdent.ExecuteNonQuery();
            }

            using (var updateHwm = connection.CreateCommand())
            {
                updateHwm.CommandText = "UPDATE index_state SET value = $newHwm WHERE name = 'fts_hwm'";
                updateHwm.Parameters.AddWithValue("$newHwm", newHwm);
                updateHwm.ExecuteNonQuery();
            }

            Execute(connection, "COMMIT;");
            return newHwm - hwm;
        }
        catch
        {
            try
            {
                Execute(connection, "ROLLBACK;");
            }
            catch (SqliteException)
            {
                // The connection may already be in a bad state after the original failure -
                // the original exception is what matters, not a failed rollback attempt.
            }

            throw;
        }
    }

    public static long ReadHwm(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_state WHERE name = 'fts_hwm'";
        var result = command.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt64(result);
    }

    /// <summary>Index lag as a row count — <c>MAX(messages.id) − hwm</c> — the build plan's
    /// own definition, surfaced so the status bar and search results can state whether the
    /// index is current.</summary>
    public static long GetLagRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(id), 0) - (SELECT value FROM index_state WHERE name = 'fts_hwm')
            FROM messages
            """;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Idle maintenance — keeps query latency flat as the index grows. Call
    /// periodically while idle, not on every tick. The <c>rank</c> column in the column list
    /// is required syntax for FTS5's special-command inserts, not a real column being
    /// written to.</summary>
    public static void Merge(SqliteConnection connection) =>
        Execute(connection, "INSERT INTO body_fts(body_fts, rank) VALUES('merge', -16)");

    /// <summary>Idle maintenance — call once, on close.</summary>
    public static void Optimize(SqliteConnection connection) =>
        Execute(connection, "INSERT INTO body_fts(body_fts) VALUES('optimize')");

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

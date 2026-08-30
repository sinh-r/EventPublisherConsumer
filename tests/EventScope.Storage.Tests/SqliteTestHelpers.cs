using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Tests;

internal static class SqliteTestHelpers
{
    public static async Task WaitForRowCountAsync(
        string databasePath, long expected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        long last;
        do
        {
            last = await CountRowsAsync(databasePath, ct);
            if (last >= expected) return;
            await Task.Delay(20, ct);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Expected {expected} rows within {timeout}, saw {last}.");
    }

    public static async Task<long> CountRowsAsync(string databasePath, CancellationToken ct)
    {
        // Pooling=False: these are throwaway assertion connections in a test that's about to
        // delete the temp directory. Pooling would keep the native handle open past Dispose,
        // the exact Windows file-lock collision the build plan §3.6 calls out — the writer's
        // own connection needs the ClearAllPools() dance in SqliteBatchWriter.Dispose(); a
        // one-off test connection is simpler to just not pool at all.
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The canonical external-content FTS contract validator (build plan §7) — run
    /// only after the writer that owns the connection has been disposed, so this doesn't
    /// race a live batch commit for the SQLite write lock.</summary>
    public static async Task AssertFtsIntegrityAsync(string databasePath, CancellationToken ct)
    {
        // Pooling=False: these are throwaway assertion connections in a test that's about to
        // delete the temp directory. Pooling would keep the native handle open past Dispose,
        // the exact Windows file-lock collision the build plan §3.6 calls out — the writer's
        // own connection needs the ClearAllPools() dance in SqliteBatchWriter.Dispose(); a
        // one-off test connection is simpler to just not pool at all.
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO body_fts(body_fts) VALUES('integrity-check')";
        await command.ExecuteNonQueryAsync(ct);
    }
}

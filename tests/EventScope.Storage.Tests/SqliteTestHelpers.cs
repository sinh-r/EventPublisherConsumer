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

    /// <summary>
    /// Waits out the rollover seal <see cref="Sqlite.SessionStore.EnsureCurrentDay"/> runs on a
    /// fire-and-forget <c>Task.Run</c> — it disposes the old day's segment writer and then its
    /// SQLite writer, and exposes no handle to await. Being able to open that day's database
    /// exclusively is the observable end of it: the writer holds the file for its whole life,
    /// so this succeeds only once it is gone.
    ///
    /// <para>
    /// This replaced a fixed <c>await Task.Delay(200)</c> that every caller here used to make.
    /// That bet held in isolation and lost under full-suite load, where the thread pool is
    /// saturated and the seal task can be queued behind everything else — the seal was still
    /// holding the day's <c>.db</c> when the test moved on and tried to delete or read it.
    /// </para>
    /// </summary>
    public static async Task WaitForRolloverSealAsync(
        string rootDirectory, string day, CancellationToken ct)
    {
        var dbPath = Path.Combine(rootDirectory, day, $"{day}.db");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using (File.Open(dbPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return;
                }
            }
            catch (IOException)
            {
                await Task.Delay(20, ct);
            }
        }

        throw new TimeoutException($"The rollover seal for {day} did not release within 30s.");
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

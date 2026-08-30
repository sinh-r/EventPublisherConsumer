using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// Verifies FTS5 with the trigram tokenizer is actually usable, by behaviour rather than a
/// version-string check (build plan §2) — failing loudly at startup rather than degrading
/// silently into search results that quietly never work.
/// </summary>
internal static class SqliteCapabilityProbe
{
    public static void Verify(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5');";
            var result = Convert.ToInt64(check.ExecuteScalar());
            if (result != 1)
            {
                throw new InvalidOperationException(
                    "This build of SQLite does not have FTS5 compiled in. EventScope cannot index or search messages without it.");
            }
        }

        try
        {
            using var probe = connection.CreateCommand();
            probe.CommandText = "CREATE VIRTUAL TABLE temp.__probe USING fts5(x, tokenize='trigram');";
            probe.ExecuteNonQuery();

            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE temp.__probe;";
            drop.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                "This build of SQLite does not support the FTS5 trigram tokenizer. EventScope cannot perform correlation-ID search without it.",
                ex);
        }
    }
}

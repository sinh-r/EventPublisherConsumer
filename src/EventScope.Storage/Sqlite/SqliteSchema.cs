using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// The schema from <c>Docs/eventscope-implementation-plan.md</c> §4, applied verbatim.
/// <c>body_fts</c>/<c>ident_fts</c> are created now, in M1b, even though the indexer that
/// populates them is M2 work — creating them later would mean a migration for a table shape
/// that never changes. Idempotent: every statement is <c>IF NOT EXISTS</c>, so opening an
/// existing day file re-applies harmlessly.
/// </summary>
internal static class SqliteSchema
{
    public static void Apply(SqliteConnection connection)
    {
        using (var pragmas = connection.CreateCommand())
        {
            // journal_size_limit bounds -wal growth per the build plan §3.4 WAL-starvation
            // note: unchecked, a long-held reader (deep scan) stalls the checkpointer and
            // -wal grows without bound, blowing the storage cap from a direction the cap
            // accounting doesn't otherwise see.
            pragmas.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA journal_size_limit = 67108864;
                """;
            pragmas.ExecuteNonQuery();
        }

        using var ddl = connection.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
                id              INTEGER PRIMARY KEY,
                enqueued_ticks  INTEGER NOT NULL,
                received_ticks  INTEGER NOT NULL,
                segment_id      INTEGER NOT NULL,
                offset          INTEGER NOT NULL,
                length          INTEGER NOT NULL,
                message_id      TEXT,
                correlation_id  TEXT,
                subject_id      INTEGER REFERENCES subjects(id),
                partition       INTEGER,
                flags           INTEGER NOT NULL DEFAULT 0,
                preview         TEXT,
                body_head       TEXT
            );

            CREATE TABLE IF NOT EXISTS subjects (id INTEGER PRIMARY KEY, name TEXT UNIQUE);

            CREATE INDEX IF NOT EXISTS ix_msg_time ON messages(enqueued_ticks);
            CREATE INDEX IF NOT EXISTS ix_msg_corr ON messages(correlation_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS body_fts USING fts5(
                body_head,
                content = 'messages', content_rowid = 'id',
                tokenize = 'unicode61'
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS ident_fts USING fts5(
                message_id, correlation_id,
                content = 'messages', content_rowid = 'id',
                tokenize = 'trigram'
            );

            CREATE TABLE IF NOT EXISTS index_state (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
            INSERT OR IGNORE INTO index_state (name, value) VALUES ('fts_hwm', 0);
            """;
        ddl.ExecuteNonQuery();
    }
}

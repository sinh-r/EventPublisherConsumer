using Microsoft.Data.Sqlite;
using EventScope.Storage.Sqlite;

namespace EventScope.Storage.Search;

/// <summary>One captured day, as the session picker needs to describe it.</summary>
/// <param name="Day">The <c>yyyy-MM-dd</c> directory name.</param>
/// <param name="RowCount">Messages captured that day; 0 for a day whose database is gone but
/// whose directory survives (retention evicts payloads and can leave the shell behind).</param>
/// <param name="MinRowId">Lowest <c>messages.id</c> in the day, or 0 when empty.</param>
/// <param name="MaxRowId">Highest <c>messages.id</c> in the day, or 0 when empty.</param>
public sealed record DaySummary(
    string Day,
    long RowCount,
    long MinEnqueuedTicks,
    long MaxEnqueuedTicks,
    long MinRowId,
    long MaxRowId)
{
    /// <summary>Whether ids run contiguously from <see cref="MinRowId"/> to <see cref="MaxRowId"/>.
    /// When they do, the n-th row of the day is <c>MinRowId + n</c>, so a scrollbar jump becomes a
    /// rowid seek instead of an <c>OFFSET</c> scan. Nothing in the write path deletes individual
    /// rows — retention drops whole day directories — so this should always hold; it is checked per
    /// day file rather than assumed, and paging falls back to <c>OFFSET</c> when it does not.</summary>
    public bool IsDense => RowCount > 0 && MaxRowId - MinRowId + 1 == RowCount;
}

/// <summary>
/// Reads already-captured messages back out of a session root, for browsing history rather than
/// searching it. The plain-paging counterpart to <see cref="FtsSearchService"/>: same day-file
/// layout, same short-lived read-only connection per query (safe alongside live ingest under WAL,
/// and never holding a handle that would block retention's directory delete), and — via
/// <see cref="MessageRowQuery"/> — the same <see cref="SearchHit"/> row shape, so the UI renders a
/// history row and a search result identically.
///
/// <para>
/// <b>Takes a root directory, not a <see cref="SessionStore"/>.</b> That type's constructor opens
/// and creates the current day's writer, so depending on it here would mean browsing yesterday's
/// capture created an empty directory for today and took a write handle on a session the user
/// never started. Everything below needs only paths.
/// </para>
///
/// <para>
/// <b>The core is synchronous</b> because the grid's indexer is: <c>DataGrid</c> asks for a row on
/// the UI thread and needs it now, and a placeholder-then-refresh scheme fights the scroll. Each
/// read is a rowid seek plus a few hundred rows out of a local file. The <c>…Async</c> wrappers
/// offload the same work to the thread pool for callers that are not on the indexer path — the day
/// summary sweep at browse-open time, and page prefetch.
/// </para>
///
/// <para>
/// <b>Paging is keyset, not <c>OFFSET</c>.</b> <c>messages.id</c> is an <c>INTEGER PRIMARY KEY</c>,
/// so <c>WHERE m.id &gt;= $from ORDER BY m.id LIMIT $take</c> is an index seek whose cost does not
/// grow with how far into the day the caller has scrolled.
/// </para>
/// </summary>
public sealed class HistoryQueryService(string rootDirectory)
{
    public string RootDirectory => rootDirectory;

    /// <summary>Every captured day under the root, oldest first, each with the counts the picker
    /// shows. A day whose database is missing or unreadable is reported with a zero count rather
    /// than skipped — the directory is real, and silently hiding it would misrepresent what is on
    /// disk.</summary>
    public IReadOnlyList<DaySummary> ListDays(CancellationToken cancellationToken = default)
    {
        var days = SessionLayout.ListDayDirectories(rootDirectory);
        var summaries = new List<DaySummary>(days.Count);

        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summaries.Add(SummarizeDay(day));
        }

        return summaries;
    }

    public Task<IReadOnlyList<DaySummary>> ListDaysAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ListDays(cancellationToken), cancellationToken);

    public DaySummary SummarizeDay(string day)
    {
        var dbPath = SessionLayout.DayDatabasePath(rootDirectory, day);
        if (!File.Exists(dbPath)) return EmptySummary(day);

        try
        {
            using var connection = new SqliteConnection(SessionLayout.ReadOnlyConnectionString(dbPath));
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*),
                       COALESCE(MIN(enqueued_ticks), 0), COALESCE(MAX(enqueued_ticks), 0),
                       COALESCE(MIN(id), 0), COALESCE(MAX(id), 0)
                FROM messages
                """;

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new DaySummary(
                    day,
                    reader.GetInt64(0),
                    reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4))
                : EmptySummary(day);
        }
        catch (SqliteException)
        {
            // A partial or malformed day file must not break the whole listing - the same
            // tolerance FtsSearchService applies when a day's index_state is missing.
            return EmptySummary(day);
        }
    }

    private static DaySummary EmptySummary(string day) => new(day, 0, 0, 0, 0, 0);

    /// <summary>
    /// One page of <paramref name="day"/>'s messages in capture order, starting at the first row
    /// whose id is at least <paramref name="fromRowId"/>. Returns fewer than <paramref name="take"/>
    /// rows only at the end of the day file.
    /// </summary>
    public IReadOnlyList<SearchHit> PageFromRowId(string day, long fromRowId, int take)
    {
        if (take <= 0) return [];

        var sql = $"""
            SELECT {MessageRowQuery.Columns}
            FROM messages m
            {MessageRowQuery.SubjectJoin}
            WHERE m.id >= $from
            ORDER BY m.id
            LIMIT $take
            """;

        return Read(day, sql, command =>
        {
            command.Parameters.AddWithValue("$from", fromRowId);
            command.Parameters.AddWithValue("$take", take);
        });
    }

    /// <summary>Positional paging, for a day whose ids are not contiguous
    /// (<see cref="DaySummary.IsDense"/> is false). Costs an index walk proportional to
    /// <paramref name="skip"/>, which is why it is the fallback and not the default.</summary>
    public IReadOnlyList<SearchHit> PageByOffset(string day, long skip, int take)
    {
        if (take <= 0) return [];

        var sql = $"""
            SELECT {MessageRowQuery.Columns}
            FROM messages m
            {MessageRowQuery.SubjectJoin}
            ORDER BY m.id
            LIMIT $take OFFSET $skip
            """;

        return Read(day, sql, command =>
        {
            command.Parameters.AddWithValue("$take", take);
            command.Parameters.AddWithValue("$skip", skip);
        });
    }

    /// <summary>Streams a whole day oldest-first, a page at a time — for callers that want every
    /// row without holding one connection open across the whole scan.</summary>
    public IEnumerable<SearchHit> EnumerateDay(string day, int pageSize)
    {
        var from = 0L;
        while (true)
        {
            var page = PageFromRowId(day, from, pageSize);
            if (page.Count == 0) yield break;

            foreach (var hit in page)
            {
                yield return hit;
            }

            from = page[^1].MessageRowId + 1;
            if (page.Count < pageSize) yield break;
        }
    }

    private IReadOnlyList<SearchHit> Read(string day, string sql, Action<SqliteCommand> bind)
    {
        var dbPath = SessionLayout.DayDatabasePath(rootDirectory, day);
        if (!File.Exists(dbPath)) return [];

        try
        {
            using var connection = new SqliteConnection(SessionLayout.ReadOnlyConnectionString(dbPath));
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);

            var page = new List<SearchHit>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // IndexHwm is an FTS-currency stamp; this path never consults the index, so it
                // reports "not applicable" rather than a plausible-looking zero.
                page.Add(MessageRowQuery.ReadHit(reader, day, SearchHit.IndexHwmNotApplicable));
            }

            return page;
        }
        catch (SqliteException)
        {
            return [];
        }
    }
}

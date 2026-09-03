using System.Runtime.CompilerServices;
using EventScope.Core.Models;
using Microsoft.Data.Sqlite;
using EventScope.Storage.Sqlite;

namespace EventScope.Storage.Search;

public enum SearchScope { Body, Identifiers }

/// <summary>
/// One message read back off disk — the shared projection for every cold read of the
/// <c>messages</c> table, whether it arrived by full-text search
/// (<see cref="FtsSearchService"/>) or by plain history paging
/// (<see cref="HistoryQueryService"/>). <see cref="Day"/> is the directory the row was actually
/// read from, which is the only trustworthy way to locate its payload again.
///
/// <para><see cref="IndexHwm"/> is meaningful only for search results — the build plan requires
/// that "every FTS result set is stamped with its IndexHwm so the UI can state whether results
/// are current". A non-search read reports <see cref="IndexHwmNotApplicable"/>.</para>
/// </summary>
public sealed record SearchHit(
    string Day,
    long MessageRowId,
    long EnqueuedTicks,
    int SegmentId,
    int Offset,
    int Length,
    string? MessageId,
    string? CorrelationId,
    string Subject,
    string? Preview,
    short Partition,
    MessageFlags Flags,
    long IndexHwm)
{
    /// <summary>Reported as <see cref="IndexHwm"/> by a read that never consulted the FTS index
    /// at all — plain history paging (<see cref="HistoryQueryService"/>). Distinct from a real
    /// hwm of 0, which means the index exists but has nothing in it yet.</summary>
    public const long IndexHwmNotApplicable = -1;
}

/// <summary>
/// Tiered full-text search against <see cref="SessionStore"/>'s day files — the FTS tier of
/// build plan §5 M2 (the in-memory ring tier lives in <c>EventScope.App</c>, over live rows;
/// deep scan is <see cref="DeepScanner"/>). Queries day files newest-first, stopping as soon
/// as <c>maxResults</c> is reached ("early exit") rather than scanning every historical day.
///
/// <para>
/// Every query opens its own short-lived, read-only connection per day file (build plan
/// §3.6: "pooled tasks, one read-only connection per query per day file") — never the day's
/// live <see cref="SqliteBatchWriter"/> connection, and safe to run concurrently with ingest
/// under WAL.
/// </para>
///
/// <para>
/// <b>Trigram's under-3-character floor.</b> A correlation/message-id query shorter than 3
/// characters matches nothing under the trigram tokenizer (build plan §3.4) — confirmed
/// directly while testing this class, not assumed. <see cref="SearchScope.Identifiers"/>
/// queries below that length fall back to a <c>LIKE '%x%'</c> scan of <c>messages</c>
/// directly instead of querying <c>ident_fts</c>, and <see cref="SearchHit"/> doesn't
/// distinguish which path answered — callers needing to say so explicitly should check
/// <c>query.Length &lt; 3</c> themselves before calling.
/// </para>
/// </summary>
public sealed class FtsSearchService(string rootDirectory)
{
    /// <summary>The session root these queries run against — callers that act on a hit (reading its
    /// payload back) need the same root the hit came from.</summary>
    public string RootDirectory => rootDirectory;

    private const int TrigramMinimumLength = 3;

    public IAsyncEnumerable<SearchHit> SearchBodyAsync(string query, int maxResults, CancellationToken cancellationToken) =>
        SearchAsync(SearchScope.Body, query, maxResults, cancellationToken);

    public IAsyncEnumerable<SearchHit> SearchIdentifiersAsync(string query, int maxResults, CancellationToken cancellationToken) =>
        SearchAsync(SearchScope.Identifiers, query, maxResults, cancellationToken);

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchScope scope, string query, int maxResults, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query) || maxResults <= 0) yield break;

        var useLikeFallback = scope == SearchScope.Identifiers && query.Length < TrigramMinimumLength;
        var remaining = maxResults;

        // Newest day first - early exit means never opening an older day file once enough
        // results have already been found in more recent ones.
        var days = SessionLayout.ListDayDirectories(rootDirectory);
        for (var i = days.Count - 1; i >= 0 && remaining > 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var day = days[i];
            var dbPath = SessionLayout.DayDatabasePath(rootDirectory, day);
            if (!File.Exists(dbPath)) continue;

            await using var connection = new SqliteConnection(SessionLayout.ReadOnlyConnectionString(dbPath));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            long hwm;
            try
            {
                hwm = FtsIndexer.ReadHwm(connection);
            }
            catch (SqliteException)
            {
                continue; // e.g. index_state missing on a malformed/partial file - skip, don't fail the whole search
            }

            await using var command = connection.CreateCommand();
            command.CommandText = BuildQuery(scope, useLikeFallback);
            command.Parameters.AddWithValue("$query", useLikeFallback ? $"%{query}%" : QuoteAsPhrase(query));
            command.Parameters.AddWithValue("$limit", remaining);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return MessageRowQuery.ReadHit(reader, day, hwm);

                remaining--;
                if (remaining <= 0) yield break;
            }
        }
    }

    /// <summary>FTS5 treats a bare <c>-</c> as its NOT operator, so a query containing one
    /// (a correlation id shaped like <c>c-1</c>, for instance) has to be quoted as a literal
    /// phrase or the parser misreads it as an exclusion.</summary>
    private static string QuoteAsPhrase(string query) => $"\"{query.Replace("\"", "\"\"")}\"";

    /// <summary>All three variants project the same columns in the same order
    /// (<see cref="MessageRowQuery.Columns"/>) so <see cref="MessageRowQuery.ReadHit"/> can read
    /// any of them. They differ only in what they join and match against.</summary>
    private static string BuildQuery(SearchScope scope, bool useLikeFallback) => scope switch
    {
        SearchScope.Body => $"""
            SELECT {MessageRowQuery.Columns}
            FROM body_fts f
            JOIN messages m ON m.id = f.rowid
            {MessageRowQuery.SubjectJoin}
            WHERE f.body_fts MATCH $query
            ORDER BY m.id DESC
            LIMIT $limit
            """,
        SearchScope.Identifiers when useLikeFallback => $"""
            SELECT {MessageRowQuery.Columns}
            FROM messages m
            {MessageRowQuery.SubjectJoin}
            WHERE m.correlation_id LIKE $query OR m.message_id LIKE $query
            ORDER BY m.id DESC
            LIMIT $limit
            """,
        SearchScope.Identifiers => $"""
            SELECT {MessageRowQuery.Columns}
            FROM ident_fts f
            JOIN messages m ON m.id = f.rowid
            {MessageRowQuery.SubjectJoin}
            WHERE f.ident_fts MATCH $query
            ORDER BY m.id DESC
            LIMIT $limit
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

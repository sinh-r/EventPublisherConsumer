using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using EventScope.Core.Models;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;

namespace EventScope.Storage.Search;

/// <summary>
/// How far a deep scan has got, in payload bytes rather than rows — UI spec §7 asks for
/// "Scanned 412 MB of 1.84 GB · 87 matches", and a row count alone has no denominator to draw
/// a determinate bar against.
///
/// <para>
/// <see cref="TotalBytes"/> is settled before the first payload is read and does not move
/// afterwards, so the bar never rebases mid-scan. It counts the payload bytes the scan will
/// read, not the size of the segment files on disk: those are LZ4-compressed and include block
/// tables and footers, so they would consistently under-report progress against what is
/// actually being decompressed.
/// </para>
/// </summary>
public readonly record struct DeepScanProgress(long BytesScanned, long TotalBytes, int Matches)
{
    /// <summary>0 to 1, clamped. Zero when there is nothing to scan, which a determinate
    /// progress bar renders as empty rather than as a division by zero.</summary>
    public double Fraction =>
        TotalBytes <= 0 ? 0d : Math.Clamp((double)BytesScanned / TotalBytes, 0d, 1d);
}

/// <summary>
/// The last-resort search tier (build plan §5 M2): streams every message's <b>full</b> body
/// (not the 2 KB <c>body_head</c> capped copy <c>body_fts</c> indexes) looking for the query as
/// a substring, and works even if the FTS index is behind or the term only appears past the
/// 2 KB cap. Reports progress via <see cref="IProgress{T}"/> so a UI can drive a progress bar,
/// and honors cancellation between every row.
///
/// <para>
/// Pages results by yielding them as an <see cref="IAsyncEnumerable{T}"/> rather than
/// collecting them all before returning, so a caller streaming into a bounded UI list never
/// holds a large in-memory result set or (per build plan §3.4's WAL-starvation note) a read
/// transaction open across the whole scan.
/// </para>
///
/// <para>
/// <b>Yields <see cref="SearchHit"/>, the same row shape every other cold read produces</b>,
/// projected through <see cref="MessageRowQuery"/> — which exists precisely so the tiers cannot
/// drift into describing the same message differently. That is also what lets deep-scan results
/// open in the history grid through the same path FTS results already use, with no second
/// hydration step. <see cref="SearchHit.IndexHwm"/> is
/// <see cref="SearchHit.IndexHwmNotApplicable"/> here: a deep scan never consults the FTS index,
/// so it has no high-water mark to report and "is the index current" is not a question it can
/// answer — nor one it needs to, since reading past the index is the entire point of this tier.
/// </para>
/// </summary>
public static class DeepScanner
{
    /// <summary>
    /// Deep-scans a whole session root, newest day first with an early exit once
    /// <paramref name="maxResults"/> is reached — the same traversal
    /// <see cref="FtsSearchService.SearchAsync"/> uses, so the two tiers agree about which
    /// matches "the first N" means. Rows within a day are read newest-first for the same
    /// reason; a caller that wants them in reading order sorts the collected results, exactly
    /// as the FTS path already does.
    /// </summary>
    public static async IAsyncEnumerable<SearchHit> ScanAsync(
        string rootDirectory,
        string query,
        int maxResults,
        IProgress<DeepScanProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query) || maxResults <= 0) yield break;

        var days = SessionLayout.ListDayDirectories(rootDirectory);
        if (days.Count == 0) yield break;

        // Settle the denominator across every day before reading a single payload, so the bar
        // fills monotonically instead of resetting each time the scan reaches a new day.
        var counters = new ScanCounters();
        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.TotalBytes += await TotalPayloadBytesAsync(
                SessionLayout.DayDatabasePath(rootDirectory, day), cancellationToken).ConfigureAwait(false);
        }

        var remaining = maxResults;
        for (var i = days.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var day = days[i];
            var dayDirectory = SessionLayout.DayDirectory(rootDirectory, day);

            await foreach (var hit in ScanOneDayAsync(
                dayDirectory, day, query, counters, progress, cancellationToken).ConfigureAwait(false))
            {
                yield return hit;
                if (--remaining == 0) yield break;
            }
        }
    }

    /// <summary>
    /// One day directory, unbounded. The denominator covers only that day, so a caller driving
    /// several days itself would see the bar rebase per day — use <see cref="ScanAsync"/> for
    /// that instead.
    /// </summary>
    public static async IAsyncEnumerable<SearchHit> ScanDayAsync(
        string dayDirectory,
        string query,
        IProgress<DeepScanProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query)) yield break;

        var day = Path.GetFileName(
            dayDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var counters = new ScanCounters
        {
            TotalBytes = await TotalPayloadBytesAsync(
                Path.Combine(dayDirectory, $"{day}.db"), cancellationToken).ConfigureAwait(false),
        };

        await foreach (var hit in ScanOneDayAsync(
            dayDirectory, day, query, counters, progress, cancellationToken).ConfigureAwait(false))
        {
            yield return hit;
        }
    }

    private static async IAsyncEnumerable<SearchHit> ScanOneDayAsync(
        string dayDirectory,
        string day,
        string query,
        ScanCounters counters,
        IProgress<DeepScanProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dbPath = Path.Combine(dayDirectory, $"{day}.db");
        if (!File.Exists(dbPath)) yield break;

        using var segmentReader = new SegmentReader(dayDirectory);
        await using var connection = new SqliteConnection(SessionLayout.ReadOnlyConnectionString(dbPath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {MessageRowQuery.Columns}
            FROM messages m
            {MessageRowQuery.SubjectJoin}
            ORDER BY m.id DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hit = MessageRowQuery.ReadHit(reader, day, SearchHit.IndexHwmNotApplicable);

            // SegmentReader keys off segment/offset/length only; the rest is carried so the hit
            // that comes back describes the row fully. An evicted payload reads back empty
            // rather than throwing, and simply cannot match.
            var header = new MessageHeader(
                sequence: hit.MessageRowId, enqueuedTicks: hit.EnqueuedTicks, rowId: hit.MessageRowId,
                segmentId: hit.SegmentId, offset: hit.Offset, length: hit.Length,
                subjectId: 0, correlationInternId: 0, partition: hit.Partition, flags: hit.Flags);

            var bytes = await segmentReader.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            var matched = !bytes.IsEmpty && ContainsSubstring(bytes.Span, query);

            counters.BytesScanned += hit.Length;
            if (matched) counters.Matches++;
            progress?.Report(new DeepScanProgress(counters.BytesScanned, counters.TotalBytes, counters.Matches));

            if (matched) yield return hit;
        }
    }

    /// <summary>
    /// The payload bytes one day file's rows account for. Best effort by design: a day file too
    /// damaged to sum is also too damaged to scan, so swallowing the error here only defers it
    /// to <see cref="ScanOneDayAsync"/>, where it belongs, instead of failing the whole scan
    /// before a single row has been read.
    /// </summary>
    private static async Task<long> TotalPayloadBytesAsync(string dbPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(dbPath)) return 0;

        try
        {
            await using var connection = new SqliteConnection(SessionLayout.ReadOnlyConnectionString(dbPath));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(length), 0) FROM messages";
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Carried across days so one <see cref="ScanAsync"/> call reports a single
    /// continuous total rather than restarting its counters at each day boundary.</summary>
    private sealed class ScanCounters
    {
        public long BytesScanned;
        public long TotalBytes;
        public int Matches;
    }

    /// <summary>Decodes as UTF8 and does an ordinal-ignore-case substring search. Correctness
    /// over cleverness here — <see cref="SegmentReader"/>'s decompressed-block cache (added
    /// in the M1-remainder pass) is what keeps repeated reads within one block cheap; this
    /// method doesn't need its own optimization on top of that.</summary>
    private static bool ContainsSubstring(ReadOnlySpan<byte> body, string query)
    {
        var maxChars = Encoding.UTF8.GetMaxCharCount(body.Length);
        char[]? rented = null;
        Span<char> buffer = maxChars <= 4096
            ? stackalloc char[maxChars]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(maxChars));

        try
        {
            var written = Encoding.UTF8.GetChars(body, buffer);
            return buffer[..written].Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }
}

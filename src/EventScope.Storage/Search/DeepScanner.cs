using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using EventScope.Core.Models;
using EventScope.Storage.Segments;

namespace EventScope.Storage.Search;

/// <summary>One deep-scan match — enough to locate the row and re-select it, not a full copy
/// of its content.</summary>
public readonly record struct DeepScanMatch(long MessageRowId, int SegmentId, int Offset, int Length);

/// <summary>
/// The last-resort search tier (build plan §5 M2): streams every message's <b>full</b> body
/// (not the 2 KB <c>body_head</c> capped copy <c>body_fts</c> indexes) looking for
/// <paramref name="query"/> as a substring, and works even if the FTS index is behind or the
/// term only appears past the 2 KB cap. Reports progress via <see cref="IProgress{T}"/> so a
/// UI can drive a progress bar over a large day file, and honors cancellation between every
/// row.
///
/// <para>
/// Pages results by yielding them as an <see cref="IAsyncEnumerable{T}"/> rather than
/// collecting them all before returning, so a caller streaming into a bounded UI list never
/// holds a large in-memory result set or (per build plan §3.4's WAL-starvation note) a read
/// transaction open across the whole scan.
/// </para>
/// </summary>
public static class DeepScanner
{
    public static async IAsyncEnumerable<DeepScanMatch> ScanDayAsync(
        string dayDirectory,
        string query,
        IProgress<long>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query)) yield break;

        var dayName = Path.GetFileName(dayDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dbPath = Path.Combine(dayDirectory, $"{dayName}.db");
        if (!File.Exists(dbPath)) yield break;

        using var segmentReader = new SegmentReader(dayDirectory);
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, segment_id, offset, length FROM messages ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var scanned = 0L;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = reader.GetInt64(0);
            var segmentId = reader.GetInt32(1);
            var offset = reader.GetInt32(2);
            var length = reader.GetInt32(3);

            var header = new MessageHeader(
                sequence: id, enqueuedTicks: 0, rowId: id, segmentId: segmentId, offset: offset,
                length: length, subjectId: 0, correlationInternId: 0, partition: 0, flags: MessageFlags.None);

            var bytes = await segmentReader.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            scanned++;
            progress?.Report(scanned);

            if (!bytes.IsEmpty && ContainsSubstring(bytes.Span, query))
            {
                yield return new DeepScanMatch(id, segmentId, offset, length);
            }
        }
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

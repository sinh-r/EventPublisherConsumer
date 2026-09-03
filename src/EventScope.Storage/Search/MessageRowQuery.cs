using System.Data.Common;
using EventScope.Core.Models;

namespace EventScope.Storage.Search;

/// <summary>
/// The one column list and reader mapping every cold read of the <c>messages</c> table goes
/// through, so full-text search (<see cref="FtsSearchService"/>) and plain history paging
/// (<see cref="HistoryQueryService"/>) cannot drift into producing differently-shaped rows for
/// the same message. Both project into <see cref="SearchHit"/>.
/// </summary>
internal static class MessageRowQuery
{
    /// <summary>Ordinals here are what <see cref="ReadHit"/> depends on — change the two
    /// together or not at all.</summary>
    public const string Columns = """
        m.id, m.enqueued_ticks, m.segment_id, m.offset, m.length,
        m.message_id, m.correlation_id, COALESCE(s.name, ''), m.preview,
        m.partition, m.flags
        """;

    public const string SubjectJoin = "LEFT JOIN subjects s ON s.id = m.subject_id";

    public static SearchHit ReadHit(DbDataReader reader, string day, long indexHwm) => new(
        Day: day,
        MessageRowId: reader.GetInt64(0),
        EnqueuedTicks: reader.GetInt64(1),
        SegmentId: reader.GetInt32(2),
        Offset: reader.GetInt32(3),
        Length: reader.GetInt32(4),
        MessageId: reader.IsDBNull(5) ? null : reader.GetString(5),
        CorrelationId: reader.IsDBNull(6) ? null : reader.GetString(6),
        Subject: reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
        Preview: reader.IsDBNull(8) ? null : reader.GetString(8),
        Partition: reader.IsDBNull(9) ? (short)0 : (short)reader.GetInt32(9),
        Flags: reader.IsDBNull(10) ? MessageFlags.None : (MessageFlags)reader.GetInt32(10),
        IndexHwm: indexHwm);
}

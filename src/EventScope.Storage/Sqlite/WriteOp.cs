namespace EventScope.Storage.Sqlite;

/// <summary>
/// Everything that can land on <see cref="SqliteBatchWriter"/>'s queue. An extensible base
/// rather than a closed set: M2 adds <c>SetFlags</c> (retention's eviction bit) and
/// <c>Rollover</c> (day-file boundary) as further cases posted through the same queue, so a
/// second write connection never has to exist — see the build plan's §3.6 collision table.
/// Only <see cref="InsertMessage"/> is needed through the end of M1b.
/// </summary>
public abstract record WriteOp
{
    /// <summary>One message's row, plus the subject string to intern — interning happens on
    /// the batch writer's own thread, alongside the insert, in the same transaction, so it
    /// never needs a second connection to the day file (see PROGRESS.md's note on this
    /// deviation from the build plan's literal "interning happens on the ingest reader
    /// thread" phrasing).</summary>
    public sealed record InsertMessage(
        long EnqueuedTicks,
        long ReceivedTicks,
        int SegmentId,
        int Offset,
        int Length,
        string? MessageId,
        string? CorrelationId,
        string Subject,
        int? Partition,
        byte Flags,
        string? Preview,
        string? BodyHead) : WriteOp;
}

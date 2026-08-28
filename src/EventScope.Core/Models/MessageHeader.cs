using System.Runtime.InteropServices;

namespace EventScope.Core.Models;

[Flags]
public enum MessageFlags : byte
{
    None = 0,
    IsLarge = 1,
    IsDeadLettered = 2,
    PayloadEvicted = 4,
}

/// <summary>
/// One row's worth of grid-relevant metadata. Struct, not class: the ring
/// buffer backing the grid holds tens of thousands of these with zero
/// per-message heap allocation.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct MessageHeader
{
    public readonly long Sequence;
    public readonly long EnqueuedTicks;
    public readonly long RowId;
    public readonly int SegmentId;
    public readonly int Offset;
    public readonly int Length;
    public readonly int SubjectId;
    public readonly int CorrelationInternId;
    public readonly short Partition;
    public readonly MessageFlags Flags;

    public MessageHeader(
        long sequence,
        long enqueuedTicks,
        long rowId,
        int segmentId,
        int offset,
        int length,
        int subjectId,
        int correlationInternId,
        short partition,
        MessageFlags flags)
    {
        Sequence = sequence;
        EnqueuedTicks = enqueuedTicks;
        RowId = rowId;
        SegmentId = segmentId;
        Offset = offset;
        Length = length;
        SubjectId = subjectId;
        CorrelationInternId = correlationInternId;
        Partition = partition;
        Flags = flags;
    }
}

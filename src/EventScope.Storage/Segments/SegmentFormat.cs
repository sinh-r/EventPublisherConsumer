namespace EventScope.Storage.Segments;

/// <summary>
/// The on-disk segment layout, shared by <see cref="SegmentWriter"/> and
/// <see cref="SegmentReader"/> so the two never drift on a magic number or a field width.
///
/// <para>
/// A segment file is a sequence of blocks followed by an optional footer:
/// <c>[block]* [block table]? [count:int32][FooterMagic:uint32]</c>. Each block is
/// <c>[BlockMagic:uint32][uncompressedLength:int32][compressedLength:int32][compressed bytes]</c>.
/// <c>offset</c> everywhere in <c>MessageHeader</c> is the uncompressed logical offset within
/// the segment — the reader binary-searches the block table by that offset, not by file
/// position, so decompression is transparent to callers.
/// </para>
///
/// <para>
/// A payload larger than <see cref="BlockSize"/> gets its own single-payload block rather
/// than being split — spanning would force multi-block reassembly on the read path for the
/// rare case, and payloads that large are already flagged <c>IsLarge</c> and never
/// previewed. A footer missing or failing its magic check (the writer died mid-file) is
/// recovered by walking block headers from offset 0 — slower, but correct, and the reason
/// every block header carries its own lengths instead of relying on the table.
/// </para>
/// </summary>
internal static class SegmentFormat
{
    public const int BlockHeaderSize = 12;
    public const int BlockTableEntrySize = 24;
    public const int FooterTailSize = 8;

    public const uint BlockMagic = 0x34534C45;
    public const uint FooterMagic = 0x34464C45;

    /// <summary>Target size of one uncompressed block before it's flushed.</summary>
    public const int BlockSize = 1024 * 1024;

    /// <summary>A segment rolls to a new file once its on-disk size reaches this.</summary>
    public const long SegmentRollSize = 64L * 1024 * 1024;

    public static string SegmentPath(string directory, int segmentId) =>
        Path.Combine(directory, $"{segmentId:D6}.seg");

    /// <summary>
    /// True once either the on-disk size or the uncompressed-offset headroom runs out.
    /// Highly repetitive JSON compresses well past 32:1 under LZ4, so a 64&#160;MB file can
    /// hold more than <see cref="int.MaxValue"/> uncompressed bytes — the offset returned to
    /// callers is an <c>int</c>, so the roll has to happen before that wraps, not just when
    /// the file gets big. Extracted as a pure function so the boundary is unit-testable
    /// without writing gigabytes of data. See PROGRESS.md &#167;0.2.
    /// </summary>
    public static bool ShouldRoll(long filePosition, long uncompressedCursor) =>
        filePosition >= SegmentRollSize || uncompressedCursor >= int.MaxValue - BlockSize;
}

/// <summary>One block's entry in a sealed segment's footer table.</summary>
internal readonly record struct BlockTableEntry(
    long UncompressedStart,
    long CompressedStart,
    int UncompressedLength,
    int CompressedLength);

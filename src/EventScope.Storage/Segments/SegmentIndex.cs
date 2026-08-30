using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace EventScope.Storage.Segments;

/// <summary>
/// Loads a segment's block table for <see cref="SegmentReader"/>: the fast path reads the
/// footer written by <see cref="SegmentWriter.Seal"/>; the recovery path walks block headers
/// from offset 0 when the footer is missing or fails its magic check (writer died mid-file,
/// or the segment is still the live, unsealed one being appended to).
/// </summary>
internal static class SegmentIndex
{
    /// <summary>Attempts the footer read first, falling back to the recovery walk.
    /// <paramref name="isSealed"/> tells the caller whether it's safe to cache the result
    /// forever (a sealed segment never changes) or must re-load on a lookup miss (the live
    /// segment keeps growing).</summary>
    public static (List<BlockTableEntry> Entries, bool IsSealed) Load(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);

        if (TryReadFooter(handle, length, out var entries))
        {
            return (entries, true);
        }

        return (RecoveryWalk(handle, length), false);
    }

    private static bool TryReadFooter(SafeFileHandle handle, long length, out List<BlockTableEntry> entries)
    {
        entries = [];
        if (length < SegmentFormat.FooterTailSize) return false;

        Span<byte> tail = stackalloc byte[SegmentFormat.FooterTailSize];
        RandomAccess.Read(handle, tail, length - SegmentFormat.FooterTailSize);

        var count = BinaryPrimitives.ReadInt32LittleEndian(tail);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        if (magic != SegmentFormat.FooterMagic || count < 0) return false;

        var tableSize = (long)count * SegmentFormat.BlockTableEntrySize;
        var tableStart = length - SegmentFormat.FooterTailSize - tableSize;
        if (tableStart < 0) return false;

        var table = new byte[tableSize];
        RandomAccess.Read(handle, table, tableStart);

        entries = new List<BlockTableEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var span = table.AsSpan(i * SegmentFormat.BlockTableEntrySize, SegmentFormat.BlockTableEntrySize);
            entries.Add(new BlockTableEntry(
                UncompressedStart: BinaryPrimitives.ReadInt64LittleEndian(span),
                CompressedStart: BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
                UncompressedLength: BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
                CompressedLength: BinaryPrimitives.ReadInt32LittleEndian(span[20..])));
        }

        return true;
    }

    /// <summary>Steps block headers from offset 0 using each header's own lengths — the
    /// reason every block carries its lengths instead of deferring entirely to the table.
    /// Stops at the first bad magic or a header/body that runs past the file's current
    /// length (a block still being written, or genuine truncation).</summary>
    private static List<BlockTableEntry> RecoveryWalk(SafeFileHandle handle, long length)
    {
        var entries = new List<BlockTableEntry>();
        var filePos = 0L;
        var uncompressedCursor = 0L;
        Span<byte> header = stackalloc byte[SegmentFormat.BlockHeaderSize];

        while (filePos + SegmentFormat.BlockHeaderSize <= length)
        {
            RandomAccess.Read(handle, header, filePos);

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic != SegmentFormat.BlockMagic) break;

            var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            if (uncompressedLength < 0 || compressedLength < 0) break;

            var compressedStart = filePos + SegmentFormat.BlockHeaderSize;
            if (compressedStart + compressedLength > length) break; // block body not fully written yet

            entries.Add(new BlockTableEntry(uncompressedCursor, compressedStart, uncompressedLength, compressedLength));
            uncompressedCursor += uncompressedLength;
            filePos = compressedStart + compressedLength;
        }

        return entries;
    }

    /// <summary>Binary search over blocks sorted by <c>UncompressedStart</c> (true by
    /// construction — blocks are appended in order) for the one spanning <paramref name="offset"/>.</summary>
    public static BlockTableEntry? FindBlock(List<BlockTableEntry> entries, int offset)
    {
        var lo = 0;
        var hi = entries.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var entry = entries[mid];
            if (offset < entry.UncompressedStart) hi = mid - 1;
            else if (offset >= entry.UncompressedStart + entry.UncompressedLength) lo = mid + 1;
            else return entry;
        }

        return null;
    }
}

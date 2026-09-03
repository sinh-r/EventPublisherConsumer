using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace EventScope.Storage.Segments;

/// <summary>
/// Append-only segment writer: 64&#160;MB rolling files, LZ4 block-compressed at ~1&#160;MB
/// uncompressed per block. Runs inline on the ingest reader thread (§3.6 of the build
/// plan) so <see cref="Append"/> returns <c>(segmentId, offset, length)</c> synchronously —
/// exactly the tuple the SQLite row needs, with no async handoff in between.
///
/// Not thread-safe by design: it has exactly one caller, the single ingest reader.
/// </summary>
public sealed class SegmentWriter : IDisposable
{
    private readonly string _directory;
    private readonly byte[] _pending = new byte[SegmentFormat.BlockSize];
    private readonly List<BlockTableEntry> _blocks = [];

    private SafeFileHandle? _handle;
    private long _filePosition;
    private long _uncompressedCursor;
    private int _pendingLength;
    private int _segmentId;
    private bool _disposed;

    /// <param name="startingSegmentId">The segment id to begin at. <see langword="null"/> — the
    /// default — resumes past whatever is already in <paramref name="directory"/>; see
    /// <see cref="NextUnusedSegmentId"/> for why that is not the same as starting at 0.</param>
    public SegmentWriter(string directory, int? startingSegmentId = null)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _segmentId = startingSegmentId ?? NextUnusedSegmentId(directory);
        OpenNewSegment();
    }

    public int CurrentSegmentId => _segmentId;

    /// <summary>
    /// One past the highest segment id present in <paramref name="directory"/>, or 0 for a
    /// directory with none.
    ///
    /// <para>
    /// <b>Why the writer cannot simply start at 0.</b> <see cref="OpenNewSegment"/> opens with
    /// <c>FileMode.Create</c>, which truncates. A day directory is reopened whenever the app is
    /// restarted without the UTC day changing, and starting at 0 there destroyed that day's
    /// earlier capture: the segment bytes were gone while the day file's rows still pointed at
    /// them, so an entire session's messages became unreadable — and, once the new run wrote at
    /// the same coordinates, those rows read back <i>another message's</i> bytes rather than
    /// failing.
    /// </para>
    ///
    /// <para>
    /// <b>Why one past the highest, and not the first free id.</b> Retention deletes individual
    /// segment files while their rows stay in the day file flagged
    /// <c>PayloadEvicted</c> (<c>RetentionService.EvictOldestSegment</c>). Filling the gap a
    /// deleted segment left would hand its id to unrelated new bytes and make those evicted rows
    /// resolve against them. Ids are therefore only ever handed out going up, and a gap stays a
    /// gap — the reader looks segments up by id and neither needs nor assumes they are contiguous.
    /// </para>
    ///
    /// <para>
    /// The cost is one partially-filled segment per restart, which is bounded by how often a user
    /// restarts and is the cheap side of this trade.
    /// </para>
    /// </summary>
    internal static int NextUnusedSegmentId(string directory)
    {
        var highest = -1;

        foreach (var path in Directory.EnumerateFiles(directory, "*.seg"))
        {
            // Anything that is not a plain segment number is not ours to reason about, so it does
            // not get a say in where writing resumes.
            if (int.TryParse(
                    Path.GetFileNameWithoutExtension(path.AsSpan()),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var id)
                && id > highest)
            {
                highest = id;
            }
        }

        return highest + 1;
    }

    /// <summary>Appends one payload. Returns its coordinates for the SQLite row: the
    /// segment it landed in (may differ from <see cref="CurrentSegmentId"/> after this call
    /// if appending it triggered a roll), its uncompressed logical offset within that
    /// segment, and its length.</summary>
    public (int SegmentId, int Offset, int Length) Append(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (payload.Length > SegmentFormat.BlockSize)
        {
            FlushPending();
            var segmentId = _segmentId;
            var offset = checked((int)_uncompressedCursor);
            WriteBlock(payload);
            RollIfNeeded();
            return (segmentId, offset, payload.Length);
        }

        if (_pendingLength + payload.Length > SegmentFormat.BlockSize)
        {
            FlushPending();
            RollIfNeeded();
        }

        var startSegmentId = _segmentId;
        var startOffset = checked((int)(_uncompressedCursor + _pendingLength));
        payload.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += payload.Length;
        return (startSegmentId, startOffset, payload.Length);
    }

    private void OpenNewSegment()
    {
        var path = SegmentFormat.SegmentPath(_directory, _segmentId);
        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _filePosition = 0;
        _uncompressedCursor = 0;
        _pendingLength = 0;
        _blocks.Clear();
    }

    private void FlushPending()
    {
        if (_pendingLength == 0) return;
        WriteBlock(_pending.AsSpan(0, _pendingLength));
        _pendingLength = 0;
    }

    private void WriteBlock(ReadOnlySpan<byte> uncompressed)
    {
        byte[]? rented = null;
        int compressedLength;
        ReadOnlySpan<byte> toWrite;

        if (uncompressed.Length == 0)
        {
            compressedLength = 0;
            toWrite = [];
        }
        else
        {
            var maxCompressed = LZ4Codec.MaximumOutputSize(uncompressed.Length);
            rented = ArrayPool<byte>.Shared.Rent(maxCompressed);
            compressedLength = LZ4Codec.Encode(uncompressed, rented.AsSpan(0, maxCompressed), LZ4Level.L00_FAST);
            toWrite = rented.AsSpan(0, compressedLength);
        }

        try
        {
            Span<byte> header = stackalloc byte[SegmentFormat.BlockHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, SegmentFormat.BlockMagic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], uncompressed.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], compressedLength);

            var headerStart = _filePosition;
            var compressedStart = headerStart + SegmentFormat.BlockHeaderSize;
            RandomAccess.Write(_handle!, header, headerStart);
            if (toWrite.Length > 0)
            {
                RandomAccess.Write(_handle!, toWrite, compressedStart);
            }

            _blocks.Add(new BlockTableEntry(_uncompressedCursor, compressedStart, uncompressed.Length, compressedLength));
            _uncompressedCursor += uncompressed.Length;
            _filePosition = compressedStart + toWrite.Length;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void RollIfNeeded()
    {
        if (!SegmentFormat.ShouldRoll(_filePosition, _uncompressedCursor)) return;

        Seal();
        _segmentId++;
        OpenNewSegment();
    }

    /// <summary>Writes the block-table footer and closes this segment's handle. Idempotent
    /// no-op if there is nothing open. A segment that is never sealed (process died) is
    /// still fully readable via <see cref="SegmentReader"/>'s footer-missing recovery path —
    /// this only makes the common case (clean shutdown, clean roll) fast to open.</summary>
    private void Seal()
    {
        if (_handle is null) return;

        FlushPending();

        if (_blocks.Count > 0)
        {
            var tableSize = _blocks.Count * SegmentFormat.BlockTableEntrySize;
            var table = new byte[tableSize];
            for (var i = 0; i < _blocks.Count; i++)
            {
                var entry = _blocks[i];
                var span = table.AsSpan(i * SegmentFormat.BlockTableEntrySize, SegmentFormat.BlockTableEntrySize);
                BinaryPrimitives.WriteInt64LittleEndian(span, entry.UncompressedStart);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], entry.CompressedStart);
                BinaryPrimitives.WriteInt32LittleEndian(span[16..], entry.UncompressedLength);
                BinaryPrimitives.WriteInt32LittleEndian(span[20..], entry.CompressedLength);
            }

            RandomAccess.Write(_handle, table, _filePosition);
            _filePosition += tableSize;
        }

        Span<byte> tail = stackalloc byte[SegmentFormat.FooterTailSize];
        BinaryPrimitives.WriteInt32LittleEndian(tail, _blocks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(tail[4..], SegmentFormat.FooterMagic);
        RandomAccess.Write(_handle, tail, _filePosition);
        _filePosition += SegmentFormat.FooterTailSize;

        _handle.Dispose();
        _handle = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Seal();
    }
}

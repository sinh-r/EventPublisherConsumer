using System.Buffers;
using System.Buffers.Binary;
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

    public SegmentWriter(string directory, int startingSegmentId = 0)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _segmentId = startingSegmentId;
        OpenNewSegment();
    }

    public int CurrentSegmentId => _segmentId;

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

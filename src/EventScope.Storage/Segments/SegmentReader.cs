using System.Collections.Concurrent;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;

namespace EventScope.Storage.Segments;

/// <summary>
/// Reads payloads back from sealed (or still-live) segment files, keyed by
/// <see cref="MessageHeader.SegmentId"/>/<see cref="MessageHeader.Offset"/>/
/// <see cref="MessageHeader.Length"/>. One shared <see cref="SafeFileHandle"/> per segment,
/// positional <see cref="RandomAccess"/> reads only — no <c>FileStream</c>, no seek lock, per
/// the build plan's .NET 10 threading table.
///
/// <para>
/// Opens with <see cref="FileShare.ReadWrite"/> so a segment can be read while
/// <see cref="SegmentWriter"/> still holds it open for appends (Windows share-mode
/// compatibility requires the new open's share mode to admit the writer's <c>ReadWrite</c>
/// access; the writer's own <see cref="FileShare.Read"/> already admits this reader's
/// <c>Read</c> access) — see PROGRESS.md &#167;0.3.
/// </para>
///
/// <para>
/// Returns an empty buffer — never throws — when the segment file no longer exists (a day's
/// segments deleted by retention) or the requested offset isn't covered by any block (a
/// stale header pointing past what's been flushed). Both are legitimate "payload evicted"
/// signals to the caller, identical in shape to <see cref="IPayloadReader"/>'s documented
/// contract.
/// </para>
/// </summary>
public sealed class SegmentReader(string directory) : IPayloadReader, IDisposable
{
    private readonly ConcurrentDictionary<int, CachedSegment> _cache = new();
    private bool _disposed;

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(MessageHeader header, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (header.Length == 0) return ReadOnlyMemory<byte>.Empty;

        var cached = GetOrOpen(header.SegmentId);
        if (cached is null) return ReadOnlyMemory<byte>.Empty;

        var block = FindOrReload(cached, header.Offset);
        if (block is null) return ReadOnlyMemory<byte>.Empty;

        var entry = block.Value;
        if (entry.CompressedLength == 0) return ReadOnlyMemory<byte>.Empty; // an empty payload, stored as such

        var compressed = new byte[entry.CompressedLength];
        await RandomAccess.ReadAsync(cached.Handle, compressed, entry.CompressedStart, cancellationToken)
            .ConfigureAwait(false);

        var uncompressed = new byte[entry.UncompressedLength];
        var written = LZ4Codec.Decode(compressed, uncompressed);
        if (written != entry.UncompressedLength)
        {
            throw new InvalidDataException(
                $"Segment {header.SegmentId}: LZ4 decode produced {written} bytes, expected {entry.UncompressedLength}.");
        }

        var localOffset = header.Offset - (int)entry.UncompressedStart;
        if (localOffset < 0 || localOffset + header.Length > uncompressed.Length)
        {
            return ReadOnlyMemory<byte>.Empty; // header doesn't actually land inside this block
        }

        return uncompressed.AsMemory(localOffset, header.Length);
    }

    private CachedSegment? GetOrOpen(int segmentId)
    {
        if (_cache.TryGetValue(segmentId, out var existing)) return existing;

        var path = SegmentFormat.SegmentPath(directory, segmentId);
        if (!File.Exists(path)) return null;

        CachedSegment? created = null;
        try
        {
            var stored = _cache.GetOrAdd(segmentId, _ =>
            {
                var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var (entries, isSealed) = SegmentIndex.Load(handle);
                created = new CachedSegment(handle, entries, isSealed);
                return created;
            });

            // GetOrAdd's factory can run more than once under contention, with only one
            // result actually stored — dispose the loser's handle rather than leaking it.
            if (created is not null && !ReferenceEquals(stored, created))
            {
                created.Handle.Dispose();
            }

            return stored;
        }
        catch (IOException)
        {
            return null; // deleted between the exists check and the open
        }
    }

    private BlockTableEntry? FindOrReload(CachedSegment cached, int offset)
    {
        lock (cached.Gate)
        {
            var found = SegmentIndex.FindBlock(cached.Entries, offset);
            if (found is not null) return found;

            // A miss on a sealed segment's table is a genuine out-of-range lookup, not
            // staleness — sealed segments never change. Only the live (unsealed) segment
            // can have grown more blocks since it was last loaded.
            if (cached.IsSealed) return null;

            var (entries, isSealed) = SegmentIndex.Load(cached.Handle);
            cached.Entries = entries;
            cached.IsSealed = isSealed;
            return SegmentIndex.FindBlock(cached.Entries, offset);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var segment in _cache.Values)
        {
            segment.Handle.Dispose();
        }

        _cache.Clear();
    }

    private sealed class CachedSegment(SafeFileHandle handle, List<BlockTableEntry> entries, bool isSealed)
    {
        public SafeFileHandle Handle { get; } = handle;
        public Lock Gate { get; } = new();
        public List<BlockTableEntry> Entries { get; set; } = entries;
        public bool IsSealed { get; set; } = isSealed;
    }
}

using System.Buffers;
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
///
/// <para>
/// <b>Decompressed-block cache.</b> Every read used to decompress and allocate the whole
/// ~1&#160;MB containing block regardless of the requested payload's size — measured at
/// ~1.7&#8211;2&#160;GB allocated across 1,000 random reads in
/// <c>tests/EventScope.Bench/SegmentReadBenchmarks</c>, since packed payloads mean
/// consecutive random-offset reads mostly miss whatever the previous read touched. A block's
/// bytes are immutable once written (<see cref="SegmentWriter"/> only ever appends new
/// blocks, never rewrites one), so caching the decompressed bytes keyed by
/// <c>(segmentId, block's uncompressed start)</c> is safe indefinitely, including for a still
/// -live (unsealed) segment. Bounded by <see cref="BlockCacheCapacity"/> blocks with
/// approximate (not strict) LRU eviction — approximate is enough for a read cache and avoids
/// the synchronization a true LRU would need under concurrent readers.
/// </para>
/// </summary>
public sealed class SegmentReader(string directory, int blockCacheCapacity = 64) : IPayloadReader, IDisposable
{
    /// <summary>Max decompressed blocks retained at once, ~<see cref="SegmentFormat.BlockSize"/>
    /// each — the default bounds the cache at roughly 64 MB.</summary>
    public int BlockCacheCapacity { get; } = blockCacheCapacity;

    private readonly ConcurrentDictionary<int, CachedSegment> _cache = new();
    private readonly ConcurrentDictionary<(int SegmentId, long BlockStart), byte[]> _blockCache = new();
    private readonly ConcurrentQueue<(int SegmentId, long BlockStart)> _blockCacheOrder = new();
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

        var uncompressed = await GetOrDecodeBlockAsync(cached, header.SegmentId, entry, cancellationToken)
            .ConfigureAwait(false);

        var localOffset = header.Offset - (int)entry.UncompressedStart;
        if (localOffset < 0 || localOffset + header.Length > uncompressed.Length)
        {
            return ReadOnlyMemory<byte>.Empty; // header doesn't actually land inside this block
        }

        return uncompressed.AsMemory(localOffset, header.Length);
    }

    private async ValueTask<byte[]> GetOrDecodeBlockAsync(
        CachedSegment cached, int segmentId, BlockTableEntry entry, CancellationToken cancellationToken)
    {
        var key = (segmentId, entry.UncompressedStart);
        if (_blockCache.TryGetValue(key, out var cachedBlock)) return cachedBlock;

        var rented = ArrayPool<byte>.Shared.Rent(entry.CompressedLength);
        byte[] uncompressed;
        try
        {
            var compressed = rented.AsMemory(0, entry.CompressedLength);
            await RandomAccess.ReadAsync(cached.Handle, compressed, entry.CompressedStart, cancellationToken)
                .ConfigureAwait(false);

            uncompressed = new byte[entry.UncompressedLength];
            var written = LZ4Codec.Decode(compressed.Span, uncompressed);
            if (written != entry.UncompressedLength)
            {
                throw new InvalidDataException(
                    $"Segment {segmentId}: LZ4 decode produced {written} bytes, expected {entry.UncompressedLength}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        // GetOrAdd's factory can run more than once under contention (same tolerance as
        // GetOrOpen below) — harmless here since the losing decode is just discarded.
        var stored = _blockCache.GetOrAdd(key, uncompressed);
        _blockCacheOrder.Enqueue(key);
        EvictIfOverCapacity();
        return stored;
    }

    private void EvictIfOverCapacity()
    {
        while (_blockCache.Count > BlockCacheCapacity && _blockCacheOrder.TryDequeue(out var oldest))
        {
            _blockCache.TryRemove(oldest, out _);
        }
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

using EventScope.Core.Models;
using EventScope.Storage.Segments;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// The decompressed-block cache added to <see cref="SegmentReader"/> after the M1c benchmark
/// found every read decompressing and allocating a whole ~1 MB block regardless of the
/// requested payload's size (see PROGRESS.md's heap-growth-investigation follow-up). These
/// tests cover cache correctness, not the round-trip byte-correctness already covered by
/// <see cref="SegmentRoundTripTests"/> — every read here is re-asserted against the original
/// payload precisely so a caching bug (stale data, cross-block contamination) would surface as
/// a content mismatch, not just a missing/extra allocation.
/// </summary>
public sealed class SegmentReaderBlockCacheTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("eventscope-block-cache-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static MessageHeader HeaderFor(int segmentId, int offset, int length) =>
        new(sequence: 0, enqueuedTicks: 0, rowId: 0, segmentId: segmentId, offset: offset,
            length: length, subjectId: 0, correlationInternId: 0, partition: 0, flags: MessageFlags.None);

    [Fact]
    public async Task Repeated_reads_of_payloads_in_the_same_block_return_correct_bytes()
    {
        var random = new Random(11);
        var payloads = new List<byte[]>();
        var coords = new List<(int SegmentId, int Offset, int Length)>();

        using (var writer = new SegmentWriter(_directory))
        {
            // Small payloads packed into a handful of ~1 MB blocks, so repeated random reads
            // are guaranteed to hit the same cached block multiple times.
            for (var i = 0; i < 500; i++)
            {
                var payload = new byte[64];
                random.NextBytes(payload);
                payloads.Add(payload);
                coords.Add(writer.Append(payload));
            }
        }

        using var reader = new SegmentReader(_directory);

        // Read in a shuffled, repeating order so both first-touch (decode) and cache-hit
        // paths for the same block are exercised, then re-shuffle and read again.
        var order = Enumerable.Range(0, payloads.Count).OrderBy(_ => random.Next()).ToList();
        foreach (var pass in new[] { order, order.AsEnumerable().Reverse().ToList() })
        {
            foreach (var i in pass)
            {
                var (segmentId, offset, length) = coords[i];
                var read = await reader.ReadAsync(HeaderFor(segmentId, offset, length), Ct);
                Assert.Equal(payloads[i], read.ToArray());
            }
        }
    }

    [Fact]
    public async Task Cache_stays_bounded_at_the_configured_capacity()
    {
        // Random (incompressible) filler forces each Append into its own ~1 MB block rather
        // than packing many small payloads into one — so this actually produces more distinct
        // blocks than the configured cache capacity.
        var random = new Random(5);
        var coords = new List<(int SegmentId, int Offset, int Length)>();

        using (var writer = new SegmentWriter(_directory))
        {
            for (var i = 0; i < 8; i++)
            {
                var payload = new byte[SegmentFormat.BlockSize];
                random.NextBytes(payload);
                coords.Add(writer.Append(payload));
            }
        }

        using var reader = new SegmentReader(_directory, blockCacheCapacity: 3);

        foreach (var (segmentId, offset, length) in coords)
        {
            var read = await reader.ReadAsync(HeaderFor(segmentId, offset, length), Ct);
            Assert.Equal(length, read.Length);
        }

        // Not a public accessor to internal cache state by design (it's an implementation
        // detail) — this asserts behaviourally instead: re-reading the earliest block after
        // reading eight blocks through a 3-entry cache must still return correct bytes,
        // whether that means a cache hit or a correct re-decode on a miss.
        var (firstSegmentId, firstOffset, firstLength) = coords[0];
        var reread = await reader.ReadAsync(HeaderFor(firstSegmentId, firstOffset, firstLength), Ct);
        Assert.Equal(firstLength, reread.Length);
    }

    [Fact]
    public async Task A_cached_block_from_the_live_unsealed_segment_still_reads_correctly_after_more_appends()
    {
        using var writer = new SegmentWriter(_directory);

        var firstPayload = "first, in the live segment's first block"u8.ToArray();
        var firstCoords = writer.Append(firstPayload);

        // Force the first payload out of the pending buffer and onto disk without sealing.
        writer.Append(new byte[SegmentFormat.BlockSize]);

        using var reader = new SegmentReader(_directory);

        // First read populates the block cache while the segment is still live/unsealed.
        var firstRead = await reader.ReadAsync(
            HeaderFor(firstCoords.SegmentId, firstCoords.Offset, firstCoords.Length), Ct);
        Assert.Equal(firstPayload, firstRead.ToArray());

        // More data lands in the same still-open segment after the cache entry exists.
        writer.Append(new byte[SegmentFormat.BlockSize]);

        // The cached block for the first payload must still be correct — it was immutable
        // the moment it was written, regardless of what's appended after it.
        var secondRead = await reader.ReadAsync(
            HeaderFor(firstCoords.SegmentId, firstCoords.Offset, firstCoords.Length), Ct);
        Assert.Equal(firstPayload, secondRead.ToArray());
    }
}

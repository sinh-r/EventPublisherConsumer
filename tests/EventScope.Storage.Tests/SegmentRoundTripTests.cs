using EventScope.Core.Models;
using EventScope.Storage.Segments;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// Real files in a temp directory, never in-memory — file size, rolling, and recovery are
/// the things under test here, exactly the reason the build plan requires real storage
/// tests (§7).
/// </summary>
public sealed class SegmentRoundTripTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("eventscope-segment-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static MessageHeader HeaderFor(int segmentId, int offset, int length) =>
        new(sequence: 0, enqueuedTicks: 0, rowId: 0, segmentId: segmentId, offset: offset,
            length: length, subjectId: 0, correlationInternId: 0, partition: 0, flags: MessageFlags.None);

    [Fact]
    public async Task Ten_thousand_mixed_size_payloads_round_trip_byte_for_byte()
    {
        var random = new Random(42);
        var payloads = new List<byte[]>();
        var coords = new List<(int SegmentId, int Offset, int Length)>();

        using (var writer = new SegmentWriter(_directory))
        {
            for (var i = 0; i < 10_000; i++)
            {
                var size = random.Next(16, 4096);
                var payload = new byte[size];
                random.NextBytes(payload);
                payloads.Add(payload);
                coords.Add(writer.Append(payload));
            }
        }

        using var reader = new SegmentReader(_directory);
        for (var i = 0; i < payloads.Count; i++)
        {
            var (segmentId, offset, length) = coords[i];
            var read = await reader.ReadAsync(HeaderFor(segmentId, offset, length), Ct);
            Assert.Equal(payloads[i], read.ToArray());
        }
    }

    [Fact]
    public async Task A_payload_larger_than_the_block_size_gets_its_own_block_and_reads_back()
    {
        var big = new byte[SegmentFormat.BlockSize + 1024];
        new Random(7).NextBytes(big);

        (int SegmentId, int Offset, int Length) coords;
        using (var writer = new SegmentWriter(_directory))
        {
            coords = writer.Append(big);
        }

        using var reader = new SegmentReader(_directory);
        var read = await reader.ReadAsync(HeaderFor(coords.SegmentId, coords.Offset, coords.Length), Ct);
        Assert.Equal(big, read.ToArray());
    }

    [Fact]
    public async Task A_payload_reads_back_while_the_writer_still_holds_the_segment_open()
    {
        using var writer = new SegmentWriter(_directory);
        var payload = "hello, still-open segment"u8.ToArray();
        var coords = writer.Append(payload);

        // A single small Append stays purely in the in-memory pending buffer — nothing is on
        // disk yet (see PROGRESS.md §0.1). Force it out to disk without sealing the segment,
        // so the read below actually exercises the live-segment / recovery-walk path rather
        // than a payload that only ever existed in memory.
        writer.Append(new byte[SegmentFormat.BlockSize]);

        // The reader must open with FileShare.ReadWrite so this doesn't throw IOException —
        // see PROGRESS.md §0.3.
        using var reader = new SegmentReader(_directory);
        var read = await reader.ReadAsync(HeaderFor(coords.SegmentId, coords.Offset, coords.Length), Ct);
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public async Task Truncating_the_footer_still_recovers_every_payload_before_the_cut()
    {
        var payloads = new List<byte[]>();
        var coords = new List<(int SegmentId, int Offset, int Length)>();
        string path;

        using (var writer = new SegmentWriter(_directory))
        {
            for (var i = 0; i < 50; i++)
            {
                var payload = new byte[200 + i];
                new Random(i).NextBytes(payload);
                payloads.Add(payload);
                coords.Add(writer.Append(payload));
            }

            path = SegmentFormat.SegmentPath(_directory, writer.CurrentSegmentId);
        } // Dispose() seals — writes the footer.

        // Simulate the writer dying mid-file: chop off the footer's tail bytes.
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        await File.WriteAllBytesAsync(path, bytes[..^SegmentFormat.FooterTailSize], Ct);

        using var reader = new SegmentReader(_directory);
        for (var i = 0; i < payloads.Count; i++)
        {
            var (segmentId, offset, length) = coords[i];
            var read = await reader.ReadAsync(HeaderFor(segmentId, offset, length), Ct);
            Assert.Equal(payloads[i], read.ToArray());
        }
    }

    [Fact]
    public async Task Forced_roll_produces_two_segments_with_independent_per_segment_offsets()
    {
        var firstSegmentPayload = new byte[SegmentFormat.BlockSize];
        new Random(1).NextBytes(firstSegmentPayload);
        var secondSegmentPayload = "lands in segment 1 at offset 0"u8.ToArray();

        (int SegmentId, int Offset, int Length) firstCoords;
        (int SegmentId, int Offset, int Length) secondCoords;

        using (var writer = new SegmentWriter(_directory))
        {
            firstCoords = writer.Append(firstSegmentPayload); // fills segment 0's pending buffer
            var filler = new byte[SegmentFormat.BlockSize];
            var fillerRandom = new Random(2);

            // Filler must be incompressible (random, not zero-filled) so the file position
            // actually grows toward the 64 MB roll size instead of compressing to nothing.
            while (writer.CurrentSegmentId == firstCoords.SegmentId)
            {
                fillerRandom.NextBytes(filler);
                writer.Append(filler);
            }

            // The filler call that actually crossed the roll boundary already landed its own
            // bytes at offset 0 of the new segment (Append flushes-then-rolls, then still
            // writes the payload that triggered it into whatever segment is now current) —
            // so this next payload's offset depends on whether that landed in pending or
            // forced its own flush. Either way it must round-trip correctly.
            secondCoords = writer.Append(secondSegmentPayload);
        }

        Assert.NotEqual(firstCoords.SegmentId, secondCoords.SegmentId);

        using var reader = new SegmentReader(_directory);
        var readSecond = await reader.ReadAsync(
            HeaderFor(secondCoords.SegmentId, secondCoords.Offset, secondCoords.Length), Ct);
        Assert.Equal(secondSegmentPayload, readSecond.ToArray());
    }

    [Fact]
    public void ShouldRoll_true_only_once_file_size_or_uncompressed_headroom_runs_out()
    {
        Assert.False(SegmentFormat.ShouldRoll(filePosition: 0, uncompressedCursor: 0));
        Assert.False(SegmentFormat.ShouldRoll(
            filePosition: SegmentFormat.SegmentRollSize - 1,
            uncompressedCursor: 0));
        Assert.True(SegmentFormat.ShouldRoll(
            filePosition: SegmentFormat.SegmentRollSize,
            uncompressedCursor: 0));

        // The overflow guard: highly compressible data can push the uncompressed cursor past
        // int.MaxValue long before the compressed file itself reaches 64 MB.
        Assert.False(SegmentFormat.ShouldRoll(
            filePosition: 0,
            uncompressedCursor: (long)int.MaxValue - SegmentFormat.BlockSize - 1));
        Assert.True(SegmentFormat.ShouldRoll(
            filePosition: 0,
            uncompressedCursor: (long)int.MaxValue - SegmentFormat.BlockSize));
    }
}

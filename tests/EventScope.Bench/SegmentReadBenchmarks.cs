using BenchmarkDotNet.Attributes;
using EventScope.Core.Models;
using EventScope.Storage.Segments;

namespace EventScope.Bench;

/// <summary>
/// Segment read latency (build plan §7 / §6 acceptance table: "row selection renders body
/// &lt; 100&#160;ms", "deep scan &#8805; 500&#160;MB/s decompressed"). Baseline goes in
/// <c>baselines/</c> once this has actually been run — see PROGRESS.md for what's measured
/// vs. still pending.
/// </summary>
[MemoryDiagnoser]
public class SegmentReadBenchmarks
{
    private string _directory = null!;
    private SegmentReader _reader = null!;
    private MessageHeader[] _headers = null!;

    [Params(256, 4096)]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("eventscope-bench-segments-").FullName;
        var random = new Random(1);
        var coords = new (int SegmentId, int Offset, int Length)[10_000];

        using (var writer = new SegmentWriter(_directory))
        {
            var payload = new byte[PayloadSize];
            for (var i = 0; i < coords.Length; i++)
            {
                random.NextBytes(payload);
                coords[i] = writer.Append(payload);
            }
        }

        _headers = new MessageHeader[coords.Length];
        for (var i = 0; i < coords.Length; i++)
        {
            _headers[i] = new MessageHeader(
                i, 0, i, coords[i].SegmentId, coords[i].Offset, coords[i].Length, 0, 0, 0, MessageFlags.None);
        }

        _reader = new SegmentReader(_directory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public async Task<int> ReadOneThousandRandomPayloads()
    {
        var random = new Random(2);
        var total = 0;
        for (var i = 0; i < 1000; i++)
        {
            var header = _headers[random.Next(_headers.Length)];
            var bytes = await _reader.ReadAsync(header, CancellationToken.None);
            total += bytes.Length;
        }

        return total;
    }
}

using System.Text;
using System.Threading.Channels;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;

namespace EventScope.Core.Ingest;

/// <summary>
/// A synthetic <see cref="IEventSource"/> that needs no broker. Infrastructure, not a test
/// double: every throughput/memory acceptance criterion in the build plan is measured
/// against this class, so its output must look enough like a real broker to be a fair
/// stand-in — plausible JSON bodies, an occasional large (&gt;64&#160;KB) payload, an
/// occasional dead-lettered message, partitioned like Kafka.
/// </summary>
public sealed class FakeEventSource : IEventSource
{
    private readonly int _messagesPerSecond;
    private readonly double _largeFraction;
    private readonly double _deadLetterFraction;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;

    public FakeEventSource(
        int messagesPerSecond = 10_000,
        double largeFraction = 0.01,
        double deadLetterFraction = 0.02,
        TimeProvider? timeProvider = null,
        int? seed = null)
    {
        if (messagesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(messagesPerSecond));

        _messagesPerSecond = messagesPerSecond;
        _largeFraction = largeFraction;
        _deadLetterFraction = deadLetterFraction;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = seed is { } s ? new Random(s) : new Random();
    }

    public SourceCapabilities Capabilities { get; } = new()
    {
        CanPeekNonDestructively = true,
        SupportsPartitions = true,
        SupportsSubscriptions = false,
        SupportsSessions = false,
        SupportsDeadLetterQueue = true,
        SupportsReplay = false,
        SupportsOffsetCommit = true,
    };

    public async Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken)
    {
        // Batch flushes ~20/s so PeriodicTimer overhead stays well under the per-message
        // rate even at 10k msg/s, while still ticking often enough that the ingest channel
        // and coalescer see a steady drip rather than one giant burst per second.
        var batchSize = Math.Max(1, _messagesPerSecond / 20);
        var period = TimeSpan.FromSeconds((double)batchSize / _messagesPerSecond);

        using var timer = new PeriodicTimer(period, _timeProvider);
        var sequence = 0L;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var i = 0; i < batchSize; i++)
                {
                    var message = GenerateMessage(sequence++);
                    await destination.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    private RawMessage GenerateMessage(long sequence)
    {
        var now = _timeProvider.GetUtcNow();
        var isLarge = _random.NextDouble() < _largeFraction;
        var isDeadLettered = _random.NextDouble() < _deadLetterFraction;
        var partition = (int)(sequence % 4);
        var subject = $"orders.created.{sequence % 16}";
        var correlationId = Guid.NewGuid().ToString();

        var bodySize = isLarge
            ? 64 * 1024 + _random.Next(1, 32 * 1024)
            : _random.Next(64, 512);

        return new RawMessage
        {
            Body = BuildJsonBody(sequence, correlationId, bodySize),
            EnqueuedTicks = now.UtcTicks,
            ReceivedTicks = now.UtcTicks,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId,
            Subject = subject,
            Partition = partition,
            IsDeadLettered = isDeadLettered,
        };
    }

    /// <summary>
    /// Writes the JSON body's UTF8 bytes directly instead of composing it through
    /// intermediate C# strings. A large message's padding is up to ~98 KB
    /// (<see cref="_largeFraction"/> of messages), and the original implementation built
    /// both a padding <c>string</c> and the final interpolated JSON <c>string</c> at that
    /// size before encoding — two avoidable Large Object Heap allocations (&gt;85,000 bytes
    /// each in UTF16) per large message, on top of the one <c>byte[]</c> that is actually
    /// needed. At the default 1% large fraction and 10k msg/s that is ~30 MB/s of pure
    /// GC churn from the synthetic-load generator itself, which is infrastructure the
    /// acceptance criteria measure through, not the thing under test. See PROGRESS.md's
    /// heap-growth investigation — direct measurement ruled out the ingest byte budget and
    /// the SQLite batch writer's queue as causes; this was the one that showed up instead.
    /// </summary>
    private static byte[] BuildJsonBody(long sequence, string correlationId, int targetSize)
    {
        var paddingLength = Math.Max(0, targetSize - 80);

        Span<char> prefixChars = stackalloc char[96 + correlationId.Length];
        var written = 0;
        "{\"sequence\":".AsSpan().CopyTo(prefixChars);
        written += "{\"sequence\":".Length;
        written += sequence.TryFormat(prefixChars[written..], out var seqLen) ? seqLen : 0;
        ",\"correlationId\":\"".AsSpan().CopyTo(prefixChars[written..]);
        written += ",\"correlationId\":\"".Length;
        correlationId.AsSpan().CopyTo(prefixChars[written..]);
        written += correlationId.Length;
        "\",\"amount\":".AsSpan().CopyTo(prefixChars[written..]);
        written += "\",\"amount\":".Length;
        written += (sequence % 1000).TryFormat(prefixChars[written..], out var amountLen) ? amountLen : 0;
        ",\"padding\":\"".AsSpan().CopyTo(prefixChars[written..]);
        written += ",\"padding\":\"".Length;
        var prefix = prefixChars[..written];

        const string suffix = "\"}";
        var prefixByteCount = Encoding.UTF8.GetByteCount(prefix);
        var body = new byte[prefixByteCount + paddingLength + suffix.Length];

        var offset = Encoding.UTF8.GetBytes(prefix, body);
        body.AsSpan(offset, paddingLength).Fill((byte)'x');
        offset += paddingLength;
        Encoding.UTF8.GetBytes(suffix, body.AsSpan(offset));

        return body;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

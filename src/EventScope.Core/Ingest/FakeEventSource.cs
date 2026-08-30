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

    private static byte[] BuildJsonBody(long sequence, string correlationId, int targetSize)
    {
        var padding = targetSize > 80 ? new string('x', targetSize - 80) : string.Empty;
        var json =
            $$"""{"sequence":{{sequence}},"correlationId":"{{correlationId}}","amount":{{sequence % 1000}},"padding":"{{padding}}"}""";
        return Encoding.UTF8.GetBytes(json);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

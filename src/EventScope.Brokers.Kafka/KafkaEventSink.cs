using System.Text;
using Confluent.Kafka;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;

namespace EventScope.Brokers.Kafka;

/// <summary>
/// <see cref="IEventSink"/> over a Kafka topic — the first (and, for M3, only)
/// <see cref="IEventSink"/> implementation (build plan §5 M3 step 10). Publishes via
/// <see cref="IProducer{TKey,TValue}.ProduceAsync"/>, which is genuinely async in the
/// Confluent client (unlike <c>Consume()</c>'s blocking-sync shape that
/// <see cref="KafkaEventSource"/> has to work around) — no dedicated thread needed here.
/// </summary>
/// <remarks>
/// <para><b>What doesn't map.</b> Kafka has no native per-message TTL (only topic-level
/// retention) and no session concept — <see cref="OutgoingMessage.TimeToLive"/> and
/// <see cref="OutgoingMessage.SessionId"/> are silently unused for this sink, same as
/// <see cref="KafkaEventSource"/>'s own capability flags already say (no dead-letter, no
/// sessions). <see cref="OutgoingMessage.PartitionKey"/> maps to the Kafka message key, which
/// is what actually determines partition placement in real Kafka — not a separate concept the
/// way it might be in ASB/SQS.</para>
/// </remarks>
public sealed class KafkaEventSink : IEventSink
{
    private readonly KafkaSinkOptions _options;
    private readonly IProducer<byte[], byte[]> _producer;

    public KafkaEventSink(
        KafkaSinkOptions options,
        Func<ProducerConfig, IProducer<byte[], byte[]>>? producerFactory = null)
    {
        _options = options;

        var config = new ProducerConfig { BootstrapServers = options.BootstrapServers };
        if (options.SecurityProtocol is { } protocol) config.SecurityProtocol = protocol;
        if (options.SaslMechanism is { } mechanism) config.SaslMechanism = mechanism;
        if (options.SaslUsername is not null) config.SaslUsername = options.SaslUsername;
        if (options.SaslPassword is not null) config.SaslPassword = options.SaslPassword;
        if (options.SslCaLocation is not null) config.SslCaLocation = options.SslCaLocation;

        _producer = (producerFactory ?? (c => new ProducerBuilder<byte[], byte[]>(c).Build()))(config);
    }

    public SourceCapabilities Capabilities { get; } = new()
    {
        CanPeekNonDestructively = true,
        SupportsPartitions = true,
        SupportsSubscriptions = false,
        SupportsSessions = false,
        SupportsDeadLetterQueue = false,
        SupportsReplay = true,
        SupportsOffsetCommit = true,
    };

    public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        var kafkaMessage = new Message<byte[], byte[]>
        {
            Value = Encoding.UTF8.GetBytes(message.Body.ToJsonString()),
            Key = message.PartitionKey is null ? null! : Encoding.UTF8.GetBytes(message.PartitionKey),
        };

        var headers = BuildHeaders(message);
        if (headers is not null) kafkaMessage.Headers = headers;

        await _producer.ProduceAsync(_options.Topic, kafkaMessage, cancellationToken).ConfigureAwait(false);
    }

    private static Headers? BuildHeaders(OutgoingMessage message)
    {
        var hasProperties = message.ApplicationProperties is { Count: > 0 };
        if (message.ContentType is null && message.CorrelationId is null && !hasProperties)
        {
            return null;
        }

        var headers = new Headers();
        if (message.ContentType is not null)
        {
            headers.Add("content-type", Encoding.UTF8.GetBytes(message.ContentType));
        }

        if (message.CorrelationId is not null)
        {
            headers.Add("correlation-id", Encoding.UTF8.GetBytes(message.CorrelationId));
        }

        if (hasProperties)
        {
            foreach (var (key, value) in message.ApplicationProperties!)
            {
                headers.Add(key, Encoding.UTF8.GetBytes(value));
            }
        }

        return headers;
    }

    public ValueTask DisposeAsync()
    {
        // Best-effort: give in-flight sends a chance to land before the underlying handle
        // goes away, but shutdown must not hang on a broker that's gone unreachable.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}

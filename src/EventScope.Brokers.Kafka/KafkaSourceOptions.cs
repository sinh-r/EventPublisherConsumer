using Confluent.Kafka;

namespace EventScope.Brokers.Kafka;

/// <summary>
/// Configuration for <see cref="KafkaEventSource"/>. See <see cref="KafkaEventSource"/>'s
/// remarks for why a fresh, per-instance throwaway consumer group combined with
/// <c>enable.auto.commit=false</c> and <c>auto.offset.reset=latest</c> means this tool never
/// affects a topic's real consumers.
/// </summary>
public sealed record KafkaSourceOptions
{
    public required string BootstrapServers { get; init; }

    public required IReadOnlyList<string> Topics { get; init; }

    /// <summary>Prefixes the random group id generated per <see cref="KafkaEventSource"/>
    /// instance — never reused across connections, and never configurable to a fixed value,
    /// because a fixed group id is what would let this tool interfere with a real consumer
    /// group's partition assignment.</summary>
    public string GroupIdPrefix { get; init; } = "eventscope";

    public AutoOffsetReset AutoOffsetReset { get; init; } = AutoOffsetReset.Latest;

    /// <summary>How long <c>Consume</c> blocks per poll before returning null so the loop can
    /// recheck cancellation. Short enough for shutdown to feel responsive, long enough that
    /// idle polling isn't a busy loop.</summary>
    public TimeSpan ConsumeTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    public SecurityProtocol? SecurityProtocol { get; init; }

    public SaslMechanism? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    public string? SslCaLocation { get; init; }
}

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

    /// <summary>Where a run starts. <see cref="KafkaStartFrom.Latest"/> — tail from now — is the
    /// default, so nothing that does not opt in changes behaviour.</summary>
    public KafkaStartFrom StartFrom { get; init; } = KafkaStartFrom.Latest;

    /// <summary>The moment to start from when <see cref="StartFrom"/> is
    /// <see cref="KafkaStartFrom.Timestamp"/>. Resolved per partition by the broker.</summary>
    public DateTimeOffset? StartTimestampUtc { get; init; }

    /// <summary>The offset to start from when <see cref="StartFrom"/> is
    /// <see cref="KafkaStartFrom.Offset"/>. Only meaningful together with <see cref="Partition"/> —
    /// offsets are per-partition, so one number applied across a whole topic means a different
    /// message in each partition.</summary>
    public long? StartOffset { get; init; }

    /// <summary>How long the broker gets to answer an offset-for-timestamp lookup before the
    /// affected partitions fall back to tailing.</summary>
    public TimeSpan OffsetLookupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The low-level client setting. Derived from <see cref="StartFrom"/> for the two
    /// modes it can express; the explicit modes seek instead. Kept settable because the
    /// measurement scripts and the env-var path both construct options directly.</summary>
    public AutoOffsetReset AutoOffsetReset { get; init; } = AutoOffsetReset.Latest;

    /// <summary>When set, the source <c>Assign</c>s to this partition of <see cref="Topics"/>
    /// instead of <c>Subscribe</c>-ing to the whole topic — <see langword="null"/> (the
    /// default) means all partitions. Requires exactly one topic in <see cref="Topics"/>;
    /// assigning one partition number across several differently-partitioned topics has no
    /// single sensible meaning.</summary>
    public int? Partition { get; init; }

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

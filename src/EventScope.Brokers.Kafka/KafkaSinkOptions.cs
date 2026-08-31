using Confluent.Kafka;

namespace EventScope.Brokers.Kafka;

/// <summary>Configuration for <see cref="KafkaEventSink"/>.</summary>
public sealed record KafkaSinkOptions
{
    public required string BootstrapServers { get; init; }

    public required string Topic { get; init; }

    public SecurityProtocol? SecurityProtocol { get; init; }

    public SaslMechanism? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    public string? SslCaLocation { get; init; }
}

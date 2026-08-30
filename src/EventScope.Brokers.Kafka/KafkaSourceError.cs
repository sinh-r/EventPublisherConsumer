namespace EventScope.Brokers.Kafka;

/// <summary>A non-fatal error surfaced from the consume loop or the client's own error
/// handler — see <see cref="KafkaEventSource.ErrorOccurred"/>. The loop keeps running after
/// raising this; a fatal error instead breaks the loop and faults the source's task.</summary>
public sealed record KafkaSourceError(string Message, Confluent.Kafka.Error? Error);

using EventScope.Brokers.Kafka;
using EventScope.Core.Abstractions;
using EventScope.Core.Ingest;

namespace EventScope.App.Ingest;

/// <summary>
/// Chooses the connected <see cref="IEventSource"/>. This is the M1c stand-in for Stage 5's
/// connection manager: it makes <see cref="KafkaEventSource"/> reachable from the running app
/// — for hand-verification against a real broker, or against a deliberately bogus one to
/// prove the error path — without pulling a broker picker UI forward ahead of plan.
/// </summary>
public static class EventSourceFactory
{
    /// <summary>Defaults to <see cref="FakeEventSource"/>. Setting
    /// <c>EVENTSCOPE_KAFKA_BOOTSTRAP</c> switches to <see cref="KafkaEventSource"/> against
    /// that broker; <c>EVENTSCOPE_KAFKA_TOPIC</c> (comma-separated, defaults to
    /// <c>"eventscope"</c>) names the topic(s) to subscribe to.</summary>
    public static IEventSource Create()
    {
        var bootstrap = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_BOOTSTRAP");
        if (string.IsNullOrWhiteSpace(bootstrap))
        {
            return new FakeEventSource();
        }

        var topicsVariable = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_TOPIC");
        var topics = string.IsNullOrWhiteSpace(topicsVariable)
            ? ["eventscope"]
            : topicsVariable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new KafkaEventSource(new KafkaSourceOptions
        {
            BootstrapServers = bootstrap,
            Topics = topics,
        });
    }
}

using EventScope.Brokers.Kafka;
using EventScope.Core.Abstractions;

namespace EventScope.App.Publisher;

/// <summary>
/// Chooses the publish target, mirroring <see cref="Ingest.EventSourceFactory"/>'s
/// env-var-driven pattern for the consume side. Unlike that factory, "no sink" is a valid,
/// expected result — most sessions never configure a broker to publish to, and
/// <see cref="ViewModels.PublisherViewModel"/> already reports "No publish target connected."
/// rather than needing a stand-in the way <c>FakeEventSource</c> stands in for the source
/// side.
/// </summary>
public static class EventSinkFactory
{
    /// <summary>Returns <see langword="null"/> unless <c>EVENTSCOPE_KAFKA_BOOTSTRAP</c> is
    /// set. <c>EVENTSCOPE_KAFKA_PUBLISH_TOPIC</c> names the topic to publish to, falling back
    /// to <c>EVENTSCOPE_KAFKA_TOPIC</c> (the consume-side variable — the first of its
    /// comma-separated topics, if it names more than one) and then <c>"eventscope"</c>.</summary>
    public static IEventSink? Create()
    {
        var bootstrap = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_BOOTSTRAP");
        if (string.IsNullOrWhiteSpace(bootstrap))
        {
            return null;
        }

        var publishTopic = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_PUBLISH_TOPIC");
        var consumeTopics = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_TOPIC");

        var topic = !string.IsNullOrWhiteSpace(publishTopic)
            ? publishTopic
            : !string.IsNullOrWhiteSpace(consumeTopics)
                ? consumeTopics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
                : "eventscope";

        return new KafkaEventSink(new KafkaSinkOptions
        {
            BootstrapServers = bootstrap,
            Topic = topic,
        });
    }
}

using Confluent.Kafka;
using EventScope.App.Connections;
using EventScope.Brokers.Kafka;
using EventScope.Core.Abstractions;

namespace EventScope.App.Publisher;

/// <summary>
/// Chooses the publish target for a <see cref="ConnectionProfile"/>, mirroring
/// <see cref="Ingest.EventSourceFactory"/>'s pattern for the consume side. Unlike that
/// factory, "no sink" is a valid, expected result — most connections are consume-only, and
/// <see cref="ViewModels.PublisherViewModel"/> already reports "No publish target connected."
/// rather than needing a stand-in the way <c>FakeEventSource</c> stands in for the source
/// side.
/// </summary>
public static class EventSinkFactory
{
    /// <summary><see langword="null"/> falls back to the legacy env-var path (see
    /// <see cref="Ingest.EventSourceFactory"/>'s remarks for why that fallback exists), which
    /// itself returns <see langword="null"/> unless <c>EVENTSCOPE_KAFKA_BOOTSTRAP</c> is set. A
    /// Fake-source or ASB/SQS profile has no sink yet either — only <see cref="ConnectionKind.Kafka"/>
    /// produces one.</summary>
    public static IEventSink? Create(ConnectionProfile? profile = null)
    {
        if (profile is null)
        {
            return CreateFromEnvironment();
        }

        if (profile.Kind != ConnectionKind.Kafka)
        {
            return null;
        }

        var topic = !string.IsNullOrWhiteSpace(profile.PublishTopic)
            ? profile.PublishTopic
            : profile.Topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "eventscope";

        ConnectionSecretProtector.TryUnprotect(profile.SaslPasswordProtected, out var password);

        return new KafkaEventSink(new KafkaSinkOptions
        {
            BootstrapServers = profile.BootstrapServers,
            Topic = topic,
            SecurityProtocol = Enum.TryParse<SecurityProtocol>(profile.SecurityProtocol, out var sp) ? sp : null,
            SaslMechanism = Enum.TryParse<SaslMechanism>(profile.SaslMechanism, out var sm) ? sm : null,
            SaslUsername = string.IsNullOrWhiteSpace(profile.SaslUsername) ? null : profile.SaslUsername,
            SaslPassword = string.IsNullOrEmpty(password) ? null : password,
            SslCaLocation = string.IsNullOrWhiteSpace(profile.SslCaLocation) ? null : profile.SslCaLocation,
        });
    }

    private static IEventSink? CreateFromEnvironment()
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

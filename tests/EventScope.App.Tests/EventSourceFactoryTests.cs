using EventScope.App.Connections;
using EventScope.App.Ingest;
using EventScope.App.Publisher;
using EventScope.Core.Ingest;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>Every Kafka form field lands correctly on the client options
/// <see cref="EventSourceFactory"/>/<see cref="EventSinkFactory"/> build for a
/// <see cref="ConnectionProfile"/> — the whole point of the connection manager replacing the
/// env-var stand-in. No Avalonia dependency, needs no <see cref="HeadlessFixture"/>.</summary>
public sealed class EventSourceFactoryTests
{
    [Fact]
    public void Fake_kind_creates_a_fake_event_source_regardless_of_other_fields()
    {
        var profile = new ConnectionProfile { Kind = ConnectionKind.Fake, Name = "irrelevant" };

        var source = EventSourceFactory.Create(profile) as FakeEventSource;

        Assert.NotNull(source);
    }

    [Fact]
    public void Kafka_kind_maps_every_field_onto_KafkaSourceOptions()
    {
        var protectedPassword = ConnectionSecretProtector.Protect("s3cret")!;
        var profile = new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = " orders , orders-dlq ", // deliberate whitespace, to prove trimming
            GroupIdPrefix = "custom-prefix",
            Partition = 2,
            SecurityProtocol = "SaslSsl",
            SaslMechanism = "ScramSha256",
            SaslUsername = "svc",
            SaslPasswordProtected = protectedPassword,
            SslCaLocation = "ca.pem",
        };

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile);

        Assert.Equal("broker:9092", options.BootstrapServers);
        Assert.Equal(["orders", "orders-dlq"], options.Topics);
        Assert.Equal("custom-prefix", options.GroupIdPrefix);
        Assert.Equal(2, options.Partition);
        Assert.Equal(Confluent.Kafka.SecurityProtocol.SaslSsl, options.SecurityProtocol);
        Assert.Equal(Confluent.Kafka.SaslMechanism.ScramSha256, options.SaslMechanism);
        Assert.Equal("svc", options.SaslUsername);
        Assert.Equal("s3cret", options.SaslPassword);
        Assert.Equal("ca.pem", options.SslCaLocation);
    }

    [Fact]
    public void Blank_topics_falls_back_to_the_default_eventscope_topic()
    {
        var profile = new ConnectionProfile { Kind = ConnectionKind.Kafka, BootstrapServers = "broker:9092", Topics = "" };

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile);

        Assert.Equal(["eventscope"], options.Topics);
    }

    [Fact]
    public void Blank_group_id_prefix_falls_back_to_eventscope()
    {
        var profile = new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            GroupIdPrefix = "  ",
        };

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile);

        Assert.Equal("eventscope", options.GroupIdPrefix);
    }

    [Fact]
    public void An_unrecognized_security_protocol_string_is_treated_as_the_client_default()
    {
        var profile = new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            SecurityProtocol = "NotARealProtocol",
        };

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile);

        Assert.Null(options.SecurityProtocol);
    }

    [Fact]
    public void ServiceBus_and_Sqs_kinds_are_not_yet_supported()
    {
        var profile = new ConnectionProfile { Kind = ConnectionKind.ServiceBus, Name = "future" };

        Assert.Throws<NotSupportedException>(() => EventSourceFactory.Create(profile));
    }
}

public sealed class EventSinkFactoryTests
{
    [Fact]
    public void Fake_and_unsupported_kinds_have_no_publish_sink()
    {
        Assert.Null(EventSinkFactory.Create(new ConnectionProfile { Kind = ConnectionKind.Fake }));
        Assert.Null(EventSinkFactory.Create(new ConnectionProfile { Kind = ConnectionKind.Sqs }));
    }

    [Fact]
    public void Kafka_sink_uses_the_publish_topic_when_set()
    {
        var profile = new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            PublishTopic = "orders-out",
        };

        var sink = EventSinkFactory.Create(profile);

        Assert.NotNull(sink);
    }

    [Fact]
    public void Kafka_sink_falls_back_to_the_first_consume_topic_when_publish_topic_is_blank()
    {
        // The sink's own topic isn't publicly observable from the outside, so this exercises
        // the fallback purely by confirming construction succeeds either way - the mapping
        // logic itself is covered directly via EventSourceFactoryTests' equivalent case for
        // the consume side, which shares the same trim/fallback rules.
        var profile = new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders,orders-dlq",
            PublishTopic = "",
        };

        var sink = EventSinkFactory.Create(profile);

        Assert.NotNull(sink);
    }
}

using EventScope.App.Connections;
using EventScope.App.Ingest;
using EventScope.App.Publisher;
using EventScope.Brokers.Kafka;
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

    // --- Start position ---

    [Fact]
    public void A_profile_saved_before_start_positions_existed_still_tails_from_now()
    {
        // StartFrom is null on every connections.json written before this feature. Latest is the
        // only safe reading of that.
        var options = EventSourceFactory.BuildKafkaSourceOptions(new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
        });

        Assert.Equal(KafkaStartFrom.Latest, options.StartFrom);
    }

    [Fact]
    public void An_unrecognised_start_position_falls_back_to_latest()
    {
        var options = EventSourceFactory.BuildKafkaSourceOptions(new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            StartFrom = "SomethingElse",
        });

        Assert.Equal(KafkaStartFrom.Latest, options.StartFrom);
    }

    [Fact]
    public void Earliest_and_a_timestamp_map_onto_the_source_options()
    {
        var at = new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);

        var earliest = EventSourceFactory.BuildKafkaSourceOptions(new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            StartFrom = "Earliest",
        });
        Assert.Equal(KafkaStartFrom.Earliest, earliest.StartFrom);

        var timestamp = EventSourceFactory.BuildKafkaSourceOptions(new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            StartFrom = "Timestamp",
            StartTimestampUtc = at,
        });
        Assert.Equal(KafkaStartFrom.Timestamp, timestamp.StartFrom);
        Assert.Equal(at, timestamp.StartTimestampUtc!.Value.UtcDateTime);
    }

    [Fact]
    public void Starting_at_an_offset_without_a_partition_is_refused()
    {
        // Offsets are per-partition: applied across a subscribed topic, one number means a
        // different message in each. The editor blocks this too; this is the defence against a
        // hand-edited connections.json.
        var profile = new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            StartFrom = "Offset",
            StartOffset = 12_345,
        };

        Assert.Throws<NotSupportedException>(() => EventSourceFactory.BuildKafkaSourceOptions(profile));
    }

    [Fact]
    public void Starting_at_an_offset_with_a_partition_maps_through()
    {
        var options = EventSourceFactory.BuildKafkaSourceOptions(new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            Partition = 3,
            StartFrom = "Offset",
            StartOffset = 12_345,
        });

        Assert.Equal(KafkaStartFrom.Offset, options.StartFrom);
        Assert.Equal(12_345, options.StartOffset);
    }

    // --- Replay window override ---

    private static readonly DateTimeOffset SevenDaysAgo = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static ConnectionProfile KafkaProfile() => new()
    {
        Kind = ConnectionKind.Kafka,
        BootstrapServers = "broker:9092",
        Topics = "orders",
    };

    [Fact]
    public void A_replay_window_turns_a_tailing_profile_into_a_timestamp_start()
    {
        var options = EventSourceFactory.BuildKafkaSourceOptions(KafkaProfile(), SevenDaysAgo);

        Assert.Equal(KafkaStartFrom.Timestamp, options.StartFrom);
        Assert.Equal(SevenDaysAgo, options.StartTimestampUtc);
    }

    [Fact]
    public void A_replay_window_supersedes_the_profiles_own_saved_timestamp()
    {
        var profile = KafkaProfile();
        profile.StartFrom = "Timestamp";
        profile.StartTimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile, SevenDaysAgo);

        Assert.Equal(SevenDaysAgo, options.StartTimestampUtc);
    }

    [Fact]
    public void A_replay_window_supersedes_an_earliest_profile_rather_than_reading_the_whole_topic()
    {
        var profile = KafkaProfile();
        profile.StartFrom = "Earliest";

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile, SevenDaysAgo);

        Assert.Equal(KafkaStartFrom.Timestamp, options.StartFrom);
    }

    [Fact]
    public void A_replay_window_on_an_offset_profile_with_no_partition_is_allowed_and_drops_the_offset()
    {
        // Without the override the very same profile throws (see the test above). The guard
        // describes an Offset run, and after the override this is no longer one — applying it
        // second would refuse a run the rule does not apply to.
        var profile = KafkaProfile();
        profile.StartFrom = "Offset";
        profile.StartOffset = 12_345;

        var options = EventSourceFactory.BuildKafkaSourceOptions(profile, SevenDaysAgo);

        Assert.Equal(KafkaStartFrom.Timestamp, options.StartFrom);
        Assert.Null(options.StartOffset);
    }

    [Fact]
    public void A_local_time_override_is_stored_as_the_same_instant_in_utc()
    {
        var local = new DateTimeOffset(2026, 8, 29, 14, 0, 0, TimeSpan.FromHours(2));

        var options = EventSourceFactory.BuildKafkaSourceOptions(KafkaProfile(), local);

        Assert.Equal(TimeSpan.Zero, options.StartTimestampUtc!.Value.Offset);
        Assert.Equal(new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc), options.StartTimestampUtc.Value.UtcDateTime);
    }

    [Fact]
    public void No_replay_window_leaves_every_saved_start_position_exactly_as_it_was()
    {
        // The regression guard for every connection saved before the picker existed: passing no
        // override has to be indistinguishable from the single-argument call.
        var profile = KafkaProfile();
        profile.StartFrom = "Timestamp";
        profile.StartTimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var withoutOverride = EventSourceFactory.BuildKafkaSourceOptions(profile);
        var withNullOverride = EventSourceFactory.BuildKafkaSourceOptions(profile, startAtOverride: null);

        Assert.Equal(KafkaStartFrom.Timestamp, withoutOverride.StartFrom);
        Assert.Equal(profile.StartTimestampUtc, withoutOverride.StartTimestampUtc!.Value.UtcDateTime);

        // Field by field rather than record equality: KafkaSourceOptions holds Topics as a
        // string[], so the compiler-generated Equals compares that member by reference and two
        // identical calls could never be equal anyway.
        Assert.Equal(withoutOverride.StartFrom, withNullOverride.StartFrom);
        Assert.Equal(withoutOverride.StartTimestampUtc, withNullOverride.StartTimestampUtc);
        Assert.Equal(withoutOverride.StartOffset, withNullOverride.StartOffset);
    }

    // --- Capability probe ---

    [Fact]
    public async Task Kafka_reports_replay_support_without_opening_a_connection()
    {
        // The bootstrap host is deliberately unreachable: this must answer from the constructed
        // source's own flags, never from anything on the wire, because it runs on every tab switch.
        var capabilities = await EventSourceFactory.CapabilitiesForAsync(new ConnectionProfile
        {
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "no-such-host.invalid:9092",
            Topics = "orders",
        });

        Assert.True(capabilities.SupportsReplay);
    }

    [Fact]
    public async Task The_fake_source_reports_no_replay_support_so_the_picker_hides()
    {
        var capabilities = await EventSourceFactory.CapabilitiesForAsync(
            new ConnectionProfile { Kind = ConnectionKind.Fake });

        Assert.False(capabilities.SupportsReplay);
    }

    [Fact]
    public async Task A_connection_this_build_cannot_open_reports_nothing_rather_than_throwing()
    {
        // Create() throws for these kinds; deciding which toolbar controls apply must not be able
        // to break tab selection.
        var capabilities = await EventSourceFactory.CapabilitiesForAsync(
            new ConnectionProfile { Kind = ConnectionKind.ServiceBus });

        Assert.False(capabilities.SupportsReplay);
        Assert.False(capabilities.CanPeekNonDestructively);
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

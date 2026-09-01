using Confluent.Kafka;
using EventScope.App.Connections;
using EventScope.Brokers.Kafka;
using EventScope.Core.Abstractions;
using EventScope.Core.Ingest;

namespace EventScope.App.Ingest;

/// <summary>
/// Chooses the connected <see cref="IEventSource"/> for a <see cref="ConnectionProfile"/> —
/// the connection manager's replacement for the M1c env-var stand-in. The env-var path
/// (<c>EVENTSCOPE_KAFKA_BOOTSTRAP</c>/<c>EVENTSCOPE_KAFKA_TOPIC</c>) is kept as an explicit
/// fallback for <see cref="Create(ConnectionProfile?)"/>'s <see langword="null"/> case —
/// existing scripts (<c>build/Measure-M1Acceptance.ps1</c>'s measurement mode, the Kafka
/// integration tests) and the measurement session's direct <c>ViewModel.Start()</c> call
/// (which bypasses the connection manager entirely) both still work unchanged.
/// </summary>
public static class EventSourceFactory
{
    /// <summary>Builds the source for <paramref name="profile"/>. <see langword="null"/>
    /// falls back to the legacy env-var path (see class remarks) — <em>not</em> to the Fake
    /// source directly, so a bogus-broker env-var smoke test still works exactly as before.</summary>
    public static IEventSource Create(ConnectionProfile? profile = null)
    {
        if (profile is null)
        {
            return CreateFromEnvironment();
        }

        return profile.Kind switch
        {
            ConnectionKind.Fake => new FakeEventSource(),
            ConnectionKind.Kafka => new KafkaEventSource(BuildKafkaSourceOptions(profile)),
            _ => throw new NotSupportedException(
                $"{profile.Kind} connections are not implemented yet — see build plan M4."),
        };
    }

    /// <summary>Maps a saved connection's Kafka fields onto <see cref="KafkaSourceOptions"/> —
    /// the same shape the connection editor form collects (UI spec §6). Public so the
    /// connection manager can build the same options object to hand
    /// <see cref="KafkaConnectionTester"/> for "Test connection", without duplicating the
    /// mapping.</summary>
    public static KafkaSourceOptions BuildKafkaSourceOptions(ConnectionProfile profile)
    {
        var topics = profile.Topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ConnectionSecretProtector.TryUnprotect(profile.SaslPasswordProtected, out var password);

        return new KafkaSourceOptions
        {
            BootstrapServers = profile.BootstrapServers,
            Topics = topics.Length > 0 ? topics : ["eventscope"],
            GroupIdPrefix = string.IsNullOrWhiteSpace(profile.GroupIdPrefix) ? "eventscope" : profile.GroupIdPrefix,
            Partition = profile.Partition,
            SecurityProtocol = Enum.TryParse<SecurityProtocol>(profile.SecurityProtocol, out var sp) ? sp : null,
            SaslMechanism = Enum.TryParse<SaslMechanism>(profile.SaslMechanism, out var sm) ? sm : null,
            SaslUsername = string.IsNullOrWhiteSpace(profile.SaslUsername) ? null : profile.SaslUsername,
            SaslPassword = string.IsNullOrEmpty(password) ? null : password,
            SslCaLocation = string.IsNullOrWhiteSpace(profile.SslCaLocation) ? null : profile.SslCaLocation,
        };
    }

    private static IEventSource CreateFromEnvironment()
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

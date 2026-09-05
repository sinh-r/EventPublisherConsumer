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
/// fallback for <see cref="Create(ConnectionProfile?, DateTimeOffset?)"/>'s <see langword="null"/>
/// case — existing scripts (<c>build/Measure-M1Acceptance.ps1</c>'s measurement mode, the Kafka
/// integration tests) and the measurement session's direct <c>ViewModel.Start()</c> call
/// (which bypasses the connection manager entirely) both still work unchanged.
/// </summary>
public static class EventSourceFactory
{
    /// <summary>What a connection this build cannot open is assumed to be capable of: nothing.
    /// Every capability-bound control therefore hides rather than offering something that would
    /// fail — the same fail-quiet direction <see cref="KafkaStartOffsets"/> takes for a partition
    /// the broker did not answer for.</summary>
    private static readonly SourceCapabilities NoCapabilities = new()
    {
        CanPeekNonDestructively = false,
        SupportsPartitions = false,
        SupportsSubscriptions = false,
        SupportsSessions = false,
        SupportsDeadLetterQueue = false,
        SupportsReplay = false,
        SupportsOffsetCommit = false,
    };

    /// <summary>Builds the source for <paramref name="profile"/>. <see langword="null"/>
    /// falls back to the legacy env-var path (see class remarks) — <em>not</em> to the Fake
    /// source directly, so a bogus-broker env-var smoke test still works exactly as before.</summary>
    /// <param name="startAtOverride">The toolbar's replay window, already resolved to a moment by
    /// <see cref="StartWindow.TryResolve"/>. <see langword="null"/> — the default — leaves the
    /// profile's own saved start position in charge, so every existing call site is unchanged.</param>
    public static IEventSource Create(ConnectionProfile? profile = null, DateTimeOffset? startAtOverride = null)
    {
        if (profile is null)
        {
            return CreateFromEnvironment();
        }

        return profile.Kind switch
        {
            ConnectionKind.Fake => new FakeEventSource(),
            ConnectionKind.Kafka => new KafkaEventSource(BuildKafkaSourceOptions(profile, startAtOverride)),
            // Reachable from a hand-edited connections.json naming a broker this build cannot
            // open, so the message is written for whoever sees it rather than for us.
            _ => throw new NotSupportedException(
                $"{profile.Kind} connections are not supported yet. EventScope currently connects to Kafka."),
        };
    }

    /// <summary>
    /// What <paramref name="profile"/>'s source can do, without running it — so a
    /// capability-bound control (the replay-window picker) can be shown or hidden the moment a
    /// tab is selected, rather than only after the first Start.
    ///
    /// <para>
    /// Cheap and safe to call on every tab switch: constructing a source opens no connection.
    /// <see cref="KafkaEventSource"/>'s constructor only generates a group id and stores a factory
    /// delegate — no librdkafka handle exists until its consume loop runs — and
    /// <see cref="FakeEventSource"/>'s does less than that. A profile this build cannot open
    /// reports <see cref="NoCapabilities"/> rather than throwing: which controls apply is not a
    /// question that should be able to break tab selection.
    /// </para>
    /// </summary>
    public static async Task<SourceCapabilities> CapabilitiesForAsync(ConnectionProfile? profile)
    {
        IEventSource source;
        try
        {
            source = Create(profile);
        }
        catch (NotSupportedException)
        {
            return NoCapabilities;
        }

        await using (source.ConfigureAwait(false))
        {
            return source.Capabilities;
        }
    }

    /// <summary>Maps a saved connection's Kafka fields onto <see cref="KafkaSourceOptions"/> —
    /// the same shape the connection editor form collects (UI spec §6). Public so the
    /// connection manager can build the same options object to hand
    /// <see cref="KafkaConnectionTester"/> for "Test connection", without duplicating the
    /// mapping.</summary>
    /// <param name="startAtOverride">A replay window resolved to a moment. When given it replaces
    /// whatever the profile says with <see cref="KafkaStartFrom.Timestamp"/> at that moment — see
    /// the override block below for why it is applied before the explicit-offset guard.</param>
    public static KafkaSourceOptions BuildKafkaSourceOptions(
        ConnectionProfile profile, DateTimeOffset? startAtOverride = null)
    {
        var topics = profile.Topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ConnectionSecretProtector.TryUnprotect(profile.SaslPasswordProtected, out var password);

        // An unparseable or absent value means Latest, matching every connection saved before start
        // positions existed - same tolerant shape as the SecurityProtocol/SaslMechanism parses below.
        var startFrom = Enum.TryParse<KafkaStartFrom>(profile.StartFrom, out var parsed)
            ? parsed
            : KafkaStartFrom.Latest;

        var startTimestampUtc = profile.StartTimestampUtc is { } saved
            ? new DateTimeOffset(DateTime.SpecifyKind(saved, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        var startOffset = profile.StartOffset;

        // A replay window wins over every saved start position, and is applied *before* the guard
        // below rather than after: the override replaces Offset with Timestamp, so the
        // offset-needs-a-partition rule no longer describes the run being built and would
        // otherwise refuse one it does not apply to. The saved offset is dropped rather than
        // carried along, so it cannot resurface later as a start position it was never meant to be.
        if (startAtOverride is { } overrideAt)
        {
            startFrom = KafkaStartFrom.Timestamp;
            startTimestampUtc = overrideAt.ToUniversalTime();
            startOffset = null;
        }

        // "Start at offset N" across a subscribed topic would mean offset N in *every* partition,
        // which is essentially never what anyone means. The connection editor blocks this too; the
        // check is repeated here so the factory cannot be talked into it by a hand-edited file.
        if (startFrom == KafkaStartFrom.Offset && profile.Partition is null)
        {
            throw new NotSupportedException(
                "Starting at an explicit offset needs an explicit partition — offsets are per-partition.");
        }

        return new KafkaSourceOptions
        {
            StartFrom = startFrom,
            StartTimestampUtc = startTimestampUtc,
            StartOffset = startOffset,
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

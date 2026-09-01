using Confluent.Kafka;

namespace EventScope.Brokers.Kafka;

/// <summary>Result of a <see cref="KafkaConnectionTester"/> probe — the three UI-spec §6
/// "Test connection" states collapse onto <see cref="Success"/> plus
/// <see cref="ErrorMessage"/> (idle/spinner are the caller's own transient UI state, not
/// something this type needs to model).</summary>
/// <remarks>The spec asks for "broker version detected"; librdkafka's admin metadata does
/// not expose the Kafka broker's own version (only the client library's), so
/// <see cref="ClientVersion"/> reports the client's instead — a stated deviation, not a
/// silent omission.</remarks>
public sealed record KafkaConnectionTestResult(
    bool Success,
    int BrokerCount,
    int? PartitionCount,
    string? ClientVersion,
    string? ErrorMessage)
{
    public static KafkaConnectionTestResult Failure(string message) => new(false, 0, null, null, message);
}

/// <summary>
/// Probes a Kafka connection for the UI spec §6 "Test connection" button —
/// <c>AdminClientBuilder</c> + <c>GetMetadata(timeout)</c>, per the build plan's Stage 5 note
/// for the connection manager. Never throws: every failure mode (unreachable broker, unknown
/// topic, auth failure) becomes a <see cref="KafkaConnectionTestResult.Success"/> of
/// <see langword="false"/> with a message, since this exists specifically to show the user
/// *why* a connection failed rather than propagate an exception into the UI.
/// </summary>
public static class KafkaConnectionTester
{
    /// <summary>Seam for tests — seeded with a real <see cref="Confluent.Kafka.Metadata"/>
    /// (a plain, publicly-constructible record-like class) rather than requiring a fake for
    /// the 20-member <see cref="IAdminClient"/> interface. <paramref name="topic"/> is
    /// <see langword="null"/> to fetch cluster-wide metadata only.</summary>
    public delegate Metadata MetadataFetcher(AdminClientConfig config, string? topic, TimeSpan timeout);

    public static KafkaConnectionTestResult Test(
        KafkaConnectionTestOptions options,
        TimeSpan timeout,
        MetadataFetcher? fetcher = null)
    {
        fetcher ??= DefaultFetch;

        try
        {
            var config = BuildConfig(options);
            var metadata = fetcher(config, options.Topic, timeout);

            if (options.Topic is { Length: > 0 } topic)
            {
                var topicMetadata = metadata.Topics.Find(t => t.Topic == topic);
                if (topicMetadata is { Error.IsError: true })
                {
                    return KafkaConnectionTestResult.Failure(topicMetadata.Error.Reason);
                }

                return new KafkaConnectionTestResult(
                    Success: true,
                    BrokerCount: metadata.Brokers.Count,
                    PartitionCount: topicMetadata?.Partitions.Count,
                    ClientVersion: Library.VersionString,
                    ErrorMessage: null);
            }

            return new KafkaConnectionTestResult(
                Success: true,
                BrokerCount: metadata.Brokers.Count,
                PartitionCount: null,
                ClientVersion: Library.VersionString,
                ErrorMessage: null);
        }
        catch (KafkaException ex)
        {
            return KafkaConnectionTestResult.Failure(ex.Error.Reason);
        }
        catch (Exception ex)
        {
            // Config-shape errors (e.g. an invalid SecurityProtocol string) and anything else
            // librdkafka's own exception hierarchy doesn't cover — surfaced as a failed test
            // rather than an unhandled exception into the connection-manager UI.
            return KafkaConnectionTestResult.Failure(ex.Message);
        }
    }

    private static Metadata DefaultFetch(AdminClientConfig config, string? topic, TimeSpan timeout)
    {
        using var admin = new AdminClientBuilder(config).Build();
        return topic is null ? admin.GetMetadata(timeout) : admin.GetMetadata(topic, timeout);
    }

    private static AdminClientConfig BuildConfig(KafkaConnectionTestOptions options)
    {
        var config = new AdminClientConfig { BootstrapServers = options.BootstrapServers };

        if (options.SecurityProtocol is { } protocol) config.SecurityProtocol = protocol;
        if (options.SaslMechanism is { } mechanism) config.SaslMechanism = mechanism;
        if (options.SaslUsername is not null) config.SaslUsername = options.SaslUsername;
        if (options.SaslPassword is not null) config.SaslPassword = options.SaslPassword;
        if (options.SslCaLocation is not null) config.SslCaLocation = options.SslCaLocation;

        return config;
    }
}

/// <summary>The subset of <see cref="KafkaSourceOptions"/> a connection test needs, plus the
/// single topic to check — kept separate from <see cref="KafkaSourceOptions"/> itself since a
/// test may target no topic at all (cluster reachability only) whereas a real source always
/// subscribes to at least one.</summary>
public sealed record KafkaConnectionTestOptions
{
    public required string BootstrapServers { get; init; }

    public string? Topic { get; init; }

    public SecurityProtocol? SecurityProtocol { get; init; }

    public SaslMechanism? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    public string? SslCaLocation { get; init; }
}

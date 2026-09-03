using System.Text.Json.Serialization;

namespace EventScope.App.Connections;

/// <summary>Which broker family a <see cref="ConnectionProfile"/> connects to. ASB and SQS
/// are here so the connection manager's empty-state buttons and saved-connection grouping
/// (UI spec §6) can name them even though their sources don't exist until M4 — the editor
/// form for those two renders disabled with a tooltip until then.</summary>
public enum ConnectionKind
{
    Fake,
    Kafka,
    ServiceBus,
    Sqs,
}

/// <summary>
/// A saved connection (UI spec §6). Field names mirror
/// <see cref="EventScope.Brokers.Kafka.KafkaSourceOptions"/> and
/// <see cref="EventScope.Brokers.Kafka.KafkaSinkOptions"/> deliberately — this is the form
/// data those get built from, not a new config shape. <see cref="SecurityProtocol"/> and
/// <see cref="SaslMechanism"/> are stored as the target enum's member name (e.g.
/// <c>"SaslSsl"</c>) rather than the enum itself, so this model has no dependency on
/// <c>Confluent.Kafka</c> and can be extended for ASB/SQS fields later without pulling in
/// every broker SDK.
/// </summary>
public sealed class ConnectionProfile
{
    /// <summary>The fixed id of the single, non-deletable, non-persisted "Fake source" entry
    /// that keeps the synthetic path reachable without a saved connection — see
    /// <see cref="CreateFakeSource"/>.</summary>
    public static readonly Guid FakeSourceId = Guid.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public ConnectionKind Kind { get; set; }

    public DateTime LastUsedUtc { get; set; }

    // --- Kafka ---

    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Comma-separated. Consume-side (build plan's existing
    /// <c>EVENTSCOPE_KAFKA_TOPIC</c> shape).</summary>
    public string Topics { get; set; } = string.Empty;

    /// <summary>Publish-side topic. Falls back to the first entry of <see cref="Topics"/> if
    /// blank, mirroring <see cref="EventScope.App.Publisher.EventSinkFactory"/>'s existing
    /// env-var fallback chain.</summary>
    public string PublishTopic { get; set; } = string.Empty;

    public string GroupIdPrefix { get; set; } = "eventscope";

    /// <summary>An explicit partition to <c>Assign</c> to instead of <c>Subscribe</c>-ing to
    /// the whole topic. <see langword="null"/> means all partitions.</summary>
    public int? Partition { get; set; }

    /// <summary>An <c>EventScope.Brokers.Kafka.KafkaStartFrom</c> member name — where a run starts.
    /// <see langword="null"/> or an unrecognised value means <c>Latest</c> (tail from now), which
    /// is both the default and what every connection saved before this existed will deserialize
    /// to.</summary>
    public string? StartFrom { get; set; }

    /// <summary>The moment to start from when <see cref="StartFrom"/> is <c>Timestamp</c>.</summary>
    public DateTime? StartTimestampUtc { get; set; }

    /// <summary>The offset to start from when <see cref="StartFrom"/> is <c>Offset</c>. Requires
    /// <see cref="Partition"/>: offsets are per-partition, so one number across a whole topic
    /// means a different message in each.</summary>
    public long? StartOffset { get; set; }

    /// <summary>A <c>Confluent.Kafka.SecurityProtocol</c> member name, or <see langword="null"/>
    /// for the client default.</summary>
    public string? SecurityProtocol { get; set; }

    /// <summary>A <c>Confluent.Kafka.SaslMechanism</c> member name, or <see langword="null"/>.</summary>
    public string? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    /// <summary>DPAPI-protected (<c>ProtectedData.Protect</c>, current-user scope), base64 —
    /// never plaintext on disk. See <see cref="ConnectionSecretProtector"/>. Never populated
    /// automatically when a saved connection is loaded into the editor; the user must retype
    /// a password to change it, same as any credential-manager UX.</summary>
    public string? SaslPasswordProtected { get; set; }

    public string? SslCaLocation { get; set; }

    /// <summary>Truncated endpoint text for the saved-connections list (UI spec §6). A plain
    /// computed property, not observable — every mutation to a saved connection replaces the
    /// whole <see cref="ConnectionProfile"/> instance in the connection manager's own saved
    /// list rather than mutating fields in place, so a fresh read here is always current for
    /// whatever instance is bound.</summary>
    [JsonIgnore]
    public string EndpointDisplay => Kind switch
    {
        ConnectionKind.Fake => "in-process",
        ConnectionKind.Kafka => Truncate(BootstrapServers, 40),
        _ => string.Empty,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    /// <summary>The built-in Fake source has no editor form and can't be deleted (see
    /// <see cref="Id"/>'s <see cref="FakeSourceId"/> remarks) — drives the saved-connections
    /// list's Edit button visibility.</summary>
    [JsonIgnore]
    public bool IsEditable => Kind != ConnectionKind.Fake;

    /// <summary>The built-in entry standing in for <see cref="EventScope.Core.Ingest.FakeEventSource"/>.
    /// Never persisted by <see cref="ConnectionStore"/> — always prepended to whatever it
    /// loads.</summary>
    public static ConnectionProfile CreateFakeSource() => new()
    {
        Id = FakeSourceId,
        Name = "Fake source",
        Kind = ConnectionKind.Fake,
    };
}

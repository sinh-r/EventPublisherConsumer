using System.Text.Json.Nodes;

namespace EventScope.Core.Models;

/// <summary>
/// A publisher-authored message: a JSON tree plus envelope, ready for
/// generator resolution and publish.
/// </summary>
public sealed class OutgoingMessage
{
    public required JsonNode Body { get; init; }
    public string? ContentType { get; init; }
    public string? PartitionKey { get; init; }
    public string? SessionId { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string>? ApplicationProperties { get; init; }
}

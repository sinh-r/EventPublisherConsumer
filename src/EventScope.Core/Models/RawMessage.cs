namespace EventScope.Core.Models;

/// <summary>
/// A message as handed off by an <see cref="Abstractions.IEventSource"/>, before
/// any storage-side extraction (preview, body_head, interning) happens.
/// </summary>
public sealed class RawMessage
{
    public required byte[] Body { get; init; }
    public required long EnqueuedTicks { get; init; }
    public required long ReceivedTicks { get; init; }
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public int? Partition { get; init; }
    public bool IsDeadLettered { get; init; }
    public IReadOnlyDictionary<string, string>? SystemProperties { get; init; }
    public IReadOnlyDictionary<string, string>? ApplicationProperties { get; init; }
}

using EventScope.Core.Models;

namespace EventScope.Core.Abstractions;

/// <summary>
/// Reads a message's payload bytes given its header. M1a's implementation
/// (<c>EventScope.App.Ingest.InMemoryPayloadStore</c>) is an in-memory ring stand-in; M1b
/// replaces it with the real async segment reader over <c>RandomAccess</c> with LZ4 block
/// framing, keyed by <see cref="MessageHeader.SegmentId"/>/<see cref="MessageHeader.Offset"/>/
/// <see cref="MessageHeader.Length"/> instead of just <see cref="MessageHeader.Sequence"/>.
/// The interface does not change between the two.
/// </summary>
public interface IPayloadReader
{
    /// <summary>Returns an empty buffer if the payload is no longer available (evicted).</summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(MessageHeader header, CancellationToken cancellationToken);
}

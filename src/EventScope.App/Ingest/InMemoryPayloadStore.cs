using EventScope.Core.Abstractions;
using EventScope.Core.Models;

namespace EventScope.App.Ingest;

/// <summary>
/// M1a stand-in for the real segment store: a fixed-capacity ring of payload bytes keyed by
/// sequence. Deliberately smaller than <c>MessageRowsView</c>'s 65,536-row window so payload
/// eviction is reachable in a short demo run without waiting to fill the whole grid ring.
/// Replaced in M1b by the async segment reader over <c>RandomAccess</c> with LZ4 framing —
/// same <see cref="IPayloadReader"/> contract, no call-site change.
/// </summary>
public sealed class InMemoryPayloadStore : IPayloadReader
{
    private readonly int _capacity;
    private readonly byte[]?[] _ring;
    private readonly long[] _sequences;

    public InMemoryPayloadStore(int capacity = 4096)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ring = new byte[capacity][];
        _sequences = new long[capacity];
        Array.Fill(_sequences, -1);
    }

    /// <summary>Overwrites whatever previously occupied this sequence's slot — the store's
    /// own eviction, independent of the grid's much larger header ring.</summary>
    public void Store(long sequence, byte[] body)
    {
        var slot = (int)(sequence % _capacity);
        _ring[slot] = body;
        _sequences[slot] = sequence;
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(MessageHeader header, CancellationToken cancellationToken)
    {
        var slot = (int)(header.Sequence % _capacity);
        if (_sequences[slot] != header.Sequence)
        {
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        var body = _ring[slot];
        return ValueTask.FromResult(body is null ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>)body);
    }
}

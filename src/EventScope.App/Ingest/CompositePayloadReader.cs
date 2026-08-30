using EventScope.Core.Abstractions;
using EventScope.Core.Models;

namespace EventScope.App.Ingest;

/// <summary>
/// Tries <paramref name="hot"/> first, falls through to <paramref name="cold"/> on a miss.
/// A just-ingested payload is essentially always still sitting in
/// <c>SegmentWriter</c>'s pending block — not yet flushed to disk — so at 10k msg/s a naive
/// segment-only reader would report "payload evicted" for almost every freshly selected row.
/// See PROGRESS.md &#167;0.1.
/// </summary>
public sealed class CompositePayloadReader(IPayloadReader hot, IPayloadReader cold) : IPayloadReader
{
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(MessageHeader header, CancellationToken cancellationToken)
    {
        var fromHot = await hot.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (!fromHot.IsEmpty) return fromHot;

        return await cold.ReadAsync(header, cancellationToken).ConfigureAwait(false);
    }
}

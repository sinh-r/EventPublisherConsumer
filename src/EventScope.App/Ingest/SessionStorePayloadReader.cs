using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Sqlite;

namespace EventScope.App.Ingest;

/// <summary>
/// Resolves the right day's <c>SegmentReader</c> before reading. A <see cref="MessageHeader"/>'s
/// <c>SegmentId</c>/<c>Offset</c> are only meaningful within the day directory they were
/// written to — segment ids restart at 0 every day — so a cold read has to know which day a
/// row belongs to before it can look anything up. Derived from <see cref="MessageHeader.EnqueuedTicks"/>
/// (UTC), the same way <see cref="SessionStore"/> computes its own day strings, so the two
/// never disagree about which directory a given moment belongs to.
/// </summary>
public sealed class SessionStorePayloadReader(SessionStore sessionStore) : IPayloadReader
{
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(MessageHeader header, CancellationToken cancellationToken)
    {
        var day = new DateTime(header.EnqueuedTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd");
        var reader = sessionStore.GetOrOpenReader(day);
        return reader.ReadAsync(header, cancellationToken);
    }
}

using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Sqlite;

namespace EventScope.App.Ingest;

/// <summary>
/// Resolves the right day's <c>SegmentReader</c> before reading. A <see cref="MessageHeader"/>'s
/// <c>SegmentId</c>/<c>Offset</c> are only meaningful within the day directory they were written
/// to — segment ids restart at 0 every day — so a cold read has to know which day a row belongs
/// to before it can look anything up.
///
/// <para>
/// <b>Prefer the pinned form.</b> Constructed with a <paramref name="day"/>, this reads that
/// directory and nothing else: the caller knows the day because it travelled with the row from the
/// writer that filed it (<see cref="EventScope.App.Collections.MessageRowsView"/>'s day ring).
/// Constructed without one, it falls back to <see cref="SessionLayout.DayFor"/>, which infers the
/// day from the message's <i>broker</i> timestamp while the directory came from the <i>writer's</i>
/// clock. Those agree only while tailing a live topic. Start a run from a Kafka backlog and every
/// replayed message is filed under today with a timestamp from weeks ago, so the inference looks in
/// a directory that either does not exist or — worse, since offsets are dense and segment ids
/// restart daily — holds a different message at the same coordinates. The fallback exists for
/// callers that genuinely have no day (the pre-storage preview path, tests), not as an equal
/// alternative.
/// </para>
/// </summary>
/// <param name="day">The day directory to read, or <see langword="null"/> to infer it.</param>
public sealed class SessionStorePayloadReader(SessionStore sessionStore, string? day = null) : IPayloadReader
{
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(MessageHeader header, CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrEmpty(day) ? SessionLayout.DayFor(header.EnqueuedTicks) : day;
        var reader = sessionStore.GetOrOpenReader(resolved);
        return reader.ReadAsync(header, cancellationToken);
    }
}

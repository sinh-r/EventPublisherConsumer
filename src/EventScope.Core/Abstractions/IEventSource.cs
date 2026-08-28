using System.Threading.Channels;
using EventScope.Core.Models;

namespace EventScope.Core.Abstractions;

/// <summary>
/// A broker connection that streams <see cref="RawMessage"/>s into a channel.
/// Implementations run their own consume loop on whatever threading model the
/// underlying client requires (see the threading table in the build plan) and
/// write into <paramref name="destination"/> from that loop.
/// </summary>
public interface IEventSource : IAsyncDisposable
{
    SourceCapabilities Capabilities { get; }

    /// <summary>
    /// Starts consuming and writing to <paramref name="destination"/> until
    /// <paramref name="cancellationToken"/> fires. The source is responsible
    /// for respecting the channel's back-pressure (a full/blocked writer
    /// means "stop calling the broker client for more").
    /// </summary>
    Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken);
}

using EventScope.Core.Models;

namespace EventScope.Core.Abstractions;

/// <summary>
/// A broker connection that can publish <see cref="OutgoingMessage"/>s back
/// out. Separate from <see cref="IEventSource"/> because not every source a
/// user connects to is also something they intend to publish to.
/// </summary>
public interface IEventSink : IAsyncDisposable
{
    SourceCapabilities Capabilities { get; }

    Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

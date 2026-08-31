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
    /// A short, broker-and-endpoint-neutral label for display ("Kafka", "Service Bus",
    /// "SQS", "Fake source") — lets the UI show what's connected without testing the
    /// concrete type (build plan §5 M4: "no <c>if (broker == …)</c> anywhere in the view
    /// layer").
    /// </summary>
    string DisplayName { get; }

    /// <summary>Raised for a non-fatal client error the source wants surfaced without
    /// breaking its consume loop (e.g. a transient broker error). Broker-neutral so the view
    /// layer can subscribe without knowing which concrete source it's watching.</summary>
    event Action<SourceError>? ErrorOccurred;

    /// <summary>
    /// Starts consuming and writing to <paramref name="destination"/> until
    /// <paramref name="cancellationToken"/> fires. The source is responsible
    /// for respecting the channel's back-pressure (a full/blocked writer
    /// means "stop calling the broker client for more").
    /// </summary>
    Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken);
}

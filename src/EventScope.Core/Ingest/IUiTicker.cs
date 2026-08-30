namespace EventScope.Core.Ingest;

/// <summary>
/// Abstracts the coalescer's tick source so <see cref="IngestCoalescer"/> stays free of
/// Avalonia. Production binds this to a 60&#160;ms <c>DispatcherTimer</c> at
/// <c>DispatcherPriority.Background</c> (see <c>EventScope.App.Ingest.DispatcherTimerTicker</c>);
/// tests drive it manually.
/// </summary>
public interface IUiTicker
{
    event Action? Tick;

    void Start();

    void Stop();
}

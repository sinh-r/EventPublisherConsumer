using Avalonia.Threading;
using EventScope.Core.Ingest;

namespace EventScope.App.Ingest;

/// <summary>
/// Production <see cref="IUiTicker"/>: a 60&#160;ms <see cref="DispatcherTimer"/> at
/// <see cref="DispatcherPriority.Background"/> — not <c>Normal</c> or <c>Send</c>, so input
/// and render outrank ingest and the 100&#160;ms frame budget holds under saturation.
/// </summary>
public sealed class DispatcherTimerTicker : IUiTicker
{
    private readonly DispatcherTimer _timer;

    public DispatcherTimerTicker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _timer.Tick += (_, _) => Tick?.Invoke();
    }

    public event Action? Tick;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}

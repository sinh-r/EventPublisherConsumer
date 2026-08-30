using EventScope.Core.Ingest;

namespace EventScope.Core.Tests;

/// <summary>Test double for <see cref="IUiTicker"/> — the coalescer's tick source under
/// direct test control instead of a real 60&#160;ms <c>DispatcherTimer</c>.</summary>
public sealed class ManualTicker : IUiTicker
{
    public event Action? Tick;

    public bool Started { get; private set; }

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void Fire() => Tick?.Invoke();
}

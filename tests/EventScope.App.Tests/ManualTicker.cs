using EventScope.Core.Ingest;

namespace EventScope.App.Tests;

/// <summary>Test double for <see cref="IUiTicker"/>, mirroring
/// EventScope.Core.Tests.ManualTicker — duplicated rather than shared across test assemblies
/// to avoid an unusual test-project-to-test-project reference for ten lines of code.</summary>
public sealed class ManualTicker : IUiTicker
{
    public event Action? Tick;

    public bool Started { get; private set; }

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void Fire() => Tick?.Invoke();
}

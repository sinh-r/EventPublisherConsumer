using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace EventScope.App.Tests;

/// <summary>
/// Avalonia.Headless.XUnit targets xunit v2 only (see the build plan's test-framework
/// decision); this project is on xunit.v3, so headless setup and dispatcher pumping are
/// done by hand instead. Deterministic pumping is wanted anyway for the coalescer tests.
/// </summary>
public static class HeadlessFixture
{
    private static readonly Lock InitGate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized) return;

            AppBuilder.Configure<HeadlessTestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            _initialized = true;
        }
    }

    /// <summary>Runs all pending dispatcher jobs — layout, bindings, event handlers —
    /// synchronously, so tests don't need a real message pump.</summary>
    public static void Pump() => Dispatcher.UIThread.RunJobs();
}

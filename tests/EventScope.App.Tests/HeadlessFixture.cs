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

            // Root cause of the long-standing "hangs with near-zero CPU" issue tracked in
            // Docs/PROGRESS.md's Blocked item 2, confirmed against Avalonia 11.3 source and
            // reproduced deterministically before this fix was written (isolating
            // IngestPipelineStorageTests/-PreviewTests/-EndToEndTests via `-class` hangs on
            // main; passes here): Application.RegisterServices() — run as part of Setup below
            // — calls AvaloniaSynchronizationContext.InstallIfNeeded(), which (AutoInstall
            // defaults to true) installs a SynchronizationContext on *this* thread whose
            // Post/Send forward to Dispatcher.Post. SetupWithoutStarting() never runs a
            // dispatcher loop, and this assembly only pumps manually via Pump() below at
            // moments tests choose — so any `await` elsewhere in the process that is NOT
            // ConfigureAwait(false) and is on a *genuinely* asynchronous task (one that
            // doesn't complete synchronously - a synchronously-completed ValueTask/Task never
            // touches the context at all) posts its continuation to a queue nothing ever
            // drains, and hangs forever. Which test happened to hang was pure luck of
            // discovery order: whichever test class's constructor called EnsureInitialized()
            // first is the one whose thread got poisoned. Opting out here removes the
            // dependency on that order entirely - this project only ever advances the
            // dispatcher through Pump(), never through await, so nothing relies on the
            // context this suppresses.
            AvaloniaSynchronizationContext.AutoInstall = false;

            AppBuilder.Configure<HeadlessTestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            // With AutoInstall = false, RegisterServices() above no longer touches
            // Dispatcher.UIThread as a side effect of installing the sync context - touch it
            // here instead so the dispatcher still binds to this thread deterministically,
            // exactly as it did before this fix, rather than to whatever thread happens to
            // reference it first.
            _ = Dispatcher.UIThread;

            _initialized = true;
        }
    }

    /// <summary>Runs all pending dispatcher jobs — layout, bindings, event handlers —
    /// synchronously, so tests don't need a real message pump.</summary>
    public static void Pump() => Dispatcher.UIThread.RunJobs();
}

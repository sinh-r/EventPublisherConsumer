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
    private static readonly CancellationTokenSource LoopCts = new();

    public static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized) return;

            // Second root cause of the long-standing "hangs with near-zero CPU" issue tracked
            // in Docs/PROGRESS.md's Blocked item 2 — the first (an auto-installed
            // AvaloniaSynchronizationContext posting continuations nothing drains) was fixed
            // below via AutoInstall = false, but that only closed the `await`-shaped half of
            // the problem. The other half: xUnit v3's in-process runner does not guarantee a
            // test method runs on the same OS thread as the constructor that called this
            // method (DataGridVirtualizationSpikeTests' and AcceptanceCriteriaTests' own
            // remarks already document this), so several tests wrap their bodies in
            // Dispatcher.UIThread.Invoke(...) to marshal onto whichever thread the dispatcher
            // is actually bound to. Invoke runs inline only when already on that thread; from
            // any other thread it queues an operation and *blocks the caller waiting for it to
            // run*. Setup previously bound the dispatcher to whatever thread first called this
            // method (an xUnit worker thread) and never ran a loop to service that queue — so
            // an Invoke from a different worker thread deadlocks forever, at near-zero CPU,
            // exactly the reported signature. Confirmed as the live mechanism: this repo's own
            // PROGRESS.md already recorded reproducing this deterministically (an earlier,
            // reverted attempt forced setup onto its own thread without a loop, and "Invoke
            // from every other thread blocked on the same unpumped dispatcher").
            //
            // Fix: give the dispatcher a real, continuously-running loop on a thread this
            // fixture owns outright, rather than whichever thread happens to call this method
            // first. Dispatcher.UIThread.Invoke from any other thread then completes correctly
            // — it queues the callback, the loop below dequeues and runs it on the owned
            // thread, and the caller unblocks with the result — instead of deadlocking.
            AvaloniaSynchronizationContext.AutoInstall = false;

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var uiThread = new Thread(() =>
            {
                try
                {
                    AppBuilder.Configure<HeadlessTestApp>()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                        .SetupWithoutStarting();

                    // Binds Dispatcher.UIThread to this thread — the one about to run MainLoop
                    // below — rather than to whatever thread references it first.
                    _ = Dispatcher.UIThread;

                    ready.SetResult();

                    // Blocks this thread for the process lifetime, continuously draining the
                    // dispatcher's job queue. Nested RunJobs() calls from within a dispatched
                    // callback (Pump(), below) are legitimate reentrant pumping on this same
                    // thread — the same pattern Avalonia's own headless test session uses.
                    Dispatcher.UIThread.MainLoop(LoopCts.Token);
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "avalonia-headless-ui",
            };
            uiThread.Start();

            // Block until setup completes (or rethrow if it failed) so the first test doesn't
            // race the background thread's own initialization.
            ready.Task.GetAwaiter().GetResult();

            _initialized = true;
        }
    }

    /// <summary>Marshals <paramref name="body"/> onto the dispatcher thread and runs it
    /// synchronously, regardless of which thread calls this — the one safe way for a test to
    /// touch Avalonia controls, since xUnit v3 does not guarantee test methods run on the same
    /// thread as <see cref="EnsureInitialized"/>. Do not call <see cref="Dispatcher.UIThread"/>'s
    /// own Invoke directly from a test; go through this so the fixture's setup always runs
    /// first.</summary>
    public static void RunOnUi(Action body)
    {
        EnsureInitialized();
        Dispatcher.UIThread.Invoke(body);
    }

    /// <summary>Runs all pending dispatcher jobs — layout, bindings, event handlers —
    /// synchronously, so tests don't need to wait on the real-time loop. Only valid from
    /// inside a <see cref="RunOnUi"/> callback (i.e. on the dispatcher thread itself).</summary>
    public static void Pump() => Dispatcher.UIThread.RunJobs();
}

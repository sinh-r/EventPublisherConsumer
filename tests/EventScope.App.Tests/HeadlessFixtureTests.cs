using Avalonia.Threading;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// Pins the two invariants whose violation caused the long-standing "hangs with near-zero
/// CPU" issue (<c>Docs/PROGRESS.md</c>'s Blocked item 2). See
/// <see cref="HeadlessFixture"/>'s own remarks for the full mechanism behind both.
/// </summary>
public sealed class HeadlessFixtureTests
{
    [Fact]
    public void EnsureInitialized_does_not_install_an_avalonia_synchronization_context()
    {
        HeadlessFixture.EnsureInitialized();

        Assert.False(
            SynchronizationContext.Current is AvaloniaSynchronizationContext,
            "an AvaloniaSynchronizationContext is installed on this thread - any genuinely " +
            "asynchronous await here would post to a dispatcher this assembly never runs a " +
            "loop for, and hang forever.");
    }

    [Fact]
    public void The_dispatcher_thread_runs_a_loop_so_a_cross_thread_Invoke_completes()
    {
        HeadlessFixture.EnsureInitialized();

        // Meaningless unless this test's own thread is genuinely not the dispatcher thread -
        // xUnit v3 does not guarantee which thread runs a test method, but EnsureInitialized()
        // now always binds the dispatcher to a dedicated thread it owns (see HeadlessFixture),
        // never to a test/xUnit worker thread, so this holds regardless of scheduling.
        Assert.False(
            Dispatcher.UIThread.CheckAccess(),
            "this test's own thread is the dispatcher thread - the assertion below would be " +
            "trivially true and wouldn't exercise the cross-thread path this test exists for.");

        var completed = false;
        var operation = Dispatcher.UIThread.InvokeAsync(() => completed = true);
        operation.Wait(TimeSpan.FromSeconds(5));

        Assert.True(completed,
            "Dispatcher.UIThread.InvokeAsync queued from a non-dispatcher thread did not " +
            "complete within 5s - nothing is running the dispatcher's loop to drain it, which " +
            "is exactly the deadlock HeadlessFixture's dedicated MainLoop thread exists to " +
            "prevent (see HeadlessFixture.RunOnUi and its call sites).");
    }
}

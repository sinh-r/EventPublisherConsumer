using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// Pins the exact invariant whose violation caused the long-standing "hangs with near-zero
/// CPU" issue (<c>Docs/PROGRESS.md</c>'s Blocked item 2): <see cref="HeadlessFixture.EnsureInitialized"/>
/// must never leave an <c>AvaloniaSynchronizationContext</c> installed on the calling thread.
/// That context posts continuations to <c>Dispatcher.UIThread</c>, and this assembly's
/// <c>SetupWithoutStarting()</c> environment never runs a dispatcher loop to drain them — so
/// any genuinely asynchronous <c>await</c> elsewhere in the process that captured it would
/// hang forever, on whichever thread happened to initialize first. See
/// <see cref="HeadlessFixture"/>'s own remarks for the full mechanism.
/// </summary>
public sealed class HeadlessFixtureTests
{
    [Fact]
    public void EnsureInitialized_does_not_install_an_avalonia_synchronization_context()
    {
        HeadlessFixture.EnsureInitialized();

        Assert.False(
            SynchronizationContext.Current is Avalonia.Threading.AvaloniaSynchronizationContext,
            "an AvaloniaSynchronizationContext is installed on this thread - any genuinely " +
            "asynchronous await here would post to a dispatcher this assembly never runs a " +
            "loop for, and hang forever.");
    }
}

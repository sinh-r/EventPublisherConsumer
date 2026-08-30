using EventScope.Core.Ingest;
using Xunit;

namespace EventScope.Core.Tests;

public class ByteBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TryAcquire_admits_up_to_the_limit_and_rejects_beyond_it()
    {
        var budget = new ByteBudget(100);

        Assert.True(budget.TryAcquire(60));
        Assert.True(budget.TryAcquire(40));
        Assert.Equal(100, budget.Used);

        Assert.False(budget.TryAcquire(1));
        Assert.Equal(100, budget.Used); // rolled back, not left at 101
    }

    [Fact]
    public void TryAcquire_admits_a_single_oversized_message_when_nothing_else_is_reserved()
    {
        var budget = new ByteBudget(100);

        Assert.True(budget.TryAcquire(500));
        Assert.Equal(500, budget.Used);

        // With the oversized message still outstanding, a second acquire of any size must
        // wait rather than being admitted too — only the *first* one on an empty budget
        // gets the exception.
        Assert.False(budget.TryAcquire(1));
    }

    [Fact]
    public async Task AcquireAsync_completes_immediately_when_room_is_available()
    {
        var budget = new ByteBudget(100);
        await budget.AcquireAsync(50, Ct);
        Assert.Equal(50, budget.Used);
    }

    [Fact]
    public async Task AcquireAsync_parks_until_release_and_then_proceeds()
    {
        var budget = new ByteBudget(100);
        Assert.True(budget.TryAcquire(100));

        var waiter = budget.AcquireAsync(10, Ct).AsTask();
        await Task.Delay(20, Ct); // give the waiter a chance to actually park
        Assert.False(waiter.IsCompleted);

        // Release below the ¾ low-water mark (75) so the waiter is unparked.
        budget.Release(30);

        var completed = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(waiter, completed);
        Assert.Equal(80, budget.Used); // 100 - 30 + 10
    }

    [Fact]
    public async Task Release_does_not_unpark_until_the_low_water_mark_is_crossed()
    {
        var budget = new ByteBudget(100);
        Assert.True(budget.TryAcquire(100));

        var waiter = budget.AcquireAsync(10, Ct).AsTask();
        await Task.Delay(20, Ct);

        // Still above the ¾ (75) low-water mark after this release (100 - 10 = 90).
        budget.Release(10);
        await Task.Delay(50, Ct);
        Assert.False(waiter.IsCompleted);

        // Crosses below 75.
        budget.Release(20);
        var completed = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(waiter, completed);
    }

    [Fact]
    public async Task Complete_cancels_a_parked_writer_instead_of_hanging()
    {
        var budget = new ByteBudget(100);
        Assert.True(budget.TryAcquire(100));

        var waiter = budget.AcquireAsync(10, Ct).AsTask();
        await Task.Delay(20, Ct);

        budget.Complete();

        var completed = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(waiter, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public async Task AcquireAsync_honours_external_cancellation()
    {
        var budget = new ByteBudget(100);
        Assert.True(budget.TryAcquire(100));

        using var cts = new CancellationTokenSource();
        var waiter = budget.AcquireAsync(10, cts.Token).AsTask();
        await Task.Delay(20, Ct);

        cts.Cancel();

        var completed = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(waiter, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public void Peak_tracks_the_high_water_mark_across_acquire_and_release()
    {
        var budget = new ByteBudget(1000);
        Assert.True(budget.TryAcquire(600));
        budget.Release(400);
        Assert.True(budget.TryAcquire(300));

        Assert.Equal(600, budget.Peak);
        Assert.Equal(500, budget.Used);
    }
}

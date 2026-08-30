using System.Threading.Channels;
using EventScope.Core.Ingest;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.Core.Tests;

public class FakeEventSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Capabilities_mirror_a_partitioned_peekable_broker()
    {
        var source = new FakeEventSource();

        Assert.True(source.Capabilities.CanPeekNonDestructively);
        Assert.True(source.Capabilities.SupportsPartitions);
        Assert.True(source.Capabilities.SupportsOffsetCommit);
    }

    [Fact]
    public async Task RunAsync_stops_promptly_when_cancelled()
    {
        var source = new FakeEventSource(messagesPerSecond: 1000, seed: 1);
        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await source.RunAsync(channel.Writer, cts.Token);

        Assert.True(channel.Reader.Count > 0, "expected at least one message before cancellation");
    }

    [Fact]
    public async Task Generated_messages_are_well_formed_and_include_both_large_and_dead_lettered()
    {
        // High fractions + a run long enough that both branches are exercised deterministically.
        var source = new FakeEventSource(messagesPerSecond: 2000, largeFraction: 0.3, deadLetterFraction: 0.3, seed: 42);
        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await source.RunAsync(channel.Writer, cts.Token);
        channel.Writer.TryComplete();

        var messages = new List<RawMessage>();
        await foreach (var m in channel.Reader.ReadAllAsync(Ct))
        {
            messages.Add(m);
        }

        Assert.NotEmpty(messages);
        Assert.All(messages, m =>
        {
            Assert.NotEmpty(m.Body);
            Assert.False(string.IsNullOrEmpty(m.Subject));
            Assert.False(string.IsNullOrEmpty(m.CorrelationId));
            Assert.NotNull(m.Partition);
        });

        Assert.Contains(messages, m => m.Body.Length > 64 * 1024);
        Assert.Contains(messages, m => m.IsDeadLettered);
    }

    [Fact]
    public async Task Same_seed_produces_the_same_message_sizes_in_order()
    {
        // PeriodicTimer runs on the real clock, so how many messages land before a
        // wall-clock cutoff isn't itself deterministic — only compare the common prefix
        // both runs are guaranteed to have produced, to avoid timing-jitter flakiness.
        async Task<List<int>> Run(int seed)
        {
            var source = new FakeEventSource(messagesPerSecond: 1000, seed: seed);
            var channel = Channel.CreateUnbounded<RawMessage>();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await source.RunAsync(channel.Writer, cts.Token);
            channel.Writer.TryComplete();

            var sizes = new List<int>();
            await foreach (var m in channel.Reader.ReadAllAsync(Ct))
            {
                sizes.Add(m.Body.Length);
            }
            return sizes;
        }

        var a = await Run(7);
        var b = await Run(7);

        var commonLength = Math.Min(a.Count, b.Count);
        Assert.True(commonLength > 10, $"expected a meaningful run in 150ms, got {a.Count} and {b.Count}");
        Assert.Equal(a.Take(commonLength), b.Take(commonLength));
    }
}

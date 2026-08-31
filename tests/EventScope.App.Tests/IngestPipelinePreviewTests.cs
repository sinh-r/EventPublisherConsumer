using System.Text;
using System.Threading.Channels;
using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.App.ViewModels;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// <see cref="IngestPipeline"/>'s preview-building was rewritten (see PROGRESS.md's
/// heap-growth-investigation follow-up) to avoid decoding the whole body and running two
/// separate <c>Replace</c> passes over it. Nothing exercised its actual output shape before —
/// existing pipeline tests use synthetic hardcoded preview strings, not real body content —
/// so this covers newline replacement and truncation through the real pipeline end to end.
/// </summary>
public sealed class IngestPipelinePreviewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-preview-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public IngestPipelinePreviewTests() => HeadlessFixture.EnsureInitialized();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Newlines_are_replaced_with_spaces_and_long_bodies_are_truncated_with_an_ellipsis()
    {
        var shortBody = "hello world"u8.ToArray();
        var withNewlines = Encoding.UTF8.GetBytes("line one\nline two\r\nline three");
        var longBody = Encoding.UTF8.GetBytes(new string('a', 300));

        using var sessionStore = new SessionStore(_root);
        var rows = new MessageRowsView(capacity: 16);
        var source = new ScriptedEventSource([shortBody, withNewlines, longBody]);
        var ticker = new ManualTicker();

        var pipeline = new IngestPipeline(source, rows, ticker, sessionStore);

        pipeline.Start();
        await source.Completed.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The drain loop that calls _coalescer.Enqueue runs on its own task, independent of
        // RunAsync completing — repeatedly tick until the coalescer has actually flushed all
        // three into the rows view, rather than assuming one tick after completion suffices.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (rows.TotalAppended < 3)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Expected 3 rows within timeout.");
            ticker.Fire();
            await Task.Delay(10, Ct);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("hello world", ((MessageRowViewModel)rows[0]!).Preview);
        Assert.Equal("line one line two  line three", ((MessageRowViewModel)rows[1]!).Preview);
        Assert.Equal(new string('a', 120) + "…", ((MessageRowViewModel)rows[2]!).Preview);

        await pipeline.DisposeAsync();
    }

    /// <summary>Emits an exact sequence of already-built bodies, then completes — deterministic
    /// content is what this test needs, not realistic pacing.</summary>
    private sealed class ScriptedEventSource(IReadOnlyList<byte[]> bodies) : IEventSource
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public SourceCapabilities Capabilities { get; } = new()
        {
            CanPeekNonDestructively = true,
            SupportsPartitions = true,
            SupportsSubscriptions = false,
            SupportsSessions = false,
            SupportsDeadLetterQueue = false,
            SupportsReplay = false,
            SupportsOffsetCommit = true,
        };

        public async Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var body in bodies)
                {
                    await destination.WriteAsync(new RawMessage
                    {
                        Body = body,
                        EnqueuedTicks = DateTime.UtcNow.Ticks,
                        ReceivedTicks = DateTime.UtcNow.Ticks,
                        Subject = "orders.created",
                        CorrelationId = Guid.NewGuid().ToString(),
                        Partition = 0,
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _completed.TrySetResult();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

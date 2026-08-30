using System.Text;
using System.Threading.Channels;
using EventScope.App.Collections;
using EventScope.Core.Abstractions;
using EventScope.Core.Ingest;
using EventScope.Core.Models;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;

namespace EventScope.App.Ingest;

/// <summary>
/// Wires one broker connection's whole ingest path: <see cref="IEventSource"/> &#8594;
/// byte-budgeted channel &#8594; segment write + preview/body_head shaping &#8594; SQLite
/// batch write &#8594; <see cref="IngestCoalescer"/> &#8594; <see cref="MessageRowsView.AppendBatch"/>.
///
/// <see cref="SegmentWriter"/> and <see cref="SqliteBatchWriter"/> are owned by the caller's
/// <see cref="SessionStore"/> and outlive any single pipeline instance — segment and day
/// files persist across a Start/Stop toggle, they don't reset with the connection. Only the
/// hot in-memory payload ring (<see cref="PayloadReader"/>'s fast path) is scoped to this
/// pipeline, since it's keyed by a sequence counter that restarts at 0 each Start.
/// </summary>
public sealed class IngestPipeline : IAsyncDisposable
{
    private readonly IEventSource _source;
    private readonly MessageRowsView _rows;
    private readonly ByteBudget _byteBudget;
    private readonly Channel<RawMessage> _channel;
    private readonly ChannelWriter<RawMessage> _budgetedWriter;
    private readonly IngestCoalescer _coalescer;
    private readonly SegmentWriter _segmentWriter;
    private readonly SqliteBatchWriter _batchWriter;
    private readonly InMemoryPayloadStore _hotStore;
    private readonly CancellationTokenSource _cts = new();

    private long _sequence;
    private Task? _sourceTask;
    private Task? _drainTask;

    public IngestPipeline(
        IEventSource source,
        MessageRowsView rows,
        IUiTicker ticker,
        SegmentWriter segmentWriter,
        SqliteBatchWriter batchWriter,
        IPayloadReader coldPayloadReader,
        long byteBudgetLimit = 256 * 1024 * 1024,
        int hotPayloadCapacity = 4096)
    {
        _source = source;
        _rows = rows;
        _segmentWriter = segmentWriter;
        _batchWriter = batchWriter;
        _byteBudget = new ByteBudget(byteBudgetLimit);
        _channel = Channel.CreateBounded<RawMessage>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _budgetedWriter = new BudgetedChannelWriter(_channel.Writer, _byteBudget);

        _hotStore = new InMemoryPayloadStore(hotPayloadCapacity);
        PayloadReader = new CompositePayloadReader(_hotStore, coldPayloadReader);

        _coalescer = new IngestCoalescer(ticker);
        _coalescer.BatchReady += OnBatchReady;
    }

    /// <summary>Hot-ring-first, cold-segment-fallback reader for the detail pane. Segment
    /// coordinates travel on the row view model itself (see <c>MessageRowViewModel.SegmentId</c>/
    /// <c>Offset</c>), so callers reconstruct a <see cref="MessageHeader"/> from the row rather
    /// than looking one up here.</summary>
    public IPayloadReader PayloadReader { get; }

    public long UiDropped => _coalescer.UiDropped;
    public long ByteBudgetUsed => _byteBudget.Used;
    public long ByteBudgetLimit => _byteBudget.Limit;

    public void Start()
    {
        _sourceTask = RunSourceAsync(_cts.Token);
        _drainTask = DrainAsync(_cts.Token);
    }

    private async Task RunSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _source.RunAsync(_budgetedWriter, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Ingest(message);
                }
                finally
                {
                    _byteBudget.Release(message.Body.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private void Ingest(RawMessage message)
    {
        var sequence = _sequence++;
        var subject = message.Subject ?? string.Empty;
        var correlationId = message.CorrelationId ?? string.Empty;

        var flags = MessageFlags.None;
        if (message.Body.Length > 64 * 1024) flags |= MessageFlags.IsLarge;
        if (message.IsDeadLettered) flags |= MessageFlags.IsDeadLettered;

        var (segmentId, offset, length) = _segmentWriter.Append(message.Body);
        _hotStore.Store(sequence, message.Body);

        // §4.4: large rows replace the preview with this text rather than showing (a
        // truncated slice of) the actual body.
        var preview = (flags & MessageFlags.IsLarge) != 0
            ? "payload not previewed"
            : BuildPreview(message.Body);

        var bodyHead = BuildBodyHead(message.Body);

        var header = new MessageHeader(
            sequence: sequence,
            enqueuedTicks: message.EnqueuedTicks,
            rowId: sequence,
            segmentId: segmentId,
            offset: offset,
            length: length,
            subjectId: 0,
            correlationInternId: 0,
            partition: (short)(message.Partition ?? 0),
            flags: flags);

        _batchWriter.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: message.EnqueuedTicks,
            ReceivedTicks: message.ReceivedTicks,
            SegmentId: segmentId,
            Offset: offset,
            Length: length,
            MessageId: message.MessageId,
            CorrelationId: message.CorrelationId,
            Subject: subject,
            Partition: message.Partition,
            Flags: (byte)flags,
            Preview: preview,
            BodyHead: bodyHead));

        _coalescer.Enqueue(header, preview, subject, correlationId);
    }

    private static string BuildPreview(byte[] body)
    {
        const int maxChars = 120;
        var text = Encoding.UTF8.GetString(body).Replace('\n', ' ').Replace('\r', ' ');
        return text.Length > maxChars ? string.Concat(text.AsSpan(0, maxChars), "…") : text;
    }

    /// <summary>First 2 KB of the body, captured at ingest for M2's FTS indexer — capturing
    /// it now means no backfill pass is needed once the indexer exists.</summary>
    private static string? BuildBodyHead(byte[] body)
    {
        const int capBytes = 2048;
        if (body.Length == 0) return null;
        return Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, capBytes));
    }

    private void OnBatchReady(
        ReadOnlyMemory<MessageHeader> headers,
        ReadOnlyMemory<string?> previews,
        ReadOnlyMemory<string> subjects,
        ReadOnlyMemory<string> correlationIds) =>
        _rows.AppendBatch(headers.Span, previews.Span, subjects.Span, correlationIds.Span);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
        _byteBudget.Complete();

        if (_sourceTask is not null)
        {
            try { await _sourceTask.ConfigureAwait(false); } catch { /* observed above */ }
        }

        if (_drainTask is not null)
        {
            try { await _drainTask.ConfigureAwait(false); } catch { /* observed above */ }
        }

        await _source.DisposeAsync().ConfigureAwait(false);
        _coalescer.Dispose();
        _cts.Dispose();
    }

    /// <summary>Acquires the byte budget before every write so a slow/blocked reader stops
    /// the source from being asked for more — see the build plan's ingest-channel design.
    /// Forces the async path (<see cref="TryWrite"/> always fails) so the budget is never
    /// bypassed by a synchronous write.</summary>
    private sealed class BudgetedChannelWriter(ChannelWriter<RawMessage> inner, ByteBudget budget)
        : ChannelWriter<RawMessage>
    {
        public override bool TryWrite(RawMessage item) => false;

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);

        public override async ValueTask WriteAsync(RawMessage item, CancellationToken cancellationToken = default)
        {
            await budget.AcquireAsync(item.Body.Length, cancellationToken).ConfigureAwait(false);
            try
            {
                await inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                budget.Release(item.Body.Length);
                throw;
            }
        }
    }
}

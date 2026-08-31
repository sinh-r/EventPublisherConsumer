using System.Text;
using System.Threading.Channels;
using EventScope.App.Collections;
using EventScope.Core.Abstractions;
using EventScope.Core.Ingest;
using EventScope.Core.Models;
using EventScope.Storage.Sqlite;

namespace EventScope.App.Ingest;

/// <summary>
/// Wires one broker connection's whole ingest path: <see cref="IEventSource"/> &#8594;
/// byte-budgeted channel &#8594; segment write + preview/body_head shaping &#8594; SQLite
/// batch write &#8594; <see cref="IngestCoalescer"/> &#8594; <see cref="MessageRowsView.AppendBatch"/>.
///
/// <see cref="SessionStore"/> is owned by the caller and outlives any single pipeline
/// instance — segment and day files persist across a Start/Stop toggle, they don't reset
/// with the connection. <see cref="SessionStore.EnsureCurrentDay"/> is called before every
/// ingest write so a day rollover is never missed, and every write goes through
/// <c>_sessionStore.SegmentWriter</c>/<c>.Writer</c> freshly each time rather than a cached
/// reference, since rollover can swap which day those point at. Only the hot in-memory
/// payload ring (<see cref="PayloadReader"/>'s fast path) is scoped to this pipeline, since
/// it's keyed by a sequence counter that restarts at 0 each Start.
/// </summary>
public sealed class IngestPipeline : IAsyncDisposable
{
    private readonly IEventSource _source;
    private readonly MessageRowsView _rows;
    private readonly ByteBudget _byteBudget;
    private readonly Channel<RawMessage> _channel;
    private readonly ChannelWriter<RawMessage> _budgetedWriter;
    private readonly IngestCoalescer _coalescer;
    private readonly SessionStore _sessionStore;
    private readonly InMemoryPayloadStore _hotStore;
    private readonly CancellationTokenSource _cts = new();

    private readonly int _indexedPrefixBytes;

    private long _sequence;
    private Task? _sourceTask;
    private Task? _drainTask;

    public IngestPipeline(
        IEventSource source,
        MessageRowsView rows,
        IUiTicker ticker,
        SessionStore sessionStore,
        long byteBudgetLimit = 256 * 1024 * 1024,
        int hotPayloadCapacity = 4096,
        int indexedPrefixBytes = 2048)
    {
        _source = source;
        _rows = rows;
        _sessionStore = sessionStore;
        _indexedPrefixBytes = indexedPrefixBytes;
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
        PayloadReader = new CompositePayloadReader(_hotStore, new SessionStorePayloadReader(sessionStore));

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
        _sessionStore.EnsureCurrentDay();

        var sequence = _sequence++;
        var subject = message.Subject ?? string.Empty;
        var correlationId = message.CorrelationId ?? string.Empty;

        var flags = MessageFlags.None;
        if (message.Body.Length > 64 * 1024) flags |= MessageFlags.IsLarge;
        if (message.IsDeadLettered) flags |= MessageFlags.IsDeadLettered;

        var (segmentId, offset, length) = _sessionStore.SegmentWriter.Append(message.Body);
        _hotStore.Store(sequence, message.Body);

        // §4.4: large rows replace the preview with this text rather than showing (a
        // truncated slice of) the actual body.
        var preview = (flags & MessageFlags.IsLarge) != 0
            ? "payload not previewed"
            : BuildPreview(message.Body);

        var bodyHead = BuildBodyHead(message.Body, _indexedPrefixBytes);

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

        _sessionStore.Writer.Enqueue(new WriteOp.InsertMessage(
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

    /// <summary>Decodes only enough of the body to cover <paramref name="maxChars"/> (a
    /// generous 4-bytes/char bound, not an exact one — cutting a multi-byte sequence at the
    /// boundary is already tolerated by the pre-existing char-level truncation below) and
    /// replaces newlines in a single pass, instead of decoding the whole body and running two
    /// separate <see cref="string.Replace(char, char)"/> passes over it.</summary>
    private static string BuildPreview(byte[] body)
    {
        const int maxChars = 120;
        var byteCount = Math.Min(body.Length, maxChars * 4);
        var decoded = Encoding.UTF8.GetString(body, 0, byteCount);

        var truncated = decoded.Length > maxChars;
        var length = truncated ? maxChars : decoded.Length;

        Span<char> buffer = length <= 128 ? stackalloc char[length] : new char[length];
        for (var i = 0; i < length; i++)
        {
            var c = decoded[i];
            buffer[i] = c is '\n' or '\r' ? ' ' : c;
        }

        return truncated ? string.Concat(buffer, "…") : new string(buffer);
    }

    /// <summary>The first <paramref name="capBytes"/> of the body (2 KB by default, settings
    /// view's "indexed prefix" per the build plan), captured at ingest for the FTS indexer —
    /// capturing it now means no backfill pass is ever needed. Changing this setting only
    /// affects newly-ingested rows; it does not rewrite <c>body_head</c> for rows already on
    /// disk (doing so would violate the FTS external-content contract in §3.4 — that column
    /// must never be updated after indexing).</summary>
    private static string? BuildBodyHead(byte[] body, int capBytes)
    {
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

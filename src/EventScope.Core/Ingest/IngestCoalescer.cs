using EventScope.Core.Models;

namespace EventScope.Core.Ingest;

/// <summary>
/// Batches ingested headers between UI ticks so the grid never sees a per-message collection
/// notification — see the build plan's verified <c>DataGrid</c> notification-handling table:
/// multi-item <c>Add</c> silently drops rows, <c>Replace</c> throws, <c>Move</c> desyncs, and
/// <c>Reset</c> is the only action that batches correctly.
///
/// <para>
/// Double-buffered: <see cref="Enqueue"/> (ingest thread) and <see cref="OnTick"/> (UI thread,
/// via <see cref="IUiTicker.Tick"/>) both take <see cref="_gate"/>, but the work done under it
/// is O(1) — a slot write plus a count bump on enqueue, a buffer-pointer swap on tick.
/// <see cref="BatchReady"/> fires after the swap, outside the lock, so subscribers (which
/// append into the grid's collection) never run while a subsequent tick could touch the same
/// buffer.
/// </para>
///
/// <para>Staging is bounded per tick; overflow increments <see cref="UiDropped"/> instead of
/// growing — the UI is deliberately lossy. The byte-bounded path (<see cref="ByteBudget"/>)
/// is the one that must lose nothing, and it sits upstream of this class.</para>
/// </summary>
public sealed class IngestCoalescer : IDisposable
{
    public delegate void BatchReadyHandler(
        ReadOnlyMemory<MessageHeader> headers,
        ReadOnlyMemory<string?> previews,
        ReadOnlyMemory<string> subjects,
        ReadOnlyMemory<string> correlationIds);

    private readonly IUiTicker _ticker;
    private readonly int _stagingCapacity;
    private readonly Lock _gate = new();

    private Buffer _active;
    private Buffer _shadow;

    public event BatchReadyHandler? BatchReady;

    public long UiDropped { get; private set; }

    public IngestCoalescer(IUiTicker ticker, int stagingCapacity = 4096)
    {
        if (stagingCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(stagingCapacity));

        _ticker = ticker;
        _stagingCapacity = stagingCapacity;
        _active = new Buffer(stagingCapacity);
        _shadow = new Buffer(stagingCapacity);

        _ticker.Tick += OnTick;
        _ticker.Start();
    }

    public void Enqueue(in MessageHeader header, string? preview, string subject, string correlationId)
    {
        lock (_gate)
        {
            var buffer = _active;
            if (buffer.Count >= _stagingCapacity)
            {
                UiDropped++;
                return;
            }

            var i = buffer.Count;
            buffer.Headers[i] = header;
            buffer.Previews[i] = preview;
            buffer.Subjects[i] = subject;
            buffer.CorrelationIds[i] = correlationId;
            buffer.Count = i + 1;
        }
    }

    private void OnTick()
    {
        Buffer ready;
        lock (_gate)
        {
            if (_active.Count == 0) return;

            ready = _active;
            _active = _shadow;
            _active.Count = 0;
            _shadow = ready;
        }

        BatchReady?.Invoke(
            ready.Headers.AsMemory(0, ready.Count),
            ready.Previews.AsMemory(0, ready.Count),
            ready.Subjects.AsMemory(0, ready.Count),
            ready.CorrelationIds.AsMemory(0, ready.Count));
    }

    public void Dispose()
    {
        _ticker.Tick -= OnTick;
        _ticker.Stop();
    }

    private sealed class Buffer
    {
        public readonly MessageHeader[] Headers;
        public readonly string?[] Previews;
        public readonly string[] Subjects;
        public readonly string[] CorrelationIds;
        public int Count;

        public Buffer(int capacity)
        {
            Headers = new MessageHeader[capacity];
            Previews = new string?[capacity];
            Subjects = new string[capacity];
            CorrelationIds = new string[capacity];
        }
    }
}

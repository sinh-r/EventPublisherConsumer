namespace EventScope.Core.Ingest;

/// <summary>
/// Gates broker-to-disk ingest by total reserved bytes, not item count —
/// <see cref="System.Threading.Channels.BoundedChannelOptions"/> only caps item count.
///
/// <para>
/// <b>Deadlock-freedom argument.</b> Only the writer ever calls <see cref="AcquireAsync"/>
/// (parks); only the reader ever calls <see cref="Release"/> (unparks). That is a single
/// directed edge writer&#8594;reader with no back-edge, so there is no cycle for a deadlock
/// to form on. The one apparent exception — a message at or above the whole budget — is
/// handled by admitting it unconditionally when nothing else is reserved, rather than
/// parking forever waiting for room that can never exist.
/// </para>
/// </summary>
public sealed class ByteBudget
{
    private readonly long _limit;
    private readonly Lock _gate = new();
    private long _used;
    private long _peak;
    private TaskCompletionSource? _space;
    private bool _completed;

    public ByteBudget(long limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
    }

    public long Limit => _limit;
    public long Used => Interlocked.Read(ref _used);
    public long Peak => Interlocked.Read(ref _peak);

    /// <summary>
    /// Fast path: one <see cref="Interlocked.Add(ref long, long)"/>, no lock, no allocation.
    /// Rolls back and returns <see langword="false"/> if that would exceed the limit — unless
    /// nothing else is reserved, in which case an oversized message is admitted anyway so it
    /// can never deadlock waiting for room the budget cannot provide.
    /// </summary>
    public bool TryAcquire(int bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes == 0) return true;

        var used = Interlocked.Add(ref _used, bytes);
        if (used <= _limit || (bytes >= _limit && used == bytes))
        {
            BumpPeak(used);
            return true;
        }

        Interlocked.Add(ref _used, -bytes);
        return false;
    }

    /// <summary>Writer-only. Parks until room is available or <paramref name="cancellationToken"/> fires.</summary>
    public ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (TryAcquire(bytes)) return ValueTask.CompletedTask;
        return new ValueTask(AcquireSlowAsync(bytes, cancellationToken));
    }

    private async Task AcquireSlowAsync(int bytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                // Recheck inside the lock, after we're prepared to enlist as a waiter — this
                // is what kills the lost wakeup where a Release lands between the failed
                // TryAcquire above and registering to be woken.
                if (TryAcquire(bytes)) return;

                if (_completed)
                {
                    throw new OperationCanceledException("ByteBudget completed while a writer was waiting.");
                }

                _space ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _space.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reader-only. Never awaits. Completes a parked writer once usage drops to the
    /// low-water mark (¾ of the limit) rather than the instant any room opens, so the gate
    /// doesn't thrash open and closed on every release.</summary>
    public void Release(int bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes == 0) return;

        lock (_gate)
        {
            var used = Interlocked.Add(ref _used, -bytes);
            if (_space is not null && used <= _limit * 3 / 4)
            {
                _space.TrySetResult();
                _space = null;
            }
        }
    }

    /// <summary>Unparks any waiting writer (with cancellation) and marks the budget closed
    /// for further waits — called on shutdown so a pending <see cref="AcquireAsync"/> cannot hang.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _space?.TrySetCanceled();
            _space = null;
        }
    }

    private void BumpPeak(long used)
    {
        long peak;
        do
        {
            peak = Interlocked.Read(ref _peak);
            if (used <= peak) return;
        } while (Interlocked.CompareExchange(ref _peak, used, peak) != peak);
    }
}

using EventScope.Core.Models;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;

namespace EventScope.Storage.Retention;

/// <summary>
/// Background cap and age enforcement over a <see cref="SessionStore"/>'s day directories.
/// Runs on <see cref="PeriodicTimer"/>, driven by <see cref="TimeProvider"/> so the interval
/// and the age cutoff are both fake-clock testable.
///
/// <para>
/// <b>Age-based deletion</b> drops whole day directories older than the configured retention
/// window — day files are dropped whole, never row-by-row, which is also why no <c>DELETE</c>
/// trigger or FTS <c>'delete'</c> row ever needs to exist (build plan §3.4).
/// </para>
///
/// <para>
/// <b>Cap enforcement</b> evicts the oldest segment across the whole store — oldest day first,
/// lowest segment id within that day — until total on-disk bytes (including <c>-wal</c> files,
/// which would otherwise blow the cap from a direction the accounting doesn't see) drop back
/// under the cap. Eviction means: delete the segment file, then mark its rows
/// <see cref="MessageFlags.PayloadEvicted"/> via <see cref="SessionStore.EnqueueSetFlags"/>
/// (never touching an FTS-indexed column). The segment a live writer is still appending to is
/// never a candidate — deleting it out from under an open <see cref="SegmentWriter"/> handle
/// would corrupt the write in progress.
/// </para>
///
/// <para>
/// When a day has no segments left, its <c>.db</c> is dropped too — there is nothing in it
/// worth keeping if every payload it references is already gone.
/// </para>
///
/// <para>
/// <b>Every delete this class performs can legitimately fail, and none of them may fault the
/// loop.</b> A reader elsewhere in the process — a history browse, or a deep scan
/// (<see cref="Search.DeepScanner"/>) walking every day file on disk — holds
/// <see cref="SegmentReader"/> handles opened <see cref="FileShare.ReadWrite"/>, which on
/// Windows does not include <c>FILE_SHARE_DELETE</c>: the delete throws
/// <see cref="IOException"/> (or <see cref="UnauthorizedAccessException"/>, which Windows
/// raises for some of the same cases) for as long as that handle is open. Retention therefore
/// treats a blocked candidate as "not now" and moves on — to the next candidate this pass, or
/// to the same one on the next tick. Letting it escape would fault the loop task and stop
/// retention for the rest of the session, silently: <see cref="Dispose"/> observes that fault
/// and has nowhere to report it, so the only symptom would be a store growing past its cap.
/// </para>
/// </summary>
public sealed class RetentionService : IDisposable
{
    private readonly string _rootDirectory;
    private readonly SessionStore _sessionStore;
    private readonly TimeProvider _time;
    private readonly PeriodicTimer _timer;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts = new();
    private long _capBytes;
    private int _retentionDays;

    /// <summary>Settable at runtime — the settings view (build plan §5 M2) changes these
    /// without needing a reconnect; the next 30 s tick (or a manually driven
    /// <see cref="RunOnce"/>) picks them up.</summary>
    public long CapBytes
    {
        get => _capBytes;
        set => _capBytes = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public int RetentionDays
    {
        get => _retentionDays;
        set => _retentionDays = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public RetentionService(
        string rootDirectory,
        SessionStore sessionStore,
        long capBytes,
        int retentionDays,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        if (capBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capBytes));
        if (retentionDays <= 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));

        _rootDirectory = rootDirectory;
        _sessionStore = sessionStore;
        _capBytes = capBytes;
        _retentionDays = retentionDays;
        _time = timeProvider ?? TimeProvider.System;
        _timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(30), _time);
        _loopTask = Task.Run(RunLoopAsync);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    RunOnce();
                }
                catch (IOException)
                {
                    // A file this pass wanted gone is still open somewhere. Defer to the next
                    // tick rather than faulting the loop — see the class remarks. The
                    // per-candidate guards below already absorb every expected case; this is
                    // the backstop that keeps the loop alive through anything they miss.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>Runs one retention pass synchronously. Exposed so tests can drive retention
    /// deterministically instead of waiting on the real timer.</summary>
    public void RunOnce()
    {
        DeleteExpiredDays();
        EnforceCap();
    }

    private void DeleteExpiredDays()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.Date.AddDays(-_retentionDays);

        foreach (var day in _sessionStore.ListDayDirectories())
        {
            if (day == _sessionStore.CurrentDay) continue;
            if (!DateTime.TryParseExact(day, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var date))
            {
                continue; // not a day directory this store recognizes
            }

            if (date >= cutoff) continue;

            // A day held open by a browse or a deep scan stays until it is released. Every
            // other expired day in this pass is still deleted.
            TryDeleteDay(day);
        }
    }

    private void EnforceCap()
    {
        while (TotalBytes() > _capBytes)
        {
            if (!EvictOldestSegment()) break; // nothing left that's safe to evict
        }

        // A day with no segments left has nothing worth keeping its .db for.
        foreach (var day in _sessionStore.ListDayDirectories())
        {
            if (day == _sessionStore.CurrentDay) continue;

            var dir = Path.Combine(_rootDirectory, day);
            if (!Directory.Exists(dir)) continue;
            if (Directory.EnumerateFiles(dir, "*.seg").Any()) continue;

            TryDeleteDay(day);
        }
    }

    /// <summary><see langword="false"/> when the day is currently held open elsewhere. The
    /// caller's only correct response is to move on: retrying in place cannot succeed until
    /// whoever holds the handle releases it, and waiting for that is not this loop's job.
    /// </summary>
    private bool TryDeleteDay(string day)
    {
        try
        {
            _sessionStore.DeleteDay(day);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool EvictOldestSegment()
    {
        foreach (var day in _sessionStore.ListDayDirectories())
        {
            var dir = Path.Combine(_rootDirectory, day);
            if (!Directory.Exists(dir)) continue;

            var segmentIds = Directory.EnumerateFiles(dir, "*.seg")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => int.Parse(name!))
                .OrderBy(id => id);

            foreach (var segmentId in segmentIds)
            {
                // Never evict the segment a live writer is still appending to.
                if (day == _sessionStore.CurrentDay && segmentId == _sessionStore.CurrentSegmentId)
                {
                    continue;
                }

                // Delete first, flag only once it succeeds. The other order marks rows
                // PayloadEvicted while their bytes are still on disk and readable, so a row
                // ends up lying about itself for as long as the delete keeps failing.
                try
                {
                    File.Delete(SegmentFormat.SegmentPath(dir, segmentId));
                }
                catch (IOException)
                {
                    continue; // held open by a reader — try the next candidate, not this one again
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                _sessionStore.EnqueueSetFlags(day, segmentId, (byte)MessageFlags.PayloadEvicted);
                return true;
            }
        }

        // Reached only when nothing anywhere could be evicted. Returning false is what stops
        // EnforceCap's loop: a locked oldest segment that reported success instead would be
        // retried forever against a total that never drops.
        return false;
    }

    private long TotalBytes()
    {
        if (!Directory.Exists(_rootDirectory)) return 0;

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*", SearchOption.AllDirectories))
        {
            // A -wal file (or a segment mid-eviction) can be truncated or deleted by a
            // concurrent checkpoint or writer between the enumeration above and this stat -
            // the chaos soak (build plan §6) reproduces this within seconds under real
            // concurrent ingest. A file that vanished mid-count contributes nothing to the
            // current total, which is exactly what "no longer exists" means here.
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return total;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        try
        {
            _loopTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Observed above via the try/catch inside RunLoopAsync; nothing further to do.
        }

        _cts.Dispose();
    }
}

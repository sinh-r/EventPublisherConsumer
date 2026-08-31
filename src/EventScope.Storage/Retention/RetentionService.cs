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
/// under the cap. Eviction means: mark the segment's rows <see cref="MessageFlags.PayloadEvicted"/>
/// via <see cref="SessionStore.EnqueueSetFlags"/> (never touching an FTS-indexed column), then
/// delete the segment file itself. The segment a live writer is still appending to is never a
/// candidate — deleting it out from under an open <see cref="SegmentWriter"/> handle would
/// corrupt the write in progress.
/// </para>
///
/// <para>
/// When a day has no segments left, its <c>.db</c> is dropped too — there is nothing in it
/// worth keeping if every payload it references is already gone.
/// </para>
/// </summary>
public sealed class RetentionService : IDisposable
{
    private readonly string _rootDirectory;
    private readonly SessionStore _sessionStore;
    private readonly long _capBytes;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private readonly PeriodicTimer _timer;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts = new();

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
                RunOnce();
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

            _sessionStore.DeleteDay(day);
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

            _sessionStore.DeleteDay(day);
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

                _sessionStore.EnqueueSetFlags(day, segmentId, (byte)MessageFlags.PayloadEvicted);
                File.Delete(SegmentFormat.SegmentPath(dir, segmentId));
                return true;
            }
        }

        return false;
    }

    private long TotalBytes()
    {
        if (!Directory.Exists(_rootDirectory)) return 0;

        return Directory.EnumerateFiles(_rootDirectory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
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

using System.Text;
using EventScope.Core.Models;
using EventScope.Storage.Retention;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// Retention (build plan §5 M2, task 2): age-based deletion of whole day directories, and cap
/// enforcement by evicting the oldest segment first. <see cref="RetentionService.RunOnce"/> is
/// driven directly rather than through the real 30s <see cref="PeriodicTimer"/>, so these run
/// at unit-test speed.
/// </summary>
public sealed class RetentionServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-retention-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<long> CountRowsAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> CountEvictedRowsAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE flags & 4 != 0";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task WriteOneMessageAsync(
        SessionStore store, int segmentId, int offset, int length, CancellationToken ct)
    {
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: segmentId, Offset: offset, Length: length,
            MessageId: null, CorrelationId: null, Subject: "orders.created",
            Partition: 0, Flags: 0, Preview: "p", BodyHead: "b"));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task A_day_directory_older_than_the_retention_window_is_deleted_entirely()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        var coords = store.SegmentWriter.Append("old day"u8.ToArray());
        store.SegmentWriter.Append(new byte[SegmentFormat.BlockSize]);
        await WriteOneMessageAsync(store, coords.SegmentId, coords.Offset, coords.Length, Ct);

        var oldDay = store.CurrentDay;
        var oldDb = Path.Combine(store.Directory, $"{oldDay}.db");
        Assert.True(File.Exists(oldDb));

        // Roll forward well past the retention window and start a new (current) day so the
        // old one is eligible.
        time.Set(new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();
        await SqliteTestHelpers.WaitForRolloverSealAsync(_root, oldDay, Ct);

        using var retention = new RetentionService(
            _root, store, capBytes: long.MaxValue, retentionDays: 14, timeProvider: time,
            interval: TimeSpan.FromHours(1));
        retention.RunOnce();

        Assert.False(Directory.Exists(Path.Combine(_root, oldDay)));
        Assert.True(Directory.Exists(store.Directory), "the current day must never be deleted");
    }

    [Fact]
    public void The_current_day_is_never_deleted_by_age_even_if_it_is_old()
    {
        // A store opened against a very old fake "now" - the current day is, by definition,
        // whatever the clock says today is, so age-based deletion must never touch it even
        // though its own date would otherwise be far outside the window.
        var time = new SettableTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        using var retention = new RetentionService(
            _root, store, capBytes: long.MaxValue, retentionDays: 1, timeProvider: time,
            interval: TimeSpan.FromHours(1));
        retention.RunOnce();

        Assert.True(Directory.Exists(store.Directory));
    }

    [Fact]
    public async Task Enforcing_the_cap_evicts_the_oldest_segment_first_and_marks_its_rows()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        // Three messages, each in its own segment: write one, then force a roll with
        // incompressible filler before writing the next, so eviction has multiple distinct
        // sealed (non-current) segments to choose the oldest from — a single small message
        // per segment would otherwise all pack into segment 0 together.
        var random = new Random(7);
        var coordsList = new List<(int SegmentId, int Offset, int Length)>();
        for (var i = 0; i < 3; i++)
        {
            var coords = store.SegmentWriter.Append(Encoding.UTF8.GetBytes($"message {i}"));
            coordsList.Add(coords);
            await WriteOneMessageAsync(store, coords.SegmentId, coords.Offset, coords.Length, Ct);

            var startingSegment = store.SegmentWriter.CurrentSegmentId;
            var filler = new byte[SegmentFormat.BlockSize];
            while (store.SegmentWriter.CurrentSegmentId == startingSegment)
            {
                random.NextBytes(filler);
                store.SegmentWriter.Append(filler);
            }
        }

        Assert.Equal(3, coordsList.Select(c => c.SegmentId).Distinct().Count());

        var dbPath = Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        var oldestSegmentPath = SegmentFormat.SegmentPath(store.Directory, coordsList[0].SegmentId);
        var secondSegmentPath = SegmentFormat.SegmentPath(store.Directory, coordsList[1].SegmentId);
        Assert.True(File.Exists(oldestSegmentPath));

        // Cap set so that evicting only the single oldest (~64 MB, forced full by the roll
        // loop above) segment is just enough to satisfy it - totalBytes - oldestSize is
        // comfortably under this, but totalBytes itself is comfortably over it.
        var totalBytes = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var oldestSegmentSize = new FileInfo(oldestSegmentPath).Length;
        var capBytes = totalBytes - (oldestSegmentSize * 3 / 4);

        using var retention = new RetentionService(
            _root, store, capBytes, retentionDays: 3650, timeProvider: time,
            interval: TimeSpan.FromHours(1));
        retention.RunOnce();

        Assert.False(File.Exists(oldestSegmentPath), "the oldest sealed segment should have been deleted");
        Assert.True(File.Exists(secondSegmentPath), "only the oldest segment should have been evicted");

        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1, await CountEvictedRowsAsync(dbPath, Ct));
        Assert.Equal(3, await CountRowsAsync(dbPath, Ct)); // rows are marked, never deleted

        await SqliteTestHelpers.AssertFtsIntegrityAsync(dbPath, Ct);
    }

    [Fact]
    public void EnforceCap_never_evicts_the_segment_the_live_writer_is_still_appending_to()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        store.SegmentWriter.Append("small"u8.ToArray()); // stays in the pending buffer - only one segment exists

        using var retention = new RetentionService(
            _root, store, capBytes: 1, retentionDays: 3650, timeProvider: time, // impossibly small cap
            interval: TimeSpan.FromHours(1));
        retention.RunOnce();

        Assert.True(File.Exists(SegmentFormat.SegmentPath(store.Directory, store.CurrentSegmentId)));
    }

    [Fact]
    public async Task A_non_current_day_with_no_segments_left_has_its_db_dropped_too()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        var coords = store.SegmentWriter.Append("only message"u8.ToArray());
        store.SegmentWriter.Append(new byte[SegmentFormat.BlockSize]); // force it to disk
        await WriteOneMessageAsync(store, coords.SegmentId, coords.Offset, coords.Length, Ct);

        var oldDay = store.CurrentDay;
        var oldDir = store.Directory;

        time.Set(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();
        await SqliteTestHelpers.WaitForRolloverSealAsync(_root, oldDay, Ct);

        // Simulate every segment in the old day having already been evicted by an earlier
        // cap-enforcement pass, without needing to actually fill the store to force it.
        foreach (var segment in Directory.EnumerateFiles(oldDir, "*.seg"))
        {
            File.Delete(segment);
        }

        Assert.True(File.Exists(Path.Combine(oldDir, $"{oldDay}.db")));

        using var retention = new RetentionService(
            _root, store, capBytes: long.MaxValue, retentionDays: 3650, timeProvider: time,
            interval: TimeSpan.FromHours(1));
        retention.RunOnce();

        Assert.False(Directory.Exists(oldDir));
    }

    private static async Task<long> CountEvictedRowsForSegmentAsync(
        string dbPath, int segmentId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE segment_id = $segment AND flags & 4 != 0";
        command.Parameters.AddWithValue("$segment", segmentId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// The coupling that motivates the guards. A history browse, or a deep scan
    /// (<see cref="Search.DeepScanner"/>) walking every day file on disk, holds
    /// <see cref="SegmentReader"/> handles opened <c>FileShare.ReadWrite</c> — which on Windows
    /// does not admit a delete. Retention has to read that as "not now" and keep going. Before
    /// the guards the <see cref="IOException"/> escaped <see cref="RetentionService.RunOnce"/>,
    /// faulted the background loop task, and stopped retention for the rest of the session with
    /// no symptom other than a store growing past its cap.
    /// </summary>
    [Fact]
    public async Task An_expired_day_still_open_for_reading_defers_instead_of_faulting_the_pass()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        var coords = store.SegmentWriter.Append("old day"u8.ToArray());
        store.SegmentWriter.Append(new byte[SegmentFormat.BlockSize]); // force it to disk
        await WriteOneMessageAsync(store, coords.SegmentId, coords.Offset, coords.Length, Ct);

        var oldDay = store.CurrentDay;
        var oldDir = store.Directory;
        var lockedSegment = SegmentFormat.SegmentPath(oldDir, coords.SegmentId);

        time.Set(new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();
        await SqliteTestHelpers.WaitForRolloverSealAsync(_root, oldDay, Ct);

        using var retention = new RetentionService(
            _root, store, capBytes: long.MaxValue, retentionDays: 14, timeProvider: time,
            interval: TimeSpan.FromHours(1));

        var reader = new SegmentReader(oldDir);
        try
        {
            // SegmentReader opens its handles lazily, so read something back to make it
            // actually hold one — constructing it alone locks nothing.
            var header = new MessageHeader(
                sequence: 1, enqueuedTicks: 0, rowId: 1, segmentId: coords.SegmentId,
                offset: coords.Offset, length: coords.Length, subjectId: 0,
                correlationInternId: 0, partition: 0, flags: MessageFlags.None);
            Assert.False((await reader.ReadAsync(header, Ct)).IsEmpty);

            retention.RunOnce(); // must not throw

            Assert.True(File.Exists(lockedSegment), "the segment being read must survive the blocked pass");
        }
        finally
        {
            reader.Dispose();
        }

        // Deletion is deferred, not abandoned: the day is still expired, so the next pass
        // finishes it. A pass blocked partway through can leave a day directory that has lost
        // its database, which SessionLayout.ListDayDirectories already anticipates and still
        // enumerates — which is exactly why retrying converges.
        retention.RunOnce();
        Assert.False(Directory.Exists(oldDir), "the day should be deleted once the reader releases it");
    }

    /// <summary>
    /// Cap enforcement has to move past a candidate it cannot delete rather than retry it. Its
    /// loop runs until total bytes drop under the cap, so a locked oldest segment that reported
    /// success would be retried forever against a total that never falls — a hung retention
    /// thread rather than a faulted one.
    /// </summary>
    [Fact]
    public async Task Cap_enforcement_skips_a_locked_segment_and_evicts_the_next_candidate()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        // Same three-distinct-segment setup as the plain cap test above.
        var random = new Random(7);
        var coordsList = new List<(int SegmentId, int Offset, int Length)>();
        for (var i = 0; i < 3; i++)
        {
            var coords = store.SegmentWriter.Append(Encoding.UTF8.GetBytes($"message {i}"));
            coordsList.Add(coords);
            await WriteOneMessageAsync(store, coords.SegmentId, coords.Offset, coords.Length, Ct);

            var startingSegment = store.SegmentWriter.CurrentSegmentId;
            var filler = new byte[SegmentFormat.BlockSize];
            while (store.SegmentWriter.CurrentSegmentId == startingSegment)
            {
                random.NextBytes(filler);
                store.SegmentWriter.Append(filler);
            }
        }

        Assert.Equal(3, coordsList.Select(c => c.SegmentId).Distinct().Count());

        var dbPath = Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        var oldestSegmentPath = SegmentFormat.SegmentPath(store.Directory, coordsList[0].SegmentId);
        var secondSegmentPath = SegmentFormat.SegmentPath(store.Directory, coordsList[1].SegmentId);

        // Sized against the second segment, since that is the one this pass can actually evict.
        var totalBytes = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var capBytes = totalBytes - (new FileInfo(secondSegmentPath).Length * 3 / 4);

        using var retention = new RetentionService(
            _root, store, capBytes, retentionDays: 3650, timeProvider: time,
            interval: TimeSpan.FromHours(1));

        // FileShare.ReadWrite is the exact share mode SegmentReader opens with, so this holds
        // the file the same way a live browse or deep scan does.
        using (File.OpenHandle(oldestSegmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            retention.RunOnce();
        }

        Assert.True(File.Exists(oldestSegmentPath), "the locked segment must survive");
        Assert.False(File.Exists(secondSegmentPath), "the next candidate should have been evicted instead");

        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The locked segment's rows must not be flagged: its bytes are still on disk and still
        // readable, so a PayloadEvicted flag would be the row lying about itself.
        Assert.Equal(0, await CountEvictedRowsForSegmentAsync(dbPath, coordsList[0].SegmentId, Ct));
        Assert.Equal(1, await CountEvictedRowsForSegmentAsync(dbPath, coordsList[1].SegmentId, Ct));
        Assert.Equal(3, await CountRowsAsync(dbPath, Ct)); // rows are marked, never deleted

        await SqliteTestHelpers.AssertFtsIntegrityAsync(dbPath, Ct);
    }

    /// <summary>
    /// The guards exist for the background loop, not for <see cref="RetentionService.RunOnce"/>,
    /// so this drives the real timer rather than calling the pass directly.
    /// <see cref="SettableTimeProvider"/> fakes only the clock, not <c>CreateTimer</c>, so the
    /// interval below is real wall-clock time while the age cutoff stays on the fake clock.
    /// </summary>
    [Fact]
    public async Task The_background_loop_keeps_running_after_a_pass_it_could_not_complete()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);

        var coords = store.SegmentWriter.Append("old day"u8.ToArray());
        store.SegmentWriter.Append(new byte[SegmentFormat.BlockSize]); // force it to disk
        await WriteOneMessageAsync(store, coords.SegmentId, coords.Offset, coords.Length, Ct);

        var oldDay = store.CurrentDay;
        var oldDir = store.Directory;
        var lockedSegment = SegmentFormat.SegmentPath(oldDir, coords.SegmentId);

        time.Set(new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();
        await SqliteTestHelpers.WaitForRolloverSealAsync(_root, oldDay, Ct);

        using var retention = new RetentionService(
            _root, store, capBytes: long.MaxValue, retentionDays: 14, timeProvider: time,
            interval: TimeSpan.FromMilliseconds(50));

        // Several ticks all fail while the handle is held. Before the guards, the first of them
        // faulted the loop task and no tick after it ever ran again.
        using (File.OpenHandle(lockedSegment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await Task.Delay(300, Ct);
            Assert.True(File.Exists(lockedSegment));
        }

        // Released: a later tick must still arrive, and finish the job. Polled against a
        // generous deadline rather than a fixed sleep — the claim is that a tick happens at
        // all, not how soon, and a fixed sleep would only make this flaky on a loaded runner.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Directory.Exists(oldDir) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(Directory.Exists(oldDir),
            "the loop must survive a blocked pass and delete the day on a later tick");
    }
}

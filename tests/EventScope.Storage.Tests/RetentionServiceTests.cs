using System.Text;
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
        await Task.Delay(200, Ct); // old day's async seal (see SessionStore's remarks)

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
        await Task.Delay(200, Ct); // old day's async seal

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
}

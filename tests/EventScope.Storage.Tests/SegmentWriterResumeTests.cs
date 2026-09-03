using System.Text;
using EventScope.Core.Models;
using EventScope.Storage.Retention;
using EventScope.Storage.Segments;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>
/// Opening a writer over a directory that already holds segments.
///
/// <para>
/// This is the restart path, and it used to lose data: <see cref="SegmentWriter"/> always opened
/// segment 0 with <c>FileMode.Create</c>, so constructing one over a day directory that already
/// had a <c>000000.seg</c> truncated it. The day's SQLite rows survive and keep pointing into it,
/// which turns an entire earlier capture into rows whose bodies cannot be read — and, once new
/// data is written at the same coordinates, into rows that read back <i>another message's</i>
/// bytes.
/// </para>
///
/// <para>
/// The fix is to resume past whatever is already on disk rather than to reopen segment 0. That
/// also has to hold against retention, which deletes individual segment files while their rows
/// stay in the day file marked evicted: reusing a deleted segment's id would make those rows
/// resolve against new, unrelated bytes.
/// </para>
/// </summary>
public sealed class SegmentWriterResumeTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("eventscope-segment-resume-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static MessageHeader HeaderFor(int segmentId, int offset, int length) =>
        new(sequence: 0, enqueuedTicks: 0, rowId: 0, segmentId: segmentId, offset: offset,
            length: length, subjectId: 0, correlationInternId: 0, partition: 0, flags: MessageFlags.None);

    [Fact]
    public async Task Reopening_a_directory_does_not_destroy_what_an_earlier_writer_left_there()
    {
        var first = Encoding.UTF8.GetBytes("written-by-the-first-writer");

        (int SegmentId, int Offset, int Length) coords;
        using (var writer = new SegmentWriter(_directory))
        {
            coords = writer.Append(first);
        }

        // The restart: a second writer over the same directory, exactly what SessionStore does
        // when the app is reopened on the same UTC day.
        using (var writer = new SegmentWriter(_directory))
        {
            writer.Append(Encoding.UTF8.GetBytes("written-by-the-second-writer"));
        }

        using var reader = new SegmentReader(_directory);
        var bytes = await reader.ReadAsync(HeaderFor(coords.SegmentId, coords.Offset, coords.Length), Ct);

        Assert.Equal(first, bytes.ToArray());
    }

    [Fact]
    public void A_reopened_writer_starts_after_the_highest_segment_already_on_disk()
    {
        using (var writer = new SegmentWriter(_directory))
        {
            Assert.Equal(0, writer.CurrentSegmentId); // a fresh directory still starts at 0
            writer.Append([1, 2, 3]);
        }

        using var resumed = new SegmentWriter(_directory);
        Assert.Equal(1, resumed.CurrentSegmentId);
    }

    [Fact]
    public async Task Both_writers_payloads_are_readable_afterwards()
    {
        var payloads = new List<byte[]>();
        var coords = new List<(int SegmentId, int Offset, int Length)>();

        // Three separate runs over one day directory, the shape of an app opened and closed
        // repeatedly without the UTC day changing.
        for (var run = 0; run < 3; run++)
        {
            using var writer = new SegmentWriter(_directory);
            for (var i = 0; i < 5; i++)
            {
                var payload = Encoding.UTF8.GetBytes($"run-{run}-payload-{i}-{new string('x', 100 + i)}");
                payloads.Add(payload);
                coords.Add(writer.Append(payload));
            }
        }

        // Every run wrote to its own segment, so no run can have overwritten another's bytes.
        Assert.Equal([0, 1, 2], coords.Select(c => c.SegmentId).Distinct().Order().ToArray());

        using var reader = new SegmentReader(_directory);
        for (var i = 0; i < payloads.Count; i++)
        {
            var bytes = await reader.ReadAsync(
                HeaderFor(coords[i].SegmentId, coords[i].Offset, coords[i].Length), Ct);
            Assert.Equal(payloads[i], bytes.ToArray());
        }
    }

    [Fact]
    public void An_explicit_starting_segment_id_still_wins()
    {
        using (var writer = new SegmentWriter(_directory))
        {
            writer.Append([1]);
        }

        using var explicitly = new SegmentWriter(_directory, startingSegmentId: 7);
        Assert.Equal(7, explicitly.CurrentSegmentId);
    }

    [Fact]
    public void A_gap_left_by_a_deleted_segment_does_not_pull_the_next_id_back_into_it()
    {
        // Retention deletes a segment file but leaves its rows in the day file, flagged evicted.
        // Handing that id out again would make those rows resolve against unrelated new bytes.
        using (var writer = new SegmentWriter(_directory))
        {
            writer.Append(new byte[SegmentFormat.BlockSize + 1]); // forces a block, then a roll
        }

        using (var writer = new SegmentWriter(_directory))
        {
            writer.Append([1]);
        }

        var segments = Directory.GetFiles(_directory, "*.seg").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.True(segments.Length >= 2, $"expected at least two segments, saw {segments.Length}");

        File.Delete(SegmentFormat.SegmentPath(_directory, 0)); // retention evicts the oldest

        using var resumed = new SegmentWriter(_directory);
        Assert.NotEqual(0, resumed.CurrentSegmentId);
        Assert.False(File.Exists(SegmentFormat.SegmentPath(_directory, 0)), "segment 0 must stay deleted");
    }

    [Fact]
    public async Task A_session_store_reopened_on_the_same_day_can_still_read_its_earlier_capture()
    {
        // The end-to-end version, and the one that matches how the bug was actually found: the
        // app is closed and reopened without the UTC day changing.
        var payload = Encoding.UTF8.GetBytes("captured-before-the-restart");
        (int SegmentId, int Offset, int Length) coords;
        string day;

        using (var store = new SessionStore(_directory))
        {
            coords = store.SegmentWriter.Append(payload);
            day = store.CurrentDay;
        }

        using var reopened = new SessionStore(_directory);
        Assert.Equal(day, reopened.CurrentDay); // guard: a midnight-straddling run proves nothing here

        var bytes = await reopened.GetOrOpenReader(day)
            .ReadAsync(HeaderFor(coords.SegmentId, coords.Offset, coords.Length), Ct);

        Assert.Equal(payload, bytes.ToArray());
    }

    [Fact]
    public async Task Retention_evicting_a_segment_after_a_restart_does_not_orphan_the_surviving_one()
    {
        // Restart, then let retention run with a cap small enough to evict. The surviving
        // segment's rows must still read; the evicted one's must not come back as something else.
        var beforeRestart = Encoding.UTF8.GetBytes(new string('a', 2048));
        var afterRestart = Encoding.UTF8.GetBytes(new string('b', 2048));

        (int SegmentId, int Offset, int Length) oldCoords;
        (int SegmentId, int Offset, int Length) newCoords;
        string day;

        using (var store = new SessionStore(_directory))
        {
            oldCoords = store.SegmentWriter.Append(beforeRestart);
            day = store.CurrentDay;
        }

        using var reopened = new SessionStore(_directory);
        newCoords = reopened.SegmentWriter.Append(afterRestart);

        // Push the pending block to disk without closing the store — a payload over the 1 MB block
        // size flushes whatever is pending ahead of itself. Retention only ever sees flushed bytes,
        // and so does the reader.
        reopened.SegmentWriter.Append(new byte[SegmentFormat.BlockSize + 1]);

        Assert.NotEqual(oldCoords.SegmentId, newCoords.SegmentId);

        using (var retention = new RetentionService(_directory, reopened, capBytes: 1, retentionDays: 3650))
        {
            retention.RunOnce();
        }

        // The live segment is never evicted, so the post-restart payload survives...
        var stillThere = await reopened.GetOrOpenReader(day)
            .ReadAsync(HeaderFor(newCoords.SegmentId, newCoords.Offset, newCoords.Length), Ct);
        Assert.Equal(afterRestart, stillThere.ToArray());

        // ...and the evicted one reads as gone rather than as somebody else's bytes.
        var evicted = await reopened.GetOrOpenReader(day)
            .ReadAsync(HeaderFor(oldCoords.SegmentId, oldCoords.Offset, oldCoords.Length), Ct);
        Assert.True(evicted.IsEmpty, "an evicted segment must read empty, never as another message's bytes");
    }
}

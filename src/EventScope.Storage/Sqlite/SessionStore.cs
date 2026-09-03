using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using EventScope.Storage.Segments;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// Owns every day's on-disk storage under one root directory. Each day is
/// <c>{root}/{yyyy-MM-dd}/</c>: a segment writer/reader pair plus one SQLite batch writer.
/// <see cref="Writer"/>/<see cref="SegmentWriter"/>/<see cref="SegmentReader"/> always refer
/// to the current day; call <see cref="EnsureCurrentDay"/> before each ingest write so a
/// rollover is never missed.
///
/// <para>
/// <b>Rollover.</b> Both the old and new day's files stay usable across the boundary. The old
/// writer and segment writer are disposed — draining whatever was already queued, then
/// sealing the segment's footer — on a background task rather than inline, so a rollover
/// never stalls ingest into the new day the way the build plan's retention criterion forbids
/// for deletion. The old day's <see cref="SegmentReader"/> is deliberately *not* disposed:
/// the detail pane can still select a row ingested before the rollover, and reads against
/// that day need to keep working — see <see cref="GetOrOpenReader"/>.
/// </para>
///
/// <para>
/// A deviation from the build plan's literal "rollover is a WriteOp on the old writer's
/// queue" phrasing: <see cref="SqliteBatchWriter.Dispose"/> already does exactly that
/// (<c>CompleteAdding</c> then <c>Join</c> drains everything queued before the call, then
/// closes), so a dedicated <c>WriteOp.Rollover</c> case would just reimplement it. Simpler to
/// call <c>Dispose()</c> from a background task than to add a new op type for the same
/// effect.
/// </para>
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly string _rootDirectory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, SegmentReader> _readersByDay = new();
    private readonly List<PinnedField> _pinnedFields;
    private bool _disposed;

    public SessionStore(string rootDirectory, TimeProvider? timeProvider = null, IReadOnlyList<PinnedField>? pinnedFields = null)
    {
        _rootDirectory = rootDirectory;
        _time = timeProvider ?? TimeProvider.System;
        _pinnedFields = pinnedFields is null ? [] : [..pinnedFields];
        CurrentDay = _time.GetUtcNow().ToString("yyyy-MM-dd");
        OpenCurrentDay();
    }

    /// <summary>The root all day directories live under — needed by search, which iterates
    /// every day file rather than just the current one.</summary>
    public string RootDirectory => _rootDirectory;

    public string CurrentDay { get; private set; }
    public string Directory { get; private set; } = null!;
    public SqliteBatchWriter Writer { get; private set; } = null!;
    public SegmentWriter SegmentWriter { get; private set; } = null!;
    public SegmentReader SegmentReader { get; private set; } = null!;

    public int CurrentSegmentId => SegmentWriter.CurrentSegmentId;

    /// <summary>Call before each ingest write. Rolls to a new day file if the clock has moved
    /// past the currently open day; a no-op otherwise.</summary>
    public void EnsureCurrentDay()
    {
        var today = _time.GetUtcNow().ToString("yyyy-MM-dd");
        if (today == CurrentDay) return;

        lock (_gate)
        {
            if (today == CurrentDay) return; // recheck under the lock

            var oldWriter = Writer;
            var oldSegmentWriter = SegmentWriter;

            CurrentDay = today;
            OpenCurrentDay();

            Task.Run(() =>
            {
                oldSegmentWriter.Dispose();
                oldWriter.Dispose();
            });
        }
    }

    /// <summary>Opens (or reuses) a reader for an arbitrary day's segment directory — needed
    /// because a <see cref="Core.Models.MessageHeader"/>'s <c>SegmentId</c>/<c>Offset</c> are
    /// only meaningful within the day directory they were written to (segment ids restart at
    /// 0 every day), so a detail-pane read against a pre-rollover row must resolve the right
    /// day first. Once opened, a reader is kept for this store's whole lifetime; retention
    /// deleting the underlying files makes later reads against it correctly return empty
    /// (see <see cref="SegmentReader"/>'s contract) rather than throw.</summary>
    public SegmentReader GetOrOpenReader(string day) =>
        day == CurrentDay ? SegmentReader : _readersByDay.GetOrAdd(day, OpenReader);

    private SegmentReader OpenReader(string day) => new(System.IO.Path.Combine(_rootDirectory, day));

    /// <summary>Every day directory under the root, oldest first — lexicographic order matches
    /// chronological order for <c>yyyy-MM-dd</c> names.</summary>
    public IReadOnlyList<string> ListDayDirectories() => SessionLayout.ListDayDirectories(_rootDirectory);

    /// <summary>Posts a flags update for one segment's rows in <paramref name="day"/>'s file.
    /// Routes through the live writer if <paramref name="day"/> is the currently active day —
    /// the only writer allowed to touch that connection (build plan §3.6). For an older,
    /// already-sealed day there is no live writer to route through and none of §3.6's
    /// contention concerns apply to a file nothing else is writing to, so this opens a
    /// short-lived direct connection instead.</summary>
    public void EnqueueSetFlags(string day, int segmentId, byte flagsToOr)
    {
        if (day == CurrentDay)
        {
            Writer.Enqueue(new WriteOp.SetFlags(segmentId, flagsToOr));
            return;
        }

        var dbPath = System.IO.Path.Combine(_rootDirectory, day, $"{day}.db");
        if (!File.Exists(dbPath)) return; // already deleted

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET flags = flags | $flags WHERE segment_id = $segmentId";
        command.Parameters.AddWithValue("$flags", flagsToOr);
        command.Parameters.AddWithValue("$segmentId", segmentId);
        command.ExecuteNonQuery();
        connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Deletes a whole day directory — its segments, its <c>.db</c>/<c>.db-wal</c>/
    /// <c>.db-shm</c>, and any open reader for it. Never the current day.</summary>
    public void DeleteDay(string day)
    {
        if (day == CurrentDay)
        {
            throw new InvalidOperationException("Cannot delete the current day's store.");
        }

        if (_readersByDay.TryRemove(day, out var reader))
        {
            reader.Dispose();
        }

        var dir = System.IO.Path.Combine(_rootDirectory, day);
        if (!System.IO.Directory.Exists(dir)) return;

        // Mirrors SqliteBatchWriter.Dispose()'s reasoning: Microsoft.Data.Sqlite pools the
        // native handle by default, so a pooled connection against this day's .db (even one
        // this class never itself opened, e.g. from a short-lived test assertion connection)
        // can leave the file locked on Windows unless every pool is cleared first.
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(dir, recursive: true);
    }

    /// <summary>Adds a pinned JSON-field column, applied to the current day file immediately
    /// (posted through the live writer's queue — build plan §3.6, an ALTER TABLE must never
    /// race a second connection to the same file) and remembered so every future day file
    /// gets it too, applied directly at open. Existing older day files are <i>not</i>
    /// migrated — the column simply won't exist there. Removing a field from
    /// configuration (not implemented here) would work the same way: it stops being applied
    /// to new day files, but this class makes no attempt to drop it from files that already
    /// have it.</summary>
    public void AddPinnedField(PinnedField field)
    {
        if (!PinnedField.IsValidName(field.Name) || !PinnedField.IsValidJsonPath(field.JsonPath))
        {
            throw new ArgumentException($"Invalid pinned field name or JSON path: '{field.Name}' / '{field.JsonPath}'.");
        }

        lock (_gate)
        {
            if (_pinnedFields.Any(f => f.Name == field.Name)) return; // already configured
            _pinnedFields.Add(field);
        }

        Writer.Enqueue(new WriteOp.AddPinnedField(field));
    }

    public IReadOnlyList<PinnedField> PinnedFields => _pinnedFields;

    private void OpenCurrentDay()
    {
        Directory = System.IO.Path.Combine(_rootDirectory, CurrentDay);
        System.IO.Directory.CreateDirectory(Directory);
        Writer = new SqliteBatchWriter(System.IO.Path.Combine(Directory, $"{CurrentDay}.db"), _time, _pinnedFields);
        SegmentWriter = new SegmentWriter(Directory);
        SegmentReader = new SegmentReader(Directory);
        _readersByDay[CurrentDay] = SegmentReader;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SegmentWriter.Dispose();
        Writer.Dispose();

        foreach (var reader in _readersByDay.Values)
        {
            reader.Dispose();
        }
    }
}

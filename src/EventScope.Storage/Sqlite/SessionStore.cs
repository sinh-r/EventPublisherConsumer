using EventScope.Storage.Segments;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// Owns one day's on-disk storage: the segment writer/reader pair and the SQLite batch
/// writer that share one directory, <c>{root}/{yyyy-MM-dd}/</c>. M1b opens today's file only
/// and keeps it open for the process lifetime — day rollover (both writers alive briefly
/// across midnight) is M2.
/// </summary>
public sealed class SessionStore : IDisposable
{
    public SessionStore(string rootDirectory, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var day = time.GetUtcNow().ToString("yyyy-MM-dd");
        Directory = Path.Combine(rootDirectory, day);
        System.IO.Directory.CreateDirectory(Directory);

        Writer = new SqliteBatchWriter(Path.Combine(Directory, $"{day}.db"), time);
        SegmentWriter = new SegmentWriter(Directory);
        SegmentReader = new SegmentReader(Directory);
    }

    public string Directory { get; }
    public SqliteBatchWriter Writer { get; }
    public SegmentWriter SegmentWriter { get; }
    public SegmentReader SegmentReader { get; }

    public void Dispose()
    {
        SegmentWriter.Dispose();
        Writer.Dispose();
        SegmentReader.Dispose();
    }
}

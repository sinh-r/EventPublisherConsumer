using System.Globalization;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// The on-disk shape of a session root, independent of any open writer. A session root holds one
/// directory per UTC day (<c>yyyy-MM-dd</c>), each containing that day's <c>{day}.db</c> and its
/// segment files.
///
/// <para>
/// Split out of <see cref="SessionStore"/> so read-only callers — history browsing and search —
/// can find day files without constructing a store. That constructor opens and creates the
/// current day's writer, which is exactly wrong for a caller that only wants to read what is
/// already there.
/// </para>
/// </summary>
public static class SessionLayout
{
    /// <summary>Every day directory under <paramref name="rootDirectory"/>, oldest first —
    /// lexicographic order matches chronological order for <c>yyyy-MM-dd</c> names. An absent root
    /// is empty, not an error: a connection that has never streamed has no directory yet.
    ///
    /// <para>Only names that actually parse as a day are returned. The base session root holds a
    /// <c>{profileId:N}</c> subdirectory per saved connection alongside its own day directories, and
    /// counting those as days would misreport what is on disk.</para></summary>
    public static IReadOnlyList<string> ListDayDirectories(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory)) return [];

        return Directory.GetDirectories(rootDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsDayName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether a directory name is a <c>yyyy-MM-dd</c> day.</summary>
    public static bool IsDayName(string name) =>
        DateTime.TryParseExact(
            name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The day file for <paramref name="day"/>. Not guaranteed to exist — a day
    /// directory can outlive its database once retention has evicted it.</summary>
    public static string DayDatabasePath(string rootDirectory, string day) =>
        Path.Combine(rootDirectory, day, $"{day}.db");

    /// <summary>The directory holding <paramref name="day"/>'s segment files.</summary>
    public static string DayDirectory(string rootDirectory, string day) =>
        Path.Combine(rootDirectory, day);

    /// <summary>A read-only, unpooled connection string for one day file — the shape every cold
    /// read in this codebase uses, safe to run concurrently with live ingest under WAL.</summary>
    public static string ReadOnlyConnectionString(string dbPath) =>
        $"Data Source={dbPath};Mode=ReadOnly;Pooling=False";

    /// <summary>
    /// The UTC day inferred from a message's enqueued ticks, matching how <see cref="SessionStore"/>
    /// formats its own day strings.
    ///
    /// <para><b>An inference, not the truth.</b> <c>EnqueuedTicks</c> is the broker's timestamp,
    /// while the directory a message was actually written to comes from the writer's clock. Those
    /// agree only while tailing a live topic. Reading a backlog, or a batch straddling midnight,
    /// makes them diverge — and since segment ids restart at 0 each day, looking bytes up under the
    /// wrong day can surface a different message entirely. Prefer the day a row was actually read
    /// from (<c>SearchHit.Day</c>) wherever it is known; use this only where nothing better
    /// exists.</para>
    /// </summary>
    public static string DayFor(long enqueuedTicks) =>
        new DateTime(enqueuedTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd");
}

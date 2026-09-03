using EventScope.App.Connections;
using EventScope.Storage.Sqlite;

namespace EventScope.App.History;

/// <summary>One browsable capture on disk, and the connection it belongs to if that is knowable.</summary>
/// <param name="ProfileId">The connection whose captures these are, or <see langword="null"/> for
/// the shared unnamespaced root — see <see cref="SessionCatalog"/>'s remarks.</param>
/// <param name="DisplayName">What the picker shows.</param>
/// <param name="Note">A qualifier the user needs in order to read the entry correctly, or empty.</param>
public sealed record SessionEntry(
    Guid? ProfileId,
    string DisplayName,
    string RootDirectory,
    int DayCount,
    string? NewestDay,
    string Note = "");

/// <summary>
/// Finds the captures that exist on disk, whether or not the connection that produced them still
/// exists in the connection manager.
///
/// <para>
/// <b>The shared root is named, not hidden.</b> Captures live under
/// <c>%LOCALAPPDATA%\EventScope\sessions</c>; each saved connection gets its own
/// <c>{profileId:N}</c> subdirectory, but the built-in Fake source, the env-var path, and every
/// session written before that namespacing existed all write day directories straight into the
/// base root. Those are commingled and cannot be told apart after the fact, so this lists the base
/// root as a single entry that says exactly that, rather than silently presenting one connection's
/// history as another's.
/// </para>
///
/// <para>
/// A capture whose connection has since been deleted still appears, identified by its id. Losing
/// access to data you captured because you tidied up a connection entry would be the wrong
/// behaviour for a debugging tool.
/// </para>
/// </summary>
public static class SessionCatalog
{
    /// <summary>The base directory every capture lives under. Mirrors what
    /// <c>MainWindowViewModel.SessionRootDirectory</c> builds, and is the single definition of it.</summary>
    public static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EventScope",
        "sessions");

    /// <summary>Where a given connection's captures live. <see langword="null"/> and the built-in
    /// Fake source both keep the unnamespaced base path — the layout every session before
    /// per-connection namespacing was already written under, so nothing on disk is orphaned.</summary>
    public static string RootFor(Guid? profileId) =>
        profileId is null || profileId == ConnectionProfile.FakeSourceId
            ? BaseDirectory
            : Path.Combine(BaseDirectory, profileId.Value.ToString("N"));

    /// <summary>
    /// Every capture on disk, newest activity first. Pass the saved connections so profile
    /// directories can be given their real names.
    /// </summary>
    public static IReadOnlyList<SessionEntry> Enumerate(
        IReadOnlyList<ConnectionProfile> savedConnections, string? baseDirectory = null)
    {
        var root = baseDirectory ?? BaseDirectory;
        if (!Directory.Exists(root)) return [];

        var namesById = savedConnections
            .Where(p => p.Id != ConnectionProfile.FakeSourceId)
            .ToDictionary(p => p.Id, p => p.Name);

        var entries = new List<SessionEntry>();

        // The base root itself, if anything ever streamed into it directly.
        var sharedDays = SessionLayout.ListDayDirectories(root);
        if (sharedDays.Count > 0)
        {
            entries.Add(new SessionEntry(
                ProfileId: null,
                DisplayName: "Fake source & legacy sessions",
                RootDirectory: root,
                DayCount: sharedDays.Count,
                NewestDay: sharedDays[^1],
                Note: "The Fake source and any pre-namespacing capture share this folder; they cannot be told apart."));
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var profileId)) continue;

            var days = SessionLayout.ListDayDirectories(directory);
            if (days.Count == 0) continue;

            var known = namesById.TryGetValue(profileId, out var displayName);

            entries.Add(new SessionEntry(
                ProfileId: profileId,
                DisplayName: known ? displayName! : $"(deleted connection) {name[..8]}…",
                RootDirectory: directory,
                DayCount: days.Count,
                NewestDay: days[^1],
                Note: known ? "" : "The connection this was captured with no longer exists."));
        }

        return [.. entries.OrderByDescending(e => e.NewestDay, StringComparer.Ordinal)];
    }
}

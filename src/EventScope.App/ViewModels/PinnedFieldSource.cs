using EventScope.Storage.Sqlite;

namespace EventScope.App.ViewModels;

/// <summary>
/// Where <see cref="DetailPaneViewModel"/> reads a row's pinned-field values from: the session
/// root directory whose day files hold them, plus the fields configured for that session.
///
/// <para>
/// Deliberately not a <see cref="SessionStore"/>, even though that is where both values come
/// from during a live run. <see cref="SessionStore"/>'s constructor opens — and creates — the
/// current day's writer, so taking one here would mean a historical row could not be inspected
/// without first creating an empty day directory and a write handle for a session the user
/// never started. These two members are exactly what the pinned-field lookup already read off
/// the store, and nothing more.
/// </para>
/// </summary>
public sealed record PinnedFieldSource(string RootDirectory, IReadOnlyList<PinnedField> Fields);

using CommunityToolkit.Mvvm.ComponentModel;
using EventScope.App.Connections;
using EventScope.App.Ingest;

namespace EventScope.App.ViewModels;

/// <summary>Per-tab status (UI spec §4.1: "a small status dot — green streaming / grey idle
/// / amber degraded / red error").</summary>
public enum ConnectionTabStatus
{
    Idle,
    Streaming,
    Degraded,
    Error,
}

/// <summary>
/// One tab strip entry (UI spec §4.1). <see cref="MainWindowViewModel"/> keeps exactly one
/// tab's connection live at a time — see its own remarks on why concurrent per-tab pipelines
/// are out of scope for this pass.
/// </summary>
public partial class ConnectionTabViewModel : ObservableObject
{
    public ConnectionProfile Profile { get; private set; }

    public string Name => Profile.Name;

    [ObservableProperty]
    public partial ConnectionTabStatus Status { get; set; } = ConnectionTabStatus.Idle;

    // Four booleans rather than a converter, matching the toolbar's own existing status-dot
    // pattern (MainWindow.axaml: two stacked Ellipses, each IsVisible-bound to a plain bool) —
    // consistent with this codebase's preference for declarative multi-element state over a
    // value converter for a small, fixed set of visual states.
    public bool IsIdle => Status == ConnectionTabStatus.Idle;
    public bool IsStreaming => Status == ConnectionTabStatus.Streaming;
    public bool IsDegraded => Status == ConnectionTabStatus.Degraded;
    public bool IsError => Status == ConnectionTabStatus.Error;

    partial void OnStatusChanged(ConnectionTabStatus value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsDegraded));
        OnPropertyChanged(nameof(IsError));
    }

    /// <summary>Drives the tab strip's active-tab underline via a declarative
    /// <c>Classes.active</c> binding (<c>MainWindow.axaml</c>) — the same safe pattern this
    /// codebase already uses for row-state styling, kept off the imperative
    /// <c>Classes.Set</c> path the M1-remainder pass found expensive at scale (not that a
    /// handful of tabs would ever hit that cost, but for consistency). Set by
    /// <see cref="MainWindowViewModel"/> whenever <c>SelectedTab</c> changes — exactly one tab
    /// is ever true at a time.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Set when <see cref="Status"/> is <see cref="ConnectionTabStatus.Error"/> —
    /// drives the warning banner's "broker error text" (UI spec §10's Error state).</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// How far back the next run on this tab should seek before streaming forward.
    ///
    /// <para>
    /// Per tab rather than one shared toolbar field, and not merely for tidiness: a single shared
    /// selection would carry "last 7 days" from one topic to the next tab the user selected, and
    /// the first sign of it would be a week of someone else's traffic already replaying. It is
    /// also deliberately <em>not</em> persisted onto <see cref="ConnectionProfile"/> — a replay
    /// window describes one run, not the connection.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial StartWindow SelectedStartWindow { get; set; } = StartWindow.ConnectionDefault;

    /// <summary>UTC, <c>yyyy-MM-dd HH:mm:ss</c>. Only read when
    /// <see cref="SelectedStartWindow"/> is <see cref="StartWindow.Custom"/>.</summary>
    [ObservableProperty]
    public partial string CustomStartTimestampText { get; set; } = string.Empty;

    /// <summary>Why the picked window could not be used, shown beside the picker. Cleared on
    /// every successful Start.</summary>
    [ObservableProperty]
    public partial string StartWindowError { get; set; } = string.Empty;

    /// <summary>Reveals the absolute-timestamp input. A plain derived bool, matching
    /// <see cref="IsIdle"/> and friends above rather than introducing a converter.</summary>
    public bool IsCustomStartWindow => SelectedStartWindow.IsCustom;

    public bool HasStartWindowError => StartWindowError.Length > 0;

    partial void OnSelectedStartWindowChanged(StartWindow value)
    {
        OnPropertyChanged(nameof(IsCustomStartWindow));

        // A stale complaint about the previous selection would otherwise sit next to a picker that
        // no longer has anything wrong with it.
        StartWindowError = string.Empty;
    }

    partial void OnStartWindowErrorChanged(string value) => OnPropertyChanged(nameof(HasStartWindowError));

    public ConnectionTabViewModel(ConnectionProfile profile)
    {
        Profile = profile;
    }

    /// <summary>Called after <see cref="ConnectionManagerViewModel.Connect"/> moves a
    /// reconnected profile to a new object instance (see that type's remarks on why saved
    /// connections are replaced, not mutated) — keeps an already-open tab pointed at the
    /// current data instead of a stale copy.</summary>
    public void UpdateProfile(ConnectionProfile profile)
    {
        Profile = profile;
        OnPropertyChanged(nameof(Name));
    }
}

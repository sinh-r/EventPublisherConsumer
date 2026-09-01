using CommunityToolkit.Mvvm.ComponentModel;
using EventScope.App.Connections;

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

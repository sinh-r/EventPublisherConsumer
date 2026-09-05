using CommunityToolkit.Mvvm.ComponentModel;

namespace EventScope.App.ViewModels;

/// <summary>
/// Display data for the connection toolbar. Every broker-specific element the toolbar shows
/// binds to one of these capability flags — never a broker-type switch — so adding a broker
/// in M4 costs zero changes here.
/// </summary>
public partial class ConnectionToolbarViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string StatusLabel { get; set; } = "Idle";

    // Defaults true (no warning) until a source is actually connected and reports otherwise —
    // the persistent SQS-style banner should reflect a known capability, not the zero value
    // of an unset bool.
    [ObservableProperty]
    public partial bool CanPeekNonDestructively { get; set; } = true;

    [ObservableProperty]
    public partial bool SupportsPartitions { get; set; }

    /// <summary>Whether this connection can start anywhere but the tail — drives the
    /// replay-window picker's visibility. Unlike the two flags above it is set the moment a tab
    /// is selected (via <see cref="EventScope.App.Ingest.EventSourceFactory.CapabilitiesForAsync"/>)
    /// rather than at Start, because the window has to be picked <em>before</em> the run it
    /// applies to.</summary>
    [ObservableProperty]
    public partial bool SupportsReplay { get; set; }

    [ObservableProperty]
    public partial double MessagesPerSecond { get; set; }

    /// <summary>The selected tab's connection name (UI spec §4.2 source selector) — set
    /// whenever the active tab changes, independent of whether it's currently streaming, so
    /// the toolbar always shows what's selected.</summary>
    [ObservableProperty]
    public partial string ConnectionName { get; set; } = string.Empty;

    /// <summary>The selected tab's topic(s), Kafka only — empty for the Fake source.</summary>
    [ObservableProperty]
    public partial string TopicLabel { get; set; } = string.Empty;

    /// <summary>"partition N", or empty when the connection consumes every partition. A
    /// plain read-only label rather than a live broker-fetched dropdown — see
    /// ConnectionManagerViewModel's own scoping note on <c>EditPartitionText</c>.</summary>
    [ObservableProperty]
    public partial string PartitionLabel { get; set; } = string.Empty;
}

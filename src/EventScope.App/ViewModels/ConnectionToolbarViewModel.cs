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

    [ObservableProperty]
    public partial double MessagesPerSecond { get; set; }
}

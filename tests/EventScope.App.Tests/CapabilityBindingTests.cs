using EventScope.App.Connections;
using EventScope.App.Ingest;
using EventScope.App.ViewModels;
using EventScope.Core.Abstractions;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// M1a-scale smoke test for the rule the build plan makes load-bearing at M4: no
/// <c>if (broker == …)</c> in the view layer, every broker-specific element binds a
/// <see cref="SourceCapabilities"/> flag. The full audit (every capability has a bound UI
/// element, machine-checked) is M4 scope; this only proves the toolbar view model actually
/// carries the flags it will bind to rather than hardcoding them.
/// </summary>
public class CapabilityBindingTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Toolbar_reflects_whatever_capabilities_the_connected_source_reports(
        bool canPeek, bool supportsPartitions)
    {
        var toolbar = new ConnectionToolbarViewModel
        {
            CanPeekNonDestructively = canPeek,
            SupportsPartitions = supportsPartitions,
        };

        Assert.Equal(canPeek, toolbar.CanPeekNonDestructively);
        Assert.Equal(supportsPartitions, toolbar.SupportsPartitions);
    }

    [Fact]
    public void The_replay_picker_binds_a_capability_flag_rather_than_a_broker_test()
    {
        // Unlike the two flags above this one defaults false and is set the moment a tab is
        // selected, because a replay window has to be picked before the run it applies to.
        var toolbar = new ConnectionToolbarViewModel();
        Assert.False(toolbar.SupportsReplay);

        toolbar.SupportsReplay = true;
        Assert.True(toolbar.SupportsReplay);
    }
}

/// <summary>
/// The replay window's per-tab state. Kept on the tab so a window picked for one connection can
/// never carry over to the next tab the user selects — see
/// <see cref="ConnectionTabViewModel.SelectedStartWindow"/>'s own remarks.
/// </summary>
public class ConnectionTabStartWindowTests
{
    private static ConnectionTabViewModel Tab() =>
        new(new ConnectionProfile { Kind = ConnectionKind.Kafka, Name = "orders" });

    [Fact]
    public void A_new_tab_starts_on_the_connection_default_so_nothing_replays_unasked()
    {
        Assert.Same(StartWindow.ConnectionDefault, Tab().SelectedStartWindow);
    }

    [Fact]
    public void Two_tabs_hold_their_windows_independently()
    {
        var orders = Tab();
        var payments = Tab();

        orders.SelectedStartWindow = StartWindow.Presets.Single(w => w.Label == "Last 7 days");

        Assert.Same(StartWindow.ConnectionDefault, payments.SelectedStartWindow);
    }

    [Fact]
    public void Picking_the_custom_entry_reveals_the_timestamp_input()
    {
        var tab = Tab();
        Assert.False(tab.IsCustomStartWindow);

        tab.SelectedStartWindow = StartWindow.Custom;

        Assert.True(tab.IsCustomStartWindow);
    }

    [Fact]
    public void Changing_the_window_clears_a_complaint_about_the_previous_one()
    {
        // A stale error would otherwise sit beside a picker that no longer has anything wrong.
        var tab = Tab();
        tab.SelectedStartWindow = StartWindow.Custom;
        tab.StartWindowError = "Start timestamp must be in the past.";
        Assert.True(tab.HasStartWindowError);

        tab.SelectedStartWindow = StartWindow.Presets.Single(w => w.Label == "Last 7 days");

        Assert.Empty(tab.StartWindowError);
        Assert.False(tab.HasStartWindowError);
    }

    [Fact]
    public void The_derived_flags_raise_change_notifications_so_the_view_follows()
    {
        var tab = Tab();
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.SelectedStartWindow = StartWindow.Custom;
        tab.StartWindowError = "nope";

        Assert.Contains(nameof(ConnectionTabViewModel.IsCustomStartWindow), raised);
        Assert.Contains(nameof(ConnectionTabViewModel.HasStartWindowError), raised);
    }
}

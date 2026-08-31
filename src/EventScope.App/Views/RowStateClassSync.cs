using Avalonia.Controls;
using EventScope.App.ViewModels;

namespace EventScope.App.Views;

/// <summary>
/// Applies a realized <see cref="DataGridRow"/>'s <c>large</c>/<c>evicted</c>/<c>deadLettered</c>
/// classes from its <see cref="MessageRowViewModel"/> at <c>DataGrid.LoadingRow</c>.
///
/// <para>
/// <b>A known, accepted limitation, not fixed here.</b> <see cref="Collections.MessageRowsView"/>'s
/// follow-mode steady state repopulates an already-realized row's same view model instance in
/// place (see its remarks) without a fresh <c>LoadingRow</c>, so these classes can go stale —
/// a row styled <c>large</c> can keep that styling after being repopulated with an ordinary
/// message. A fix subscribing to the view model's <c>PropertyChanged</c> for the row's loaded
/// lifetime was built and measured: at 10k msg/s it made the M1 heap-growth acceptance
/// criterion measurably worse (~290–340 MB growth over 60s, vs. ~55–94 MB without it, plus a
/// reintroduced shutdown delay) even after filtering the handler to the three relevant
/// properties — the cost is in Avalonia's per-<c>Classes.Set</c> style re-evaluation, not
/// invocation count. Reverted rather than shipped: this cosmetic staleness (only visible
/// during the narrow window between a row's flags changing and it next being scrolled
/// off/on-screen) is a much smaller problem than a 4–6x regression on a criterion this pass
/// had just fixed. See PROGRESS.md's heap-growth-investigation follow-up for the numbers.
/// A cheaper fix, not attempted this pass, is a declarative <c>Classes.large="{Binding
/// IsLarge}"</c> binding placed directly on template content — proven safe here for the SIZE
/// column's cell (see <c>MainWindow.axaml</c>) — but <c>DataGridRow</c> itself isn't
/// user-templated, so the same approach doesn't directly apply at the row level.
/// </para>
///
/// <para>
/// Extracted out of <see cref="MainWindow"/> only for a small amount of testability/reuse —
/// this is otherwise unchanged from the inline code-behind this pass found it in.
/// </para>
/// </summary>
public sealed class RowStateClassSync
{
    public void OnLoadingRow(DataGridRow row, MessageRowViewModel? vm)
    {
        if (vm is null) return;
        ApplyClasses(row, vm);
    }

    private static void ApplyClasses(DataGridRow row, MessageRowViewModel vm)
    {
        row.Classes.Set("large", vm.IsLarge);
        row.Classes.Set("evicted", vm.IsEvicted);
        row.Classes.Set("deadLettered", vm.IsDeadLettered);
    }
}

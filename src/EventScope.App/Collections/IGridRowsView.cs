using System.Collections;
using Avalonia.Collections;
using EventScope.App.ViewModels;

namespace EventScope.App.Collections;

/// <summary>
/// What the message grid binds to, whichever source is driving it: the live ring
/// (<see cref="MessageRowsView"/>) or rows read back off disk (<see cref="HistoryRowsView"/>).
///
/// <para>
/// <b><see cref="IDataGridCollectionView"/> is load-bearing on every implementation.</b> Avalonia's
/// <c>DataGrid</c> wraps any <c>ItemsSource</c> that is not already one in a
/// <c>DataGridCollectionView</c>, whose <c>CopySourceToInternalList</c> eagerly enumerates the
/// entire source — the exact materialization both views exist to avoid. Implementing the marker
/// interface is what stops the wrap; <see cref="IList"/> is what then makes
/// <c>DataGridDataConnection</c> resolve items through the indexer. See
/// <see cref="MessageRowsView"/>'s remarks for the full derivation and the spike that measured it.
/// Any new implementation must carry both, and must be covered by a bind-time indexer-read test.
/// </para>
/// </summary>
public interface IGridRowsView : IList, IReadOnlyList<MessageRowViewModel>, IDataGridCollectionView
{
    /// <summary>Rows currently addressable. Redeclared to disambiguate <see cref="IList"/>'s
    /// <c>Count</c> from <see cref="IReadOnlyCollection{T}"/>'s — both base interfaces declare one,
    /// and the implementations return the same value for each.</summary>
    new int Count { get; }

    /// <summary>Hook to <c>DataGrid.UnloadingRow</c> so the row view model can be recycled.</summary>
    void NotifyRowUnloaded(int index);

    /// <summary>Hook to the view model backing <c>DataGrid.SelectedItem</c> changing — the
    /// selection must never be recycled out from under the detail pane.</summary>
    void SetSelected(MessageRowViewModel? vm);

    /// <summary>The instant search tier: marks currently-realized rows that match, with no
    /// filtering or requery.</summary>
    void SetSearchQuery(string? query);

    /// <summary>Forces a <c>Reset</c> without otherwise changing what is shown.</summary>
    void ForceReset();

    /// <summary>Test/diagnostic instrumentation — counts indexer reads, which is how
    /// virtualization is proven rather than assumed.</summary>
    long IndexerReads { get; }

    void ResetIndexerReadCount();
}

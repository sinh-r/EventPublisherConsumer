using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Collections;
using EventScope.Storage.Search;

namespace EventScope.App.ViewModels;

/// <summary>
/// Drives the search bar. Two tiers wired here (build plan §5 M2): the instant ring filter
/// (<see cref="MessageRowsView.SetSearchQuery"/>, applied on every keystroke with no delay)
/// and body FTS (debounced 150 ms, matching the debounce this codebase already uses
/// elsewhere for inline validation), which reports a match count stamped with whether the
/// index is current. Deep scan (<see cref="DeepScanner"/>) is wired in the backend but has no
/// UI here — its own overlay is Stage 5 per the build plan's own milestone boundary, not M2.
/// Identifier (message-id/correlation-id) search is likewise backend-ready
/// (<see cref="FtsSearchService.SearchIdentifiersAsync"/>) but not exposed by this single
/// search box this pass — a scope selector for it is the same Stage-5-shaped polish as the
/// segmented-control toolbar widget already deferred elsewhere in this codebase.
/// </summary>
public partial class SearchViewModel : ObservableObject
{
    private const int MaxResults = 500;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly MessageRowsView _rows;
    private readonly Func<FtsSearchService?> _searchServiceProvider;
    private CancellationTokenSource? _debounceCts;

    private IReadOnlyList<SearchHit> _lastResults = [];
    private string _lastResultsRoot = string.Empty;
    private string _lastResultsQuery = string.Empty;

    /// <summary>
    /// Raised when the user asks to see the matches themselves rather than just their count —
    /// carries the hits, the session root they came from (needed to read their payloads back), and
    /// a label for the banner.
    ///
    /// <para>
    /// This is a separate, explicit gesture rather than something the debounced search does on
    /// every keystroke. FTS searches every day file on disk, so results routinely span past
    /// sessions; swapping the grid out from under a live stream as you type would be hostile.
    /// The instant ring tier keeps behaving exactly as it did.
    /// </para>
    /// </summary>
    public event Action<IReadOnlyList<SearchHit>, string, string>? ResultsRequested;

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>Empty when there's no active query. Otherwise one of "N matches",
    /// "500+ matches" (the cap was hit), or "not connected" (no session yet to search).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Whether the last search's results are known current — the FTS index might be
    /// behind under sustained high-throughput ingest (see PROGRESS.md's step 5 finding: a
    /// real run left index lag in the hundreds of thousands of rows during active streaming).
    /// <see langword="true"/> until a search actually runs and finds otherwise.</summary>
    [ObservableProperty]
    public partial bool IndexIsCurrent { get; set; } = true;

    public SearchViewModel(MessageRowsView rows, Func<FtsSearchService?> searchServiceProvider)
    {
        _rows = rows;
        _searchServiceProvider = searchServiceProvider;
    }

    /// <summary>The matches from the last completed search, oldest first.</summary>
    public IReadOnlyList<SearchHit> LastResults => _lastResults;

    public bool HasResults => _lastResults.Count > 0;

    /// <summary>Opens the last search's matches in the message grid. Enabled only once a search has
    /// actually returned something — the count in the status text is what tells the user it is
    /// worth pressing.</summary>
    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ShowResults() =>
        ResultsRequested?.Invoke(_lastResults, _lastResultsRoot, $"matches for “{_lastResultsQuery}”");

    partial void OnQueryChanged(string value)
    {
        // Instant tier: no delay, applies to whatever's already realized on screen.
        _rows.SetSearchQuery(value);

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        if (string.IsNullOrEmpty(value))
        {
            _debounceCts = null;
            StatusText = string.Empty;
            IndexIsCurrent = true;
            _lastResults = [];
            ShowResultsCommand.NotifyCanExecuteChanged();
            return;
        }

        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = RunFtsSearchAsync(value, cts);
    }

    private async Task RunFtsSearchAsync(string query, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer keystroke
        }

        var service = _searchServiceProvider();
        if (service is null)
        {
            StatusText = "not connected";
            return;
        }

        IsSearching = true;
        try
        {
            var hits = new List<SearchHit>();
            long? hwm = null;
            await foreach (var hit in service.SearchBodyAsync(query, MaxResults, cts.Token).ConfigureAwait(true))
            {
                hits.Add(hit);
                hwm ??= hit.IndexHwm; // only the newest day's hwm - early exit means older days may never open
            }

            if (cts.IsCancellationRequested) return;

            // FTS returns newest-first (newest day, then id DESC). The grid reads oldest-first in
            // every other mode, so reverse here rather than silently inverting the reading
            // direction when the user opens the results.
            hits.Reverse();
            _lastResults = hits;
            _lastResultsRoot = service.RootDirectory;
            _lastResultsQuery = query;
            ShowResultsCommand.NotifyCanExecuteChanged();

            var count = hits.Count;
            StatusText = count >= MaxResults ? $"{MaxResults}+ matches" : $"{count} match{(count == 1 ? "" : "es")}";

            // Compares the searched day's hwm against total rows this session has ingested so
            // far - an approximation (a prior session's rows aren't counted), but the one
            // build plan §3.4 actually calls for: whether an ingest burst has left FTS behind
            // right now. A search with zero hits has no hwm to compare and leaves this as it
            // last was, rather than asserting "current" on no evidence.
            if (hwm is { } value)
            {
                IndexIsCurrent = value >= _rows.TotalAppended;
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        finally
        {
            IsSearching = false;
        }
    }
}

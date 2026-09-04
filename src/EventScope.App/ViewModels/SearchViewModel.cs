using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Collections;
using EventScope.Core.Ingest;
using EventScope.Storage.Search;

namespace EventScope.App.ViewModels;

/// <summary>
/// Drives the search bar. Three tiers wired here (build plan §5 M2): the instant ring filter
/// (<see cref="MessageRowsView.SetSearchQuery"/>, applied on every keystroke with no delay);
/// body FTS (debounced 150 ms, matching the debounce this codebase already uses elsewhere for
/// inline validation), which reports a match count stamped with whether the index is current;
/// and deep scan (<see cref="DeepScanner"/>), which reads every full body off disk and so finds
/// what FTS structurally cannot — a term past the 2 KB indexed prefix, or anything at all while
/// the index is behind.
///
/// <para>
/// Deep scan is never debounced. It is minutes of disk work over the whole session root, so it
/// runs only when explicitly asked for, reports progress into an overlay (UI spec §7) while it
/// runs, and is cancellable throughout. Identifier (message-id/correlation-id) search is still
/// backend-ready (<see cref="FtsSearchService.SearchIdentifiersAsync"/>) but not exposed by this
/// single search box: a scope selector for it needs the segmented control from UI spec §9's
/// component inventory, which is Stage 5 polish and does not exist yet.
/// </para>
/// </summary>
public partial class SearchViewModel : ObservableObject
{
    private const int MaxResults = 500;

    /// <summary>Deep scan's own cap, higher than <see cref="MaxResults"/>: it is the tier people
    /// reach for when FTS came back short, so truncating it as aggressively would defeat the
    /// point of having waited for it.</summary>
    private const int MaxDeepResults = 5000;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly MessageRowsView _rows;
    private readonly Func<FtsSearchService?> _searchServiceProvider;
    private readonly IUiTicker _deepScanTicker;
    private CancellationTokenSource? _debounceCts;

    private IReadOnlyList<SearchHit> _lastResults = [];
    private string _lastResultsRoot = string.Empty;
    private string _lastResultsQuery = string.Empty;

    // Written by the scanning thread on every row, read by the UI ticker every 60 ms. Three
    // separate longs rather than one struct field so neither side needs a lock on a path that
    // runs millions of times: a reader can see a byte count from one row alongside a match
    // count from the next, which for a progress display is invisible and self-correcting.
    private long _deepBytesScanned;
    private long _deepTotalBytes;
    private long _deepMatches;
    private readonly Stopwatch _deepScanClock = new();

    /// <summary>
    /// Raised when the user asks to see the matches themselves rather than just their count —
    /// carries the hits, the session root they came from (needed to read their payloads back), and
    /// a label for the banner.
    ///
    /// <para>
    /// For the FTS tier this is a separate, explicit gesture rather than something the debounced
    /// search does on every keystroke: FTS searches every day file on disk, so results routinely
    /// span past sessions, and swapping the grid out from under a live stream as you type would
    /// be hostile. A deep scan raises it on its own when it finishes, because starting one is
    /// already that explicit gesture. The instant ring tier keeps behaving exactly as it did.
    /// </para>
    /// </summary>
    public event Action<IReadOnlyList<SearchHit>, string, string>? ResultsRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeepScanCommand))]
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

    /// <summary>Drives the deep-search overlay's visibility (UI spec §7).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeepScanCommand))]
    public partial bool IsDeepScanning { get; set; }

    /// <summary>The query the running scan was started with — the overlay names it, and it can
    /// differ from <see cref="Query"/> by the time the scan finishes.</summary>
    [ObservableProperty]
    public partial string DeepScanQuery { get; set; } = string.Empty;

    /// <summary>0–100, for a determinate <c>ProgressBar</c>'s default range.</summary>
    [ObservableProperty]
    public partial double DeepScanProgressValue { get; set; }

    /// <summary>The overlay's live counter — UI spec §7's
    /// "Scanned 412 MB of 1.84 GB · 87 matches · 3.2s elapsed".</summary>
    [ObservableProperty]
    public partial string DeepScanStatusText { get; set; } = string.Empty;

    public SearchViewModel(
        MessageRowsView rows,
        Func<FtsSearchService?> searchServiceProvider,
        IUiTicker deepScanTicker)
    {
        _rows = rows;
        _searchServiceProvider = searchServiceProvider;
        _deepScanTicker = deepScanTicker;
        _deepScanTicker.Tick += RefreshDeepScanDisplay;
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

    private bool CanDeepScan => !IsDeepScanning && !string.IsNullOrWhiteSpace(Query);

    /// <summary>
    /// Reads every message body in the session root looking for the query as a substring. Minutes
    /// of work where FTS takes milliseconds, so it publishes its results itself when it finishes
    /// rather than making the user press anything else, and reports into the overlay throughout.
    ///
    /// <para>
    /// A cancelled scan still publishes what it found. Cancelling is how someone says "that's
    /// enough", not "throw that away", and the banner label says the results are partial.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeepScan), IncludeCancelCommand = true)]
    private async Task DeepScanAsync(CancellationToken cancellationToken)
    {
        var service = _searchServiceProvider();
        if (service is null)
        {
            StatusText = "not connected";
            return;
        }

        var query = Query;
        var root = service.RootDirectory;

        DeepScanQuery = query;
        Volatile.Write(ref _deepBytesScanned, 0);
        Volatile.Write(ref _deepTotalBytes, 0);
        Volatile.Write(ref _deepMatches, 0);
        DeepScanProgressValue = 0;
        DeepScanStatusText = "Measuring…"; // the denominator is summed before the first row is read
        _deepScanClock.Restart();
        IsDeepScanning = true;
        _deepScanTicker.Start();

        var hits = new List<SearchHit>();
        var cancelled = false;
        try
        {
            // ConfigureAwait(true) to stay on the UI thread between yields, matching this
            // file's FTS path. DeepScanner's own awaits are ConfigureAwait(false), so the
            // decompression and substring search stay on the thread pool either way.
            await foreach (var hit in DeepScanner
                .ScanAsync(root, query, MaxDeepResults, new ProgressRelay(this), cancellationToken)
                .ConfigureAwait(true))
            {
                hits.Add(hit);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            _deepScanTicker.Stop();
            _deepScanClock.Stop();
            RefreshDeepScanDisplay(); // one last update, so the overlay's final numbers are real
            IsDeepScanning = false;
        }

        // DeepScanner yields newest day first and newest row first within a day, the same
        // traversal FTS uses. The grid reads oldest-first in every other mode, so order them
        // that way rather than silently inverting the reading direction when they open — the
        // same correction the FTS path makes with its Reverse().
        hits.Sort(static (left, right) =>
        {
            var byDay = string.CompareOrdinal(left.Day, right.Day);
            return byDay != 0 ? byDay : left.MessageRowId.CompareTo(right.MessageRowId);
        });

        _lastResults = hits;
        _lastResultsRoot = root;
        _lastResultsQuery = query;
        ShowResultsCommand.NotifyCanExecuteChanged();

        var suffix = hits.Count >= MaxDeepResults ? "+" : string.Empty;
        StatusText = $"{hits.Count}{suffix} deep match{(hits.Count == 1 ? "" : "es")}";

        var label = cancelled
            ? $"partial deep scan for “{query}”"
            : $"deep scan for “{query}”";
        ResultsRequested?.Invoke(hits, root, label);
    }

    /// <summary>Pulls the latest counters onto the bound properties. Driven by the 60 ms UI
    /// ticker rather than by the scan itself: a scan reports once per message, and posting each
    /// of those to the dispatcher would flood it with millions of updates to render a bar that
    /// moves a pixel at a time.</summary>
    private void RefreshDeepScanDisplay()
    {
        var scanned = Volatile.Read(ref _deepBytesScanned);
        var total = Volatile.Read(ref _deepTotalBytes);
        var matches = Volatile.Read(ref _deepMatches);

        DeepScanProgressValue = total <= 0 ? 0d : Math.Clamp(scanned * 100d / total, 0d, 100d);
        DeepScanStatusText =
            $"Scanned {FormatBytes(scanned)} of {FormatBytes(total)} · " +
            $"{matches} match{(matches == 1 ? "" : "es")} · " +
            $"{_deepScanClock.Elapsed.TotalSeconds:0.0}s elapsed";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
    };

    /// <summary>Stores the scan's reports into plain fields instead of marshalling each one to
    /// the UI thread the way <see cref="Progress{T}"/> would. See
    /// <see cref="RefreshDeepScanDisplay"/> for why that matters at this call rate.</summary>
    private sealed class ProgressRelay(SearchViewModel owner) : IProgress<DeepScanProgress>
    {
        public void Report(DeepScanProgress value)
        {
            Volatile.Write(ref owner._deepBytesScanned, value.BytesScanned);
            Volatile.Write(ref owner._deepTotalBytes, value.TotalBytes);
            Volatile.Write(ref owner._deepMatches, value.Matches);
        }
    }

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

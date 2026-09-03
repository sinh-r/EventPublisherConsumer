using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Collections;
using EventScope.App.Connections;
using EventScope.App.History;
using EventScope.Storage.Search;
using EventScope.Storage.Segments;

namespace EventScope.App.ViewModels;

/// <summary>One captured day as the picker lists it.</summary>
public sealed record DayEntry(string Day, long RowCount)
{
    public string CountLabel => RowCount == 0 ? "empty" : $"{RowCount:N0} messages";
}

/// <summary>
/// Drives browsing already-captured sessions: which capture, which day, and the grid of rows read
/// back off disk.
///
/// <para>
/// Deliberately independent of the ingest pipeline and of <c>SessionStore</c>. Browsing must work
/// with no connection started — that is most of the point — and constructing a
/// <c>SessionStore</c> would create today's directory and take a write handle on a session the
/// user never ran. Everything here goes through <see cref="HistoryQueryService"/> and
/// <see cref="HistoryPayloadReaders"/>, both of which need only a path.
/// </para>
/// </summary>
public partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<ConnectionProfile>> _savedConnectionsProvider;
    private readonly string? _baseDirectory;
    private HistoryPayloadReaders? _payloadReaders;
    private HistoryQueryService? _query;

    /// <param name="baseDirectory">Overrides where captures are looked for. Production passes
    /// <see langword="null"/> and gets <see cref="SessionCatalog.BaseDirectory"/>; tests point it
    /// at a temporary root.</param>
    public HistoryViewModel(
        Func<IReadOnlyList<ConnectionProfile>> savedConnectionsProvider, string? baseDirectory = null)
    {
        _savedConnectionsProvider = savedConnectionsProvider;
        _baseDirectory = baseDirectory;
    }

    /// <summary>The in-flight day-list load, or a completed task. Selecting a capture kicks the
    /// load off without blocking the UI, so anything that needs the picker populated — a test, or
    /// a caller opening a day programmatically — has to be able to wait for it.</summary>
    public Task DaysLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>The grid's item source while browsing. Bound once and re-pointed at a new page
    /// source as the user opens different days.</summary>
    public HistoryRowsView Rows { get; } = new();

    public ObservableCollection<SessionEntry> Sessions { get; } = [];

    public ObservableCollection<DayEntry> Days { get; } = [];

    [ObservableProperty]
    public partial SessionEntry? SelectedSession { get; set; }

    [ObservableProperty]
    public partial DayEntry? SelectedDay { get; set; }

    /// <summary>What is open, for the history banner. Empty when nothing is open.</summary>
    [ObservableProperty]
    public partial string OpenDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>True once a day or session is actually open in the grid — the toolbar's
    /// "back to live" affordance and the banner both key off this.</summary>
    public bool HasOpenBrowse => Rows.Count > 0 || OpenDescription.Length > 0;

    partial void OnOpenDescriptionChanged(string value) => OnPropertyChanged(nameof(HasOpenBrowse));

    /// <summary>Rescans the disk for captures. Cheap — directory listings only, no day file is
    /// opened until a session is selected.</summary>
    public void RefreshSessions()
    {
        var previous = SelectedSession?.RootDirectory;

        Sessions.Clear();
        foreach (var entry in SessionCatalog.Enumerate(_savedConnectionsProvider(), _baseDirectory))
        {
            Sessions.Add(entry);
        }

        SelectedSession = Sessions.FirstOrDefault(s => s.RootDirectory == previous) ?? Sessions.FirstOrDefault();

        StatusText = Sessions.Count == 0
            ? "Nothing captured yet — start a connection and let it stream."
            : string.Empty;
    }

    partial void OnSelectedSessionChanged(SessionEntry? value) => DaysLoaded = LoadDaysAsync(value);

    private async Task LoadDaysAsync(SessionEntry? session)
    {
        Days.Clear();
        SelectedDay = null;

        if (session is null)
        {
            _query = null;
            return;
        }

        _query = new HistoryQueryService(session.RootDirectory);

        IsLoading = true;
        try
        {
            // Counting rows opens every day file, so it runs off the UI thread - a long-running
            // capture can hold a lot of days.
            var summaries = await _query.ListDaysAsync(CancellationToken.None).ConfigureAwait(true);

            // Newest first: the day you want is almost always the most recent one.
            foreach (var summary in summaries.OrderByDescending(s => s.Day, StringComparer.Ordinal))
            {
                Days.Add(new DayEntry(summary.Day, summary.RowCount));
            }

            SelectedDay = Days.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Opens one day in the grid.</summary>
    [RelayCommand]
    private async Task OpenDayAsync()
    {
        if (_query is null || SelectedDay is null) return;
        await OpenAsync([SelectedDay.Day], SelectedDay.Day).ConfigureAwait(true);
    }

    /// <summary>Opens every day of the selected capture as one continuous list.</summary>
    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        if (_query is null || Days.Count == 0) return;

        var days = Days.Select(d => d.Day).OrderBy(d => d, StringComparer.Ordinal).ToList();
        await OpenAsync(days, $"{days.Count} days").ConfigureAwait(true);
    }

    private async Task OpenAsync(IReadOnlyList<string> days, string label)
    {
        if (_query is null) return;

        IsLoading = true;
        try
        {
            var query = _query;
            var summaries = await Task.Run(
                () => days.Select(query.SummarizeDay).ToList(), CancellationToken.None).ConfigureAwait(true);

            var total = summaries.Sum(s => s.RowCount);
            var source = new DayRangePageSource(query, summaries, label);

            Rows.SetSource(source);

            // Reopening the payload readers per browse is what releases the previous day's file
            // handles - retention cannot delete a day directory on Windows while one is held.
            _payloadReaders?.Dispose();
            _payloadReaders = new HistoryPayloadReaders(query.RootDirectory);

            OpenDescription = $"{SelectedSession?.DisplayName} · {label} · {total:N0} messages";
            StatusText = total == 0 ? "That day has no messages left on disk." : string.Empty;
            BrowseOpened?.Invoke();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Shows an already-materialized result set — search hits — in the same grid.</summary>
    public void ShowResults(IReadOnlyList<SearchHit> results, string rootDirectory, string label)
    {
        Rows.SetSource(new FixedResultsPageSource(results, label));

        _payloadReaders?.Dispose();
        _payloadReaders = new HistoryPayloadReaders(rootDirectory);

        OpenDescription = label;
        StatusText = results.Count == 0 ? "No matches." : string.Empty;
        BrowseOpened?.Invoke();
    }

    /// <summary>The payload reader for a browsed row, resolved against the day the row itself
    /// names rather than a day inferred from its timestamp. See
    /// <see cref="HistoryPayloadReaders"/>'s remarks for why that distinction matters.</summary>
    public Core.Abstractions.IPayloadReader? ReaderFor(MessageRowViewModel? row) =>
        _payloadReaders is null || row is null || row.Day.Length == 0 ? null : _payloadReaders.ForDay(row.Day);

    /// <summary>The pinned-field lookup for the capture currently open.</summary>
    public PinnedFieldSource? PinnedFieldsFor(IReadOnlyList<Storage.Sqlite.PinnedField> configured) =>
        _query is null ? null : new PinnedFieldSource(_query.RootDirectory, configured);

    /// <summary>Raised when a browse is opened, so the shell can switch the grid to it.</summary>
    public event Action? BrowseOpened;

    /// <summary>Closes the browse and releases every day-file handle it held.</summary>
    public void Close()
    {
        Rows.SetSource(null);
        _payloadReaders?.Dispose();
        _payloadReaders = null;
        OpenDescription = string.Empty;
        StatusText = string.Empty;
    }

    public void Dispose() => Close();
}

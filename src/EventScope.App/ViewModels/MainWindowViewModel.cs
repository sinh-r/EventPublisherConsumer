using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Collections;
using EventScope.App.Connections;
using EventScope.App.History;
using EventScope.App.Ingest;
using EventScope.App.Publisher;
using EventScope.App.Settings;
using EventScope.Core.Abstractions;
using EventScope.Storage.Retention;
using EventScope.Storage.Search;
using EventScope.Storage.Sqlite;

namespace EventScope.App.ViewModels;

/// <summary>
/// Owns the ingest pipeline's lifetime, the tab strip, and the window's four workspace
/// regions' view models.
///
/// <para><b>One connection runs at a time.</b> Selecting a different tab stops whichever
/// pipeline is currently running before anything about the new tab starts — see
/// <see cref="HandleTabSwitchAsync"/>. Genuinely concurrent per-tab pipelines would need one
/// <see cref="MessageRowsView"/> ring per tab (~20 MB each per the build plan §3.1) and
/// shared-<see cref="SessionStore"/> write routing under §3.6 collision #1; that is real
/// scope for a later pass, not a side effect of the connection manager landing.</para>
///
/// <para><b>Storage is namespaced per connection.</b> Each non-Fake profile's messages live
/// under their own subdirectory (<see cref="SessionRootDirectory"/>) so switching connections
/// never mixes one topic's messages into another's day files. The Fake source (and the
/// pre-connection-manager env-var path, still reachable via <see cref="EventSourceFactory.Create(ConnectionProfile?)"/>'s
/// <see langword="null"/> case) keeps the exact original unnamespaced path, so existing
/// on-disk sessions from before this pass are not orphaned.</para>
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _statsTimer;
    private IngestPipeline? _pipeline;
    private SessionStore? _sessionStore;
    private RetentionService? _retentionService;
    private IEventSink? _sink;
    private ConnectionTabViewModel? _runningTab;
    private Guid? _activeProfileId;
    private long _lastStatsTotal;
    private DateTime _lastStatsTimeUtc;

    public MessageRowsView Rows { get; } = new();

    /// <summary>Diagnostic accessors for the measurement session (see
    /// <c>MainWindow.Measurement.cs</c>) to correlate heap growth against in-flight
    /// buffering rather than guessing at it.</summary>
    public long CurrentByteBudgetUsed => _pipeline?.ByteBudgetUsed ?? 0;
    public long CurrentByteBudgetLimit => _pipeline?.ByteBudgetLimit ?? 0;
    public int CurrentBatchWriterPending => _sessionStore?.Writer.PendingCount ?? 0;

    public ConnectionToolbarViewModel Toolbar { get; } = new();

    public StatusBarViewModel StatusBar { get; } = new();

    public DetailPaneViewModel Detail { get; } = new();

    public SearchViewModel Search { get; }

    public SettingsViewModel Settings { get; }

    public PublisherViewModel Publisher { get; }

    public ConnectionManagerViewModel ConnectionManager { get; }

    /// <summary>Browsing already-captured sessions. Independent of the ingest pipeline — history
    /// must be readable with no connection started.</summary>
    public HistoryViewModel History { get; }

    /// <summary>Tab strip entries (UI spec §4.1) — one per connection the user has opened
    /// this session, not one per saved connection.</summary>
    public ObservableCollection<ConnectionTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial ConnectionTabViewModel? SelectedTab { get; set; }

    /// <summary>Shown as a full overlay over the workspace (same pattern as the settings
    /// overlay) — the connection manager / launcher (UI spec §6). Auto-opened at cold start
    /// (no measurement mode) so a new connection is discoverable without the user having to
    /// find a menu; the Fake source is auto-selected underneath it regardless, so a user who
    /// closes it immediately and hits Start still gets the exact zero-config behaviour this
    /// app had before the connection manager existed.</summary>
    [ObservableProperty]
    public partial bool IsConnectionManagerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsPublisherOpen { get; set; }

    [ObservableProperty]
    public partial bool IsHistoryOpen { get; set; }

    /// <summary>Which source the message grid is showing. Live ingest keeps running in history
    /// mode — the live ring is pinned rather than stopped, so its "N new messages" counter tells
    /// you what arrived while you were reading, and nothing is lost by looking away.</summary>
    [ObservableProperty]
    public partial GridMode Mode { get; set; }

    /// <summary>What the grid binds to. Both implementations virtualize; see
    /// <see cref="IGridRowsView"/> for why that is load-bearing rather than incidental.</summary>
    public IGridRowsView ActiveRows => Mode == GridMode.Live ? Rows : History.Rows;

    public bool IsHistoryMode => Mode == GridMode.History;

    partial void OnModeChanged(GridMode value)
    {
        if (value == GridMode.History) Rows.Pin();
        else Rows.Unpin();

        OnPropertyChanged(nameof(ActiveRows));
        OnPropertyChanged(nameof(IsHistoryMode));
        RefreshStats();
    }

    /// <summary>UI spec §10's Error state: "Red tab dot, banner with broker error text, retry
    /// button." Recomputed from <see cref="SelectedTab"/>'s own status, including live updates
    /// while that tab stays selected — see <see cref="OnSelectedTabChanged"/>.</summary>
    public bool HasConnectionError => SelectedTab?.Status == ConnectionTabStatus.Error;

    public string ConnectionErrorText => SelectedTab?.ErrorText ?? string.Empty;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void TogglePublisher() => IsPublisherOpen = !IsPublisherOpen;

    [RelayCommand]
    private void ToggleConnectionManager() => IsConnectionManagerOpen = !IsConnectionManagerOpen;

    /// <summary>Opens the history drawer, rescanning what is on disk. Deliberately does not switch
    /// the grid on its own — picking a day is what does that.</summary>
    [RelayCommand]
    private void ToggleHistory()
    {
        IsHistoryOpen = !IsHistoryOpen;
        if (IsHistoryOpen) History.RefreshSessions();
    }

    /// <summary>Returns the grid to the live stream. The browse is closed rather than merely
    /// hidden, so its day-file handles are released — retention cannot delete a day directory on
    /// Windows while one is still open.</summary>
    [RelayCommand]
    private void BackToLive()
    {
        History.Close();
        Mode = GridMode.Live;
        IsHistoryOpen = false;
    }

    /// <summary>"Use as publish template" (build plan §5 M3 step 10): schema-infers a
    /// generator per leaf from the currently selected message's body and opens the publisher
    /// panel on it. A no-op if nothing is selected or the body isn't parseable JSON — this is
    /// a convenience, not a path that should ever throw into the UI.</summary>
    [RelayCommand]
    private void UseSelectedAsTemplate()
    {
        if (Detail.BodyText is not { Length: > 0 } bodyText) return;

        System.Text.Json.Nodes.JsonNode? json;
        try
        {
            json = System.Text.Json.Nodes.JsonNode.Parse(bodyText);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        Publisher.LoadFromConsumedMessage(json);
        IsPublisherOpen = true;
    }

    public MainWindowViewModel()
    {
        // Keyed off the selected tab rather than the live session store: FTS reads day files
        // straight off disk, so search now works on a connection that has never been started
        // this run - which is the whole point of being able to look at past captures.
        Search = new SearchViewModel(
            Rows,
            () => SelectedTab is { } tab ? new FtsSearchService(SessionRootDirectory(tab.Profile.Id)) : null,
            // Its own ticker, started only for the duration of a deep scan: the ingest
            // coalescer's is running whenever a stream is, and a scan can outlive one.
            new DispatcherTimerTicker());
        Settings = new SettingsViewModel(_settings, () => _sessionStore, () => _retentionService);
        Publisher = new PublisherViewModel(sinkProvider: () => _sink ??= EventSinkFactory.Create(SelectedTab?.Profile));

        ConnectionManager = new ConnectionManagerViewModel(ConnectionStore.Load(), profiles => ConnectionStore.Save(profiles));

        // Constructed after ConnectionManager so the saved-connection names it resolves capture
        // directories with are actually available to it.
        History = new HistoryViewModel(() => ConnectionManager.SavedConnections.ToList());
        History.BrowseOpened += () => Mode = GridMode.History;

        // FTS already searches every day file on disk; this is what finally lets those matches be
        // opened rather than only counted.
        Search.ResultsRequested += (hits, root, label) => History.ShowResults(hits, root, label);
        ConnectionManager.ConnectRequested += profile =>
        {
            OpenOrSelectTab(profile);
            IsConnectionManagerOpen = false;
        };

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();

        // Preserves this app's original zero-config behaviour: a fresh launch is immediately
        // ready to stream the Fake source without the user ever touching the connection
        // manager. EVENTSCOPE_MEASURE (see MainWindow.Measurement.cs) skips the launcher
        // overlay entirely — an unattended measurement run must never sit behind a modal.
        OpenOrSelectTab(ConnectionProfile.CreateFakeSource());
        if (Environment.GetEnvironmentVariable("EVENTSCOPE_MEASURE") is null)
        {
            IsConnectionManagerOpen = true;
        }
    }

    /// <summary>Opens a new tab for <paramref name="profile"/>, or re-selects and refreshes an
    /// already-open tab for the same connection (by <see cref="ConnectionProfile.Id"/>) —
    /// reconnecting from the connection manager never creates a duplicate tab.</summary>
    private void OpenOrSelectTab(ConnectionProfile profile)
    {
        var existing = Tabs.FirstOrDefault(t => t.Profile.Id == profile.Id);
        if (existing is not null)
        {
            existing.UpdateProfile(profile);
            SelectedTab = existing;
            return;
        }

        var tab = new ConnectionTabViewModel(profile);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void SelectTab(ConnectionTabViewModel? tab)
    {
        if (tab is not null) SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(ConnectionTabViewModel? tab)
    {
        if (tab is null) return;

        var wasSelected = SelectedTab == tab;
        Tabs.Remove(tab);
        if (wasSelected)
        {
            SelectedTab = Tabs.Count > 0 ? Tabs[0] : null;
        }
    }

    partial void OnSelectedTabChanged(ConnectionTabViewModel? oldValue, ConnectionTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedTabPropertyChanged;
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedTabPropertyChanged;
            newValue.IsSelected = true;
        }

        SyncToolbarToTab(newValue);
        OnPropertyChanged(nameof(HasConnectionError));
        OnPropertyChanged(nameof(ConnectionErrorText));

        if (oldValue == newValue) return;
        _ = HandleTabSwitchAsync(newValue?.Profile.Id);
    }

    private void OnSelectedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionTabViewModel.Status) or nameof(ConnectionTabViewModel.ErrorText))
        {
            OnPropertyChanged(nameof(HasConnectionError));
            OnPropertyChanged(nameof(ConnectionErrorText));
        }
    }

    private void SyncToolbarToTab(ConnectionTabViewModel? tab)
    {
        var profile = tab?.Profile;
        Toolbar.ConnectionName = profile?.Name ?? string.Empty;
        Toolbar.TopicLabel = profile?.Kind == ConnectionKind.Kafka ? profile.Topics : string.Empty;
        Toolbar.PartitionLabel = profile?.Partition is { } p ? $"partition {p}" : string.Empty;
    }

    /// <summary>Stops whatever's currently running (if anything) and, only when the
    /// connection actually changed, tears down the old connection's <see cref="SessionStore"/>
    /// and publish <see cref="IEventSink"/> off the UI thread — <c>SqliteBatchWriter.Dispose</c>'s
    /// <c>Thread.Join()</c> can briefly block, the same reason day rollover already does its
    /// own teardown on a background task (see <c>Docs/PROGRESS.md</c>'s M2 step 4 entry).
    /// Clearing <see cref="_sink"/> here (rather than caching it for the process lifetime, the
    /// M3 original's assumption when only one connection could ever exist) is what stops the
    /// publisher panel from silently publishing to a previous connection's topic after the
    /// user switches tabs — <see cref="PublisherViewModel"/>'s sink provider lazily rebuilds it
    /// against whatever <see cref="SelectedTab"/> is current the next time it's asked. Re-selecting
    /// the *same* tab is a no-op past the stop, so toggling between two already-open tabs of
    /// the same connection never pays a teardown cost it doesn't need.</summary>
    private async Task HandleTabSwitchAsync(Guid? newProfileId)
    {
        if (_pipeline is not null)
        {
            await StopAsync().ConfigureAwait(true);
        }

        if (_activeProfileId == newProfileId) return;
        _activeProfileId = newProfileId;

        var oldStore = _sessionStore;
        var oldRetention = _retentionService;
        var oldSink = _sink;
        _sessionStore = null;
        _retentionService = null;
        _sink = null;

        if (oldStore is not null || oldRetention is not null || oldSink is not null)
        {
            await Task.Run(async () =>
            {
                oldRetention?.Dispose();
                oldStore?.Dispose();
                if (oldSink is not null)
                {
                    await oldSink.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(true);
        }

        RefreshStats();
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (_pipeline is null) Start();
        else _ = StopAsync();
    }

    [RelayCommand]
    private void Retry()
    {
        if (SelectedTab is not { } tab) return;

        tab.Status = ConnectionTabStatus.Idle;
        tab.ErrorText = null;
        _ = RestartAsync();
    }

    private async Task RestartAsync()
    {
        if (_pipeline is not null)
        {
            await StopAsync().ConfigureAwait(true);
        }

        Start();
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Rows.IsPinned) Rows.Unpin();
        else Rows.Pin();

        RefreshStats();
    }

    public void Start()
    {
        if (_pipeline is not null) return;

        var tab = SelectedTab;
        if (tab is null)
        {
            Toolbar.StatusLabel = "No connection selected — open a connection first.";
            return;
        }

        var profile = tab.Profile;

        _sessionStore ??= new SessionStore(
            SessionRootDirectory(profile.Id),
            pinnedFields: _settings.PinnedFields.Select(f => new PinnedField(f.Name, f.JsonPath)).ToList());
        _retentionService ??= new RetentionService(
            SessionRootDirectory(profile.Id), _sessionStore, _settings.RetentionCapBytes, _settings.RetentionDays);

        IEventSource source;
        try
        {
            source = EventSourceFactory.Create(profile);
        }
        catch (NotSupportedException ex)
        {
            tab.Status = ConnectionTabStatus.Error;
            tab.ErrorText = ex.Message;
            Toolbar.StatusLabel = ex.Message;
            return;
        }

        Toolbar.CanPeekNonDestructively = source.Capabilities.CanPeekNonDestructively;
        Toolbar.SupportsPartitions = source.Capabilities.SupportsPartitions;

        // Every IEventSource — regardless of concrete broker — raises errors through the
        // same broker-neutral event, so this subscription needs no type test (build plan §5
        // M4: "no if (broker == …) anywhere in the view layer"). A broker's consume loop may
        // fire from its own dedicated or pooled thread, never guaranteed to be the UI thread,
        // so every property touch below is marshalled.
        var displayName = source.DisplayName;
        source.ErrorOccurred += error => Dispatcher.UIThread.Post(() => OnSourceError(tab, displayName, error));

        _pipeline = new IngestPipeline(
            source,
            Rows,
            new DispatcherTimerTicker(),
            _sessionStore,
            indexedPrefixBytes: _settings.IndexedPrefixBytes);
        _pipeline.Start();

        _runningTab = tab;
        tab.Status = ConnectionTabStatus.Streaming;
        tab.ErrorText = null;

        Toolbar.IsRunning = true;
        Toolbar.StatusLabel = $"Streaming ({displayName})";
        _lastStatsTotal = Rows.TotalAppended;
        _lastStatsTimeUtc = DateTime.UtcNow;
    }

    private void OnSourceError(ConnectionTabViewModel tab, string displayName, SourceError error)
    {
        Toolbar.StatusLabel = $"{displayName} error: {error.Message}";
        tab.Status = error.IsFatal ? ConnectionTabStatus.Error : ConnectionTabStatus.Degraded;
        tab.ErrorText = error.Message;
    }

    /// <summary>Each non-Fake connection gets its own subdirectory, keyed by
    /// <see cref="ConnectionProfile.Id"/>, so two connections' messages never land in the same
    /// day files. <see langword="null"/> (the pre-connection-manager env-var path) and the
    /// built-in Fake source both keep the exact original unnamespaced path — the layout every
    /// session before this pass was already written under — so nothing on disk is orphaned by
    /// this change.</summary>
    private static string SessionRootDirectory(Guid? profileId) => SessionCatalog.RootFor(profileId);

    private async Task StopAsync()
    {
        var pipeline = _pipeline;
        if (pipeline is null) return;

        var tab = _runningTab;
        _pipeline = null;
        _runningTab = null;

        Toolbar.IsRunning = false;
        Toolbar.StatusLabel = "Idle";
        Toolbar.MessagesPerSecond = 0;

        if (tab is not null && tab.Status != ConnectionTabStatus.Error)
        {
            tab.Status = ConnectionTabStatus.Idle;
        }

        await pipeline.DisposeAsync().ConfigureAwait(true);
        RefreshStats();
    }

    public async Task OnSelectedRowChangedAsync(MessageRowViewModel? vm)
    {
        ActiveRows.SetSelected(vm);

        // Pinning is a live-ring concept: it freezes a window that would otherwise scroll out from
        // under the selection. History does not move, so there is nothing to freeze.
        if (vm is not null && Mode == GridMode.Live)
        {
            Rows.Pin();
        }

        // Both modes resolve a payload against the day the row names rather than one inferred from
        // its timestamp - see HistoryPayloadReaders' remarks on why the day has to travel with the
        // row. A browsed row reads through its own day's segments, since the pipeline may not even
        // be running; a live row still reads hot-ring-first, with only the cold fallback pinned to
        // the day the writer filed it under. That is what makes a replayed Kafka backlog - old
        // messages written under today - readable in the live grid.
        var (reader, pinnedFields) = Mode == GridMode.History
            ? (History.ReaderFor(vm), History.PinnedFieldsFor(ConfiguredPinnedFields()))
            : (_pipeline?.ReaderFor(vm?.Day), CurrentPinnedFieldSource());

        await Detail.LoadAsync(vm, reader, pinnedFields).ConfigureAwait(true);
        RefreshStats();
    }

    /// <summary>The pinned fields configured in settings, in the storage layer's shape. The same
    /// projection <see cref="Start"/> hands to a new <see cref="SessionStore"/>.</summary>
    private IReadOnlyList<PinnedField> ConfiguredPinnedFields() =>
        [.. _settings.PinnedFields.Select(f => new PinnedField(f.Name, f.JsonPath))];

    /// <summary>The live session's pinned-field lookup source, or <see langword="null"/>
    /// before a connection has been started. A <see cref="PinnedFieldSource"/> rather than the
    /// <see cref="SessionStore"/> itself so the detail pane never depends on the writer-owning
    /// type — history mode has no store to hand it.</summary>
    private PinnedFieldSource? CurrentPinnedFieldSource() =>
        _sessionStore is null
            ? null
            : new PinnedFieldSource(_sessionStore.RootDirectory, _sessionStore.PinnedFields);

    private void RefreshStats()
    {
        if (_pipeline is not null)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastStatsTimeUtc).TotalSeconds;
            if (elapsed >= 0.2) // avoid a noisy rate over a too-short window between stats ticks
            {
                var total = Rows.TotalAppended;
                Toolbar.MessagesPerSecond = (total - _lastStatsTotal) / elapsed;
                _lastStatsTotal = total;
                _lastStatsTimeUtc = now;
            }
        }

        StatusBar.Update(
            totalAppended: Rows.TotalAppended,
            uiDropped: _pipeline?.UiDropped ?? StatusBar.UiDropped,
            // What the grid is actually showing - in history mode that is the browse, not the ring.
            visibleRowCount: ActiveRows.Count,
            byteBudgetUsed: _pipeline?.ByteBudgetUsed ?? 0,
            byteBudgetLimit: _pipeline?.ByteBudgetLimit ?? 0,
            isPinned: Rows.IsPinned,
            pinnedNewCount: Rows.PinnedNewCount,
            indexLag: _sessionStore?.Writer.IndexLag ?? 0);
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer.Stop();
        if (_pipeline is not null)
        {
            await _pipeline.DisposeAsync().ConfigureAwait(true);
        }

        _retentionService?.Dispose();
        _sessionStore?.Dispose();

        if (_sink is not null)
        {
            await _sink.DisposeAsync().ConfigureAwait(true);
        }
    }
}

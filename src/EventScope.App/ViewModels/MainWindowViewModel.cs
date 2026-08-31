using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.App.Publisher;
using EventScope.App.Settings;
using EventScope.Core.Abstractions;
using EventScope.Storage.Retention;
using EventScope.Storage.Search;
using EventScope.Storage.Sqlite;

namespace EventScope.App.ViewModels;

/// <summary>
/// Owns the ingest pipeline's lifetime and the window's four regions' view models. M1a ran
/// a single always-available <see cref="EventScope.Core.Ingest.FakeEventSource"/> connection over an in-memory
/// payload stand-in; M1b adds a <see cref="SessionStore"/> (segment files + SQLite) that
/// persists across a Start/Stop toggle — stopping the stream doesn't delete what's already
/// on disk, so the store is created once, on first Start, and disposed only when the window
/// closes. The connection manager and multi-connection tabs are Stage 5.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _statsTimer;
    private IngestPipeline? _pipeline;
    private SessionStore? _sessionStore;
    private RetentionService? _retentionService;
    private IEventSink? _sink;
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

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsPublisherOpen { get; set; }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void TogglePublisher() => IsPublisherOpen = !IsPublisherOpen;

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
        Search = new SearchViewModel(Rows, () => _sessionStore is null ? null : new FtsSearchService(_sessionStore));
        Settings = new SettingsViewModel(_settings, () => _sessionStore, () => _retentionService);
        Publisher = new PublisherViewModel(sinkProvider: () => _sink ??= EventSinkFactory.Create());
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (_pipeline is null) Start();
        else _ = StopAsync();
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

        _sessionStore ??= new SessionStore(
            DefaultSessionRootDirectory(),
            pinnedFields: _settings.PinnedFields.Select(f => new PinnedField(f.Name, f.JsonPath)).ToList());
        _retentionService ??= new RetentionService(
            DefaultSessionRootDirectory(), _sessionStore, _settings.RetentionCapBytes, _settings.RetentionDays);

        // EventSourceFactory reads EVENTSCOPE_KAFKA_BOOTSTRAP/TOPIC to pick a real
        // KafkaEventSource instead of the default FakeEventSource — see its remarks. Nothing
        // else here branches on which one came back: that's the capability abstraction
        // paying for itself, not a broker-type switch.
        var source = EventSourceFactory.Create();
        Toolbar.CanPeekNonDestructively = source.Capabilities.CanPeekNonDestructively;
        Toolbar.SupportsPartitions = source.Capabilities.SupportsPartitions;

        // Every IEventSource — regardless of concrete broker — raises errors through the
        // same broker-neutral event, so this subscription needs no type test (build plan §5
        // M4: "no if (broker == …) anywhere in the view layer"). A broker's consume loop may
        // fire from its own dedicated or pooled thread, never guaranteed to be the UI thread,
        // so every property touch below is marshalled.
        source.ErrorOccurred += error =>
            Dispatcher.UIThread.Post(() => Toolbar.StatusLabel = $"{source.DisplayName} error: {error.Message}");

        _pipeline = new IngestPipeline(
            source,
            Rows,
            new DispatcherTimerTicker(),
            _sessionStore,
            indexedPrefixBytes: _settings.IndexedPrefixBytes);
        _pipeline.Start();

        Toolbar.IsRunning = true;
        Toolbar.StatusLabel = $"Streaming ({source.DisplayName})";
        _lastStatsTotal = Rows.TotalAppended;
        _lastStatsTimeUtc = DateTime.UtcNow;
    }

    private static string DefaultSessionRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EventScope",
        "sessions");

    private async Task StopAsync()
    {
        var pipeline = _pipeline;
        if (pipeline is null) return;

        _pipeline = null;
        Toolbar.IsRunning = false;
        Toolbar.StatusLabel = "Idle";
        Toolbar.MessagesPerSecond = 0;

        await pipeline.DisposeAsync().ConfigureAwait(true);
        RefreshStats();
    }

    public async Task OnSelectedRowChangedAsync(MessageRowViewModel? vm)
    {
        Rows.SetSelected(vm);
        if (vm is not null)
        {
            Rows.Pin();
        }

        await Detail.LoadAsync(vm, _pipeline, _sessionStore).ConfigureAwait(true);
        RefreshStats();
    }

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
            visibleRowCount: Rows.Count,
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

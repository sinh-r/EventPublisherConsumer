using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Collections;
using EventScope.App.Ingest;
using EventScope.Brokers.Kafka;
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
    private readonly DispatcherTimer _statsTimer;
    private IngestPipeline? _pipeline;
    private SessionStore? _sessionStore;
    private long _lastStatsTotal;
    private DateTime _lastStatsTimeUtc;

    public MessageRowsView Rows { get; } = new();

    public ConnectionToolbarViewModel Toolbar { get; } = new();

    public StatusBarViewModel StatusBar { get; } = new();

    public DetailPaneViewModel Detail { get; } = new();

    public MainWindowViewModel()
    {
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

        _sessionStore ??= new SessionStore(DefaultSessionRootDirectory());

        // EventSourceFactory reads EVENTSCOPE_KAFKA_BOOTSTRAP/TOPIC to pick a real
        // KafkaEventSource instead of the default FakeEventSource — see its remarks. Nothing
        // else here branches on which one came back: that's the capability abstraction
        // paying for itself, not a broker-type switch.
        var source = EventSourceFactory.Create();
        Toolbar.CanPeekNonDestructively = source.Capabilities.CanPeekNonDestructively;
        Toolbar.SupportsPartitions = source.Capabilities.SupportsPartitions;

        if (source is KafkaEventSource kafka)
        {
            // Fires from the dedicated Kafka consume thread (see KafkaEventSource's remarks),
            // never the UI thread — must marshal before touching a bound view-model property.
            kafka.ErrorOccurred += error =>
                Dispatcher.UIThread.Post(() => Toolbar.StatusLabel = $"Kafka error: {error.Message}");
        }

        _pipeline = new IngestPipeline(
            source,
            Rows,
            new DispatcherTimerTicker(),
            _sessionStore.SegmentWriter,
            _sessionStore.Writer,
            _sessionStore.SegmentReader);
        _pipeline.Start();

        Toolbar.IsRunning = true;
        Toolbar.StatusLabel = source is KafkaEventSource ? "Streaming (Kafka)" : "Streaming";
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

        await Detail.LoadAsync(vm, _pipeline).ConfigureAwait(true);
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
            pinnedNewCount: Rows.PinnedNewCount);
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer.Stop();
        if (_pipeline is not null)
        {
            await _pipeline.DisposeAsync().ConfigureAwait(true);
        }

        _sessionStore?.Dispose();
    }
}

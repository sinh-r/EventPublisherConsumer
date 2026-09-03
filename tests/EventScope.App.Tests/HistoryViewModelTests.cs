using EventScope.App.Connections;
using EventScope.App.ViewModels;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// The feature end to end, minus the window: write a capture to disk exactly as ingest does, then
/// browse it back the way the UI does — discover the capture, list its days, open one, and read a
/// row's body out of the grid.
///
/// <para>
/// This is the test that actually answers "can I see events from before I started streaming?", so
/// it deliberately goes through real <see cref="SessionStore"/> day files rather than a fake page
/// source, and it never constructs an ingest pipeline: browsing has to work with nothing running.
/// </para>
/// </summary>
public sealed class HistoryViewModelTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("eventscope-history-vm-").FullName;
    private readonly Guid _profileId = Guid.NewGuid();

    private readonly List<HistoryViewModel> _created = [];

    public void Dispose()
    {
        // Every browse holds day-file handles until it is closed. Releasing them here is not
        // test hygiene for its own sake: if this delete fails, retention could not have deleted
        // the day either, which is the failure mode worth catching.
        foreach (var history in _created) history.Dispose();
        Directory.Delete(_base, recursive: true);
    }

    private string ProfileRoot => Path.Combine(_base, _profileId.ToString("N"));

    private ConnectionProfile Profile => new() { Id = _profileId, Name = "prod-kafka", Kind = ConnectionKind.Kafka };

    private async Task<string> WriteCaptureAsync(int messageCount)
    {
        using var store = new SessionStore(ProfileRoot);
        for (var i = 0; i < messageCount; i++)
        {
            var body = $"{{\"order\":{i}}}";
            var coords = store.SegmentWriter.Append(System.Text.Encoding.UTF8.GetBytes(body));
            store.Writer.Enqueue(new WriteOp.InsertMessage(
                EnqueuedTicks: DateTime.UtcNow.Ticks, ReceivedTicks: DateTime.UtcNow.Ticks,
                SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
                MessageId: $"m-{i}", CorrelationId: $"c-{i}", Subject: "orders.created",
                Partition: 0, Flags: 0, Preview: body, BodyHead: body));
        }

        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return store.CurrentDay;
    }

    private HistoryViewModel NewViewModel(IReadOnlyList<ConnectionProfile>? saved = null)
    {
        var history = new HistoryViewModel(() => saved ?? [Profile], _base);
        _created.Add(history);
        return history;
    }

    /// <summary>Discovers captures and waits for the selected one's day list, which loads off the
    /// UI thread.</summary>
    private static async Task RefreshAsync(HistoryViewModel history)
    {
        history.RefreshSessions();
        await history.DaysLoaded;
    }

    [Fact]
    public async Task Finds_a_capture_written_by_a_connection_that_is_not_running()
    {
        await WriteCaptureAsync(3);

        var history = NewViewModel();
        await RefreshAsync(history);

        var session = Assert.Single(history.Sessions);
        Assert.Equal("prod-kafka", session.DisplayName);
        Assert.Equal(_profileId, session.ProfileId);
        Assert.Equal(1, session.DayCount);
    }

    [Fact]
    public async Task Lists_the_days_of_the_selected_capture_with_their_message_counts()
    {
        var day = await WriteCaptureAsync(4);

        var history = NewViewModel();
        await RefreshAsync(history);

        var entry = Assert.Single(history.Days);
        Assert.Equal(day, entry.Day);
        Assert.Equal(4, entry.RowCount);
        Assert.Equal("4 messages", entry.CountLabel);
    }

    [Fact]
    public async Task Opening_a_day_fills_the_grid_with_rows_captured_before_this_run()
    {
        await WriteCaptureAsync(5);

        var history = NewViewModel();
        await RefreshAsync(history);
        await history.OpenDayCommand.ExecuteAsync(null);

        Assert.Equal(5, history.Rows.Count);
        Assert.Contains("5 messages", history.OpenDescription);

        var row = (MessageRowViewModel)history.Rows[0]!;
        Assert.Equal("{\"order\":0}", row.Preview);
        Assert.Equal("orders.created", row.Subject);
        Assert.Equal("c-0", row.CorrelationId);
    }

    [Fact]
    public async Task A_browsed_rows_body_reads_back_off_disk_with_no_pipeline_running()
    {
        // The whole point: no IngestPipeline exists in this test, and the payload still resolves.
        await WriteCaptureAsync(3);

        var history = NewViewModel();
        await RefreshAsync(history);
        await history.OpenDayCommand.ExecuteAsync(null);

        var row = (MessageRowViewModel)history.Rows[1]!;
        var detail = new DetailPaneViewModel();

        await detail.LoadAsync(row, history.ReaderFor(row), pinnedFields: null);

        Assert.Equal("{\"order\":1}", detail.BodyText);
        Assert.False(detail.IsUnavailable);
    }

    [Fact]
    public async Task A_row_carries_the_day_it_was_captured_under()
    {
        var day = await WriteCaptureAsync(2);

        var history = NewViewModel();
        await RefreshAsync(history);
        await history.OpenDayCommand.ExecuteAsync(null);

        Assert.Equal(day, ((MessageRowViewModel)history.Rows[0]!).Day);
    }

    [Fact]
    public async Task Closing_a_browse_empties_the_grid_and_releases_its_handles()
    {
        await WriteCaptureAsync(2);

        var history = NewViewModel();
        await RefreshAsync(history);
        await history.OpenDayCommand.ExecuteAsync(null);
        Assert.Equal(2, history.Rows.Count);

        history.Close();

        Assert.Empty(history.Rows);
        Assert.Equal(string.Empty, history.OpenDescription);

        // Nothing may still hold the day directory open, or retention could not delete it.
        Directory.Delete(ProfileRoot, recursive: true);
    }

    [Fact]
    public void Reports_plainly_when_nothing_has_ever_been_captured()
    {
        var history = NewViewModel();
        history.RefreshSessions();

        Assert.Empty(history.Sessions);
        Assert.Contains("Nothing captured yet", history.StatusText);
    }

    [Fact]
    public async Task A_capture_whose_connection_was_deleted_is_still_browsable_and_says_so()
    {
        await WriteCaptureAsync(2);

        // The connection is gone from the manager, but its captured data is not.
        var history = NewViewModel(saved: []);
        await RefreshAsync(history);

        var session = Assert.Single(history.Sessions);
        Assert.Contains("deleted connection", session.DisplayName);
        Assert.Contains("no longer exists", session.Note);
    }
}

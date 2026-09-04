using System.Text;
using EventScope.App.Collections;
using EventScope.App.ViewModels;
using EventScope.Storage.Search;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// <see cref="SearchViewModel"/>'s deep-scan tier — the explicit, cancellable, whole-disk
/// search behind the UI spec §7 overlay.
///
/// <para>
/// No <see cref="HeadlessFixture"/>: a <see cref="MessageRowsView"/> is not itself an Avalonia
/// UI object and <see cref="SearchViewModel"/> only ever touches plain properties, so nothing
/// here needs a dispatcher — the same reasoning <c>MessageRowsViewSearchTests</c> records. That
/// also keeps these tests clear of the headless dispatcher hazards documented in PROGRESS.md's
/// Blocked item 2 entirely, rather than relying on the fixture's workarounds for them.
/// </para>
/// </summary>
public sealed class SearchViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-searchvm-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task WriteMessageAsync(SessionStore store, byte[] body, CancellationToken ct)
    {
        var coords = store.SegmentWriter.Append(body);
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
            MessageId: null, CorrelationId: null, Subject: "orders.created",
            Partition: 0, Flags: 0, Preview: "p",
            BodyHead: Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 2048))));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }


    private SearchViewModel BuildViewModel(bool connected = true) => new(
        new MessageRowsView(capacity: 64),
        () => connected ? new FtsSearchService(_root) : null,
        new ManualTicker());

    private sealed record Published(IReadOnlyList<SearchHit> Hits, string Root, string Label);

    private static List<Published> CapturePublications(SearchViewModel viewModel)
    {
        var published = new List<Published>();
        viewModel.ResultsRequested += (hits, root, label) => published.Add(new Published(hits, root, label));
        return published;
    }

    /// <summary>
    /// The end-to-end reason this tier exists, driven through the view model: a term that only
    /// appears past the 2 KB <c>body_head</c> prefix is invisible to FTS by construction, and a
    /// deep scan finds it anyway. It publishes on its own when it completes — starting one is
    /// already the explicit gesture that "Show matches" is for the FTS tier.
    /// </summary>
    [Fact]
    public async Task A_deep_scan_finds_a_term_past_the_indexed_prefix_and_opens_the_results_itself()
    {
        using (var store = new SessionStore(_root))
        {
            var padding = new string('x', 3000);
            await WriteMessageAsync(
                store, Encoding.UTF8.GetBytes($"{{\"padding\":\"{padding}\",\"needle\":\"findme\"}}"), Ct);
            await WriteMessageAsync(store, "nothing relevant here"u8.ToArray(), Ct);
        }

        var viewModel = BuildViewModel();
        var published = CapturePublications(viewModel);
        viewModel.Query = "findme";

        await viewModel.DeepScanCommand.ExecuteAsync(null);

        var result = Assert.Single(published);
        Assert.Equal(_root, result.Root);
        Assert.Equal("deep scan for “findme”", result.Label);
        Assert.Equal(1, Assert.Single(result.Hits).MessageRowId);
        Assert.Same(result.Hits, viewModel.LastResults); // "Show matches" reopens the same set
        Assert.True(viewModel.ShowResultsCommand.CanExecute(null));
    }

    /// <summary>
    /// DeepScanner yields newest-first so its early exit keeps the newest matches. The grid reads
    /// oldest-first in every other mode, so the view model has to put them back — the same
    /// correction the FTS path makes with its Reverse().
    /// </summary>
    [Fact]
    public async Task Deep_scan_results_are_published_oldest_first()
    {
        using (var store = new SessionStore(_root))
        {
            for (var i = 0; i < 4; i++)
            {
                await WriteMessageAsync(store, Encoding.UTF8.GetBytes($"needle number {i}"), Ct);
            }
        }

        var viewModel = BuildViewModel();
        var published = CapturePublications(viewModel);
        viewModel.Query = "needle";

        await viewModel.DeepScanCommand.ExecuteAsync(null);

        var hits = Assert.Single(published).Hits;
        Assert.Equal([1L, 2L, 3L, 4L], hits.Select(h => h.MessageRowId));
    }

    /// <summary>
    /// The overlay's final state has to be real, not whatever the last 60 ms tick happened to
    /// catch: the scan can finish between ticks, and a bar frozen at 87% under a closed overlay
    /// is the kind of thing nobody notices until they do.
    /// </summary>
    [Fact]
    public async Task A_completed_scan_closes_the_overlay_on_a_full_bar_and_a_real_final_count()
    {
        using (var store = new SessionStore(_root))
        {
            await WriteMessageAsync(store, "needle one"u8.ToArray(), Ct);
            await WriteMessageAsync(store, "needle two"u8.ToArray(), Ct);
            await WriteMessageAsync(store, "not a match"u8.ToArray(), Ct);
        }

        var viewModel = BuildViewModel();
        viewModel.Query = "needle";

        await viewModel.DeepScanCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeepScanning);
        Assert.Equal("needle", viewModel.DeepScanQuery);
        Assert.Equal(100d, viewModel.DeepScanProgressValue);
        Assert.Contains("2 matches", viewModel.DeepScanStatusText);
        Assert.StartsWith("Scanned ", viewModel.DeepScanStatusText);
    }

    /// <summary>
    /// Whether the cancel lands before the scan finishes is a race by nature, so this pins the
    /// invariant that holds either way: the overlay never stays open, and the results are
    /// published exactly once. A cancelled scan still publishes what it found — cancelling means
    /// "that's enough", not "throw that away".
    /// </summary>
    [Fact]
    public async Task Cancelling_a_deep_scan_still_closes_the_overlay_and_publishes_once()
    {
        using (var store = new SessionStore(_root))
        {
            for (var i = 0; i < 50; i++)
            {
                await WriteMessageAsync(store, Encoding.UTF8.GetBytes($"needle number {i}"), Ct);
            }
        }

        var viewModel = BuildViewModel();
        var published = CapturePublications(viewModel);
        viewModel.Query = "needle";

        var running = viewModel.DeepScanCommand.ExecuteAsync(null);
        viewModel.DeepScanCancelCommand.Execute(null);
        await running;

        Assert.False(viewModel.IsDeepScanning);
        var result = Assert.Single(published);
        Assert.Contains("deep scan for “needle”", result.Label);
        Assert.True(result.Hits.Count <= 50);
    }

    [Fact]
    public void Deep_scan_is_unavailable_until_there_is_something_to_search_for()
    {
        var viewModel = BuildViewModel();

        Assert.False(viewModel.DeepScanCommand.CanExecute(null));

        viewModel.Query = "  ";
        Assert.False(viewModel.DeepScanCommand.CanExecute(null)); // whitespace is not a query

        viewModel.Query = "needle";
        Assert.True(viewModel.DeepScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Deep_scan_without_a_connection_says_so_rather_than_scanning_nothing()
    {
        var viewModel = BuildViewModel(connected: false);
        var published = CapturePublications(viewModel);
        viewModel.Query = "needle";

        await viewModel.DeepScanCommand.ExecuteAsync(null);

        Assert.Equal("not connected", viewModel.StatusText);
        Assert.False(viewModel.IsDeepScanning);
        Assert.Empty(published);
    }
}

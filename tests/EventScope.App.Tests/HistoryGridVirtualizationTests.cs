using Avalonia.Controls;
using Avalonia.Data;
using EventScope.App.Collections;
using EventScope.App.ViewModels;
using EventScope.Core.Models;
using EventScope.Storage.Search;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// The same claim <see cref="DataGridVirtualizationSpikeTests"/> proves for the live ring, proven
/// again for <see cref="HistoryRowsView"/> — and for swapping the grid between the two.
///
/// <para>
/// This is not ceremony. Avalonia's <c>DataGrid</c> wraps any <c>ItemsSource</c> that is not
/// already an <c>IDataGridCollectionView</c> in one whose <c>CopySourceToInternalList</c>
/// enumerates the entire source at bind time. That regression was measured once already on the
/// live ring (65,536 reads before the fix, 15 after) and is recorded in PROGRESS.md. A history
/// browse can address far more rows than the ring holds, so the same mistake here would be worse,
/// and it fails silently — the grid still works, it just reads the whole capture into memory first.
/// </para>
/// </summary>
public class HistoryGridVirtualizationTests
{
    private const int HistoricalRowCount = 2_000_000;

    public HistoryGridVirtualizationTests() => HeadlessFixture.EnsureInitialized();

    /// <summary>Stands in for a multi-million-row capture without touching disk — the point here is
    /// what the grid asks for, not where the rows come from.</summary>
    private sealed class SyntheticPageSource(long count) : IHistoryPageSource
    {
        public long Count { get; } = count;
        public string Description => "synthetic";
        public long Reads;

        public bool TryGet(long index, out SearchHit hit)
        {
            Interlocked.Increment(ref Reads);
            hit = new SearchHit(
                Day: "2026-08-29",
                MessageRowId: index + 1,
                EnqueuedTicks: DateTime.UnixEpoch.Ticks + index,
                SegmentId: 0,
                Offset: (int)(index % 1024) * 128,
                Length: 128,
                MessageId: $"m-{index}",
                CorrelationId: $"c-{index}",
                Subject: $"orders.created.{index % 16}",
                Preview: $"preview-{index}",
                Partition: (short)(index % 4),
                Flags: MessageFlags.None,
                IndexHwm: SearchHit.IndexHwmNotApplicable);
            return true;
        }

        public void Dispose() { }
    }

    private static DataGrid BuildGrid(System.Collections.IEnumerable itemsSource)
    {
        var grid = new DataGrid
        {
            ItemsSource = itemsSource,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            RowHeight = 26,
            Width = 800,
            Height = 400,
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Subject",
            Binding = new Binding(nameof(MessageRowViewModel.Subject)),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Preview",
            Binding = new Binding(nameof(MessageRowViewModel.Preview)),
        });

        return grid;
    }

    private static MessageRowsView BuildLiveView(int rowCount)
    {
        var view = new MessageRowsView(65_536);
        var headers = new MessageHeader[rowCount];
        var previews = new string?[rowCount];
        var subjects = new string[rowCount];
        var correlationIds = new string[rowCount];

        for (var i = 0; i < rowCount; i++)
        {
            headers[i] = new MessageHeader(
                sequence: i, enqueuedTicks: DateTime.UtcNow.Ticks + i, rowId: i,
                segmentId: 0, offset: i * 128, length: 128,
                subjectId: i % 16, correlationInternId: i % 1000,
                partition: (short)(i % 4), flags: MessageFlags.None);
            previews[i] = $"live-{i}";
            subjects[i] = "orders.created";
            correlationIds[i] = $"c-{i}";
        }

        view.AppendBatch(headers, previews, subjects, correlationIds);
        return view;
    }

    [Fact]
    public void Binding_a_multi_million_row_history_does_not_materialize_it()
    {
        // RunOnUi marshals onto the dispatcher thread - required, not stylistic. See
        // HeadlessFixture.RunOnUi's remarks and PROGRESS.md's dispatcher-loop finding.
        HeadlessFixture.RunOnUi(() =>
        {
            var source = new SyntheticPageSource(HistoricalRowCount);
            var view = new HistoryRowsView();
            view.SetSource(source);

            var grid = BuildGrid(view);
            var window = new Window { Content = grid, Width = 800, Height = 400 };

            window.Show();
            HeadlessFixture.Pump();

            Assert.Equal(HistoricalRowCount, view.Count);
            Assert.True(
                view.IndexerReads is > 0 and < 200,
                $"expected only the visible screenful to be realized at bind time, got {view.IndexerReads} reads");
            Assert.True(
                source.Reads < 200,
                $"expected the page source to be asked for only what was realized, got {source.Reads} reads");

            window.Close();
        });
    }

    [Fact]
    public void Swapping_the_grid_between_live_and_history_and_back_stays_virtualized()
    {
        // The mode switch is the one genuinely new risk in this design: both sources implement
        // IDataGridCollectionView, but DataGrid's teardown path on ItemsSource change is what
        // decides whether the incoming source gets wrapped after all.
        HeadlessFixture.RunOnUi(() =>
        {
            var live = BuildLiveView(200_000);
            var history = new HistoryRowsView();
            history.SetSource(new SyntheticPageSource(HistoricalRowCount));

            var grid = BuildGrid(live);
            var window = new Window { Content = grid, Width = 800, Height = 400 };

            window.Show();
            HeadlessFixture.Pump();

            history.ResetIndexerReadCount();
            grid.ItemsSource = history;
            HeadlessFixture.Pump();

            Assert.True(
                history.IndexerReads is > 0 and < 200,
                $"switching to history materialized {history.IndexerReads} rows");

            live.ResetIndexerReadCount();
            grid.ItemsSource = live;
            HeadlessFixture.Pump();

            Assert.True(
                live.IndexerReads is > 0 and < 200,
                $"switching back to live materialized {live.IndexerReads} rows");

            window.Close();
        });
    }

    [Fact]
    public void Rebinding_a_history_source_in_place_does_not_materialize_the_new_one()
    {
        // Opening a different day reuses the same view, so SetSource's Reset must behave the same
        // way a fresh bind does.
        HeadlessFixture.RunOnUi(() =>
        {
            var view = new HistoryRowsView();
            view.SetSource(new SyntheticPageSource(1_000));

            var grid = BuildGrid(view);
            var window = new Window { Content = grid, Width = 800, Height = 400 };

            window.Show();
            HeadlessFixture.Pump();

            var bigger = new SyntheticPageSource(HistoricalRowCount);
            view.ResetIndexerReadCount();
            view.SetSource(bigger);
            HeadlessFixture.Pump();

            Assert.Equal(HistoricalRowCount, view.Count);
            Assert.True(
                view.IndexerReads < 200,
                $"rebinding materialized {view.IndexerReads} rows");

            window.Close();
        });
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.VisualTree;
using EventScope.App.Collections;
using EventScope.App.ViewModels;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// Stage 1 of the build plan: prove <see cref="MessageRowsView"/> actually gets DataGrid
/// onto the IList fast path before any real UI is built on top of it. Two claims to prove:
/// (a) scrolling touches only realized indices, not the whole backing store; (b) selection
/// survives a forced Reset by object identity, per DataGridDataConnection's recycleRows
/// behaviour.
/// </summary>
public class DataGridVirtualizationSpikeTests
{
    private const int RingCapacity = 65_536;
    private const int SyntheticRowCount = 200_000;

    public DataGridVirtualizationSpikeTests() => HeadlessFixture.EnsureInitialized();

    private static MessageRowsView BuildPopulatedView(int rowCount, int capacity = RingCapacity)
    {
        var view = new MessageRowsView(capacity);
        var headers = new MessageHeader[rowCount];
        var previews = new string?[rowCount];
        var subjects = new string[rowCount];
        var correlationIds = new string[rowCount];

        for (var i = 0; i < rowCount; i++)
        {
            headers[i] = new MessageHeader(
                sequence: i,
                enqueuedTicks: DateTime.UtcNow.Ticks + i,
                rowId: i,
                segmentId: 0,
                offset: i * 128,
                length: 128,
                subjectId: i % 16,
                correlationInternId: i % 1000,
                partition: (short)(i % 4),
                flags: MessageFlags.None);
            previews[i] = $"preview-{i}";
            subjects[i] = $"orders.created.{i % 16}";
            correlationIds[i] = Guid.NewGuid().ToString();
        }

        view.AppendBatch(headers, previews, subjects, correlationIds);
        return view;
    }

    private static DataGrid BuildGrid(MessageRowsView view)
    {
        var grid = new DataGrid
        {
            ItemsSource = view,
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
            Header = "Correlation ID",
            Binding = new Binding(nameof(MessageRowViewModel.CorrelationId)),
        });

        return grid;
    }

    [Fact]
    public void View_reports_fixed_window_length_once_ring_is_full()
    {
        var view = BuildPopulatedView(SyntheticRowCount);

        Assert.Equal(RingCapacity, view.Count);
        Assert.Equal(SyntheticRowCount, view.TotalAppended);
    }

    [Fact]
    public void Initial_bind_does_not_materialize_the_backing_store()
    {
        // RunOnUi marshals onto the dispatcher thread regardless of which thread this method
        // body happens to run on — see HeadlessFixture.RunOnUi's remarks.
        HeadlessFixture.RunOnUi(() =>
        {
            var view = BuildPopulatedView(SyntheticRowCount);
            var grid = BuildGrid(view);
            var window = new Window { Content = grid, Width = 800, Height = 400 };

            window.Show();
            HeadlessFixture.Pump();

            // Before implementing IDataGridCollectionView, DataGrid wrapped a plain IList in
            // its own DataGridCollectionView, whose CopySourceToInternalList() enumerated all
            // 65,536 rows at bind time — exactly the cost this class exists to avoid. A
            // 400px-tall grid at RowHeight 26 shows ~15 rows; realizing a couple of screenfuls
            // of buffer is fine, realizing the whole ring is the regression this guards.
            Assert.True(
                view.IndexerReads is > 0 and < 200,
                $"expected only the visible screenful to be realized at bind time, got {view.IndexerReads} reads");

            window.Close();
        });
    }

    [Fact]
    public void A_small_scroll_touches_only_the_newly_revealed_rows()
    {
        // See HeadlessFixture.RunOnUi's remarks on why this wrapper is required, not optional.
        HeadlessFixture.RunOnUi(() =>
        {
            var view = BuildPopulatedView(SyntheticRowCount);
            var grid = BuildGrid(view);
            var window = new Window { Content = grid, Width = 800, Height = 400 };

            window.Show();
            HeadlessFixture.Pump();

            var verticalScrollBar = grid.GetVisualDescendants().OfType<ScrollBar>()
                .First(s => s.Orientation == Orientation.Vertical);
            var valueBefore = verticalScrollBar.Value;

            // Initial layout necessarily touches the first screenful; only bound the cost of
            // a *subsequent* scroll, which is the steady-state operation the spec cares about.
            view.ResetIndexerReadCount();

            // Drive real mouse-wheel input through the headless platform rather than poking
            // ScrollBar.Value directly — DataGrid reacts to the wheel event, not to the
            // ScrollBar's Value property changing out from under it. DataGrid realizes rows
            // incrementally as they scroll into view (cost proportional to rows crossed, not
            // to a fixed per-frame count), so keep this to a small, single-gesture scroll.
            window.MouseWheel(new Point(400, 200), new Vector(0, -3));
            HeadlessFixture.Pump();

            Assert.True(
                verticalScrollBar.Value != valueBefore,
                $"scroll did not move the ScrollBar: value stayed at {valueBefore} (max={verticalScrollBar.Maximum})");

            // A handful of newly revealed rows, not the 65,536-row backing store.
            Assert.True(
                view.IndexerReads is > 0 and < 100,
                $"expected a bounded number of indexer reads after one small scroll, got {view.IndexerReads}. " +
                $"scrollbar value {valueBefore} -> {verticalScrollBar.Value} (max={verticalScrollBar.Maximum})");

            window.Close();
        });
    }

    [Fact]
    public void Selection_survives_a_forced_reset_by_object_identity()
    {
        // See HeadlessFixture.RunOnUi's remarks on why this wrapper is required, not optional.
        HeadlessFixture.RunOnUi(() =>
        {
            var view = BuildPopulatedView(SyntheticRowCount);
            var grid = BuildGrid(view);
            var window = new Window { Content = grid, Width = 800, Height = 400 };

            window.Show();
            HeadlessFixture.Pump();

            // Row 150,000 (by append order) sits at a fixed offset from the window base once
            // the ring is full and no longer growing.
            const long targetSequence = 150_000;
            var windowBase = view.TotalAppended - view.Count;
            var targetIndex = (int)(targetSequence - windowBase);

            var selectedVm = (MessageRowViewModel)view[targetIndex]!;
            view.SetSelected(selectedVm);
            grid.SelectedItem = selectedVm;
            HeadlessFixture.Pump();

            Assert.Same(selectedVm, grid.SelectedItem);

            view.ForceReset();
            HeadlessFixture.Pump();

            Assert.Same(selectedVm, grid.SelectedItem);
            Assert.Equal(targetSequence, selectedVm.Sequence);

            window.Close();
        });
    }
}

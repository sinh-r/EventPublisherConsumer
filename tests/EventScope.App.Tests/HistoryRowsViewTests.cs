using EventScope.App.Collections;
using EventScope.App.ViewModels;
using EventScope.Core.Models;
using EventScope.Storage.Search;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// <see cref="HistoryRowsView"/>'s realization, pooling and selection behaviour, driven through a
/// hand-rolled <see cref="IHistoryPageSource"/> rather than a real day file. Object-level, with no
/// headless fixture: none of this touches Avalonia's dispatcher, and the DataGrid-facing claims
/// are proven separately in <see cref="HistoryGridVirtualizationTests"/>.
/// </summary>
public class HistoryRowsViewTests
{
    /// <summary>Counts what the view actually asked for, which is how "it virtualizes" is proven
    /// rather than asserted. Can be told to fail a specific index, standing in for a day file
    /// deleted underneath a browse.</summary>
    private sealed class FakePageSource(long count) : IHistoryPageSource
    {
        public long Count { get; } = count;
        public string Description => "fake";
        public List<long> Requested { get; } = [];
        public long? FailAt { get; set; }
        public bool Disposed { get; private set; }

        public bool TryGet(long index, out SearchHit hit)
        {
            Requested.Add(index);

            if (index == FailAt)
            {
                hit = null!;
                return false;
            }

            hit = new SearchHit(
                Day: "2026-08-29",
                MessageRowId: index + 1,
                EnqueuedTicks: DateTime.UnixEpoch.Ticks + index,
                SegmentId: 0,
                Offset: (int)index * 128,
                Length: 128,
                MessageId: $"m-{index}",
                CorrelationId: $"c-{index}",
                Subject: $"orders.created.{index % 4}",
                Preview: $"preview-{index}",
                Partition: (short)(index % 4),
                Flags: MessageFlags.None,
                IndexHwm: SearchHit.IndexHwmNotApplicable);
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private static HistoryRowsView ViewOver(IHistoryPageSource source)
    {
        var view = new HistoryRowsView();
        view.SetSource(source);
        return view;
    }

    [Fact]
    public void Reports_the_sources_row_count_without_reading_any_row()
    {
        var source = new FakePageSource(5_000_000);
        var view = ViewOver(source);

        Assert.Equal(5_000_000, view.Count);
        Assert.Equal(5_000_000, view.TotalRows);
        Assert.Empty(source.Requested);
    }

    [Fact]
    public void Reading_a_row_touches_only_that_row()
    {
        var source = new FakePageSource(1_000_000);
        var view = ViewOver(source);

        var row = (MessageRowViewModel)view[42]!;

        Assert.Equal([42], source.Requested);
        Assert.Equal("preview-42", row.Preview);
        Assert.Equal(42, row.Sequence);
    }

    [Fact]
    public void A_row_carries_the_day_it_came_from_so_its_payload_can_be_found_again()
    {
        var view = ViewOver(new FakePageSource(10));

        var row = (MessageRowViewModel)view[3]!;

        Assert.Equal("2026-08-29", row.Day);
        Assert.Equal(3, row.Partition % 4);
    }

    [Fact]
    public void A_realized_row_is_served_from_cache_on_the_second_read()
    {
        var source = new FakePageSource(100);
        var view = ViewOver(source);

        var first = view[7];
        var second = view[7];

        Assert.Same(first, second);
        Assert.Equal([7], source.Requested);
    }

    [Fact]
    public void An_unloaded_row_is_recycled_rather_than_reallocated()
    {
        var view = ViewOver(new FakePageSource(100));

        var first = (MessageRowViewModel)view[1]!;
        view.NotifyRowUnloaded(1);
        var second = (MessageRowViewModel)view[2]!;

        Assert.Same(first, second);
        Assert.Equal("preview-2", second.Preview);
    }

    [Fact]
    public void The_selected_row_is_never_recycled_out_from_under_the_detail_pane()
    {
        var view = ViewOver(new FakePageSource(100));

        var selected = (MessageRowViewModel)view[1]!;
        view.SetSelected(selected);
        view.NotifyRowUnloaded(1);

        var other = (MessageRowViewModel)view[2]!;

        Assert.NotSame(selected, other);
        Assert.Equal("preview-1", selected.Preview);
    }

    [Fact]
    public void Selection_survives_a_forced_reset_by_object_identity()
    {
        // DataGrid re-resolves SelectedItem by reference after a Reset, so the same instance must
        // still be reachable at its index - the same contract MessageRowsView maintains.
        var view = ViewOver(new FakePageSource(100));

        var selected = (MessageRowViewModel)view[5]!;
        view.SetSelected(selected);
        view.ForceReset();

        Assert.Same(selected, view[5]);
        Assert.Equal(5, view.IndexOf(selected));
    }

    [Fact]
    public void IndexOf_rejects_a_row_that_is_no_longer_realized_at_its_index()
    {
        var view = ViewOver(new FakePageSource(100));

        var row = (MessageRowViewModel)view[5]!;
        view.NotifyRowUnloaded(5);

        Assert.Equal(-1, view.IndexOf(row));
    }

    [Fact]
    public void An_out_of_range_index_throws()
    {
        var view = ViewOver(new FakePageSource(10));

        Assert.Throws<ArgumentOutOfRangeException>(() => view[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[-1]);
    }

    [Fact]
    public void A_row_the_source_cannot_produce_renders_as_unavailable_rather_than_throwing()
    {
        // A day file deleted mid-browse must degrade to one unusable row, not tear down the grid.
        var source = new FakePageSource(10) { FailAt = 4 };
        var view = ViewOver(source);

        var row = (MessageRowViewModel)view[4]!;

        Assert.Equal("(unavailable)", row.Preview);
        Assert.True(row.IsEvicted);
        Assert.Equal("preview-3", ((MessageRowViewModel)view[3]!).Preview);
    }

    [Fact]
    public void The_instant_search_query_marks_matching_realized_rows()
    {
        var view = ViewOver(new FakePageSource(100));

        var row = (MessageRowViewModel)view[11]!;
        Assert.False(row.IsSearchHit);

        view.SetSearchQuery("preview-11");
        Assert.True(row.IsSearchHit);

        view.SetSearchQuery(null);
        Assert.False(row.IsSearchHit);
    }

    [Fact]
    public void Binding_a_new_source_disposes_the_old_one_and_releases_its_day_handles()
    {
        // Leaving a browse has to let go of the day directory, or retention cannot delete it.
        var first = new FakePageSource(10);
        var view = ViewOver(first);
        _ = view[1];

        var second = new FakePageSource(20);
        view.SetSource(second);

        Assert.True(first.Disposed);
        Assert.Equal(20, view.Count);
        Assert.Equal("preview-0", ((MessageRowViewModel)view[0]!).Preview);
    }

    [Fact]
    public void Clearing_the_source_empties_the_view()
    {
        var view = ViewOver(new FakePageSource(10));

        view.SetSource(null);

        Assert.Empty(view);
        Assert.True(view.IsEmpty);
    }
}

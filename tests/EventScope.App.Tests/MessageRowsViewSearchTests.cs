using EventScope.App.Collections;
using EventScope.App.ViewModels;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// <see cref="MessageRowsView.SetSearchQuery"/> — the instant tier of tiered search. No
/// visual tree is involved (a <see cref="MessageRowsView"/> is not itself an Avalonia UI
/// object, only its consumers like <c>DataGrid</c> are), so this needs no
/// <see cref="HeadlessFixture"/>, matching <c>DataGridVirtualizationSpikeTests</c>' own
/// object-level construction of the same class.
/// </summary>
public sealed class MessageRowsViewSearchTests
{
    private static MessageRowsView BuildPopulatedView(int rowCount, int capacity = 16)
    {
        var view = new MessageRowsView(capacity);
        var headers = new MessageHeader[rowCount];
        var previews = new string?[rowCount];
        var subjects = new string[rowCount];
        var correlationIds = new string[rowCount];

        for (var i = 0; i < rowCount; i++)
        {
            headers[i] = new MessageHeader(
                sequence: i, enqueuedTicks: DateTime.UtcNow.Ticks + i, rowId: i,
                segmentId: 0, offset: i * 64, length: 64, subjectId: 0, correlationInternId: 0,
                partition: 0, flags: MessageFlags.None);
            previews[i] = i == 2 ? "order confirmed" : $"preview-{i}";
            subjects[i] = "orders.created";
            correlationIds[i] = i == 4 ? "special-correlation" : Guid.NewGuid().ToString();
        }

        view.AppendBatch(headers, previews, subjects, correlationIds);
        return view;
    }

    [Fact]
    public void Realized_rows_matching_the_query_are_marked_as_search_hits()
    {
        var view = BuildPopulatedView(5);

        // Realize every row before searching, the way a fully-scrolled-into-view grid would.
        for (var i = 0; i < view.Count; i++) _ = view[i];

        view.SetSearchQuery("confirmed");

        for (var i = 0; i < view.Count; i++)
        {
            var vm = (MessageRowViewModel)view[i]!;
            Assert.Equal(i == 2, vm.IsSearchHit);
        }
    }

    [Fact]
    public void Search_also_matches_against_correlation_id_and_subject()
    {
        var view = BuildPopulatedView(5);
        for (var i = 0; i < view.Count; i++) _ = view[i];

        view.SetSearchQuery("special-correlation");
        Assert.True(((MessageRowViewModel)view[4]!).IsSearchHit);
        Assert.False(((MessageRowViewModel)view[0]!).IsSearchHit);

        view.SetSearchQuery("orders.created");
        for (var i = 0; i < view.Count; i++)
        {
            Assert.True(((MessageRowViewModel)view[i]!).IsSearchHit);
        }
    }

    [Fact]
    public void Clearing_the_query_clears_every_realized_row_immediately()
    {
        var view = BuildPopulatedView(5);
        for (var i = 0; i < view.Count; i++) _ = view[i];

        view.SetSearchQuery("confirmed");
        Assert.True(((MessageRowViewModel)view[2]!).IsSearchHit);

        view.SetSearchQuery(null);
        for (var i = 0; i < view.Count; i++)
        {
            Assert.False(((MessageRowViewModel)view[i]!).IsSearchHit);
        }
    }

    [Fact]
    public void A_newly_realized_row_after_the_query_was_set_is_still_evaluated()
    {
        var view = BuildPopulatedView(5);
        view.SetSearchQuery("confirmed"); // set before row 2 is ever realized

        var vm = (MessageRowViewModel)view[2]!;
        Assert.True(vm.IsSearchHit);
    }

    [Fact]
    public void A_steady_state_refresh_recomputes_search_hit_state_for_the_new_content()
    {
        var view = BuildPopulatedView(16); // fills the 16-capacity ring exactly
        for (var i = 0; i < view.Count; i++) _ = view[i];

        view.SetSearchQuery("preview-0"); // matches only the row currently holding sequence 0
        Assert.True(((MessageRowViewModel)view[0]!).IsSearchHit);

        // One more append shifts the window by one without a Reset (ring already full) -
        // MessageRowsView.RecomputeFollowWindow's steady-state path refreshes every realized
        // row in place, which must include re-evaluating search-hit state for whatever now
        // occupies that index, not leaving the old row's stale result behind.
        view.AppendBatch(
            [new MessageHeader(16, DateTime.UtcNow.Ticks, 16, 0, 0, 64, 0, 0, 0, MessageFlags.None)],
            ["preview-16"], ["orders.created"], [Guid.NewGuid().ToString()]);

        Assert.False(((MessageRowViewModel)view[0]!).IsSearchHit);
    }
}

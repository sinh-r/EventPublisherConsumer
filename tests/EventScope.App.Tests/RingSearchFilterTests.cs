using EventScope.App.Search;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>
/// <see cref="RingSearchFilter"/> holds no Avalonia dependency at all, so unlike most of this
/// assembly these tests need no <see cref="HeadlessFixture"/> — plain object construction and
/// method calls, safe on whatever thread the test runner happens to use.
/// </summary>
public sealed class RingSearchFilterTests
{
    [Fact]
    public void An_empty_or_null_query_deactivates_the_filter()
    {
        var filter = new RingSearchFilter();
        filter.SetQuery("orders");
        Assert.True(filter.IsActive);

        filter.SetQuery(null);
        Assert.False(filter.IsActive);
        Assert.False(filter.Matches("orders.created"));

        filter.SetQuery("orders");
        filter.SetQuery(string.Empty);
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Matches_is_a_case_insensitive_substring_check()
    {
        var filter = new RingSearchFilter();
        filter.SetQuery("ORDER");

        Assert.True(filter.Matches("orders.created"));
        Assert.True(filter.Matches("an order was placed"));
        Assert.False(filter.Matches("nothing relevant here"));
    }

    [Fact]
    public void Matches_is_false_for_null_or_empty_candidate_text()
    {
        var filter = new RingSearchFilter();
        filter.SetQuery("x");

        Assert.False(filter.Matches(null));
        Assert.False(filter.Matches(string.Empty));
    }

    [Fact]
    public void An_inactive_filter_matches_nothing()
    {
        var filter = new RingSearchFilter();
        Assert.False(filter.Matches("orders.created"));
    }
}

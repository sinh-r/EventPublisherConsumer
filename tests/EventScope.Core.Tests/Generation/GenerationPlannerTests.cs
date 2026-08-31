using EventScope.Core.Generation;
using Xunit;

namespace EventScope.Core.Tests.Generation;

public sealed class GenerationPlannerTests
{
    [Fact]
    public void A_leaf_with_no_refs_has_no_diagnostics_and_fills_first()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.a", "plain")]);

        Assert.False(plan.Diagnostics.HasIssues);
        Assert.Equal([0], plan.FillOrder);
    }

    [Fact]
    public void A_ref_target_is_ordered_before_its_dependent()
    {
        var plan = GenerationPlanner.Build([
            new LeafTemplate("$.a", "{{ref:$.b}}"),
            new LeafTemplate("$.b", "{{guid}}"),
        ]);

        Assert.False(plan.Diagnostics.HasIssues);
        var positionOfA = plan.FillOrder.ToList().IndexOf(0);
        var positionOfB = plan.FillOrder.ToList().IndexOf(1);
        Assert.True(positionOfB < positionOfA, "the referenced leaf (b) must fill before its dependent (a)");
    }

    [Fact]
    public void An_unresolved_ref_is_reported_with_its_span_and_is_not_a_cycle()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.a", "{{ref:$.missing}}")]);

        Assert.Empty(plan.Diagnostics.Cycles);
        var unresolved = Assert.Single(plan.Diagnostics.Unresolved);
        Assert.Equal("$.a", unresolved.FromPath);
        Assert.Equal("$.missing", unresolved.TargetPath);
        Assert.Equal("{{ref:$.missing}}".Length, unresolved.Span.Length);
    }

    [Fact]
    public void A_self_reference_is_reported_as_a_one_hop_cycle()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.a", "{{ref:$.a}}")]);

        var cycle = Assert.Single(plan.Diagnostics.Cycles);
        var hop = Assert.Single(cycle.Hops);
        Assert.Equal("$.a", hop.FromPath);
        Assert.Equal("$.a", hop.ToPath);
    }

    [Fact]
    public void A_two_node_cycle_is_reported_as_a_closed_walk()
    {
        var plan = GenerationPlanner.Build([
            new LeafTemplate("$.a", "{{ref:$.b}}"),
            new LeafTemplate("$.b", "{{ref:$.a}}"),
        ]);

        var cycle = Assert.Single(plan.Diagnostics.Cycles);
        Assert.Equal(2, cycle.Hops.Count);
        // The walk closes: each hop's target is the next hop's source, and the last hop's
        // target is the first hop's source.
        for (var i = 0; i < cycle.Hops.Count; i++)
        {
            var next = cycle.Hops[(i + 1) % cycle.Hops.Count];
            Assert.Equal(cycle.Hops[i].ToPath, next.FromPath);
        }
    }

    [Fact]
    public void Every_leaf_still_appears_exactly_once_in_fill_order_even_when_cyclic()
    {
        var plan = GenerationPlanner.Build([
            new LeafTemplate("$.a", "{{ref:$.b}}"),
            new LeafTemplate("$.b", "{{ref:$.a}}"),
            new LeafTemplate("$.c", "plain"),
        ]);

        Assert.Equal(3, plan.FillOrder.Count);
        Assert.Equal([0, 1, 2], plan.FillOrder.OrderBy(i => i));
    }

    [Fact]
    public void A_leaf_depending_on_a_cycle_without_being_in_it_is_not_itself_reported_as_cyclic()
    {
        // c -> a -> b -> a (a/b cycle; c merely depends on a, and is not part of any SCC).
        var plan = GenerationPlanner.Build([
            new LeafTemplate("$.a", "{{ref:$.b}}"),
            new LeafTemplate("$.b", "{{ref:$.a}}"),
            new LeafTemplate("$.c", "{{ref:$.a}}"),
        ]);

        var cycle = Assert.Single(plan.Diagnostics.Cycles);
        Assert.DoesNotContain(cycle.Hops, h => h.FromPath == "$.c" || h.ToPath == "$.c");
    }

    [Fact]
    public void A_hundred_thousand_node_chain_completes_without_overflowing_the_stack()
    {
        const int n = 100_000;
        var leaves = new LeafTemplate[n];
        leaves[0] = new LeafTemplate("$0", "root");
        for (var i = 1; i < n; i++)
        {
            leaves[i] = new LeafTemplate($"${i}", $"{{{{ref:${i - 1}}}}}");
        }

        var plan = GenerationPlanner.Build(leaves);

        Assert.False(plan.Diagnostics.HasIssues);
        Assert.Equal(n, plan.FillOrder.Count);

        var position = new int[n];
        for (var i = 0; i < plan.FillOrder.Count; i++) position[plan.FillOrder[i]] = i;
        for (var i = 1; i < n; i++)
        {
            Assert.True(position[i - 1] < position[i], $"leaf {i - 1} must fill before its dependent {i}");
        }
    }

    [Fact]
    public void An_injected_back_edge_on_a_long_chain_is_reported_as_a_single_cycle()
    {
        const int n = 1_000;
        var leaves = new LeafTemplate[n];
        leaves[0] = new LeafTemplate("$0", "root");
        for (var i = 1; i < n - 1; i++)
        {
            leaves[i] = new LeafTemplate($"${i}", $"{{{{ref:${i - 1}}}}}");
        }
        // Back-edge: the last node points back into the middle of the chain, closing a loop
        // over [500 .. n-1].
        leaves[n - 1] = new LeafTemplate($"${n - 1}", $"{{{{ref:$500}}}}{{{{ref:${n - 2}}}}}");
        leaves[500] = new LeafTemplate("$500", $"{{{{ref:${n - 1}}}}}");

        var plan = GenerationPlanner.Build(leaves);

        var cycle = Assert.Single(plan.Diagnostics.Cycles);
        Assert.True(cycle.Hops.Count >= 2);
        // Everything before the loop (0..499) is unaffected and keeps a real fill order.
        var position = new int[n];
        for (var i = 0; i < plan.FillOrder.Count; i++) position[plan.FillOrder[i]] = i;
        for (var i = 1; i < 500; i++)
        {
            Assert.True(position[i - 1] < position[i]);
        }
    }
}

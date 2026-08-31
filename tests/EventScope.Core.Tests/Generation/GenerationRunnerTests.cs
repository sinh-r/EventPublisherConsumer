using EventScope.Core.Generation;
using Xunit;

namespace EventScope.Core.Tests.Generation;

public sealed class GenerationRunnerTests
{
    [Fact]
    public void A_plain_literal_leaf_fills_to_itself()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.a", "plain text")]);
        var values = new GenerationRunner().Fill(plan);

        Assert.Equal("plain text", values[0]);
    }

    [Fact]
    public void A_ref_resolves_to_its_targets_generated_value()
    {
        var plan = GenerationPlanner.Build([
            new LeafTemplate("$.a", "prefix-{{ref:$.b}}"),
            new LeafTemplate("$.b", "literal-value"),
        ]);
        var values = new GenerationRunner().Fill(plan);

        Assert.Equal("prefix-literal-value", values[0]);
        Assert.Equal("literal-value", values[1]);
    }

    [Fact]
    public void A_burst_of_a_thousand_guids_are_all_distinct()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.id", "{{guid}}")]);
        var runner = new GenerationRunner();

        var ids = new HashSet<string>();
        for (var i = 0; i < 1_000; i++)
        {
            var values = runner.Fill(plan);
            ids.Add(values[0]!);
        }

        Assert.Equal(1_000, ids.Count);
    }

    [Fact]
    public void Guid_values_are_valid_version_seven_guids()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.id", "{{guid}}")]);
        var values = new GenerationRunner().Fill(plan);

        Assert.True(Guid.TryParse(values[0], out _));
    }

    [Fact]
    public void Int_without_a_range_argument_uses_the_default_range()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.n", "{{int}}")]);
        var values = new GenerationRunner().Fill(plan);

        Assert.True(int.TryParse(values[0], out var n));
        Assert.InRange(n, 0, 1_000_000);
    }

    [Fact]
    public void Int_with_a_range_argument_stays_within_the_inclusive_bounds()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.n", "{{int:5..7}}")]);
        var runner = new GenerationRunner();

        for (var i = 0; i < 50; i++)
        {
            var values = runner.Fill(plan);
            var n = int.Parse(values[0]!);
            Assert.InRange(n, 5, 7);
        }
    }

    [Fact]
    public void Pick_chooses_one_of_the_pipe_separated_options()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.color", "{{pick:red|green|blue}}")]);
        var runner = new GenerationRunner();

        for (var i = 0; i < 20; i++)
        {
            var values = runner.Fill(plan);
            Assert.Contains(values[0], new[] { "red", "green", "blue" });
        }
    }

    [Fact]
    public void Now_defaults_to_round_trip_iso_and_reads_from_the_injected_time_provider()
    {
        var fixedTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var time = new TestTimeProvider(fixedTime);
        var plan = GenerationPlanner.Build([new LeafTemplate("$.at", "{{now}}")]);
        var values = new GenerationRunner(time).Fill(plan);

        Assert.Equal(fixedTime.ToString("O"), values[0]);
    }

    [Fact]
    public void An_unresolved_ref_fills_to_an_empty_contribution_rather_than_throwing()
    {
        var plan = GenerationPlanner.Build([new LeafTemplate("$.a", "prefix-{{ref:$.missing}}")]);
        var values = new GenerationRunner().Fill(plan);

        Assert.Equal("prefix-", values[0]);
    }

    [Fact]
    public void A_cyclic_leaf_still_fills_something_rather_than_hanging_or_throwing()
    {
        var plan = GenerationPlanner.Build([
            new LeafTemplate("$.a", "{{ref:$.b}}"),
            new LeafTemplate("$.b", "{{ref:$.a}}"),
        ]);

        var values = new GenerationRunner().Fill(plan);

        Assert.Equal(2, values.Count);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

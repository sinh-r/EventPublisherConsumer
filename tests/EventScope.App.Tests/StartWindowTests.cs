using EventScope.App.Ingest;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>Advanceable fake clock, so "last 7 days" can be asserted as an exact instant rather
/// than a tolerance around <c>DateTime.UtcNow</c>. Hand-rolled and per-project, matching the
/// copies already in <c>EventScope.Storage.Tests</c> and <c>EventScope.Acceptance.Tests</c>.</summary>
internal sealed class SettableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// Resolving a picked replay window to the moment a run starts at. No Avalonia and no
/// <see cref="HeadlessFixture"/> — this is arithmetic and parsing, like
/// <see cref="EventSourceFactoryTests"/>.
/// </summary>
public sealed class StartWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static SettableTimeProvider Clock() => new(Now);

    [Fact]
    public void The_connection_default_resolves_to_nothing_so_the_saved_profile_governs()
    {
        // Not a failure: a null start means "don't override", which is why the resolved moment is
        // an out parameter rather than the return value.
        Assert.True(StartWindow.TryResolve(
            StartWindow.ConnectionDefault, customText: null, Clock(), out var startUtc, out var error));

        Assert.Null(startUtc);
        Assert.Empty(error);
    }

    [Fact]
    public void A_tab_that_has_never_had_a_window_picked_behaves_as_the_connection_default()
    {
        Assert.True(StartWindow.TryResolve(window: null, customText: null, Clock(), out var startUtc, out _));

        Assert.Null(startUtc);
    }

    [Theory]
    [InlineData("Last 1 hour", 0, 1)]
    [InlineData("Last 6 hours", 0, 6)]
    [InlineData("Last 24 hours", 1, 0)]
    [InlineData("Last 7 days", 7, 0)]
    [InlineData("Last 14 days", 14, 0)]
    [InlineData("Last 30 days", 30, 0)]
    public void Every_preset_resolves_to_exactly_its_own_distance_before_now(string label, int days, int hours)
    {
        var window = StartWindow.Presets.Single(w => w.Label == label);

        Assert.True(StartWindow.TryResolve(window, customText: null, Clock(), out var startUtc, out _));

        Assert.Equal(Now - TimeSpan.FromDays(days) - TimeSpan.FromHours(hours), startUtc);
    }

    [Fact]
    public void The_window_is_measured_from_the_clock_at_resolution_not_from_when_it_was_picked()
    {
        // What makes a Retry an hour later replay the last seven days from *then*, matching what
        // the label on the picker still says.
        var clock = Clock();
        var window = StartWindow.Presets.Single(w => w.Label == "Last 7 days");

        Assert.True(StartWindow.TryResolve(window, null, clock, out var first, out _));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(StartWindow.TryResolve(window, null, clock, out var second, out _));

        Assert.Equal(TimeSpan.FromHours(1), second!.Value - first!.Value);
    }

    [Fact]
    public void A_custom_timestamp_resolves_to_that_instant_as_utc()
    {
        Assert.True(StartWindow.TryResolve(
            StartWindow.Custom, "2026-08-29 14:03:00", Clock(), out var startUtc, out _));

        Assert.Equal(new DateTimeOffset(2026, 8, 29, 14, 3, 0, TimeSpan.Zero), startUtc);
    }

    [Fact]
    public void A_custom_date_with_no_time_is_accepted_as_midnight_utc()
    {
        Assert.True(StartWindow.TryResolve(StartWindow.Custom, "  2026-08-29  ", Clock(), out var startUtc, out _));

        Assert.Equal(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero), startUtc);
    }

    [Fact]
    public void An_unreadable_custom_timestamp_is_refused_with_the_connection_editors_own_message()
    {
        Assert.False(StartWindow.TryResolve(StartWindow.Custom, "not-a-date", Clock(), out var startUtc, out var error));

        Assert.Null(startUtc);
        Assert.Equal(Connections.StartTimestampFormat.Requirement, error);
    }

    [Fact]
    public void A_blank_custom_timestamp_is_refused_rather_than_silently_meaning_now()
    {
        Assert.False(StartWindow.TryResolve(StartWindow.Custom, "   ", Clock(), out _, out var error));

        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_future_custom_timestamp_is_refused_instead_of_quietly_tailing()
    {
        // The broker would not report this as an error: OffsetsForTimes answers with nothing and
        // KafkaStartOffsets falls back to Offset.End, so the run would look fine and show no
        // backlog at all. Refusing here is the only place the reason can still be given.
        Assert.False(StartWindow.TryResolve(
            StartWindow.Custom, "2027-01-01 00:00:00", Clock(), out var startUtc, out var error));

        Assert.Null(startUtc);
        Assert.Contains("past", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_picker_leads_with_the_connection_default_and_ends_with_custom()
    {
        // Order is load-bearing: the first entry is what an untouched tab runs with, and it must
        // be the one that changes nothing about a saved connection.
        Assert.Same(StartWindow.ConnectionDefault, StartWindow.Presets[0]);
        Assert.Same(StartWindow.Custom, StartWindow.Presets[^1]);
    }

    [Fact]
    public void Only_the_custom_entry_asks_for_a_typed_timestamp()
    {
        Assert.Single(StartWindow.Presets, w => w.IsCustom);
    }

    [Fact]
    public void The_banner_names_both_the_window_and_the_moment_it_landed_on()
    {
        var window = StartWindow.Presets.Single(w => w.Label == "Last 7 days");
        var at = new DateTimeOffset(2026, 8, 29, 14, 3, 0, TimeSpan.Zero);

        var description = StartWindow.Describe(window, at);

        Assert.Contains("last 7 days", description, StringComparison.Ordinal);
        Assert.Contains("2026-08-29 14:03 UTC", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_window_is_described_by_its_moment_alone()
    {
        var at = new DateTimeOffset(2026, 8, 29, 14, 3, 0, TimeSpan.Zero);

        // "the custom… (since …)" would read as nonsense, so the label is dropped for this one.
        Assert.Equal("since 2026-08-29 14:03 UTC", StartWindow.Describe(StartWindow.Custom, at));
    }
}

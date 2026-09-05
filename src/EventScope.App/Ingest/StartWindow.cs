using EventScope.App.Connections;

namespace EventScope.App.Ingest;

/// <summary>
/// One entry in the consumer toolbar's replay-window picker: how far back a run should seek
/// before it starts streaming forward.
///
/// <para>
/// A window is a <em>per-run override</em>, never written to the saved connection. It resolves to
/// a single UTC moment that <see cref="EventSourceFactory"/> turns into
/// <c>KafkaStartFrom.Timestamp</c>; the broker then resolves that moment to a per-partition
/// offset. There is deliberately no end boundary — a replay runs straight on into live traffic
/// rather than stopping when it catches up, so this is a normal live run that simply started
/// earlier.
/// </para>
///
/// <para>
/// A window can never reach further back than the topic's own retention. Asking for 30 days on a
/// topic that keeps 3 gives 3, silently, because that is all the broker has — see
/// <c>KafkaStartOffsets</c>'s remarks on why an unanswerable partition falls back to tailing
/// rather than to the beginning.
/// </para>
/// </summary>
/// <param name="Label">What the picker shows.</param>
/// <param name="Lookback">How far back from "now", or <see langword="null"/> for the two entries
/// that do not describe a duration.</param>
/// <param name="IsCustom">Whether this entry takes an absolute timestamp typed by the user
/// instead of a duration.</param>
public sealed record StartWindow(string Label, TimeSpan? Lookback, bool IsCustom = false)
{
    /// <summary>Leaves the saved connection's own start position in charge — which may itself be
    /// <c>Earliest</c> or a saved timestamp, not necessarily "tail from now". Labelled for what it
    /// does rather than "Live (now)", which would misreport such a profile.</summary>
    public static readonly StartWindow ConnectionDefault = new("Connection default", null);

    /// <summary>Reveals the absolute-timestamp input. Carries no duration of its own.</summary>
    public static readonly StartWindow Custom = new("Custom…", null, IsCustom: true);

    /// <summary>The picker's contents, shortest window first after the default.</summary>
    public static IReadOnlyList<StartWindow> Presets { get; } =
    [
        ConnectionDefault,
        new("Last 1 hour", TimeSpan.FromHours(1)),
        new("Last 6 hours", TimeSpan.FromHours(6)),
        new("Last 24 hours", TimeSpan.FromDays(1)),
        new("Last 7 days", TimeSpan.FromDays(7)),
        new("Last 14 days", TimeSpan.FromDays(14)),
        new("Last 30 days", TimeSpan.FromDays(30)),
        Custom,
    ];

    /// <summary>
    /// The UTC moment <paramref name="window"/> starts at.
    /// </summary>
    /// <param name="customText">The typed timestamp, read only when the window
    /// <see cref="IsCustom"/>.</param>
    /// <param name="startUtc"><see langword="null"/> when the saved connection's own start
    /// position governs — which is not a failure, and is why this is separate from the return
    /// value.</param>
    /// <returns><see langword="false"/>, with <paramref name="error"/> set for the user, when a
    /// custom timestamp cannot be read or is not in the past.</returns>
    public static bool TryResolve(
        StartWindow? window,
        string? customText,
        TimeProvider time,
        out DateTimeOffset? startUtc,
        out string error)
    {
        startUtc = null;
        error = string.Empty;

        // A null window is the state a tab is in before anything is picked, and means the same
        // thing as the default entry rather than being a caller bug.
        if (window is null || window == ConnectionDefault) return true;

        var now = time.GetUtcNow();

        if (window.IsCustom)
        {
            if (!StartTimestampFormat.TryParseUtc(customText, out var parsed))
            {
                error = StartTimestampFormat.Requirement;
                return false;
            }

            var at = new DateTimeOffset(parsed, TimeSpan.Zero);
            if (at >= now)
            {
                // A future timestamp is not an error the broker would report: OffsetsForTimes
                // simply answers with nothing and KafkaStartOffsets falls back to tailing. The
                // run would look like it worked and show no backlog at all, so it is refused
                // here instead, where the reason can still be explained.
                error = "Start timestamp must be in the past.";
                return false;
            }

            startUtc = at;
            return true;
        }

        if (window.Lookback is { } lookback)
        {
            startUtc = now - lookback;
        }

        return true;
    }

    /// <summary>Banner text for a resolved window — the label the user picked, plus the moment it
    /// actually landed on, since "last 7 days" alone does not say when the run began.</summary>
    public static string Describe(StartWindow window, DateTimeOffset startUtc) =>
        window.IsCustom
            ? $"since {startUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
            : $"the {window.Label.ToLowerInvariant()} (since {startUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC)";
}

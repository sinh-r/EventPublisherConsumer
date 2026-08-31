namespace EventScope.Acceptance.Tests;

/// <summary>Advanceable fake clock, duplicated from <c>EventScope.Storage.Tests</c> (that copy
/// is <c>internal</c> to its own assembly) — the build plan's "fake the clock" M2 criterion in
/// the flesh, hand-rolled rather than pulling in <c>Microsoft.Extensions.Time.Testing</c> for
/// one small test double.</summary>
internal sealed class SettableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan delta) => _now += delta;
}

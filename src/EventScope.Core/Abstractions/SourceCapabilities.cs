namespace EventScope.Core.Abstractions;

/// <summary>
/// Describes what a broker connection can and cannot do. The UI binds every
/// broker-specific control to one of these flags instead of switching on
/// broker type, so adding a broker never touches EventScope.App.
/// </summary>
public sealed record SourceCapabilities
{
    public required bool CanPeekNonDestructively { get; init; }
    public required bool SupportsPartitions { get; init; }
    public required bool SupportsSubscriptions { get; init; }
    public required bool SupportsSessions { get; init; }
    public required bool SupportsDeadLetterQueue { get; init; }
    public required bool SupportsReplay { get; init; }
    public required bool SupportsOffsetCommit { get; init; }
}

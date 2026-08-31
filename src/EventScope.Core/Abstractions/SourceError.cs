namespace EventScope.Core.Abstractions;

/// <summary>
/// A broker-neutral error surfaced from a running <see cref="IEventSource"/> via
/// <see cref="IEventSource.ErrorOccurred"/>. Non-fatal errors are informational — the
/// source's consume loop keeps running after raising one; a fatal error instead breaks the
/// loop and faults the <see cref="IEventSource.RunAsync"/> task, so <see cref="IsFatal"/>
/// exists mainly for a UI that wants to distinguish "transient, ignore" from "the connection
/// is dead" without inspecting <see cref="Exception"/>.
/// </summary>
public sealed record SourceError(string Message, bool IsFatal = false, Exception? Exception = null);

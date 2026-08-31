namespace EventScope.Core.Generation;

/// <summary>A slice of a generator template string, for inline diagnostics (build plan
/// §3.5: "reported with its span and line so the editor can render ... at line 8
/// inline"). <see cref="Line"/> is 1-based.</summary>
public readonly record struct TextSpan(int Start, int Length, int Line);

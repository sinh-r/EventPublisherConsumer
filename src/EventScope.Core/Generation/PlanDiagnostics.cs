namespace EventScope.Core.Generation;

/// <summary>One hop of a reported reference cycle: the leaf the {{ref:...}} token lives in,
/// and that token's own span for inline "at line N" reporting.</summary>
public sealed record CycleHop(string FromPath, string ToPath, TextSpan Span);

/// <summary>A closed walk of {{ref:...}} tokens — build plan §3.5: "$.a → $.b → $.c → $.a,
/// each hop with its TextSpan + line". One representative cycle is reported per strongly
/// connected component (or per self-loop); a component's own internal structure may contain
/// more than one cycle, but naming one is enough to tell the user where to look.</summary>
public sealed record RefCycle(IReadOnlyList<CycleHop> Hops);

/// <summary>A {{ref:$.path}} token whose path does not match any leaf in the template — not
/// a cycle, reported separately so the editor can render it inline before publish.</summary>
public sealed record UnresolvedRef(string FromPath, string TargetPath, TextSpan Span);

public sealed record PlanDiagnostics(
    IReadOnlyList<RefCycle> Cycles,
    IReadOnlyList<UnresolvedRef> Unresolved)
{
    public static readonly PlanDiagnostics None = new([], []);

    public bool HasIssues => Cycles.Count > 0 || Unresolved.Count > 0;
}

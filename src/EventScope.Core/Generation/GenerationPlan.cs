using System.Collections.Frozen;

namespace EventScope.Core.Generation;

/// <summary>
/// The output of <see cref="GenerationPlanner.Build"/> — build plan §3.5 pass 1. Depends
/// only on tree structure and token text, never on generated values, which is what makes it
/// cacheable across a burst of fills (recompute once, invalidate on edit).
/// </summary>
public sealed class GenerationPlan
{
    /// <summary>Every leaf, indexed by its position here — this index is what
    /// <see cref="FillOrder"/> and <see cref="IndexByPath"/> refer to.</summary>
    public IReadOnlyList<LeafTemplate> Leaves { get; }

    /// <summary>Leaf <c>i</c>'s lexed template, parallel to <see cref="Leaves"/>.</summary>
    public IReadOnlyList<IReadOnlyList<TemplateSegment>> Segments { get; }

    public FrozenDictionary<string, int> IndexByPath { get; }

    /// <summary>Every leaf index exactly once, in an order safe for <see cref="GenerationRunner"/>
    /// to fill in: a leaf's {{ref}} targets always appear earlier. Leaves involved in (or
    /// depending on) a reported cycle are appended at the end, in index order, since no
    /// dependency-respecting order exists for them — see <see cref="Diagnostics"/>.</summary>
    public IReadOnlyList<int> FillOrder { get; }

    public PlanDiagnostics Diagnostics { get; }

    internal GenerationPlan(
        IReadOnlyList<LeafTemplate> leaves,
        IReadOnlyList<IReadOnlyList<TemplateSegment>> segments,
        FrozenDictionary<string, int> indexByPath,
        IReadOnlyList<int> fillOrder,
        PlanDiagnostics diagnostics)
    {
        Leaves = leaves;
        Segments = segments;
        IndexByPath = indexByPath;
        FillOrder = fillOrder;
        Diagnostics = diagnostics;
    }
}

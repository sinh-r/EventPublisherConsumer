namespace EventScope.Core.Generation;

/// <summary>One JSON leaf's generator template, keyed by its JSON path (e.g. <c>$.a.b[0]</c>)
/// — the unit <see cref="GenerationPlanner"/> builds a dependency graph over.</summary>
public sealed record LeafTemplate(string Path, string Template);

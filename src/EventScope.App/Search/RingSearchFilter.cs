using System.Buffers;

namespace EventScope.App.Search;

/// <summary>
/// The instant tier of tiered search (build plan §5 M2): a per-keystroke substring check
/// against whatever's currently in memory, backed by <see cref="SearchValues{T}"/> for SIMD
/// substring search — "which is what makes the 'instant' scope instant" per the plan's own
/// phrasing. Holds no data itself; callers pass each candidate string to
/// <see cref="Matches"/> (e.g. once per realized row's preview/subject/correlation id).
/// </summary>
public sealed class RingSearchFilter
{
    private SearchValues<string>? _values;

    /// <summary>The active query, or <see langword="null"/> when search is off.</summary>
    public string? Query { get; private set; }

    public bool IsActive => _values is not null;

    public void SetQuery(string? query)
    {
        Query = string.IsNullOrEmpty(query) ? null : query;
        _values = Query is null ? null : SearchValues.Create([Query], StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(string? text) =>
        _values is not null && !string.IsNullOrEmpty(text) && text.AsSpan().ContainsAny(_values);
}

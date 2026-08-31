using System.Text.RegularExpressions;

namespace EventScope.Storage.Sqlite;

/// <summary>One user-configured pinned JSON field: a stable column name and the JSON path
/// (against <c>body_head</c>) it extracts.</summary>
public sealed partial record PinnedField(string Name, string JsonPath)
{
    /// <summary>Identifier-safe column/index name — validated once here rather than trusted
    /// from settings input, since it's embedded directly into DDL (build plan §5 M2:
    /// "pinned JSON-field columns"). SQLite double-quoted identifiers can technically hold
    /// almost anything if escaped, but restricting to this is simpler and removes a whole
    /// class of DDL-injection concerns for what is, after all, free-form settings text.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>A conservative but real check, not a rubber stamp: must start with <c>$</c>
    /// (the JSONPath document root) and contain only characters valid in the
    /// <c>$.a.b[0]</c> dotted/indexed shapes <c>json_extract</c> actually accepts.</summary>
    public static bool IsValidJsonPath(string path) => PathPattern().IsMatch(path);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]*|\[[0-9]+\])*$")]
    private static partial Regex PathPattern();
}

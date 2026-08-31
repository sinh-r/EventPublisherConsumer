using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// Applies user-configured pinned JSON-field columns (build plan §5 M2): one
/// <c>ALTER TABLE ... ADD COLUMN ... GENERATED ALWAYS AS (json_extract(body_head, path))
/// VIRTUAL</c> plus an index, per field, idempotent against columns that already exist.
///
/// <para>
/// <b>Extracted from <c>body_head</c>, not the full body.</b> <c>messages</c> never stores
/// a message's full JSON — only the first 2 KB (or whatever <c>IndexedPrefixBytes</c> is
/// configured to). A pinned field whose value lives past that prefix resolves to
/// <see langword="NULL"/>, which is the "null-resolution" case the build plan calls out;
/// this class doesn't warn about it itself, since it has no way to know the field was
/// present-but-truncated versus genuinely absent from the message — that distinction is a
/// UI-layer concern (surfaced from a NULL pinned value, not from here).
/// </para>
/// </summary>
public static class PinnedFieldsSchema
{
    internal static void Apply(SqliteConnection connection, IReadOnlyList<PinnedField> fields)
    {
        if (fields.Count == 0) return;

        var existingColumns = GetExistingColumns(connection);
        foreach (var field in fields)
        {
            if (!PinnedField.IsValidName(field.Name) || !PinnedField.IsValidJsonPath(field.JsonPath))
            {
                // Defense in depth: the settings UI validates before this is ever reached,
                // but this method builds raw DDL, so it re-validates rather than trusting
                // its caller for something this sensitive.
                throw new ArgumentException(
                    $"Invalid pinned field name or JSON path: '{field.Name}' / '{field.JsonPath}'.");
            }

            var columnName = ColumnName(field.Name);
            if (existingColumns.Contains(columnName)) continue;

            using (var alter = connection.CreateCommand())
            {
                // json_extract's path argument must be a constant expression in a GENERATED
                // ALWAYS AS clause - it can't be a bind parameter here. Safe because both
                // the name and path were just validated against a strict identifier/JSONPath
                // pattern above, and the path is additionally quote-escaped as a literal.
                alter.CommandText = $"""
                    ALTER TABLE messages ADD COLUMN "{columnName}" TEXT
                        GENERATED ALWAYS AS (json_extract(body_head, '{EscapeLiteral(field.JsonPath)}')) VIRTUAL
                    """;
                alter.ExecuteNonQuery();
            }

            using (var index = connection.CreateCommand())
            {
                index.CommandText = $"""CREATE INDEX IF NOT EXISTS "ix_pinned_{columnName}" ON messages("{columnName}")""";
                index.ExecuteNonQuery();
            }
        }
    }

    public static string ColumnName(string fieldName) => $"pinned_{fieldName}";

    private static HashSet<string> GetExistingColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(messages)";
        using var reader = command.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}

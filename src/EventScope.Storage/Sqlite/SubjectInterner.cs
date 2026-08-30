using Microsoft.Data.Sqlite;

namespace EventScope.Storage.Sqlite;

/// <summary>
/// Per-day-file subject string interning against the <c>subjects</c> table. Not thread-safe
/// by design — used exclusively from <see cref="SqliteBatchWriter"/>'s dedicated thread,
/// alongside the same connection it seeds from, so a new subject never needs a second write
/// connection to intern. IDs are not stable across day files; a cross-day search has to join
/// through each file's own <c>subjects</c> table rather than assume shared IDs.
/// </summary>
internal sealed class SubjectInterner
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, int> _ids = new();
    private int _nextId = 1;

    public SubjectInterner(SqliteConnection connection)
    {
        _connection = connection;
        Seed();
    }

    private void Seed()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM subjects";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            _ids[reader.GetString(1)] = id;
            if (id >= _nextId) _nextId = id + 1;
        }
    }

    public int Intern(string subject)
    {
        if (_ids.TryGetValue(subject, out var existing)) return existing;

        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO subjects (id, name) VALUES ($id, $name)";
        insert.Parameters.AddWithValue("$id", _nextId);
        insert.Parameters.AddWithValue("$name", subject);
        insert.ExecuteNonQuery();

        var id = _nextId++;
        _ids[subject] = id;
        return id;
    }
}

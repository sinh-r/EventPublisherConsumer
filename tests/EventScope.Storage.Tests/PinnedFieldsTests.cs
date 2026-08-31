using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventScope.Storage.Tests;

/// <summary>Pinned JSON-field columns (build plan §5 M2): one <c>ALTER TABLE ... GENERATED
/// ALWAYS AS (json_extract(body_head, path)) VIRTUAL</c> column per configured field, plus an
/// index.</summary>
public sealed class PinnedFieldsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-pinned-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task WriteMessageAsync(SessionStore store, string bodyHead, CancellationToken ct)
    {
        var coords = store.SegmentWriter.Append(System.Text.Encoding.UTF8.GetBytes(bodyHead));
        store.Writer.Enqueue(new WriteOp.InsertMessage(
            EnqueuedTicks: 0, ReceivedTicks: 0,
            SegmentId: coords.SegmentId, Offset: coords.Offset, Length: coords.Length,
            MessageId: null, CorrelationId: null, Subject: "orders.created",
            Partition: 0, Flags: 0, Preview: "p", BodyHead: bodyHead));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private static async Task<string?> ReadPinnedValueAsync(string dbPath, string columnName, long id, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT "{columnName}" FROM messages WHERE id = $id""";
        command.Parameters.AddWithValue("$id", id);
        var result = await command.ExecuteScalarAsync(ct);
        return result as string;
    }

    [Fact]
    public async Task A_field_configured_at_open_extracts_its_value_for_every_row()
    {
        var fields = new[] { new PinnedField("customerId", "$.customerId") };
        using var store = new SessionStore(_root, pinnedFields: fields);

        await WriteMessageAsync(store, """{"customerId":"cust-42","amount":10}""", Ct);

        var dbPath = System.IO.Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        var value = await ReadPinnedValueAsync(dbPath, PinnedFieldsSchema.ColumnName("customerId"), 1, Ct);

        Assert.Equal("cust-42", value);
    }

    [Fact]
    public async Task A_field_added_mid_session_applies_to_the_live_day_file()
    {
        using var store = new SessionStore(_root);
        await WriteMessageAsync(store, """{"region":"eu-west"}""", Ct);

        store.AddPinnedField(new PinnedField("region", "$.region"));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var dbPath = System.IO.Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        var value = await ReadPinnedValueAsync(dbPath, PinnedFieldsSchema.ColumnName("region"), 1, Ct);

        // The column is generated, so even a row inserted *before* the column existed
        // resolves correctly - SQLite computes generated columns from the underlying data
        // (body_head) at read time, not at insert time.
        Assert.Equal("eu-west", value);
    }

    [Fact]
    public async Task A_field_whose_path_is_absent_from_the_message_resolves_to_null()
    {
        var fields = new[] { new PinnedField("missing", "$.doesNotExist") };
        using var store = new SessionStore(_root, pinnedFields: fields);

        await WriteMessageAsync(store, """{"present":"yes"}""", Ct);

        var dbPath = System.IO.Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        var value = await ReadPinnedValueAsync(dbPath, PinnedFieldsSchema.ColumnName("missing"), 1, Ct);

        Assert.Null(value);
    }

    [Fact]
    public void Adding_the_same_field_name_twice_is_a_no_op_not_a_duplicate_column_error()
    {
        using var store = new SessionStore(_root);
        store.AddPinnedField(new PinnedField("region", "$.region"));

        // If this actually tried to ALTER TABLE ADD COLUMN a second time, SQLite would throw
        // "duplicate column name" - it must not even attempt it.
        var exception = Record.Exception(() => store.AddPinnedField(new PinnedField("region", "$.region")));
        Assert.Null(exception);
        Assert.Single(store.PinnedFields);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    [InlineData("1startsWithDigit")]
    [InlineData("")]
    public void An_invalid_field_name_is_rejected(string invalidName)
    {
        using var store = new SessionStore(_root);
        Assert.Throws<ArgumentException>(() => store.AddPinnedField(new PinnedField(invalidName, "$.x")));
    }

    [Theory]
    [InlineData("not-a-path")]
    [InlineData("$.a; DROP TABLE messages;")]
    [InlineData("")]
    public void An_invalid_json_path_is_rejected(string invalidPath)
    {
        using var store = new SessionStore(_root);
        Assert.Throws<ArgumentException>(() => store.AddPinnedField(new PinnedField("x", invalidPath)));
    }

    [Fact]
    public async Task Multiple_pinned_fields_each_get_their_own_column()
    {
        var fields = new[]
        {
            new PinnedField("customerId", "$.customerId"),
            new PinnedField("region", "$.region"),
        };
        using var store = new SessionStore(_root, pinnedFields: fields);

        await WriteMessageAsync(store, """{"customerId":"cust-1","region":"us-east"}""", Ct);

        var dbPath = System.IO.Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        Assert.Equal("cust-1", await ReadPinnedValueAsync(dbPath, PinnedFieldsSchema.ColumnName("customerId"), 1, Ct));
        Assert.Equal("us-east", await ReadPinnedValueAsync(dbPath, PinnedFieldsSchema.ColumnName("region"), 1, Ct));
    }

    [Fact]
    public async Task A_new_day_file_applies_every_configured_field_directly_at_open()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SessionStore(_root, time);
        store.AddPinnedField(new PinnedField("region", "$.region"));
        await store.Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        time.Set(new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));
        store.EnsureCurrentDay();

        await WriteMessageAsync(store, """{"region":"ap-south"}""", Ct);

        var newDbPath = System.IO.Path.Combine(store.Directory, $"{store.CurrentDay}.db");
        var value = await ReadPinnedValueAsync(newDbPath, PinnedFieldsSchema.ColumnName("region"), 1, Ct);

        Assert.Equal("ap-south", value);
    }
}

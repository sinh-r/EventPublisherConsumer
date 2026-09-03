using CommunityToolkit.Mvvm.ComponentModel;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using EventScope.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace EventScope.App.ViewModels;

/// <summary>
/// Drives the detail pane for whatever row is selected. The 50&#160;ms delay before showing
/// a spinner (so fast reads don't flicker) lives here — it's UI behaviour, not something the
/// payload reader itself should know about.
/// </summary>
public partial class DetailPaneViewModel : ObservableObject
{
    [ObservableProperty]
    public partial MessageRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string? BodyText { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsUnavailable { get; set; }

    [ObservableProperty]
    public partial string UnavailableReason { get; set; } = string.Empty;

    /// <summary>"name: value" per configured pinned field (build plan §5 M2), or empty if
    /// none are configured. Read on demand from the row's day file rather than carried live
    /// on the ring — see this property's remarks on why that's a deliberate scope decision,
    /// not an oversight.</summary>
    [ObservableProperty]
    public partial string PinnedFieldsText { get; set; } = string.Empty;

    public bool HasPinnedFieldsText => PinnedFieldsText.Length > 0;

    partial void OnPinnedFieldsTextChanged(string value) => OnPropertyChanged(nameof(HasPinnedFieldsText));

    private CancellationTokenSource? _cts;

    public async Task LoadAsync(MessageRowViewModel? row, IPayloadReader? payloadReader, PinnedFieldSource? pinnedFields)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Selected = row;
        BodyText = null;
        IsLoading = false;
        IsUnavailable = false;
        UnavailableReason = string.Empty;
        PinnedFieldsText = string.Empty;

        if (row is null || payloadReader is null)
        {
            return;
        }

        // Best-effort, independent of the payload-read path below: pinned values are a
        // generated SQLite column, not something carried on the in-memory ring the way
        // subject/correlationId are - retrofitting a per-field ring array for a
        // runtime-configurable, arbitrary-cardinality field set would touch the most
        // carefully-validated part of this codebase (MessageRowsView's fixed-shape ring) for
        // a feature most rows never use. A quick on-demand read here, mirroring how the
        // payload itself is already read on demand, is the proportionate choice - see
        // PROGRESS.md's step 7 entry for the full reasoning.
        _ = LoadPinnedFieldsAsync(row, pinnedFields, cts.Token);

        if (row.IsLarge)
        {
            IsUnavailable = true;
            UnavailableReason = "payload not previewed";
            return;
        }

        // row.IsEvicted is a flag frozen onto the header at ingest time (drives the grid's
        // 0.55-opacity row style) — retention (M2) is what actually sets it. A row can also
        // legitimately report its payload gone without that flag, e.g. once retention has
        // deleted its segment file but the row itself is still visible in the grid's much
        // larger header ring; PayloadReader.ReadAsync returning empty below covers that case
        // too, identically.
        if (row.IsEvicted)
        {
            IsUnavailable = true;
            UnavailableReason = "payload evicted";
            return;
        }

        // Segment coordinates ride on the row itself rather than a side lookup — see
        // PROGRESS.md §0.4. Only the fields PayloadReader actually reads are meaningful here.
        var header = new MessageHeader(
            sequence: row.Sequence,
            enqueuedTicks: row.Time.Ticks,
            rowId: row.Sequence,
            segmentId: row.SegmentId,
            offset: row.Offset,
            length: row.Size,
            subjectId: 0,
            correlationInternId: 0,
            partition: row.Partition,
            flags: MessageFlags.None);

        var delayTask = Task.Delay(50, cts.Token);
        var readTask = payloadReader.ReadAsync(header, cts.Token).AsTask();

        var first = await Task.WhenAny(delayTask, readTask).ConfigureAwait(true);
        if (cts.IsCancellationRequested) return;

        if (first == delayTask && !readTask.IsCompleted)
        {
            IsLoading = true;
        }

        ReadOnlyMemory<byte> bytes;
        try
        {
            bytes = await readTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested) return;
        IsLoading = false;

        if (bytes.IsEmpty)
        {
            IsUnavailable = true;
            UnavailableReason = "payload evicted";
            return;
        }

        BodyText = System.Text.Encoding.UTF8.GetString(bytes.Span);
    }

    private async Task LoadPinnedFieldsAsync(MessageRowViewModel row, PinnedFieldSource? source, CancellationToken ct)
    {
        if (source is null || source.Fields.Count == 0) return;

        // The day the row actually names, falling back to its timestamp only when it names none.
        // (segment_id, offset) is unique per message *within a day*, so looking in the wrong day
        // file can match a different message's row - the same hazard the payload read has.
        var day = string.IsNullOrEmpty(row.Day)
            ? SessionLayout.DayFor(row.Time.Ticks)
            : row.Day;

        var dbPath = SessionLayout.DayDatabasePath(source.RootDirectory, day);
        if (!File.Exists(dbPath)) return;

        // No SQLite row id travels on MessageRowViewModel (it's an in-memory ring sequence,
        // not the day file's autoincrement id) - (segment_id, offset) is what's actually
        // unique per message within a day, so that's the lookup key here.
        var fields = source.Fields;
        var columns = string.Join(", ", fields.Select(f => $"\"{PinnedFieldsSchema.ColumnName(f.Name)}\""));

        try
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(ct).ConfigureAwait(true);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM messages WHERE segment_id = $segmentId AND offset = $offset LIMIT 1";
            command.Parameters.AddWithValue("$segmentId", row.SegmentId);
            command.Parameters.AddWithValue("$offset", row.Offset);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || !await reader.ReadAsync(ct).ConfigureAwait(true)) return;

            var lines = new List<string>(fields.Count);
            for (var i = 0; i < fields.Count; i++)
            {
                var value = reader.IsDBNull(i) ? "(not present)" : reader.GetString(i);
                lines.Add($"{fields[i].Name}: {value}");
            }

            if (!ct.IsCancellationRequested)
            {
                PinnedFieldsText = string.Join('\n', lines);
            }
        }
        catch (SqliteException)
        {
            // Best-effort - a pinned column that doesn't exist yet on this particular day
            // file (added mid-session, after this day was already open, but somehow the
            // ALTER TABLE hasn't landed) must not break the rest of the detail pane.
        }
    }
}

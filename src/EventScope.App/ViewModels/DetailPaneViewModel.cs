using CommunityToolkit.Mvvm.ComponentModel;
using EventScope.App.Ingest;
using EventScope.Core.Models;

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

    private CancellationTokenSource? _cts;

    public async Task LoadAsync(MessageRowViewModel? row, IngestPipeline? pipeline)
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

        if (row is null || pipeline is null)
        {
            return;
        }

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
        var readTask = pipeline.PayloadReader.ReadAsync(header, cts.Token).AsTask();

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
}

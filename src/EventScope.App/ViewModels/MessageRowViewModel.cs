using CommunityToolkit.Mvvm.ComponentModel;
using EventScope.Core.Models;
using EventScope.Storage.Search;

namespace EventScope.App.ViewModels;

/// <summary>
/// A recyclable grid row. Instances are pooled by <see cref="Collections.MessageRowsView"/>
/// and repopulated in place — never allocated per message. Binding uses partial
/// properties (Toolkit 8.4 + C# 14) so there is no backing-field naming dance.
/// </summary>
public partial class MessageRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial long Sequence { get; set; }

    [ObservableProperty]
    public partial DateTime Time { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CorrelationId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Size { get; set; }

    [ObservableProperty]
    public partial short Partition { get; set; }

    [ObservableProperty]
    public partial int SegmentId { get; set; }

    [ObservableProperty]
    public partial int Offset { get; set; }

    [ObservableProperty]
    public partial string Preview { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLarge { get; set; }

    [ObservableProperty]
    public partial bool IsEvicted { get; set; }

    [ObservableProperty]
    public partial bool IsDeadLettered { get; set; }

    /// <summary>The day directory this row's payload lives in — the directory it was actually
    /// written to or read from, never one inferred from its timestamp, because a message's broker
    /// timestamp and the day the writer filed it under are different things the moment a backlog is
    /// read. Live rows carry the writer's day, history rows the directory they came out of.
    ///
    /// <para>Empty only for a row appended with no day known — the pre-storage preview path and
    /// tests that construct a view directly. Such a row falls back to inference, which is correct
    /// only while tailing. See <see cref="EventScope.Storage.Segments.HistoryPayloadReaders"/>.</para></summary>
    [ObservableProperty]
    public partial string Day { get; set; } = string.Empty;

    /// <summary>Set by <see cref="Collections.MessageRowsView"/>, not by <see cref="Populate"/>
    /// itself — search state is orthogonal to a row's own header/content and is recomputed
    /// against the live <see cref="Search.RingSearchFilter"/> query on every populate.</summary>
    [ObservableProperty]
    public partial bool IsSearchHit { get; set; }

    /// <summary>
    /// Repopulates this instance in place for <paramref name="sequence"/>. Called both
    /// when a row is first realized and, in follow mode, on every coalescer tick for
    /// every currently-realized row — so it must not allocate beyond the interned
    /// strings it's handed.
    /// </summary>
    /// <param name="day">The day directory the writer filed this message under, or empty when the
    /// caller has no storage behind it. See <see cref="Day"/>.</param>
    public void Populate(
        long sequence,
        in MessageHeader header,
        string subject,
        string correlationId,
        string? preview,
        string day = "")
    {
        Sequence = sequence;
        Time = new DateTime(header.EnqueuedTicks, DateTimeKind.Utc);
        Subject = subject;
        CorrelationId = correlationId;
        Size = header.Length;
        Partition = header.Partition;
        SegmentId = header.SegmentId;
        Offset = header.Offset;
        Preview = preview ?? string.Empty;
        IsLarge = (header.Flags & MessageFlags.IsLarge) != 0;
        IsEvicted = (header.Flags & MessageFlags.PayloadEvicted) != 0;
        IsDeadLettered = (header.Flags & MessageFlags.IsDeadLettered) != 0;
        Day = day;
    }

    /// <summary>
    /// Repopulates this instance from a row read back off disk. The counterpart to
    /// <see cref="Populate"/> for history and search-result grids, which have a
    /// <see cref="SearchHit"/> rather than a ring header.
    /// </summary>
    /// <param name="index">The row's position in the view presenting it — the history views'
    /// equivalent of a ring sequence, and what <c>IndexOf</c> resolves a selection by.</param>
    public void PopulateFromStore(long index, SearchHit hit)
    {
        Sequence = index;
        Time = new DateTime(hit.EnqueuedTicks, DateTimeKind.Utc);
        Subject = hit.Subject;
        CorrelationId = hit.CorrelationId ?? string.Empty;
        Size = hit.Length;
        Partition = hit.Partition;
        SegmentId = hit.SegmentId;
        Offset = hit.Offset;
        Preview = hit.Preview ?? string.Empty;
        IsLarge = (hit.Flags & MessageFlags.IsLarge) != 0;
        IsEvicted = (hit.Flags & MessageFlags.PayloadEvicted) != 0;
        IsDeadLettered = (hit.Flags & MessageFlags.IsDeadLettered) != 0;
        Day = hit.Day;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using EventScope.Core.Models;

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
    public partial string Preview { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLarge { get; set; }

    [ObservableProperty]
    public partial bool IsEvicted { get; set; }

    [ObservableProperty]
    public partial bool IsDeadLettered { get; set; }

    /// <summary>
    /// Repopulates this instance in place for <paramref name="sequence"/>. Called both
    /// when a row is first realized and, in follow mode, on every coalescer tick for
    /// every currently-realized row — so it must not allocate beyond the interned
    /// strings it's handed.
    /// </summary>
    public void Populate(long sequence, in MessageHeader header, string subject, string correlationId, string? preview)
    {
        Sequence = sequence;
        Time = new DateTime(header.EnqueuedTicks, DateTimeKind.Utc);
        Subject = subject;
        CorrelationId = correlationId;
        Size = header.Length;
        Partition = header.Partition;
        Preview = preview ?? string.Empty;
        IsLarge = (header.Flags & MessageFlags.IsLarge) != 0;
        IsEvicted = (header.Flags & MessageFlags.PayloadEvicted) != 0;
        IsDeadLettered = (header.Flags & MessageFlags.IsDeadLettered) != 0;
    }
}

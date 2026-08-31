using CommunityToolkit.Mvvm.ComponentModel;

namespace EventScope.App.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    public partial long TotalAppended { get; set; }

    [ObservableProperty]
    public partial long UiDropped { get; set; }

    [ObservableProperty]
    public partial int VisibleRowCount { get; set; }

    [ObservableProperty]
    public partial long ByteBudgetUsed { get; set; }

    [ObservableProperty]
    public partial long ByteBudgetLimit { get; set; }

    [ObservableProperty]
    public partial double MeterFraction { get; set; }

    [ObservableProperty]
    public partial bool MeterIsHot { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial long PinnedNewCount { get; set; }

    /// <summary>Rows the FTS index is behind by — <c>MAX(messages.id) - fts_hwm</c> on the
    /// current day file. A first-class metric per the build plan (§3.4): the index can fall
    /// meaningfully behind under sustained high-throughput ingest, since indexing only runs
    /// while the batch writer is otherwise idle.</summary>
    [ObservableProperty]
    public partial long IndexLag { get; set; }

    public bool HasIndexLag => IndexLag > 0;

    partial void OnIndexLagChanged(long value) => OnPropertyChanged(nameof(HasIndexLag));

    public void Update(long totalAppended, long uiDropped, int visibleRowCount, long byteBudgetUsed, long byteBudgetLimit, bool isPinned, long pinnedNewCount, long indexLag)
    {
        TotalAppended = totalAppended;
        UiDropped = uiDropped;
        VisibleRowCount = visibleRowCount;
        ByteBudgetUsed = byteBudgetUsed;
        ByteBudgetLimit = byteBudgetLimit;
        MeterFraction = byteBudgetLimit <= 0 ? 0 : Math.Clamp((double)byteBudgetUsed / byteBudgetLimit, 0, 1);
        MeterIsHot = MeterFraction >= 0.9;
        IsPinned = isPinned;
        PinnedNewCount = pinnedNewCount;
        IndexLag = indexLag;
    }
}

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

    public void Update(long totalAppended, long uiDropped, int visibleRowCount, long byteBudgetUsed, long byteBudgetLimit, bool isPinned, long pinnedNewCount)
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
    }
}

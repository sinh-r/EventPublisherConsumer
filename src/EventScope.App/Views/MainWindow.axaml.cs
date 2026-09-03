using Avalonia.Controls;
using Avalonia.Interactivity;
using EventScope.App.ViewModels;

namespace EventScope.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private readonly RowStateClassSync _rowStateClassSync = new();

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        MaybeStartMeasurementSession();
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Classes.Set("odd", e.Row.Index % 2 == 1);
        _rowStateClassSync.OnLoadingRow(e.Row, e.Row.DataContext as MessageRowViewModel);
    }

    // Routed through ActiveRows, not Rows: in history mode the recycled row belongs to the
    // history view, and returning it to the live ring's pool would hand a stale instance to a
    // different logical row.
    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e) =>
        ViewModel.ActiveRows.NotifyRowUnloaded(e.Row.Index);

    private async void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = (sender as DataGrid)?.SelectedItem as MessageRowViewModel;
        await ViewModel.OnSelectedRowChangedAsync(selected);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e) =>
        await ViewModel.DisposeAsync();
}

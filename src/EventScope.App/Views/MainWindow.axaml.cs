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

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e) =>
        ViewModel.Rows.NotifyRowUnloaded(e.Row.Index);

    private async void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = (sender as DataGrid)?.SelectedItem as MessageRowViewModel;
        await ViewModel.OnSelectedRowChangedAsync(selected);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e) =>
        await ViewModel.DisposeAsync();
}

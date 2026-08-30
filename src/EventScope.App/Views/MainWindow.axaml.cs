using Avalonia.Controls;
using Avalonia.Interactivity;
using EventScope.App.ViewModels;

namespace EventScope.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not MessageRowViewModel vm) return;

        e.Row.Classes.Set("odd", e.Row.Index % 2 == 1);
        e.Row.Classes.Set("large", vm.IsLarge);
        e.Row.Classes.Set("evicted", vm.IsEvicted);
        e.Row.Classes.Set("deadLettered", vm.IsDeadLettered);
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

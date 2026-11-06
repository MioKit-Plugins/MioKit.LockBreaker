using Avalonia.Controls;
using Avalonia.Interactivity;
using MioKit.LockBreaker.Views;

namespace MioKit.LockBreaker.Preview.Views;

public partial class MainWindow : Window
{
    private LockQueryWindowModelBase? _model;
    private bool _isReady;

    public MainWindow()
    {
        InitializeComponent();
        ScenarioPicker.ItemsSource = PreviewScenario.Names;
        ScenarioPicker.SelectedIndex = 0;
        _isReady = true;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await ShowScenarioAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _model?.Dispose();
        base.OnClosed(e);
    }

    private async void OnScenarioChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isReady)
            await ShowScenarioAsync();
    }

    private async Task ShowScenarioAsync()
    {
        _model?.Dispose();
        var scenario = ScenarioPicker.SelectedItem as string ?? "files";
        _model = PreviewScenario.CreateModel(scenario);
        PluginPreviewHost.Content = new ProcessListUserControl { DataContext = _model };
        await PreviewScenario.LoadAsync(_model, scenario);
    }

    private void OnOpenFileWindow(object? sender, RoutedEventArgs e) => PreviewScenario.CreateWindow("files").Show();

    private void OnOpenPortWindow(object? sender, RoutedEventArgs e) => PreviewScenario.CreateWindow("ports").Show();
}

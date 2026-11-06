using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MioKit.LockBreaker.Models;
using Ti.Avalonia.Shadcn.Controls;

namespace MioKit.LockBreaker.Views;

public partial class ExplorerLockWindow : ShadWindow
{
    public ExplorerLockWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LockQueryWindowModelBase model)
            model.ConfirmTerminateAsync = ConfirmTerminateAsync;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is ExplorerLockWindowModel model)
        {
            model.ConfirmTerminateAsync = null;
            model.Dispose();
        }
        base.OnClosed(e);
    }

    private Task<bool> ConfirmTerminateAsync(IReadOnlyList<LockOwnerItem> targets) =>
        TerminateConfirm.ShowAsync(this, targets);

    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExplorerLockWindowModel model)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要查询占用的文件",
            AllowMultiple = true
        });

        model.AddTargets(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExplorerLockWindowModel model)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要查询占用的文件夹",
            AllowMultiple = true
        });

        model.AddTargets(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
    }
}

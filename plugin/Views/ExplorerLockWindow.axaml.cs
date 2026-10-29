using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ti.Avalonia.Shadcn.Controls;

namespace MioKit.LockBreaker.Views;

public partial class ExplorerLockWindow : ShadWindow
{
    public ExplorerLockWindow()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        try
        {
            if (DataContext is ExplorerLockWindowModel { AutoQueryOnLoad: true } model)
                await model.RefreshAsync();
        }
        catch
        {
            // 查询失败已在 ViewModel 中写入 StatusText
        }
    }

    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExplorerLockWindowModel model)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要查询占用的文件",
            AllowMultiple = false
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            model.TargetPath = path;
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExplorerLockWindowModel model)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要查询占用的文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            model.TargetPath = path;
    }
}
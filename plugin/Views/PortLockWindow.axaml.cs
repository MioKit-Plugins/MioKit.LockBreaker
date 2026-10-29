using Avalonia.Interactivity;
using Ti.Avalonia.Shadcn.Controls;

namespace MioKit.LockBreaker.Views;

public partial class PortLockWindow : ShadWindow
{
    public PortLockWindow()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        try
        {
            if (DataContext is PortLockWindowModel { AutoQueryOnLoad: true } model)
                await model.RefreshAsync();
        }
        catch
        {
            // 查询失败已在 ViewModel 中写入 StatusText
        }
    }
}
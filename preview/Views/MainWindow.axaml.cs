using Avalonia.Controls;

namespace MioKit.LockBreaker.Preview.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LoadPluginPreview();
    }

    private void LoadPluginPreview()
    {
        // 示例：在 plugin/Views 中创建 UserControl 后，在此 new 并赋给 PluginPreviewHost.Content
        // PluginPreviewHost.Content = new MioKit.LockBreaker.Views.MyPluginView();
    }
}
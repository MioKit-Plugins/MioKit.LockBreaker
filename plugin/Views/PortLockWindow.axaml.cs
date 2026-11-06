using Ti.Avalonia.Shadcn.Controls;

namespace MioKit.LockBreaker.Views;

public partial class PortLockWindow : ShadWindow
{
    public PortLockWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is PortLockWindowModel model)
            model.Dispose();
        base.OnClosed(e);
    }
}

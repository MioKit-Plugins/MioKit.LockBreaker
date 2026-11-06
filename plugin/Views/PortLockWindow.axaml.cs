using MioKit.LockBreaker.Models;
using Ti.Avalonia.Shadcn.Controls;

namespace MioKit.LockBreaker.Views;

public partial class PortLockWindow : ShadWindow
{
    public PortLockWindow()
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
        if (DataContext is PortLockWindowModel model)
        {
            model.ConfirmTerminateAsync = null;
            model.Dispose();
        }
        base.OnClosed(e);
    }

    private Task<bool> ConfirmTerminateAsync(IReadOnlyList<LockOwnerItem> targets) =>
        TerminateConfirm.ShowAsync(this, targets);
}

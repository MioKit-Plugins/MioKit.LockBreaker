using Avalonia.Controls;
using MioKit.LockBreaker.Models;
using Ti.Avalonia.Shadcn.Controls.Dialog;
using Ti.Avalonia.Shadcn.Extensions;

namespace MioKit.LockBreaker.Views;

internal static class TerminateConfirm
{
    public static async Task<bool> ShowAsync(Control host, IReadOnlyList<LockOwnerItem> targets)
    {
        if (targets.Count == 0) return false;

        var count = targets.DistinctBy(target => target.ProcessId).Count();
        var title = count == 1 ? "结束该进程？" : $"结束 {count} 个进程？";
        var dialog = host.GetShadDialogManager()
            .CreateDialog()
            .WithTitle(title)
            .WithContent(new TerminateConfirmDialog { DataContext = TerminateConfirmViewModel.From(targets) })
            .WithWarningIcon()
            .WithYesNoResult("结束进程", "取消", "Destructive");

        return await dialog.TryShowAsync() is true;
    }
}

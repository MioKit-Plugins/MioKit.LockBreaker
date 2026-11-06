using Avalonia.Threading;
using MioKit.LockBreaker.Views;

namespace MioKit.LockBreaker.Services;

public sealed class LockWindowLauncher(ILockOperations operations)
{
    public const string FileWindowKey = "lock-finder.file-lock-window";
    public const string PortWindowKey = "lock-finder.port-lock-window";

    public Task OpenFileLockAsync(IReadOnlyList<string>? paths = null, bool autoQuery = false,
        CancellationToken cancellationToken = default) => OnUIThreadAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = WindowManager.ShowUniqueWindow<ExplorerLockWindow>(FileWindowKey,
            FileLockFinderConst.PluginId, new WindowLifecycleActions<ExplorerLockWindow>
            {
                OnCreated = w => w.DataContext = new ExplorerLockWindowModel(operations)
            });
        var model = (ExplorerLockWindowModel)window.DataContext!;
        if (paths is not null) model.SetTargets(paths);
        return autoQuery && model.HasTargets ? model.RefreshAsync(cancellationToken) : Task.CompletedTask;
    });

    public Task OpenPortLockAsync(int? port = null, bool autoQuery = false,
        CancellationToken cancellationToken = default) => OnUIThreadAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = WindowManager.ShowUniqueWindow<PortLockWindow>(PortWindowKey,
            FileLockFinderConst.PluginId, new WindowLifecycleActions<PortLockWindow>
            {
                OnCreated = w => w.DataContext = new PortLockWindowModel(operations)
            });
        var model = (PortLockWindowModel)window.DataContext!;
        if (port is not null) model.PortText = port.Value.ToString();
        return autoQuery && model.Port is not null ? model.RefreshAsync(cancellationToken) : Task.CompletedTask;
    });

    private static async Task OnUIThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }
        Task work = Task.CompletedTask;
        await Dispatcher.UIThread.InvokeAsync((Action)(() => work = action()));
        await work.ConfigureAwait(false);
    }
}

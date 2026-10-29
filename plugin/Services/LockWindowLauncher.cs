using MioKit.LockBreaker.Views;

namespace MioKit.LockBreaker.Services;

internal static class LockWindowLauncher
{
    public const string FileWindowKey = "lock-finder.file-lock-window";
    public const string PortWindowKey = "lock-finder.port-lock-window";

    public static void OpenFileLock(string? path = null, bool autoQuery = false)
    {
        var model = new ExplorerLockWindowModel
        {
            TargetPath = path ?? string.Empty,
            AutoQueryOnLoad = autoQuery && !string.IsNullOrWhiteSpace(path)
        };

        var window = WindowManager.ShowUniqueWindow<ExplorerLockWindow>(
            key: FileWindowKey,
            onCreate: w => w.DataContext = model);
        window.DataContext = model;
        if (model.AutoQueryOnLoad && window.IsLoaded)
            _ = model.RefreshAsync();
    }

    public static void OpenPortLock(int? port = null, bool autoQuery = false)
    {
        var model = new PortLockWindowModel
        {
            PortText = port?.ToString() ?? string.Empty,
            AutoQueryOnLoad = autoQuery && port is not null
        };

        var window = WindowManager.ShowUniqueWindow<PortLockWindow>(
            key: PortWindowKey,
            onCreate: w => w.DataContext = model);
        window.DataContext = model;
        if (model.AutoQueryOnLoad && window.IsLoaded)
            _ = model.RefreshAsync();
    }
}

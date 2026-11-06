namespace MioKit.LockBreaker.Services;

internal sealed record ExplorerSelectionResolution(IReadOnlyList<string> Paths, string? Error)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Resolves only the Explorer selection captured for this invocation's target window.
/// </summary>
internal static class ExplorerSelectionResolver
{
    public static ExplorerSelectionResolution Resolve(InvocationSnapshot? snapshot)
    {
        if (snapshot is null)
            return Failure("未获取到调用上下文，请从资源管理器重新触发");

        var foreground = snapshot.ForegroundWindow;
        if (!foreground.IsExplorerWindow || foreground.Handle is not { } handle || handle.IsNull)
            return Failure("请先在资源管理器中选中文件或文件夹");

        var capture = snapshot.CaptureReport?[CaptureResources.ExplorerSelection];
        if (capture is { Status: CaptureStatus.Empty })
            return Failure("资源管理器中没有选中的文件或文件夹");

        if (capture is not null && capture.Status != CaptureStatus.Acquired)
            return Failure("未能读取资源管理器中的选中项，请重新触发");

        var explorer = snapshot.Explorer;
        if (explorer is null)
            return Failure("未能读取资源管理器中的选中项，请重新触发");

        var windows = explorer.ExplorerWindowSource;
        ExplorerWindowInfo? target = null;
        if (explorer.ActiveIndex >= 0 && explorer.ActiveIndex < windows.Count)
        {
            var active = windows[explorer.ActiveIndex];
            if (!active.IsDesktop && active.Handle == handle)
                target = active;
        }

        target ??= windows.FirstOrDefault(window => !window.IsDesktop && window.Handle == handle);
        if (target is null)
            return Failure("未找到本次触发的资源管理器窗口，请重新触发");

        var paths = target.SelectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return paths.Length == 0
            ? Failure("资源管理器中没有选中的文件或文件夹")
            : new ExplorerSelectionResolution(paths, null);
    }

    private static ExplorerSelectionResolution Failure(string error) => new([], error);
}

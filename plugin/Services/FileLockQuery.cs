using MioKit.LockBreaker.Models;

namespace MioKit.LockBreaker.Services;

/// <summary>
/// Thin facade over Restart Manager + NT handle scan for UI callers.
/// </summary>
internal static class FileLockQuery
{
    public static IReadOnlyList<LockOwnerItem> FindLockOwners(string path)
    {
        var byPid = new Dictionary<int, LockingProcess>();

        foreach (var process in RestartManagerFinder.FindLockingProcesses(path))
            byPid[process.ProcessId] = process;

        foreach (var process in HandleFinder.FindLockingProcesses(path))
        {
            if (!byPid.ContainsKey(process.ProcessId))
                byPid[process.ProcessId] = process;
        }

        return byPid.Values
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .Select(LockOwnerItem.FromFileLock)
            .ToList();
    }
}

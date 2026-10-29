using System.Diagnostics;

namespace MioKit.LockBreaker.Services;

internal static class ProcessTerminator
{
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "smss", "wininit", "winlogon", "lsass",
        "services", "svchost", "dwm", "system", "explorer",
        "idle", "system idle process"
    };

    public static bool IsCriticalProcess(string processName) =>
        CriticalProcesses.Contains(processName);

    public static bool IsUnmanageable(int processId, string? processName = null)
    {
        if (processId <= 0)
            return true;

        if (!string.IsNullOrWhiteSpace(processName) && IsCriticalProcess(processName))
            return true;

        return false;
    }

    public static string Kill(int processId, string? processName = null)
    {
        if (IsUnmanageable(processId, processName))
        {
            var label = string.IsNullOrWhiteSpace(processName)
                ? $"PID {processId}"
                : $"{processName} (PID {processId})";
            return $"拒绝结束系统/不可管理进程：{label}";
        }

        try
        {
            var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            if (IsCriticalProcess(name))
                return $"拒绝结束关键系统进程：{name} (PID {processId})";

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return $"已结束：{name} (PID {processId})";
        }
        catch (ArgumentException)
        {
            return $"进程 {processId} 已退出";
        }
        catch (Exception ex)
        {
            var label = string.IsNullOrWhiteSpace(processName) ? $"PID {processId}" : $"{processName} (PID {processId})";
            return $"结束失败：{label} — {ex.Message}";
        }
    }

    public static IReadOnlyList<string> KillMany(IEnumerable<(int ProcessId, string ProcessName)> targets)
    {
        var messages = new List<string>();
        foreach (var (pid, name) in targets)
        {
            if (IsUnmanageable(pid, name))
                continue;
            messages.Add(Kill(pid, name));
        }

        if (messages.Count == 0)
            messages.Add("没有可结束的进程");

        return messages;
    }
}

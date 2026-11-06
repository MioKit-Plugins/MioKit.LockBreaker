using MioKit.LockBreaker.Models;

namespace MioKit.LockBreaker.Views;

public sealed record TerminateConfirmProcessItem(string DisplayName, string PidText);

public sealed class TerminateConfirmViewModel
{
    public required string Description { get; init; }
    public required IReadOnlyList<TerminateConfirmProcessItem> Processes { get; init; }
    public bool HasProcesses => Processes.Count > 0;

    public static TerminateConfirmViewModel From(IReadOnlyList<LockOwnerItem> targets)
    {
        var processes = targets
            .DistinctBy(target => target.ProcessId)
            .Take(8)
            .Select(target => new TerminateConfirmProcessItem(
                string.IsNullOrWhiteSpace(target.ProcessName) ? $"PID {target.ProcessId}" : target.ProcessName,
                $"PID {target.ProcessId}"))
            .ToArray();
        var remaining = targets.DistinctBy(target => target.ProcessId).Count() - processes.Length;
        var description = targets.Count == 1
            ? "即将结束选中的占用进程。系统保护进程不会被结束。"
            : $"即将结束 {targets.DistinctBy(target => target.ProcessId).Count()} 个可管理的占用进程。系统保护进程不会被结束。";
        if (remaining > 0)
            description += $" 另有 {remaining} 个进程未在下方列出。";

        return new TerminateConfirmViewModel
        {
            Description = description,
            Processes = processes
        };
    }
}

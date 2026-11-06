using MioKit.LockBreaker.Models;

namespace MioKit.LockBreaker.Services;

/// <summary>Window-facing boundary for OS work, including host task ownership.</summary>
public interface ILockOperations
{
    Task<LockQueryResult> QueryFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
    Task<LockQueryResult> QueryPortAsync(int port, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> TerminateAsync(IReadOnlyList<LockOwnerItem> targets, CancellationToken cancellationToken);
}

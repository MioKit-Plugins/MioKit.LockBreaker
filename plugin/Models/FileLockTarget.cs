namespace MioKit.LockBreaker.Models;

internal sealed record FileLockTarget(string Path, bool IsDirectory);
internal sealed record HandleLockMatch(LockingProcess Process, IReadOnlyList<string> TargetPaths);
internal sealed record HandleScanResult(IReadOnlyList<HandleLockMatch> Matches,
    IReadOnlyList<string> ScannedTargets, IReadOnlyList<LockQueryDiagnostic> Diagnostics);

namespace MioKit.LockBreaker.Models;

public sealed class LockQueryResult
{
    public IReadOnlyList<LockOwnerItem> Owners { get; init; } = [];
    public IReadOnlyList<LockQueryDiagnostic> Diagnostics { get; init; } = [];
    public bool HasFailures => Diagnostics.Any(diagnostic => diagnostic.IsFailure);
    public bool HasCoverageLimitations => Diagnostics.Any(diagnostic => diagnostic.Kind == LockDiagnosticKind.CoverageLimitation);
    public int CompletedTargetCount { get; init; }
    public int TotalTargetCount { get; init; }
}

public enum LockQueryState
{
    Idle,
    Loading,
    Completed,
    PartialFailure,
    Failed,
    Cancelled
}
